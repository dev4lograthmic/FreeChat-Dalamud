# FreeChat.Server

The transport, pairing, session, filtering, replay, and backpressure
implementation for the FreeChat game-side server. This project does **not**
depend on Dalamud; the plugin implements `IChatSink` and injects chat events via
`FreeChatServer.PublishChatEvent`.

## Start

```csharp
var options = new ServerOptions
{
    BindAddress = IPAddress.Loopback,
    Port = 14777,
    CertificatePath = "freechat.pfx",
    DeviceStorePath = "freechat-devices.json",
};

var server = new FreeChatServer(options, chatSink, pairingApproval);
await server.StartAsync();

Console.WriteLine($"pairing code: {server.Pairing.CurrentCode}");
Console.WriteLine($"fingerprint: {TlsCertificate.Hex(server.Fingerprint)}");
```

The plugin UI should display `server.Pairing.CurrentCode` and the fingerprint,
and implement `IPairingApproval` to show the device approval prompt.

## Main types

| Type | Responsibility |
|---|---|
| `WebSocketServer` | TCP/TLS/HTTP Upgrade/WebSocket frames |
| `ClientConnection` | Per-connection read/write queues and message size limits |
| `SessionHandler` | Handshake, authentication, subscription, replay, send dispatch |
| `PairingService` | One-time pairing code, device approval, challenge |
| `DeviceAuthenticator` | P-256 challenge signature verification |
| `JsonDeviceStore` | Device public keys; never stores chat content |
| `ChatFilterMatcher` | Category/LogKind/keyword/regex filtering |
| `ChatBacklog` | Current-session ring buffer and replay |
| `TokenBucket` | Send rate limiting |

## Tests

```powershell
dotnet test tests\FreeChat.Server.Tests\FreeChat.Server.Tests.csproj -c Release
```

Coverage: pairing, challenge signatures, wrong pin, subscription, filtering,
replay, sending, JSON debug, and device revocation.
