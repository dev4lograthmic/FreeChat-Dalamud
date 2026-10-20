using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FreeChat.Dalamud;
using FreeChat.Protocol.V1;

namespace FreeChat.Dalamud.Chat;

public sealed record ChatCaptureStatistics(
    long PreHandle,
    long PostHandle,
    long Structured,
    DateTimeOffset? LastEventAt);

public sealed class ChatCapture : IDisposable
{
    private readonly IChatGui chatGui;
    private readonly ServerHost server;
    private readonly ChatEventFactory factory;
    private readonly Func<string?> localPlayerName;
    private readonly IPluginLog log;
    private long preHandleCount;
    private long postHandleCount;
    private long structuredCount;
    private DateTimeOffset? lastEventAt;
    private bool disposed;

    public ChatCaptureStatistics Statistics => new(
        Interlocked.Read(ref this.preHandleCount),
        Interlocked.Read(ref this.postHandleCount),
        Interlocked.Read(ref this.structuredCount),
        this.lastEventAt);

    public ChatCapture(
        IChatGui chatGui,
        ServerHost server,
        ChatEventFactory factory,
        Func<string?> localPlayerName,
        IPluginLog log)
    {
        this.chatGui = chatGui;
        this.server = server;
        this.factory = factory;
        this.localPlayerName = localPlayerName;
        this.log = log;

        this.chatGui.ChatMessage += this.OnChatMessage;
        this.chatGui.ChatMessageUnhandled += this.OnChatMessageUnhandled;
        this.chatGui.LogMessage += this.OnLogMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            this.server.Publish(this.factory.Create(
                message,
                ChatSource.PreHandle,
                this.localPlayerName()));
            Interlocked.Increment(ref this.preHandleCount);
            this.lastEventAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "FreeChat could not capture a pre-handle chat message.");
        }
    }

    private void OnChatMessageUnhandled(IChatMessage message)
    {
        try
        {
            this.server.Publish(this.factory.Create(
                message,
                ChatSource.PostHandle,
                this.localPlayerName()));
            Interlocked.Increment(ref this.postHandleCount);
            this.lastEventAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "FreeChat could not capture a displayed chat message.");
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (message.IsHandled)
            return;

        try
        {
            this.server.Publish(this.factory.CreateStructured(message, this.localPlayerName()));
            Interlocked.Increment(ref this.structuredCount);
            this.lastEventAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "FreeChat could not capture a structured log message.");
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.chatGui.ChatMessage -= this.OnChatMessage;
        this.chatGui.ChatMessageUnhandled -= this.OnChatMessageUnhandled;
        this.chatGui.LogMessage -= this.OnLogMessage;
    }
}
