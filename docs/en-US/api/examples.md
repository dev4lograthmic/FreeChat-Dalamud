# Examples and minimal client flow

## 1. Shared JSON examples

These examples are language-neutral and use proto3 JSON:

| File | Contents |
|---|---|
| [client_hello.json](../../shared/protocol/examples/client_hello.json) | First client message |
| [pairing_required.json](../../shared/protocol/examples/pairing_required.json) | Unknown device |
| [pair_request.json](../../shared/protocol/examples/pair_request.json) | Pairing request |
| [challenge.json](../../shared/protocol/examples/challenge.json) | Challenge |
| [challenge_response.json](../../shared/protocol/examples/challenge_response.json) | Signed response |
| [welcome.json](../../shared/protocol/examples/welcome.json) | Authentication success |
| [subscribe.json](../../shared/protocol/examples/subscribe.json) | Subscription |
| [chat_event_say.json](../../shared/protocol/examples/chat_event_say.json) | Say event |
| [chat_event_tell.json](../../shared/protocol/examples/chat_event_tell.json) | Tell event |
| [chat_event_map.json](../../shared/protocol/examples/chat_event_map.json) | Map link rendered as chunks |
| [send_request_raw.json](../../shared/protocol/examples/send_request_raw.json) | Raw send |
| [send_request_chat.json](../../shared/protocol/examples/send_request_chat.json) | Structured send |
| [send_result_submitted.json](../../shared/protocol/examples/send_result_submitted.json) | Submitted result |
| [send_result_echoed.json](../../shared/protocol/examples/send_result_echoed.json) | Echoed result |
| [error_command_blocked.json](../../shared/protocol/examples/error_command_blocked.json) | Blocked command error |
| [session_ended.json](../../shared/protocol/examples/session_ended.json) | Session end |

## 2. Minimal client flow

```text
1. Parse the deep link:
   host / port / sid / fp / code

2. Connect:
   wss://host:port/ws
   Sec-WebSocket-Protocol: freechat.v1.protobuf
   TLS public-key pin == fp

3. Send ClientHello:
   protocolVersion=1
   deviceId, devicePublicKey
   clientName, clientVersion, platform

4. Handle responses:
   PairingRequired -> PairRequest(code, devicePublicKey)
   Challenge       -> sign ChallengeResponse
   Welcome         -> save sessionId/deviceId

5. Send Subscribe:
   sources=[POST_HANDLE]
   filter={}
   fromSequence=0

6. Read ChatEvent.

7. Send SendRequest:
   rawText or chat
   wait for SendResult(SUBMITTED)
```

## 3. Challenge signature

```text
input = "FreeChat/v1 challenge"
      || 0x00 || server_id
      || 0x00 || device_id
      || 0x00 || nonce

signature = ECDSA_P256_SHA256_P1363(input)
```

- Public key: X9.63 `0x04 || X || Y`, 65 bytes.
- Signature: IEEE P1363, 64 bytes `r || s`.
- Nonce: 32 bytes, valid for 60 seconds, single-use.

## 4. Reference implementation

Build and run the CLI:

```powershell
dotnet build src\FreeChat.Cli\FreeChat.Cli.csproj -c Release
dotnet run --project src\FreeChat.Cli -- pair --uri "<deep link>"
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<deep link>"
dotnet run --project src\FreeChat.Cli -- send --uri "<deep link>" --raw "/echo test"
```

The CLI source is in [`src/FreeChat.Cli`](../../../src/FreeChat.Cli).

## 5. Debugging tips

- Use `freechat.v1.json` and `--print-json` to inspect fields, then switch back
  to protobuf.
- Track `requestId`; do not match only by message type.
- Correlate send acknowledgements by `requestId` and game echoes by
  `originRequestId`.
- On connection failure, check the TLS pin before retrying pairing.
- After `SessionEnded`, do not reuse the old `sessionId`/`sequence`.
