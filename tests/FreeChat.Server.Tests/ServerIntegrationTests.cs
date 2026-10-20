using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using FreeChat.Protocol.V1;
using FreeChat.Server;
using Google.Protobuf;
using Xunit;

namespace FreeChat.Server.Tests;

public sealed class ServerIntegrationTests
{
    [Fact]
    public async Task Pair_Subscribe_Receive_Ping()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();
        await using var session = await PairAsync(context, publicKey);
        var client = session.Client;
        await SubscribeAsync(client);

        context.Server.PublishChatEvent(TestKeys.Chat("hello"));
        var chat = await client.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.ChatEvent, chat.PayloadCase);
        Assert.Equal("hello", chat.ChatEvent.Text);

        var nonce = RandomNumberGenerator.GetBytes(32);
        await client.SendAsync(new Envelope
        {
            RequestId = "ping",
            Ping = new Ping { Nonce = ByteString.CopyFrom(nonce) },
        });
        var pong = await client.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.Pong, pong.PayloadCase);
        Assert.Equal(nonce, pong.Pong.Nonce.ToByteArray());

        key.Dispose();
    }

    [Fact]
    public async Task WrongPin_IsRejected()
    {
        await using var context = await TestServerContext.StartAsync();
        var wrong = RandomNumberGenerator.GetBytes(32);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            TestClient.ConnectAsync(context.Server.Port, wrong));
    }

    [Fact]
    public async Task KnownDevice_UsesChallenge()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();

        string deviceId;
        await using (var first = await PairAsync(context, publicKey))
            deviceId = first.Welcome.DeviceId;

        await using var second = await TestClient.ConnectAsync(context.Server.Port, context.Server.Fingerprint);
        await second.SendAsync(TestKeys.ClientHello(publicKey, deviceId));

        var challenge = await second.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.Challenge, challenge.PayloadCase);

        var input = DeviceAuthenticator.BuildChallengeInput(
            context.Server.ServerId,
            deviceId,
            challenge.Challenge.Nonce.ToByteArray());
        var signature = key.SignData(
            input,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        await second.SendAsync(new Envelope
        {
            RequestId = "challenge",
            ChallengeResponse = new ChallengeResponse
            {
                Signature = ByteString.CopyFrom(signature),
            },
        });

        var welcome = await second.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.Welcome, welcome.PayloadCase);
        Assert.Equal(deviceId, welcome.Welcome.DeviceId);
        key.Dispose();
    }

    [Fact]
    public async Task ExpiredChallenge_IsRejected()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();

        string deviceId;
        await using (var first = await PairAsync(context, publicKey))
            deviceId = first.Welcome.DeviceId;

        await using var second = await TestClient.ConnectAsync(context.Server.Port, context.Server.Fingerprint);
        await second.SendAsync(TestKeys.ClientHello(publicKey, deviceId));
        var challenge = await second.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.Challenge, challenge.PayloadCase);

        context.Clock.Advance(TimeSpan.FromSeconds(11));
        var input = DeviceAuthenticator.BuildChallengeInput(
            context.Server.ServerId,
            deviceId,
            challenge.Challenge.Nonce.ToByteArray());
        var signature = key.SignData(
            input,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await second.SendAsync(new Envelope
        {
            RequestId = "challenge",
            ChallengeResponse = new ChallengeResponse { Signature = ByteString.CopyFrom(signature) },
        });

        var response = await second.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.AuthError, response.PayloadCase);
        Assert.Equal(ErrorCode.Unauthenticated, response.AuthError.Code);
        key.Dispose();
    }

    [Fact]
    public async Task SendRequest_ReachesSink()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();
        await using var session = await PairAsync(context, publicKey);
        var client = session.Client;
        await SubscribeAsync(client);

        await client.SendAsync(new Envelope
        {
            RequestId = "send-1",
            SendRequest = new SendRequest { RawText = "/p hello" },
        });

        var result = await client.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.SendResult, result.PayloadCase);
        Assert.Equal(SendStatus.Submitted, result.SendResult.Status);
        Assert.Equal("/p hello", context.Sink.LastRequest?.RawText);
        Assert.Equal("send-1", context.Sink.LastContext?.RequestId);
        key.Dispose();
    }

    [Fact]
    public async Task SendRateLimit_ReturnsRateLimited()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();
        await using var session = await PairAsync(context, publicKey);
        var client = session.Client;

        for (var i = 0; i < 10; i++)
        {
            await client.SendAsync(new Envelope
            {
                RequestId = $"send-{i}",
                SendRequest = new SendRequest { RawText = $"/echo {i}" },
            });
            var result = await client.ReceiveAsync();
            Assert.Equal(SendStatus.Submitted, result.SendResult.Status);
        }

        await client.SendAsync(new Envelope
        {
            RequestId = "send-limited",
            SendRequest = new SendRequest { RawText = "/echo limited" },
        });
        var limited = await client.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.SendResult, limited.PayloadCase);
        Assert.Equal(SendStatus.RateLimited, limited.SendResult.Status);
        Assert.Equal(ErrorCode.RateLimited, limited.SendResult.Error);
        key.Dispose();
    }

    [Fact]
    public async Task OversizedMessage_ClosesWithMessageTooBig()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();
        await using var session = await PairAsync(context, publicKey);
        var client = session.Client;

        await client.SendRawAsync(new byte[(1024 * 1024) + 1]);
        var status = await client.ReceiveCloseAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((WebSocketCloseStatus)1009, status);
        key.Dispose();
    }

    [Fact]
    public async Task Filter_ExcludesSayWhenOnlyTellRequested()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();
        await using var session = await PairAsync(context, publicKey);
        var client = session.Client;
        await SubscribeAsync(client, new ChatFilter { IncludeCategories = { ChatCategory.Tell } });

        context.Server.PublishChatEvent(TestKeys.Chat("say", channel: ChatChannel.Say, logKind: 10));
        context.Server.PublishChatEvent(TestKeys.Chat("tell", channel: ChatChannel.Tell, logKind: 13));

        var chat = await client.ReceiveAsync();
        Assert.Equal("tell", chat.ChatEvent.Text);
        key.Dispose();
    }

    [Fact]
    public async Task Replay_FromSequence_ReplaysBacklog()
    {
        await using var context = await TestServerContext.StartAsync();
        context.Server.PublishChatEvent(TestKeys.Chat("one"));
        context.Server.PublishChatEvent(TestKeys.Chat("two"));

        var (key, publicKey) = TestKeys.Create();
        await using var session = await PairAsync(context, publicKey);
        var client = session.Client;
        await SubscribeAsync(client, fromSequence: 1);

        var replay = await client.ReceiveAsync();
        Assert.Equal("two", replay.ChatEvent.Text);
        Assert.Equal(2, replay.ChatEvent.Sequence);
        key.Dispose();
    }

    [Fact]
    public async Task JsonDebug_Transport_Works()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();
        await using var session = await PairAsync(context, publicKey, json: true);
        var client = session.Client;
        await SubscribeAsync(client);

        context.Server.PublishChatEvent(TestKeys.Chat("json"));
        var chat = await client.ReceiveAsync();
        Assert.Equal("json", chat.ChatEvent.Text);
        key.Dispose();
    }

    [Fact]
    public async Task RevokedDevice_IsAskedToPairAgain()
    {
        await using var context = await TestServerContext.StartAsync();
        var (key, publicKey) = TestKeys.Create();

        string deviceId;
        await using (var first = await PairAsync(context, publicKey))
            deviceId = first.Welcome.DeviceId;

        Assert.True(context.Server.Devices.Revoke(deviceId));

        await using var second = await TestClient.ConnectAsync(context.Server.Port, context.Server.Fingerprint);
        await second.SendAsync(TestKeys.ClientHello(publicKey, deviceId));
        var response = await second.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.PairingRequired, response.PayloadCase);
        key.Dispose();
    }

    [Fact]
    public async Task OneClientDisconnect_DoesNotAffectAnother()
    {
        await using var context = await TestServerContext.StartAsync();
        var (keyA, publicKeyA) = TestKeys.Create();
        var (keyB, publicKeyB) = TestKeys.Create();

        await using var first = await PairAsync(context, publicKeyA);
        context.Server.Pairing.GenerateCode();
        await using var second = await PairAsync(context, publicKeyB);
        await SubscribeAsync(first.Client);
        await SubscribeAsync(second.Client);

        await first.DisposeAsync();
        context.Server.PublishChatEvent(TestKeys.Chat("still here"));

        var chat = await second.Client.ReceiveAsync();
        Assert.Equal("still here", chat.ChatEvent.Text);
        keyA.Dispose();
        keyB.Dispose();
    }

    [Fact]
    public async Task PortConflict_FailsToStart()
    {
        await using var context = await TestServerContext.StartAsync();
        var options = new ServerOptions
        {
            BindAddress = System.Net.IPAddress.Loopback,
            Port = context.Server.Port,
            ServerId = "conflict",
            ServerName = "Conflict",
            CertificatePath = Path.Combine(context.Root, "conflict.pfx"),
            DeviceStorePath = Path.Combine(context.Root, "conflict-devices.json"),
        };
        await using var conflict = new FreeChatServer(options, new RecordingChatSink());
        await Assert.ThrowsAnyAsync<SocketException>(() => conflict.StartAsync());
    }

    private static async Task<PairSession> PairAsync(
        TestServerContext context,
        byte[] publicKey,
        bool json = false)
    {
        var client = await TestClient.ConnectAsync(context.Server.Port, context.Server.Fingerprint, json);
        await client.SendAsync(TestKeys.ClientHello(publicKey));

        var pairing = await client.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.PairingRequired, pairing.PayloadCase);

        await client.SendAsync(TestKeys.PairRequest(context.Server.Pairing.CurrentCode, publicKey));
        var result = await client.ReceiveAsync();
        Assert.True(result.PairResult.Ok);
        var welcome = await client.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.Welcome, welcome.PayloadCase);

        return new PairSession(client, welcome.Welcome.DeviceId, welcome.Welcome);
    }

    private static async Task SubscribeAsync(
        TestClient client,
        ChatFilter? filter = null,
        long fromSequence = 0)
    {
        await client.SendAsync(TestKeys.Subscribe(filter, fromSequence));
        var subscribed = await client.ReceiveAsync();
        Assert.Equal(Envelope.PayloadOneofCase.Subscribed, subscribed.PayloadCase);
    }

    private sealed class PairSession : IAsyncDisposable
    {
        public PairSession(TestClient client, string deviceId, Welcome welcome)
        {
            this.Client = client;
            this.DeviceId = deviceId;
            this.Welcome = welcome;
        }

        public TestClient Client { get; }
        public string DeviceId { get; }
        public Welcome Welcome { get; }

        public ValueTask DisposeAsync() => this.Client.DisposeAsync();
    }
}

