# Protocol examples

[简体中文](../../zh-CN/protocol/examples.md)

These are **proto3 JSON** examples for the debug `freechat.v1.json` encoding.
They use the same field numbers/semantics as the binary protobuf encoding.

The JSON files live in `docs/shared/protocol/examples/` and are shared by both
language versions.

Notes:

- `int64`/`uint64` values are JSON strings, per proto3 JSON mapping.
- `bytes` values are base64.
- Enums are names.
- `google.protobuf.Timestamp` is RFC3339.
- Placeholder keys/signatures are syntactically valid base64 but are **not**
  real cryptographic material.
- For binary vectors, generate them from the same `.proto` in Phase 1.

Files:

| File | Envelope payload |
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
