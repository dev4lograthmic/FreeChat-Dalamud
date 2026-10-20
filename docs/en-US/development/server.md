# FreeChat.Server

[简体中文](../../zh-CN/development/server.md)

Transport, pairing, session, filtering, replay, and backpressure for the
FreeChat game-side server. This project has **no Dalamud dependency**; the plugin
implements `IChatSink` and injects chat events through
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
and implement `IPairingApproval` to show a confirmation dialog.

## Main pieces

| Type | Responsibility |
|---|---|
| `WebSocketServer` | TCP/TLS/HTTP upgrade/WebSocket framing |
| `ClientConnection` | Per-connection read/write queues and message limits |
| `SessionHandler` | Handshake, auth, subscribe, replay, send dispatch |
| `PairingService` | One-time code, device approval, challenge creation |
| `DeviceAuthenticator` | P-256 challenge signature verification |
| `JsonDeviceStore` | Device public keys; no chat content |
| `ChatFilterMatcher` | Category/LogKind/keyword/regex filters |
| `ChatBacklog` | Current-session ring buffer and replay |
| `TokenBucket` | Send rate limiting |

## Tests

```powershell
dotnet test tests\FreeChat.Server.Tests\FreeChat.Server.Tests.csproj -c Release
```

The test suite covers pairing, challenge signatures, wrong pin rejection,
subscriptions, filtering, replay, sending, JSON debug transport, and device
revocation.

## Related

- [Protocol project](protocol.md)
- [Protocol specification](../protocol/freechat-v1.md)