internal sealed class TestServerContext : IAsyncDisposable
{
    private TestServerContext(FreeChatServer server, RecordingChatSink sink, FakeClock clock, string root)
    {
        this.Server = server;
        this.Sink = sink;
        this.Clock = clock;
        this.Root = root;
    }

    public FreeChatServer Server { get; }
    public RecordingChatSink Sink { get; }
    public FakeClock Clock { get; }
    public string Root { get; }

    public static async Task<TestServerContext> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "freechat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var clock = new FakeClock();
        var devices = new JsonDeviceStore(Path.Combine(root, "devices.json"));
        var sink = new RecordingChatSink();
        var options = new ServerOptions
        {
            BindAddress = System.Net.IPAddress.Loopback,
            Port = 0,
            ServerId = "test-server",
            ServerName = "Test Server",
            CertificatePath = Path.Combine(root, "server.pfx"),
            DeviceStorePath = Path.Combine(root, "devices.json"),
            EnableJsonDebug = true,
            PairingCodeLifetime = TimeSpan.FromMinutes(5),
            SendRateLimitPerSecond = 10,
            SendRateLimitBurst = 10,
        };

        var server = new FreeChatServer(options, sink, new AutoApprovePairing(), devices, clock);
        server.UpdateCharacter(new CharacterRef
        {
            Name = "Tester",
            WorldId = 86,
            WorldName = "Typhon",
            IsPlayer = true,
            IsLocal = true,
        }, "owner");
        await server.StartAsync();

        return new TestServerContext(server, sink, clock, root);
    }

    public async ValueTask DisposeAsync()
    {
        await this.Server.DisposeAsync();
        try
        {
            Directory.Delete(this.Root, recursive: true);
        }
        catch
        {
            // Best effort; the OS may still hold a certificate key file.
        }
    }
}
