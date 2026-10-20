using FreeChat.Protocol;
using FreeChat.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace FreeChat.Server;

internal sealed class SessionHandler : IAsyncDisposable
{
    private readonly FreeChatServer server;
    private readonly ClientConnection connection;
    private readonly object gate = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");

    private SessionState state = SessionState.Connected;
    private DeviceRecord? device;
    private ChallengeState? challenge;
    private ChatFilterMatcher? filter;
    private ChatSource[] sources = [ChatSource.PostHandle];
    private ContentMode contentMode = ContentMode.Current;
    private bool subscribed;
    private int disposed;

    public SessionHandler(FreeChatServer server, ClientConnection connection)
    {
        this.server = server;
        this.connection = connection;
    }

    public string SessionId => this.sessionId;
    public bool IsSubscribed => this.subscribed;

    public async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.PayloadCase)
        {
            case Envelope.PayloadOneofCase.ClientHello:
                await this.HandleClientHelloAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.PairRequest:
                await this.HandlePairRequestAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.ChallengeResponse:
                await this.HandleChallengeResponseAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.Subscribe:
                await this.HandleSubscribeAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.ReplayRequest:
                await this.HandleReplayRequestAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.SendRequest:
                await this.HandleSendRequestAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.SendCancel:
                await this.HandleSendCancelAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.Ping:
                await this.SendAsync(new Envelope
                {
                    RequestId = envelope.RequestId,
                    Pong = new Pong
                    {
                        Nonce = envelope.Ping.Nonce,
                        ServerTime = Timestamp.FromDateTimeOffset(this.server.Clock.UtcNow),
                    },
                }).ConfigureAwait(false);
                break;
            case Envelope.PayloadOneofCase.Unsubscribe:
                this.subscribed = false;
                break;
            default:
                await this.SendErrorAsync(
                    ErrorCode.InvalidRequest,
                    "Unsupported message.",
                    envelope.RequestId).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleClientHelloAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (this.state != SessionState.Connected)
        {
            await this.SendErrorAsync(ErrorCode.InvalidRequest, "ClientHello already received.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        var hello = envelope.ClientHello;
        this.state = SessionState.HelloReceived;

        if (hello.ProtocolVersion != ProtocolConstants.Version)
        {
            await this.SendAuthErrorAsync(
                ErrorCode.UnsupportedVersion,
                $"Protocol version {hello.ProtocolVersion} is not supported.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var publicKey = hello.DevicePublicKey.ToByteArray();
        DeviceRecord? known = null;
        if (!string.IsNullOrEmpty(hello.DeviceId)
            && this.server.Devices.TryGet(hello.DeviceId, out var byId)
            && !byId.Revoked)
        {
            if (publicKey.Length == 0 || byId.PublicKey.AsSpan().SequenceEqual(publicKey))
                known = byId;
        }

        known ??= publicKey.Length > 0 ? this.server.Devices.FindByPublicKey(publicKey) : null;

        if (known is null)
        {
            this.state = SessionState.PairingPending;
            await this.SendAsync(new Envelope
            {
                RequestId = envelope.RequestId,
                PairingRequired = new PairingRequired
                {
                    Server = this.server.CreateServerInfo(),
                    Reason = "unknown_device",
                    ExpiresAt = Timestamp.FromDateTimeOffset(this.server.Pairing.CodeExpiresAt),
                    CodeLength = 6,
                },
            }).ConfigureAwait(false);
            return;
        }

        this.device = known;
        this.state = SessionState.ChallengePending;
        this.challenge = DeviceAuthenticator.CreateChallenge(
            this.server.Clock,
            this.server.Options.ChallengeTimeout);

        await this.SendAsync(new Envelope
        {
            RequestId = envelope.RequestId,
            Challenge = new Challenge
            {
                Nonce = ByteString.CopyFrom(this.challenge.Nonce),
                Algorithm = DeviceAuthenticator.Algorithm,
                ExpiresAt = Timestamp.FromDateTimeOffset(this.challenge.ExpiresAt),
            },
        }).ConfigureAwait(false);
    }

    private async Task HandlePairRequestAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (this.state != SessionState.PairingPending)
        {
            await this.SendErrorAsync(ErrorCode.InvalidRequest, "Pairing is not pending.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        var request = new PairingRequest(
            null,
            envelope.PairRequest.DeviceName,
            envelope.PairRequest.Platform,
            envelope.PairRequest.DevicePublicKey.ToByteArray());

        var result = await this.server.Pairing.PairAsync(
            envelope.PairRequest.Code,
            request,
            this.connection.RemoteAddress?.ToString(),
            cancellationToken).ConfigureAwait(false);

        await this.SendAsync(new Envelope
        {
            RequestId = envelope.RequestId,
            PairResult = new PairResult
            {
                Ok = result.Ok,
                DeviceId = result.DeviceId ?? string.Empty,
                Error = result.Error,
                Message = result.Message ?? string.Empty,
            },
        }).ConfigureAwait(false);

        if (!result.Ok)
            return;

        this.device = this.server.Devices.TryGet(result.DeviceId!, out var paired) ? paired : null;
        if (this.device is null)
        {
            await this.SendAuthErrorAsync(ErrorCode.Internal, "Paired device could not be loaded.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await this.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleChallengeResponseAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (this.state != SessionState.ChallengePending
            || this.device is null
            || this.challenge is null)
        {
            await this.SendErrorAsync(ErrorCode.InvalidRequest, "No challenge is pending.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        if (this.server.Clock.UtcNow >= this.challenge.ExpiresAt)
        {
            await this.SendAuthErrorAsync(ErrorCode.Unauthenticated, "Challenge expired.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var ok = DeviceAuthenticator.Verify(
            this.device,
            this.server.ServerId,
            this.device.DeviceId,
            this.challenge.Nonce,
            envelope.ChallengeResponse.Signature.ToByteArray());

        if (!ok)
        {
            await this.SendAuthErrorAsync(ErrorCode.Unauthenticated, "Challenge signature is invalid.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        this.challenge = null;
        await this.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (this.device is null)
        {
            await this.SendAuthErrorAsync(ErrorCode.Internal, "No device is available.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        this.server.Devices.Touch(this.device.DeviceId, this.server.Clock.UtcNow);
        this.state = SessionState.Authenticated;
        this.server.RegisterSession(this);

        await this.SendAsync(new Envelope
        {
            Welcome = new Welcome
            {
                Server = this.server.CreateServerInfo(),
                SessionId = this.sessionId,
                OldestSequence = this.server.Backlog.OldestSequence,
                LatestSequence = this.server.Backlog.LatestSequence,
                DeviceId = this.device.DeviceId,
                EnabledFeatures = { this.server.EnabledFeatures },
            },
        }).ConfigureAwait(false);
    }

    private async Task HandleSubscribeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (this.state < SessionState.Authenticated)
        {
            await this.SendErrorAsync(ErrorCode.Unauthenticated, "Authenticate before subscribing.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        ChatFilterMatcher matcher;
        try
        {
            matcher = new ChatFilterMatcher(envelope.Subscribe.Filter ?? new ChatFilter());
        }
        catch (FilterValidationException ex)
        {
            await this.SendErrorAsync(ex.Code, ex.Message, envelope.RequestId).ConfigureAwait(false);
            return;
        }

        var requestedSources = envelope.Subscribe.Sources.Count == 0
            ? new[] { ChatSource.PostHandle }
            : envelope.Subscribe.Sources.ToArray();
        var mode = envelope.Subscribe.ContentMode == ContentMode.Unspecified
            ? ContentMode.Current
            : envelope.Subscribe.ContentMode;

        lock (this.gate)
        {
            this.filter = matcher;
            this.sources = requestedSources;
            this.contentMode = mode;
            this.subscribed = true;
        }

        var oldest = this.server.Backlog.OldestSequence;
        var latest = this.server.Backlog.LatestSequence;
        var from = envelope.Subscribe.FromSequence;
        var truncated = from > 0 && oldest > 0 && from < oldest - 1;

        await this.SendAsync(new Envelope
        {
            RequestId = envelope.RequestId,
            Subscribed = new Subscribed
            {
                EffectiveFilter = matcher.Filter,
                EffectiveSources = { requestedSources },
                EffectiveContentMode = mode,
                OldestSequence = oldest,
                LatestSequence = latest,
                ReplayTruncated = truncated,
                ReplayFromSequence = truncated ? oldest : Math.Max(from + 1, 0),
            },
        }).ConfigureAwait(false);

        if (from > 0)
            await this.ReplayAsync(matcher, from, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReplayRequestAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (this.state < SessionState.Authenticated)
        {
            await this.SendErrorAsync(ErrorCode.Unauthenticated, "Authenticate before replaying.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        ChatFilterMatcher matcher;
        try
        {
            matcher = envelope.ReplayRequest.Filter is null
                ? this.filter ?? new ChatFilterMatcher(new ChatFilter())
                : new ChatFilterMatcher(envelope.ReplayRequest.Filter);
        }
        catch (FilterValidationException ex)
        {
            await this.SendErrorAsync(ex.Code, ex.Message, envelope.RequestId).ConfigureAwait(false);
            return;
        }

        var from = envelope.ReplayRequest.FromSequence;
        var to = envelope.ReplayRequest.ToSequence > 0 ? envelope.ReplayRequest.ToSequence : (long?)null;
        await this.ReplayAsync(matcher, from, to, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplayAsync(
        ChatFilterMatcher matcher,
        long fromSequence,
        long? toSequence,
        CancellationToken cancellationToken)
    {
        var oldest = this.server.Backlog.OldestSequence;
        var latest = this.server.Backlog.LatestSequence;
        if (latest == 0)
            return;

        var truncated = fromSequence < oldest - 1;
        if (truncated)
        {
            await this.SendAsync(new Envelope
            {
                HistoryTruncated = new HistoryTruncated
                {
                    RequestedFromSequence = fromSequence,
                    OldestSequence = oldest,
                    LatestSequence = latest,
                },
            }).ConfigureAwait(false);
        }

        var start = truncated ? oldest : fromSequence + 1;
        var end = toSequence is > 0 ? Math.Min(toSequence.Value, latest) : latest;
        foreach (var entry in this.server.Backlog.After(start - 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Event.Sequence > end)
                break;
            if (matcher.Matches(entry.Event))
                await this.SendChatAsync(entry.Event).ConfigureAwait(false);
        }
    }

    private async Task HandleSendRequestAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (this.state < SessionState.Authenticated)
        {
            await this.SendErrorAsync(ErrorCode.Unauthenticated, "Authenticate before sending.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        if (!this.server.SendLimiter.TryTake())
        {
            await this.SendAsync(new Envelope
            {
                RequestId = envelope.RequestId,
                SendResult = new SendResult
                {
                    Status = SendStatus.RateLimited,
                    Error = ErrorCode.RateLimited,
                    Message = "Send rate limit exceeded.",
                },
            }).ConfigureAwait(false);
            await this.SendErrorAsync(ErrorCode.RateLimited, "Send rate limit exceeded.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        var context = new SendContext(
            this.sessionId,
            this.server.OwnerKey,
            this.server.Character,
            envelope.RequestId);

        try
        {
            var result = await this.server.ChatSink
                .SendAsync(context, envelope.SendRequest, cancellationToken)
                .ConfigureAwait(false);

            await this.SendAsync(new Envelope
            {
                RequestId = envelope.RequestId,
                SendResult = result,
            }).ConfigureAwait(false);
        }
        catch (SendRejectedException ex)
        {
            await this.SendAsync(new Envelope
            {
                RequestId = envelope.RequestId,
                SendResult = new SendResult
                {
                    Status = SendStatus.Failed,
                    Error = ex.Code,
                    Message = ex.Message,
                },
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await this.SendAsync(new Envelope
            {
                RequestId = envelope.RequestId,
                SendResult = new SendResult
                {
                    Status = SendStatus.Failed,
                    Error = ErrorCode.Internal,
                    Message = ex.Message,
                },
            }).ConfigureAwait(false);
        }
    }

    private async Task HandleSendCancelAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (this.state < SessionState.Authenticated)
        {
            await this.SendErrorAsync(ErrorCode.Unauthenticated, "Authenticate before cancelling.", envelope.RequestId)
                .ConfigureAwait(false);
            return;
        }

        var context = new SendContext(
            this.sessionId,
            this.server.OwnerKey,
            this.server.Character,
            envelope.RequestId);

        var result = await this.server.ChatSink
            .CancelAsync(context, envelope.SendCancel.TargetRequestId, cancellationToken)
            .ConfigureAwait(false);

        await this.SendAsync(new Envelope
        {
            RequestId = envelope.RequestId,
            SendResult = result,
        }).ConfigureAwait(false);
    }

    public bool TrySendChat(ChatEvent chatEvent)
    {
        ChatFilterMatcher? matcher;
        ChatSource[] currentSources;
        lock (this.gate)
        {
            if (!this.subscribed || this.filter is null)
                return false;
            matcher = this.filter;
            currentSources = this.sources;
        }

        if (!currentSources.Contains(chatEvent.Source))
            return false;
        if (!matcher.Matches(chatEvent))
            return false;

        return this.connection.Send(new Envelope { ChatEvent = chatEvent });
    }

    private Task SendChatAsync(ChatEvent chatEvent) =>
        this.SendAsync(new Envelope { ChatEvent = chatEvent });

    private Task SendAsync(Envelope envelope) => Task.FromResult(this.connection.Send(envelope));

    private async Task SendErrorAsync(ErrorCode code, string message, string? requestId)
    {
        await this.SendAsync(new Envelope
        {
            RequestId = requestId ?? string.Empty,
            Error = new Error { Code = code, Message = message },
        }).ConfigureAwait(false);
    }

    private async Task SendAuthErrorAsync(ErrorCode code, string message, CancellationToken cancellationToken)
    {
        await this.SendAsync(new Envelope
        {
            AuthError = new AuthError { Code = code, Message = message },
        }).ConfigureAwait(false);
        await this.connection.CloseAsync(CloseCode.AuthFailed, message).ConfigureAwait(false);
    }

    public async Task EndAsync(string reason)
    {
        await this.SendAsync(new Envelope
        {
            SessionEnded = new SessionEnded
            {
                Reason = reason,
                EndedAt = Timestamp.FromDateTimeOffset(this.server.Clock.UtcNow),
            },
        }).ConfigureAwait(false);
        await this.connection.CloseAsync(CloseCode.GoingAway, reason).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.server.RemoveSession(this);
        await this.connection.DisposeAsync().ConfigureAwait(false);
    }
}

internal enum SessionState
{
    Connected,
    HelloReceived,
    PairingPending,
    ChallengePending,
    Authenticated,
    Subscribed,
    Closed,
}
