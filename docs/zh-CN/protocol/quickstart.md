# 协议快速开始

[English](../../en-US/protocol/quickstart.md)

从配对链接到实时聊天订阅的最短路径。CLI 是可执行的参考实现；完整语义见
[协议规范](freechat-v1.md)。

## 0. 准备

- FreeChat 插件正在运行，`wss://host:port/ws` 可访问。
- 插件 **FreeChat → 配对** 里的深链接：
  `freechat://pair?v=1&host=...&port=...&sid=...&fp=...&code=...`。
- 一个 P-256 设备密钥。正式客户端每次安装生成一次，并安全保存私钥。
- 从 `proto/freechat/v1/` 生成的 protobuf/JSON 编解码器。C# 可直接引用
  `FreeChat.Protocol` 并调用 `EnvelopeCodec`。

先试参考客户端：

```powershell
dotnet run --project src\FreeChat.Cli -- pair --uri "<深链接>"
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<深链接>"
```

## 1. 连接与 pinning

使用 WSS，不要用明文 WS。只提供一个子协议：

```text
Sec-WebSocket-Protocol: freechat.v1.protobuf
```

`freechat.v1.json` 仅用于调试，且必须在插件里开启。校验服务端证书时使用
公钥 pinning：

```text
SHA-256( 0x04 || X || Y ) == 深链接里的 fp
```

不要依赖自签证书链或主机名。比较的是原始 65 字节 P-256 公钥，不是证书哈希。

## 2. 发送 ClientHello

```csharp
var hello = new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    ClientHello = new ClientHello
    {
        ProtocolVersion = 1,
        ClientName = "my-client",
        ClientVersion = "0.1.0",
        Platform = "windows", // windows / ios / android / macos / cli / other
        DeviceId = deviceId,
        DevicePublicKey = ByteString.CopyFrom(publicKey), // 0x04 || X || Y
        PairingCode = pairingCode, // 仅首次配对
    },
};
await SendAsync(EnvelopeCodec.Encode(hello, json: false));
```

服务端会返回：

- `PairingRequired` — 未知设备；用深链接里的配对码发送 `PairRequest`。
  服务端不会把配对码发回来。
- `Challenge` — 已知设备；对 challenge 签名。
- `AuthError` — 失败并关闭。

## 3. 配对或回答 challenge

配对：

```csharp
var pair = new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    PairRequest = new PairRequest
    {
        Code = pairingCode,
        DeviceName = "My Phone",
        Platform = "ios",
        DevicePublicKey = ByteString.CopyFrom(publicKey),
    },
};
```

Challenge：

```text
input = "FreeChat/v1 challenge" || 0x00 || server_id || 0x00 || device_id || 0x00 || nonce
signature = ECDSA_P256_SHA256_P1363(input)
```

发送 `ChallengeResponse.signature`（64 字节，`r || s`）。成功后服务端返回
`Welcome`，包含 `session_id`、sequence 范围和服务端信息。重连时要保存
`device_id`、私钥和 `server_id`。

## 4. 订阅

```csharp
var subscribe = new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    Subscribe = new Subscribe
    {
        Filter = new ChatFilter(),           // 无限制
        Sources = { ChatSource.PostHandle }, // 默认
        ContentMode = ContentMode.Current,
        FromSequence = 0,                    // 只看实时
    },
};
```

服务端返回 `Subscribed`，然后推送 `ChatEvent`。`from_sequence > 0` 会回放该
sequence 之后的历史；如果太旧，会先收到 `HistoryTruncated`。

常用过滤：`include_categories`、`exclude_categories`、`keyword`、`regex`、
`include_own`（可选；未设置表示包含自己）、`include_battle`。

## 5. 接收

每个 `ChatEvent` 包含：

- `sequence` 和 `message_id`（`server_id:session_id:sequence`）
- `channel`、`log_kind`、`source`
- `sender`、`text`、`raw_sender`、`raw_message`
- `segments`（文本、图标、物品、地图、自动翻译、玩家、状态）
- `outgoing`、`tell_peer`、`channel_name`
- 如果是某个 `SendRequest` 的游戏回显，带 `origin_request_id`

优先渲染 `segments`；不支持时回退到 `text` 或 `raw_message`。

## 6. 发送

原始文本或命令：

```csharp
new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    SendRequest = new SendRequest { RawText = "/echo hello" },
};
```

结构化频道：

```csharp
new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    SendRequest = new SendRequest
    {
        Chat = new ChatSend
        {
            Channel = ChatChannel.Party,
            Text = "hello",
        },
    },
};
```

用 request id 关联 `SendResult`。`SUBMITTED` 只表示游戏接受了输入，不代表
对方收到。服务端目前会在 echo `ChatEvent` 上设置 `origin_request_id`，但
暂不单独发送 `ECHOED` 的 `SendResult`。

## 7. Keepalive 与关闭

- 收到 `Ping` 时用相同 nonce 回 `Pong`。
- 也可以主动发 `Ping` 测延迟。
- 收到 `SessionEnded` 或 `ServerShutdown` 时停止并稍后重连。
- 正常关闭使用 close code `1000`。

## 最小 C# 骨架

```csharp
using System.Net.WebSockets;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;

var ws = new ClientWebSocket();
ws.Options.AddSubProtocol(ProtocolConstants.ProtobufSubProtocol);
ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
    PinMatches(cert, expectedFingerprint); // 用 ECDsa + SHA256 实现

await ws.ConnectAsync(new Uri($"wss://{host}:{port}/ws"), ct);

// 发送：EnvelopeCodec.Encode(envelope, json: false) 作为 binary frame
// 接收：读取 binary frame 后调用 EnvelopeCodec.Decode(bytes, json: false)
```

## 接下来

- [协议规范](freechat-v1.md) — 字段、过滤、错误码、关闭码。
- [CLI](../development/cli.md) — 参考命令和示例。
- [一致性测试](conformance.md) — 用例和当前状态。
- [示例](../../shared/protocol/examples/) — proto3 JSON envelope。
