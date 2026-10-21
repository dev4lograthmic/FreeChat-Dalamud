using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace FreeChat.Server;

public sealed class FreeChatServer : IAsyncDisposable
{
    private readonly ServerOptions options;
    private readonly IClock clock;
    private readonly IDeviceStore devices;
    private readonly CertificateManager certificates;
    private readonly WebSocketServer transport;
    private readonly ConcurrentDictionary<string, SessionHandler> sessions = new(StringComparer.Ordinal);
    private readonly object sequenceGate = new();
    private readonly object echoGate = new();
    private readonly Dictionary<string, DateTimeOffset> submittedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> pendingEchoTimestamps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Envelope>> pendingEchoResults = new(StringComparer.Ordinal);

    private long sequence;
    private string sessionId = Guid.NewGuid().ToString("N");
    private string? ownerKey;
    private CharacterRef? character;
    private int stopped;

    public FreeChatServer(
        ServerOptions options,
        IChatSink chatSink,
        IPairingApproval? pairingApproval = null,
        IDeviceStore? deviceStore = null,
        IClock? clock = null,
        ISocialDataProvider? social = null)
    {
        this.options = options;
        this.ChatSink = chatSink;
        this.Social = social;
        this.clock = clock ?? new SystemClock();
        this.devices = deviceStore ?? new JsonDeviceStore(
            options.DeviceStorePath
            ?? Path.Combine(Path.GetTempPath(), "freechat-devices.json"));
        this.certificates = CertificateManager.LoadOrCreate(
            options.CertificatePath,
            options.CertificatePassword);
        this.Pairing = new PairingService(
            this.devices,
            pairingApproval ?? new AutoApprovePairing(),
            this.clock,
            options.PairingCodeLifetime);
        this.Backlog = new ChatBacklog(options.BacklogMessages, options.BacklogBytes);
        this.SendLimiter = new TokenBucket(
            options.SendRateLimitBurst,
            options.SendRateLimitPerSecond,
            this.clock);
        this.transport = new WebSocketServer(
            options,
            this.certificates.Certificate,
            this.OnConnectionAsync);

        var features = new List<string>(ProtocolConstants.DefaultFeatures);
        if (social?.FriendListAvailable == true)
            features.Add(ProtocolConstants.FriendListFeature);
        if (social?.FriendPresenceAvailable == true)
            features.Add(ProtocolConstants.FriendPresenceFeature);
        this.EnabledFeatures = features;
    }

    public string ServerId => this.options.ServerId;
    public string ServerName => this.options.ServerName;
    public string SessionId => this.sessionId;
    public string? OwnerKey => this.ownerKey;
    public CharacterRef? Character => this.character;
    public IClock Clock => this.clock;
    public ServerOptions Options => this.options;
    public IDeviceStore Devices => this.devices;
    public PairingService Pairing { get; }
    public ChatBacklog Backlog { get; }
    public TokenBucket SendLimiter { get; }
    public IChatSink ChatSink { get; }
    public ISocialDataProvider? Social { get; }
    public IReadOnlyList<string> EnabledFeatures { get; }
    public byte[] Fingerprint => this.certificates.Fingerprint;
    public int Port => this.transport.Port;
    public IReadOnlyList<ClientSessionInfo> Sessions =>
        this.sessions.Values
            .Select(x => x.GetInfo())
            .OrderBy(x => x.ConnectedAt)
            .ToArray();

    public bool DisconnectSession(string sessionId)
    {
        if (!this.sessions.TryGetValue(sessionId, out var session))
            return false;

        _ = session.EndAsync("disconnected_by_user");
        return true;
    }

    /// <summary>
    /// Queues or sends a best-effort ECHOED notification. SUBMITTED is already
    /// terminal; this method only decides whether the optional notification can
    /// be delivered to the originating session.
    /// </summary>
    public bool SendEchoResult(
        string sessionId,
        string requestId,
        int partIndex,
        int partCount,
        string originMessageId)
    {
        var key = EchoKey(sessionId, requestId);
        var envelope = new Envelope
        {
            RequestId = requestId,
            SendResult = new SendResult
            {
                Status = SendStatus.Echoed,
                PartIndex = partIndex,
                PartCount = partCount,
                OriginMessageId = originMessageId,
            },
        };

        var now = this.clock.UtcNow;
        lock (this.echoGate)
        {
            this.CleanupEchoState(now);
            if (!this.submittedAt.ContainsKey(key))
            {
                if (!this.pendingEchoResults.TryGetValue(key, out var queued))
                {
                    queued = new List<Envelope>();
                    this.pendingEchoResults[key] = queued;
                    this.pendingEchoTimestamps[key] = now;
                }

                queued.Add(envelope);
                return true;
            }
        }

        return this.sessions.TryGetValue(sessionId, out var session) && session.TrySend(envelope);
    }

