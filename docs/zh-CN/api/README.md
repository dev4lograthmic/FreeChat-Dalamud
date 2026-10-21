# FreeChat 插件 API 参考

客户端通过 `freechat.v1` 协议：

- 接收结构化聊天事件
- 订阅/过滤聊天来源
- 回放当前游戏会话的历史
- 通过游戏聊天框发送文本或命令
- 配对设备、验证设备密钥

协议源文件在
[`proto/freechat/v1/`](../../../proto/freechat/v1/)；
共享 JSON 示例在
[`docs/shared/protocol/examples/`](../../shared/protocol/examples/)。

## 快速开始

1. 在插件 **配对** 标签复制深链接：

   ```text
   freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
   ```

2. 用深链接里的 `host`、`port`、`fp` 连接：

   ```text
   wss://<host>:<port>/ws
   Sec-WebSocket-Protocol: freechat.v1.protobuf
   ```

3. 在 TLS 握手阶段校验：

   ```text
   SHA-256(0x04 || X || Y) == fp
   ```

4. 发送 `ClientHello`，按服务端响应完成配对或 challenge。
5. 收到 `Welcome` 后发送 `Subscribe`。
6. 接收 `ChatEvent`；需要发送时使用 `SendRequest`。

完整的可运行参考实现见
[`FreeChat.Cli`](../development/cli.md)。

## API 文档目录

| 文档 | 内容 |
|---|---|
| [传输与连接](transport.md) | WSS、子协议、TLS pinning、Envelope、分帧、限制、超时 |
| [认证与配对](auth.md) | 深链接、ClientHello、PairRequest、Challenge、Welcome |
| [订阅与回放](subscription.md) | Subscribe、过滤器、回放、HistoryTruncated、会话重置 |
| [聊天事件](chat-events.md) | ChatEvent 字段、频道、来源、Chunks/Segments、回显关联 |
| [发送消息](send.md) | raw_text、结构化频道、队列、限流、SendResult、取消 |
| [错误与关闭](errors.md) | ErrorCode、SendStatus、CloseCode、慢消费者 |
| [示例](examples.md) | proto3 JSON 示例、CLI 示例、最小客户端流程 |

## 消息清单

| 方向 | 消息 | 用途 |
|---|---|---|
| C→S | `ClientHello` | 协议版本、客户端信息、设备公钥 |
| S→C | `PairingRequired` | 未知设备，等待配对 |
| C→S | `PairRequest` | 配对码 + 设备信息 |
| S→C | `PairResult` | 配对结果 |
| S→C | `Challenge` | 已知设备的 nonce |
| C→S | `ChallengeResponse` | 设备签名 |
| S→C | `Welcome` | 认证成功、session 信息 |
| S→C | `AuthError` | 认证失败 |
| C→S | `Subscribe` | 订阅/过滤/回放 |
| S→C | `Subscribed` | 订阅生效 |
| C→S | `Unsubscribe` | 取消订阅 |
| C→S | `ReplayRequest` | 显式回放 |
| S→C | `HistoryTruncated` | 历史超出 backlog |
| S→C | `ChatEvent` | 聊天事件 |
| C→S | `SendRequest` | raw/结构化发送 |
| S→C | `SendResult` | 发送状态 |
| C→S | `SendCancel` | 取消请求（当前实现有限） |
| C→S/S→C | `Ping` / `Pong` | 延迟测量 |
| S→C | `Error` | 请求/连接错误 |
| S→C | `RateLimitInfo` | 限流信息 |
| S→C | `SessionEnded` | 登出/会话结束 |
| S→C | `ServerShutdown` | 服务端关闭 |

## 能力

`ServerInfo.capabilities.features` / `Welcome.enabled_features` 使用点分字符串：

```text
chat.segments
chat.chunks
chat.chunks.party_finder
chat.chunks.quest
chat.raw
chat.original
chat.sources.pre_handle
chat.sources.post_handle
chat.sources.structured
filters.regex
replay.sequence
send.raw
send.structured
send.cancel
send.echo
pairing.qr
pairing.device_keys
tls.pinning.sha256
```

客户端应只使用双方都声明支持的能力；未知能力忽略。

## 当前实现状态

- 协议版本：`freechat.v1`。
- 默认子协议：`freechat.v1.protobuf`。
- `freechat.v1.json` 仅在插件设置开启 JSON debug 时可用。
- 当前服务端在发送回显时会设置 `ChatEvent.origin_request_id`，但**暂不发送单独的 `SendResult(ECHOED)`**。
- `SendResult(SUBMITTED)` 表示游戏聊天框接受了输入，不代表对方收到。
- 服务端只保存当前游戏会话的 backlog，不持久化聊天记录。
- 默认只监听 loopback；LAN 需要在插件设置里显式开启。

## 安全提示

- 必须 pin 服务端 P-256 公钥指纹，不能 trust-all。
- 不要记录或转发配对码、设备私钥。
- 客户端只能发送用户明确要求的内容；不要自动向公开频道或通讯贝广播。
- 服务端默认拦截账号/会话类命令：`/logout`、`/quit`、`/shutdown`、`/exit`、`/delete`、`/resign`。
