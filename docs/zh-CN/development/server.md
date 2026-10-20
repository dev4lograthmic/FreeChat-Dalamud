# FreeChat.Server

[English](../../en-US/development/server.md)

FreeChat 游戏侧服务端的传输、配对、会话、过滤、回放和背压实现。这个项目
**不依赖 Dalamud**；插件实现 `IChatSink`，并通过
`FreeChatServer.PublishChatEvent` 注入聊天事件。

## 启动

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

插件 UI 应显示 `server.Pairing.CurrentCode` 和指纹，并实现
`IPairingApproval` 来弹出设备确认框。

## 主要类型

| 类型 | 职责 |
|---|---|
| `WebSocketServer` | TCP/TLS/HTTP Upgrade/WebSocket 帧 |
| `ClientConnection` | 每连接读写队列和消息大小限制 |
| `SessionHandler` | 握手、认证、订阅、回放、发送分发 |
| `PairingService` | 一次性配对码、设备确认、challenge |
| `DeviceAuthenticator` | P-256 challenge 签名验证 |
| `JsonDeviceStore` | 设备公钥；不保存聊天内容 |
| `ChatFilterMatcher` | 分类/LogKind/关键词/正则过滤 |
| `ChatBacklog` | 当前会话 ring buffer 和回放 |
| `TokenBucket` | 发送限流 |

## 测试

```powershell
dotnet test tests\FreeChat.Server.Tests\FreeChat.Server.Tests.csproj -c Release
```

覆盖：配对、challenge 签名、错误 pin、订阅、过滤、回放、发送、JSON debug、
设备撤销。

## 相关

- [协议项目](protocol.md)
- [协议规范](../protocol/freechat-v1.md)
