# 传输与连接

## 1. 端点

```text
wss://<host>:<port>/ws
```

- 默认端口：`14777`；实际值以插件 UI 和深链接为准。
- 默认绑定：`127.0.0.1`。
- 开启 LAN 后绑定 `0.0.0.0`；客户端使用插件 UI 显示的 LAN 地址。
- v1 只支持 WSS，不支持明文 `ws://`。
- 浏览器客户端不在 v1 范围内。

## 2. TLS 与 pinning

服务端使用 ECDSA P-256 自签证书，TLS 1.2/1.3。

客户端**必须**在 TLS 握手阶段校验服务端公钥 pin：

```text
public_key = 0x04 || X(32) || Y(32)   // X9.63 uncompressed, 65 bytes
fingerprint = SHA-256(public_key)     // 32 bytes
fp = lowercase hex(fingerprint)       // 64 hex chars
```

- 比较的是**原始公钥指纹**，不是证书 SHA-256、不是证书序列号。
- `fp` 来自插件配对深链接或插件 **Pairing** 标签。
- 不匹配时立即断开，**不得发送** `ClientHello` 或配对码。
- 证书轮换但复用同一密钥时 pin 不变；换密钥后客户端必须更新 pin。

## 3. WebSocket 子协议

HTTP Upgrade 必须提供：

```http
Sec-WebSocket-Protocol: freechat.v1.protobuf
```

调试时可以使用：

```http
Sec-WebSocket-Protocol: freechat.v1.json
```

- `freechat.v1.protobuf`：默认，二进制帧，payload 为 `Envelope` 的 protobuf。
- `freechat.v1.json`：文本帧，payload 为 `Envelope` 的 proto3 JSON；只有插件设置
  `Enable JSON debug transport` 开启时服务端才接受。
- 服务端选择第一个支持的子协议，并在 `101 Switching Protocols` 响应中回显。
- 不支持任何子协议时返回 HTTP `400`。

### JSON 编码规则

- 一条 WebSocket 文本消息 = 一个 `Envelope`。
- 字段名使用 lowerCamelCase（`request_id` -> `requestId`）。
- `int64`/`uint64` 以字符串输出，例如 `"42"`。
- `bytes` 以 base64 输出。
- 枚举使用名字，例如 `"CHAT_CHANNEL_SAY"`。
- `google.protobuf.Timestamp` 使用 RFC3339，例如 `"2026-10-20T12:34:56.789Z"`。
- JSON 与 protobuf 承载相同字段；生产环境默认关闭 JSON。

## 4. Envelope 与分帧

所有消息都包在：

```proto
message Envelope {
  string request_id = 1;
  oneof payload { ... }
}
```

规则：

- 每条 WebSocket 消息只放一个 `Envelope`。
- 不要把多个 `Envelope` 拼进一条消息。
- `request_id` 由客户端生成，服务端在响应中原样回显。
- 推送消息（`ChatEvent`、`Pong`、`SessionEnded`、`ServerShutdown` 等）可以没有
  `request_id`。
- 同一连接内的 `request_id` 应唯一；建议 UUID/ULID。
- 未知 oneof 字段和未知枚举值应被忽略；客户端可回退到 `*_UNSPECIFIED`。

## 5. 默认限制

以下为实现默认值，插件设置可调整部分项：

| 项 | 默认值 |
|---|---|
| 单条 WebSocket 消息 | 1 MiB |
| HTTP 请求头 | 8 KiB |
| 最大客户端数 | 32 |
| 发送限流 | 4 条/秒，burst 4 |
| 发送队列 | 32 条/连接 |
| backlog | 5000 条 / 32 MiB |
| WebSocket 握手超时 | 5 秒 |
| `ClientHello` 超时 | 5 秒 |
| 配对确认超时 | 60 秒 |
| challenge 响应超时 | 10 秒 |
| 首个 `Subscribe` 建议超时 | 10 秒 |
| WebSocket keepalive | 20 秒 |

超过单条消息上限时服务端关闭 `1009`。

## 6. 保活

- WebSocket 层由 `KeepAliveInterval` 维护。
- 应用层可以发送：

```proto
message Ping {
  bytes nonce = 1;
  google.protobuf.Timestamp client_time = 2;
}
```

服务端返回：

```proto
message Pong {
  bytes nonce = 1;
  google.protobuf.Timestamp server_time = 2;
}
```

客户端应使用相同 nonce 关联 `Pong`，并可用时间差估算延迟。

## 7. 关闭

- 正常关闭：`1000`。
- 服务端停止/插件卸载：`SessionEnded` 或 `ServerShutdown`，随后关闭。
- 认证失败：`AuthError`，关闭 `4001`。
- 设备被撤销：`4003`。
- 协议/编码不匹配：`4004`。
- 慢消费者：`4005`。
- 消息过大：`1009`。
- 内部错误：`1011`。

完整列表见 [错误与关闭](errors.md)。

## 8. 客户端重连

- 收到 `SessionEnded` 或 `ServerShutdown` 后停止当前订阅并关闭 socket。
- 使用保存的 `device_id`、设备私钥和 `server_id` 重连。
- 如果 `Welcome.session_id` 变化，清空旧 session 的 `last_sequence`。
- 需要历史时发送 `ReplayRequest` 或带 `from_sequence` 的 `Subscribe`。