    /// <summary>Called after the terminal SUBMITTED result has been queued.</summary>
    public void MarkSendSubmitted(string sessionId, string requestId)
    {
        var key = EchoKey(sessionId, requestId);
        List<Envelope>? pending = null;
        lock (this.echoGate)
        {
            this.submittedAt[key] = this.clock.UtcNow;
            if (this.pendingEchoResults.Remove(key, out var queued))
            {
                pending = queued;
                this.pendingEchoTimestamps.Remove(key);
            }

            this.CleanupEchoState(this.clock.UtcNow);
        }

        if (pending is null || !this.sessions.TryGetValue(sessionId, out var session))
            return;

        foreach (var envelope in pending)
            session.TrySend(envelope);
    }

    private static string EchoKey(string sessionId, string requestId) =>
        sessionId + "\0" + requestId;

    private void CleanupEchoState(DateTimeOffset now)
    {
        foreach (var entry in this.submittedAt.Where(x => now - x.Value > TimeSpan.FromMinutes(2)).ToArray())
            this.submittedAt.Remove(entry.Key);

        foreach (var entry in this.pendingEchoTimestamps.Where(x => now - x.Value > TimeSpan.FromSeconds(30)).ToArray())
        {
            this.pendingEchoTimestamps.Remove(entry.Key);
            this.pendingEchoResults.Remove(entry.Key);
        }
    }

    public Task StartAsync()
    {
        this.transport.Start();
        return Task.CompletedTask;
    }

    public void PublishChatEvent(ChatEvent chatEvent)
    {
        lock (this.sequenceGate)
        {
            chatEvent.Sequence = ++this.sequence;
            chatEvent.MessageId = $"{this.ServerId}:{this.sessionId}:{chatEvent.Sequence}";
            if (chatEvent.Timestamp is null)
                chatEvent.Timestamp = Timestamp.FromDateTimeOffset(this.clock.UtcNow);
        }

        this.Backlog.Append(chatEvent);

        foreach (var session in this.sessions.Values)
            session.TrySendChat(chatEvent);
    }

    public void UpdateCharacter(CharacterRef? value, string? owner)
    {
        this.character = value?.Clone();
        this.ownerKey = owner;
    }

    public async Task ResetSessionAsync(string reason = "character_changed")
    {
        lock (this.sequenceGate)
        {
            this.sessionId = Guid.NewGuid().ToString("N");
            this.sequence = 0;
            this.Backlog.Clear();
        }

        var current = this.sessions.Values.ToArray();
        foreach (var session in current)
        {
            try
            {
                await session.EndAsync(reason).ConfigureAwait(false);
            }
            catch
            {
                // Best effort during session reset.
            }
        }
    }

    public ServerInfo CreateServerInfo() => new()
    {
        ServerId = this.ServerId,
        ServerName = this.ServerName,
        Character = this.Character?.Clone() ?? new CharacterRef(),
        ProtocolVersion = ProtocolConstants.Version,
        ServerVersion = "0.1.0",
        PublicKeyFingerprint = ByteString.CopyFrom(this.Fingerprint),
        Capabilities = this.CreateCapabilities(),
        ServerTime = Timestamp.FromDateTimeOffset(this.clock.UtcNow),
    };

    internal void RegisterSession(SessionHandler session) =>
        this.sessions.TryAdd(session.SessionId, session);

    internal void RemoveSession(SessionHandler session) =>
        this.sessions.TryRemove(session.SessionId, out _);

    private ServerCapabilities CreateCapabilities()
    {
        var capabilities = new ServerCapabilities
        {
            MaxMessageBytes = (uint)this.options.MaxMessageBytes,
            MaxClients = (uint)this.options.MaxClients,
            MaxSendQueue = 128,
            MaxSendBytes = 64 * 1024,
            BacklogMessages = (uint)this.options.BacklogMessages,
            BacklogBytes = (uint)Math.Min(this.options.BacklogBytes, uint.MaxValue),
            RateLimitWindowMs = 1000,
            RateLimitMax = (uint)this.options.SendRateLimitPerSecond,
        };

        capabilities.Features.AddRange(this.EnabledFeatures);
        capabilities.SupportedSources.AddRange(new[]
        {
            ChatSource.PreHandle,
            ChatSource.PostHandle,
            ChatSource.Structured,
        });
        foreach (var channel in System.Enum.GetValues<ChatChannel>())
        {
            if (channel != ChatChannel.Unspecified)
                capabilities.SupportedChannels.Add(channel);
        }

        return capabilities;
    }

    private async Task OnConnectionAsync(
        WebSocket socket,
        bool isJson,
        IPAddress? remoteAddress,
        CancellationToken cancellationToken)
    {
        SessionHandler? session = null;
        var connection = new ClientConnection(
            socket,
            isJson,
            remoteAddress,
            this.options.MaxMessageBytes,
            (envelope, token) => session!.HandleAsync(envelope, token));

        session = new SessionHandler(this, connection);
        this.sessions[session.SessionId] = session;

        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            this.sessions.TryRemove(session.SessionId, out _);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref this.stopped, 1) != 0)
            return;

        var current = this.sessions.Values.ToArray();
        foreach (var session in current)
        {
            try
            {
                await session.EndAsync("server_shutdown").ConfigureAwait(false);
            }
            catch
            {
                // Best effort.
            }
        }

        await this.transport.DisposeAsync().ConfigureAwait(false);
        this.certificates.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
    }
}
