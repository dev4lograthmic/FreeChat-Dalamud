using FreeChat.Cli;
using Xunit;

namespace FreeChat.Server.Tests;

public sealed class CliClientTests
{
    [Fact]
    public void Cli_ParsesPairingLink()
    {
        var options = CliOptions.Parse(
        [
            "--uri",
            "freechat://pair?v=1&host=192.168.1.10&port=14777"
            + "&sid=server-1&fp=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            + "&code=123456",
        ]);
        var endpoint = CliEndpoint.FromOptions(options);

        Assert.Equal("192.168.1.10", endpoint.Host);
        Assert.Equal(14777, endpoint.Port);
        Assert.Equal("server-1", endpoint.ServerId);
        Assert.Equal("123456", endpoint.Code);
        Assert.Equal(32, endpoint.Fingerprint.Length);
    }

    [Fact]
    public void Cli_RequiresFingerprint()
    {
        var options = CliOptions.Parse(["--host", "127.0.0.1", "--port", "14777"]);
        var error = Assert.Throws<CliException>(() => CliEndpoint.FromOptions(options));
        Assert.Contains("fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cli_RejectsCorruptState()
    {
        var path = Path.Combine(Path.GetTempPath(), "freechat-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json");
        try
        {
            Assert.Throws<CliException>(() => DeviceIdentity.LoadExisting(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Cli_StateSaveIsSafeForConcurrentProcesses()
    {
        var path = Path.Combine(Path.GetTempPath(), "freechat-tests", Guid.NewGuid().ToString("N"), "state.json");
        var identity = DeviceIdentity.LoadOrCreate(path, "test-cli");
        try
        {
            Parallel.For(0, 8, _ => identity.Save(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_RejectsWrongFingerprint()
    {
        await using var context = await TestServerContext.StartAsync();
        var statePath = Path.Combine(context.Root, "cli-wrong-pin", "state.json");
        var identity = DeviceIdentity.LoadOrCreate(statePath, "test-cli");
        var endpoint = new CliEndpoint(
            "127.0.0.1",
            context.Server.Port,
            new byte[32],
            context.Server.Pairing.CurrentCode,
            context.Server.ServerId);

        await using var client = new FreeChatClient(endpoint, identity, statePath, json: false, printJson: false);
        var error = await Assert.ThrowsAsync<CliException>(() =>
            client.AuthenticateAsync(endpoint.Code, CancellationToken.None));
        Assert.Contains("pin", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_JsonTransport_Authenticates()
    {
        await using var context = await TestServerContext.StartAsync();
        var statePath = Path.Combine(context.Root, "cli-json", "state.json");
        var identity = DeviceIdentity.LoadOrCreate(statePath, "test-cli");
        var code = context.Server.Pairing.CurrentCode;
        var endpoint = new CliEndpoint(
            "127.0.0.1",
            context.Server.Port,
            context.Server.Fingerprint,
            code,
            context.Server.ServerId);

        await using var client = new FreeChatClient(endpoint, identity, statePath, json: true, printJson: false);
        var welcome = await client.AuthenticateAsync(code, CancellationToken.None);
        Assert.Equal(context.Server.ServerId, welcome.Server.ServerId);
    }

    [Fact]
    public async Task Cli_PairsThenReconnectsWithChallenge()
    {
        await using var context = await TestServerContext.StartAsync();
        var statePath = Path.Combine(context.Root, "cli", "state.json");
        var identity = DeviceIdentity.LoadOrCreate(statePath, "test-cli");
        var code = context.Server.Pairing.CurrentCode;
        var endpoint = new CliEndpoint(
            "127.0.0.1",
            context.Server.Port,
            context.Server.Fingerprint,
            code,
            context.Server.ServerId);

        string deviceId;
        await using (var client = new FreeChatClient(endpoint, identity, statePath, json: false, printJson: false))
        {
            var welcome = await client.AuthenticateAsync(code, CancellationToken.None);
            Assert.Equal(context.Server.ServerId, welcome.Server.ServerId);
            Assert.False(string.IsNullOrEmpty(welcome.DeviceId));
            deviceId = welcome.DeviceId;
        }

        var reloaded = DeviceIdentity.LoadExisting(statePath);
        Assert.Equal(deviceId, reloaded.DeviceId);
        Assert.Equal(context.Server.ServerId, reloaded.ServerId);

        await using var reconnect = new FreeChatClient(
            endpoint with { Code = null },
            reloaded,
            statePath,
            json: false,
            printJson: false);
        var second = await reconnect.AuthenticateAsync(null, CancellationToken.None);
        Assert.Equal(deviceId, second.DeviceId);

        await reconnect.SubscribeAsync(
            sources: null,
            keyword: null,
            regex: null,
            includeBattle: false,
            excludeOwn: false,
            fromSequence: 0,
            CancellationToken.None);
        context.Server.PublishChatEvent(TestKeys.Chat("cli event"));
        await reconnect.RunAsync(1, CancellationToken.None);

        var send = await reconnect.SendAsync(
            new FreeChat.Protocol.V1.SendRequest { RawText = "/echo cli" },
            CancellationToken.None);
        Assert.Equal(FreeChat.Protocol.V1.SendStatus.Submitted, send.Status);
        Assert.Equal("/echo cli", context.Sink.LastRequest?.RawText);
    }
}
