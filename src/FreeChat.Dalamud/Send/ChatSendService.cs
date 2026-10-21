using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FreeChat.Dalamud.Logging;
using FreeChat.Protocol.V1;
using FreeChat.Server;

namespace FreeChat.Dalamud.Send;

/// <summary>A best-effort game echo matched to the request and part that produced it.</summary>
public sealed record SendEchoMatch(
    string SessionId,
    string RequestId,
    int PartIndex,
    int PartCount);

public sealed class ChatSendService : IChatSink, IDisposable
{
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private readonly UiLog uiLog;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Channel<SendJob> queue = Channel.CreateBounded<SendJob>(
        new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly object echoGate = new();
    private readonly List<PendingEcho> pendingEchoes = new();
    private readonly Stopwatch sinceLastPart = Stopwatch.StartNew();

    private SendJob? current;
    private int disposed;
    private volatile string? lastStatus;
    private SendResult? lastResult;
    private string? lastEcho;

    public ChatSendService(
        Configuration configuration,
        UiLog uiLog,
        IFramework framework,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.uiLog = uiLog;
        this.framework = framework;
        this.log = log;
        this.framework.Update += this.OnFrameworkUpdate;
    }

    public string? LastStatus => this.lastStatus;
    public SendResult? LastResult => this.lastResult;
    public string? LastEcho => this.lastEcho;

    public async Task<SendResult> SendAsync(
        SendContext context,
        SendRequest request,
        CancellationToken cancellationToken)
    {
        this.lastEcho = null;

        if (!this.TryBuildParts(request, out var parts, out var failure))
        {
            this.lastStatus = FormatStatus(failure);
            this.lastResult = failure;
            if (failure.Error == ErrorCode.CommandBlocked)
            {
                this.log.Warning("FreeChat send blocked by command policy.");
                this.uiLog.Add(UiLogLevel.Warning, "Send blocked by command policy.");
            }
            else
            {
                this.uiLog.Add(UiLogLevel.Warning, $"Send rejected: {failure.Message}");
            }

            return failure;
        }

        var job = new SendJob(context, parts);
        if (!this.queue.Writer.TryWrite(job))
        {
            var full = Error(SendStatus.QueueFull, ErrorCode.QueueFull, "Send queue is full.");
            this.lastStatus = FormatStatus(full);
            this.lastResult = full;
            this.uiLog.Add(UiLogLevel.Warning, "Send queue is full.");
            return full;
        }

        try
        {
            var result = await job.Completion.Task
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            this.lastStatus = FormatStatus(result);
            this.lastResult = result;
            return result;
        }
        catch (OperationCanceledException)
        {
            job.Completion.TrySetCanceled(cancellationToken);
            var cancelled = Error(SendStatus.Cancelled, ErrorCode.InvalidRequest, "Send was cancelled.");
            this.lastStatus = FormatStatus(cancelled);
            this.lastResult = cancelled;
            return cancelled;
        }
        catch (TimeoutException)
        {
            var timeout = Error(SendStatus.Failed, ErrorCode.Internal, "Send timed out.");
            this.lastStatus = FormatStatus(timeout);
            this.lastResult = timeout;
            this.uiLog.Add(UiLogLevel.Error, "Send timed out.");
            return timeout;
        }
    }

    public Task<SendResult> SendTestAsync(string text) =>
        this.SendTestAsync(text, ChatChannel.Unspecified, null, null);

    public Task<SendResult> SendTestAsync(
        string text,
        ChatChannel channel,
        string? targetName,
        string? targetWorld)
    {
        var request = channel == ChatChannel.Unspecified
            ? new SendRequest { RawText = text }
            : new SendRequest
            {
                Chat = new ChatSend
                {
                    Channel = channel,
                    Text = text,
                    Target = channel == ChatChannel.Tell
                        ? new CharacterRef
                        {
                            Name = targetName ?? string.Empty,
                            WorldName = targetWorld ?? string.Empty,
                        }
                        : null,
                },
            };

        return this.SendAsync(
            new SendContext("ui", "ui", null, Guid.NewGuid().ToString("N")),
            request,
            CancellationToken.None);
    }

    public Task<SendResult> CancelAsync(
        SendContext context,
        string targetRequestId,
        CancellationToken cancellationToken)
    {
        var result = new SendResult
        {
            Status = SendStatus.Cancelled,
            Message = "Only queued sends can be cancelled; this request is already in flight.",
        };
        this.lastResult = result;
        this.lastStatus = FormatStatus(result);
        return Task.FromResult(result);
    }

    public bool TryAttachEcho(ChatEvent chatEvent, out SendEchoMatch? match)
    {
        match = null;
        if (string.IsNullOrEmpty(chatEvent.Text))
            return false;

        var now = DateTimeOffset.UtcNow;
        lock (this.echoGate)
        {
            for (var i = 0; i < this.pendingEchoes.Count; i++)
            {
                var pending = this.pendingEchoes[i];
                if (now - pending.At > EchoWindow)
                {
                    this.pendingEchoes.RemoveAt(i--);
                    continue;
                }

                if (!pending.Part.EndsWith(chatEvent.Text, StringComparison.Ordinal))
                    continue;

                chatEvent.OriginRequestId = pending.Context.RequestId;
                match = new SendEchoMatch(
                    pending.Context.SessionId,
                    pending.Context.RequestId,
                    pending.PartIndex,
                    pending.PartCount);
                this.pendingEchoes.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    public void UpdateLastEcho(ChatEvent chatEvent)
    {
        if (string.IsNullOrEmpty(chatEvent.OriginRequestId))
            return;

        this.lastEcho = $"{chatEvent.OriginRequestId} -> {chatEvent.MessageId}";
        this.uiLog.Add(UiLogLevel.Debug, $"Echo matched: {this.lastEcho}");
    }

    private bool TryBuildParts(
        SendRequest request,
        out string[] parts,
        out SendResult failure)
    {
        parts = [];
        failure = new SendResult();

        switch (request.ContentCase)
        {
            case SendRequest.ContentOneofCase.RawText:
            {
                var text = Normalize(request.RawText);
                if (string.IsNullOrEmpty(text))
                    return Fail(SendStatus.Failed, ErrorCode.InvalidRequest, "Text is empty.", out failure);

                if (Encoding.UTF8.GetByteCount(text) > ChatTextSplitter.MaxBytes)
                    return Fail(SendStatus.Failed, ErrorCode.TextTooLong, "Raw text exceeds 500 UTF-8 bytes.", out failure);

                if (CommandPolicy.IsBlocked(text, this.configuration.BlockedCommands))
                    return Fail(SendStatus.Blocked, ErrorCode.CommandBlocked, "Command is blocked by server policy.", out failure);

                parts = [text];
                return true;
            }
            case SendRequest.ContentOneofCase.Chat:
            {
                var chat = request.Chat;
                var text = Normalize(chat.Text);
                if (string.IsNullOrEmpty(text))
                    return Fail(SendStatus.Failed, ErrorCode.InvalidRequest, "Text is empty.", out failure);

                string prefix;
                try
                {
                    prefix = chat.Channel == ChatChannel.Tell
                        ? ChatChannelCommands.TellPrefix(
                            chat.Target?.Name ?? string.Empty,
                            chat.Target?.WorldName ?? string.Empty)
                        : ChatChannelCommands.Prefix(chat.Channel);
                }
                catch (Exception ex)
                {
                    return Fail(SendStatus.Failed, ErrorCode.InvalidRequest, ex.Message, out failure);
                }

                try
                {
                    parts = ChatTextSplitter.SplitWithPrefix(prefix, text);
                    return true;
                }
                catch (Exception ex)
                {
                    return Fail(SendStatus.Failed, ErrorCode.TextTooLong, ex.Message, out failure);
                }
            }
            default:
                return Fail(SendStatus.Failed, ErrorCode.InvalidRequest, "Send request has no content.", out failure);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.current is null)
        {
            if (!this.queue.Reader.TryRead(out this.current))
                return;
        }

        if (this.current.Index > 0)
        {
            var interval = TimeSpan.FromMilliseconds(
                Math.Clamp(this.configuration.SendIntervalMs, 0, 5000));
            if (this.sinceLastPart.Elapsed < interval)
                return;
        }

        var part = this.current.Parts[this.current.Index];
        var pending = new PendingEcho(
            this.current.Context,
            this.current.Index,
            this.current.Parts.Length,
            part,
            DateTimeOffset.UtcNow);
        lock (this.echoGate)
        {
            this.pendingEchoes.Add(pending);
            if (this.pendingEchoes.Count > 64)
                this.pendingEchoes.RemoveRange(0, this.pendingEchoes.Count - 64);
        }

        if (!this.ProcessChatBoxEntry(part))
        {
            lock (this.echoGate)
                this.pendingEchoes.Remove(pending);
            this.Finish(this.current, Error(SendStatus.Failed, ErrorCode.Internal, "The game rejected the chat input."));
            this.current = null;
            return;
        }

        this.sinceLastPart.Restart();
        this.current.Index++;

        if (this.current.Index < this.current.Parts.Length)
            return;

        this.Finish(this.current, new SendResult
        {
            Status = SendStatus.Submitted,
            PartCount = this.current.Parts.Length,
        });
        this.current = null;
    }

    private unsafe bool ProcessChatBoxEntry(string text)
    {
        if (!this.framework.IsInFrameworkUpdateThread)
            return false;

        var module = UIModule.Instance();
        if (module is null)
            return false;

        var value = Utf8String.FromString(text);
        try
        {
            module->ProcessChatBoxEntry(value, 0, false);
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "FreeChat failed to submit chat input.");
            this.uiLog.Add(UiLogLevel.Error, $"Chat input failed: {ex.Message}");
            return false;
        }
        finally
        {
            value->Dtor();
            IMemorySpace.Free(value);
        }
    }

    private void Finish(SendJob job, SendResult result)
    {
        this.lastStatus = FormatStatus(result);
        this.lastResult = result;
        this.uiLog.Add(
            result.Status == SendStatus.Submitted ? UiLogLevel.Info : UiLogLevel.Error,
            result.Status == SendStatus.Submitted
                ? $"Send submitted ({result.PartCount} part(s))."
                : $"Send failed: {result.Message}");
        job.Completion.TrySetResult(result);
    }

    private static string FormatStatus(SendResult result) =>
        string.IsNullOrEmpty(result.Message)
            ? result.Status.ToString()
            : $"{result.Status}: {result.Message}";

    private static SendResult Error(SendStatus status, ErrorCode code, string message) => new()
    {
        Status = status,
        Error = code,
        Message = message,
    };

    private static bool Fail(
        SendStatus status,
        ErrorCode code,
        string message,
        out SendResult failure)
    {
        failure = Error(status, code, message);
        return false;
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("\0", string.Empty)
            .Trim();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.framework.Update -= this.OnFrameworkUpdate;
        this.queue.Writer.TryComplete();
        if (this.current is not null)
            this.Finish(this.current, Error(SendStatus.Cancelled, ErrorCode.ShuttingDown, "Plugin unloaded."));
        this.current = null;

        while (this.queue.Reader.TryRead(out var job))
            this.Finish(job, Error(SendStatus.Cancelled, ErrorCode.ShuttingDown, "Plugin unloaded."));
    }

    private sealed class SendJob
    {
        public SendJob(SendContext context, string[] parts)
        {
            this.Context = context;
            this.Parts = parts;
            this.Completion = new TaskCompletionSource<SendResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public SendContext Context { get; }
        public string[] Parts { get; }
        public int Index { get; set; }
        public TaskCompletionSource<SendResult> Completion { get; }
    }

    private sealed record PendingEcho(
        SendContext Context,
        int PartIndex,
        int PartCount,
        string Part,
        DateTimeOffset At);
}
