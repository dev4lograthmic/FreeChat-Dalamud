# 示例与最小客户端流程

## 1. 共享 JSON 示例

以下示例与语言无关，使用 proto3 JSON 编码：

| 文件 | 内容 |
|---|---|
| [client_hello.json](../../shared/protocol/examples/client_hello.json) | 第一条客户端消息 |
| [pairing_required.json](../../shared/protocol/examples/pairing_required.json) | 未知设备 |
| [pair_request.json](../../shared/protocol/examples/pair_request.json) | 配对请求 |
| [challenge.json](../../shared/protocol/examples/challenge.json) | challenge |
| [challenge_response.json](../../shared/protocol/examples/challenge_response.json) | 签名响应 |
| [welcome.json](../../shared/protocol/examples/welcome.json) | 认证成功 |
| [subscribe.json](../../shared/protocol/examples/subscribe.json) | 订阅 |
| [chat_event_say.json](../../shared/protocol/examples/chat_event_say.json) | Say 事件 |
| [chat_event_tell.json](../../shared/protocol/examples/chat_event_tell.json) | Tell 事件 |
| [chat_event_map.json](../../shared/protocol/examples/chat_event_map.json) | 地图链接的 chunks 表示 |
| [send_request_raw.json](../../shared/protocol/examples/send_request_raw.json) | raw 发送 |
| [send_request_chat.json](../../shared/protocol/examples/send_request_chat.json) | 结构化发送 |
| [player_list_request.json](../../shared/protocol/examples/player_list_request.json) | 好友列表请求 |
| [player_list_snapshot.json](../../shared/protocol/examples/player_list_snapshot.json) | 好友列表快照页 |
| [friend_presence_request.json](../../shared/protocol/examples/friend_presence_request.json) | 好友在线状态请求 |
| [friend_presence.json](../../shared/protocol/examples/friend_presence.json) | 好友在线状态结果 |
| [send_result_submitted.json](../../shared/protocol/examples/send_result_submitted.json) | 提交回执 |
| [send_result_echoed.json](../../shared/protocol/examples/send_result_echoed.json) | 回显回执 |
| [error_command_blocked.json](../../shared/protocol/examples/error_command_blocked.json) | 黑名单错误 |
| [session_ended.json](../../shared/protocol/examples/session_ended.json) | 会话结束 |

## 2. 最小客户端流程

```text
1. 解析深链接：
   host / port / sid / fp / code

2. 连接：
   wss://host:port/ws
   Sec-WebSocket-Protocol: freechat.v1.protobuf
   TLS 证书公钥 pin == fp

3. 发送 ClientHello：
   protocolVersion=1
   deviceId, devicePublicKey
   clientName, clientVersion, platform

4. 处理响应：
   PairingRequired -> PairRequest(code, devicePublicKey)
   Challenge       -> 签名 ChallengeResponse
   Welcome         -> 保存 sessionId/deviceId

5. 发送 Subscribe：
   sources=[POST_HANDLE]
   filter={}
   fromSequence=0

6. 读取 ChatEvent。

7. 发送 SendRequest：
   rawText 或 chat
   等待 SendResult(SUBMITTED)
```

## 3. 挑战签名

```text
input = "FreeChat/v1 challenge"
      || 0x00 || server_id
      || 0x00 || device_id
      || 0x00 || nonce

signature = ECDSA_P256_SHA256_P1363(input)
```

- 公钥：X9.63 `0x04 || X || Y`，65 字节。
- 签名：IEEE P1363，64 字节 `r || s`。
- nonce：32 字节，60 秒有效，一次性。

## 4. 参考实现

运行 CLI：

```powershell
dotnet build src\FreeChat.Cli\FreeChat.Cli.csproj -c Release
dotnet run --project src\FreeChat.Cli -- pair --uri "<deep link>"
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<deep link>"
dotnet run --project src\FreeChat.Cli -- send --uri "<deep link>" --raw "/echo test"
```

CLI 文档见 [FreeChat CLI](../development/cli.md)。

## 5. 调试建议

- 先用 `freechat.v1.json` 和 `--print-json` 看清字段，再切回 protobuf。
- 记录 `requestId`，不要只按消息类型匹配。
- 发送回执以 `requestId` 关联；游戏回显以 `originRequestId` 关联。
- 连接失败先检查 TLS pin，而不是重试配对。
- 收到 `SessionEnded` 后不要继续使用旧 `sessionId`/`sequence`。
