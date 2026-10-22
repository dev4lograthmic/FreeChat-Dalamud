using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;
using FreeChat.Server;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace FreeChat.Server.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan duration) => this.UtcNow += duration;
}

internal sealed class InMemoryDeviceStore : IDeviceStore
{
    private readonly Dictionary<string, DeviceRecord> devices = new(StringComparer.Ordinal);

    public bool TryGet(string deviceId, out DeviceRecord device) =>
        this.devices.TryGetValue(deviceId, out device!);

    public DeviceRecord? FindByPublicKey(ReadOnlySpan<byte> publicKey)
    {
        foreach (var device in this.devices.Values)
        {
            if (!device.Revoked && device.PublicKey.AsSpan().SequenceEqual(publicKey))
                return device;
        }

        return null;
    }

    public IReadOnlyCollection<DeviceRecord> List() => this.devices.Values.ToArray();

    public DeviceRecord AddOrUpdate(DeviceRecord device)
    {
        this.devices[device.DeviceId] = device;
        return device;
    }

    public bool Revoke(string deviceId)
    {
        if (!this.devices.TryGetValue(deviceId, out var device))
            return false;
        device.Revoked = true;
        return true;
    }

    public void Touch(string deviceId, DateTimeOffset at)
    {
        if (this.devices.TryGetValue(deviceId, out var device))
            device.LastSeenAt = at;
    }
}

internal sealed class RecordingChatSink : IChatSink
{
    public SendRequest? LastRequest { get; private set; }
    public SendContext? LastContext { get; private set; }

    public Task<SendResult> SendAsync(SendContext context, SendRequest request, CancellationToken cancellationToken)
    {
        this.LastContext = context;
        this.LastRequest = request;
        return Task.FromResult(new SendResult { Status = SendStatus.Submitted });
    }

    public Task<SendResult> CancelAsync(SendContext context, string targetRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(new SendResult { Status = SendStatus.Cancelled });
}

internal sealed class FakeSocialProvider : ISocialDataProvider
{
    public FakeSocialProvider(
        IReadOnlyList<PlayerInfo>? players = null,
        bool friendListAvailable = true,
        bool friendPresenceAvailable = true)
    {
        this.Players = players ?? Array.Empty<PlayerInfo>();
        this.FriendListAvailable = friendListAvailable;
        this.FriendPresenceAvailable = friendPresenceAvailable;
    }

    public IReadOnlyList<PlayerInfo> Players { get; }
    public bool FriendListAvailable { get; }
    public bool FriendPresenceAvailable { get; }
    public SocialRequestContext? LastContext { get; private set; }

    public Task<FriendListData> GetFriendListAsync(
        SocialRequestContext context,
        CancellationToken cancellationToken)
    {
        this.LastContext = context;
        return Task.FromResult(new FriendListData(PlayerListStatus.Success, this.Players));
    }

    public Task<FriendPresenceData> GetFriendPresenceAsync(
        ulong contentId,
        SocialRequestContext context,
        CancellationToken cancellationToken)
    {
        this.LastContext = context;
        var known = this.Players.Any(x => x.ContentId == contentId);
        return Task.FromResult(known
            ? new FriendPresenceData(PlayerListStatus.Success, PresenceState.Online)
            : new FriendPresenceData(PlayerListStatus.Unavailable, PresenceState.Unknown, "not a friend"));
    }

    public static IReadOnlyList<PlayerInfo> CreatePlayers(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new PlayerInfo
            {
                ContentId = (ulong)i,
                Name = $"Friend {i}",
                HomeWorldId = 86,
                HomeWorldName = "Typhon",
                CurrentWorldId = 86,
                CurrentWorldName = "Typhon",
                JobId = 19,
                JobName = "PLD",
            })
            .ToArray();
}

internal sealed class TestClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket;
    private readonly bool json;

    private TestClient(ClientWebSocket socket, bool json)
    {
        this.socket = socket;
        this.json = json;
    }

    public static async Task<TestClient> ConnectAsync(int port, byte[] expectedFingerprint, bool json = false)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null)
                return false;

            using var cert = certificate as X509Certificate2
                             ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return CryptographicOperations.FixedTimeEquals(
                TlsCertificate.Fingerprint(cert),
                expectedFingerprint);
        };
        socket.Options.AddSubProtocol(
            json ? ProtocolConstants.JsonSubProtocol : ProtocolConstants.ProtobufSubProtocol);

        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/ws"), CancellationToken.None);
        return new TestClient(socket, json);
    }

    public Task SendAsync(Envelope envelope)
    {
        var bytes = EnvelopeCodec.Encode(envelope, this.json);
        return this.socket.SendAsync(
            new ArraySegment<byte>(bytes),
            this.json ? System.Net.WebSockets.WebSocketMessageType.Text : System.Net.WebSockets.WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None);
    }

    public Task SendRawAsync(byte[] bytes) =>
        this.socket.SendAsync(
            new ArraySegment<byte>(bytes),
            System.Net.WebSockets.WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None);

    public async Task<System.Net.WebSockets.WebSocketCloseStatus?> ReceiveCloseAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        while (true)
        {
            var result = await this.socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                return result.CloseStatus;
        }
    }

    public async Task<Envelope> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        System.Net.WebSockets.WebSocketReceiveResult result;
        do
        {
            result = await this.socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return EnvelopeCodec.Decode(message.GetBuffer().AsSpan(0, (int)message.Length), this.json);
    }

    public async Task<Envelope?> TryReceiveAsync(TimeSpan timeout)
    {
        try
        {
            return await this.ReceiveAsync(timeout);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task CloseAsync()
    {
        if (this.socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await this.socket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                "test done",
                cancellation.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.CloseAsync();
        }
        catch
        {
        }

        this.socket.Dispose();
    }
}

internal static class TestKeys
{
    public const ulong LocalContentId = 12345678901234567;

    public static (ECDsa Key, byte[] PublicKey) Create()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (key, publicKey);
    }

    public static Envelope ClientHello(byte[] publicKey, string deviceId = "test-device") => new()
    {
        RequestId = "hello",
        ClientHello = new ClientHello
        {
            ProtocolVersion = ProtocolConstants.Version,
            ClientName = "test",
            ClientVersion = "0.1.0",
            Platform = "test",
            DeviceId = deviceId,
            DevicePublicKey = ByteString.CopyFrom(publicKey),
        },
    };

    public static Envelope PairRequest(string code, byte[] publicKey) => new()
    {
        RequestId = "pair",
        PairRequest = new PairRequest
        {
            Code = code,
            DeviceName = "test device",
            Platform = "test",
            DevicePublicKey = ByteString.CopyFrom(publicKey),
        },
    };

    public static Envelope Subscribe(ChatFilter? filter = null, long fromSequence = 0) => new()
    {
        RequestId = "sub",
        Subscribe = new Subscribe
        {
            Filter = filter ?? new ChatFilter(),
            Sources = { ChatSource.PostHandle },
            ContentMode = ContentMode.Current,
            FromSequence = fromSequence,
        },
    };

    public static ChatEvent Chat(
        string text,
        ChatSource source = ChatSource.PostHandle,
        ChatChannel channel = ChatChannel.Say,
        uint logKind = 10) => new()
    {
        Channel = channel,
        LogKind = logKind,
        Source = source,
        Text = text,
        Sender = new CharacterRef { Name = "Tester", WorldName = "Typhon", IsPlayer = true },
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };
}
