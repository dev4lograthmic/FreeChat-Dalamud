# 协议示例

[English](../../en-US/protocol/examples.md)

这些是 debug 编码 `freechat.v1.json` 使用的 **proto3 JSON** 示例，字段编号/语义
与二进制 protobuf 完全一致。

JSON 文件位于 `docs/shared/protocol/examples/`，中英文文档共用同一份。

说明：

- `int64`/`uint64` 按 proto3 JSON 映射输出为字符串。
- `bytes` 输出为 base64。
- 枚举输出为名字。
- `google.protobuf.Timestamp` 输出 RFC3339。
- 示例里的 key/签名是合法 base64 占位符，**不是**真实密钥材料。
- 二进制向量在 Phase 1 从同一份 `.proto` 生成。

文件：

| 文件 | Envelope payload |
|---|---|
| `client_hello.json` | `ClientHello` |
| `pairing_required.json` | `PairingRequired` |
| `pair_request.json` | `PairRequest` |
| `challenge.json` | `Challenge` |
| `challenge_response.json` | `ChallengeResponse` |
| `welcome.json` | `Welcome` |
| `subscribe.json` | `Subscribe` |
| `chat_event_say.json` | `ChatEvent` (Say) |
| `chat_event_tell.json` | `ChatEvent` (Tell) |
| `send_request_raw.json` | `SendRequest` (raw) |
| `send_request_chat.json` | `SendRequest` (structured) |
| `send_result_submitted.json` | `SendResult` |
| `send_result_echoed.json` | `SendResult` |
| `error_command_blocked.json` | `Error` |
| `session_ended.json` | `SessionEnded` |
