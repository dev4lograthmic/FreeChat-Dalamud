# FreeChat Plugin API Reference

This is the **WebSocket API exposed by the FreeChat Dalamud plugin**, not a
Dalamud C# plugin API.

The plugin runs a WSS server inside the game process. Clients use the
`freechat.v1` protocol to:

- receive structured chat events
- subscribe to and filter chat sources
- replay history from the current game session
- send text or commands through the game chat box
- pair devices and authenticate device keys

The protocol source is
[`proto/freechat/v1/`](../../../proto/freechat/v1/); shared JSON examples are in
[`docs/shared/protocol/examples/`](../../shared/protocol/examples/).

## Quick start

1. Copy the deep link from the plugin's **Pairing** tab:

   ```text
   freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
   ```

2. Connect using `host`, `port`, and `fp` from the link:

   ```text
   wss://<host>:<port>/ws
   Sec-WebSocket-Protocol: freechat.v1.protobuf
   ```

3. Validate the server during TLS setup:

   ```text
   SHA-256(0x04 || X || Y) == fp
   ```

4. Send `ClientHello`; complete pairing or the challenge.
5. After `Welcome`, send `Subscribe`.
6. Receive `ChatEvent`; use `SendRequest` to send.

A runnable reference implementation is in
[`src/FreeChat.Cli`](../../../src/FreeChat.Cli).

## API documentation

| Document | Contents |
|---|---|
| [Transport](transport.md) | WSS, subprotocols, TLS pinning, envelopes, framing, limits, timeouts |
| [Authentication](auth.md) | Deep links, ClientHello, pairing, challenge, Welcome |
| [Subscriptions](subscription.md) | Subscribe, filters, replay, HistoryTruncated, session handling |
| [Chat events](chat-events.md) | ChatEvent fields, channels, sources, segments, echo correlation |
| [Sending](send.md) | raw_text, structured channels, queue, rate limits, SendResult, cancel |
| [Errors](errors.md) | ErrorCode, SendStatus, CloseCode, slow consumers |
| [Examples](examples.md) | proto3 JSON examples, CLI examples, minimal client flow |

## Message inventory

| Direction | Message | Purpose |
|---|---|---|
| C→S | `ClientHello` | Protocol version, client info, device public key |
| S→C | `PairingRequired` | Unknown device; waiting for pairing |
| C→S | `PairRequest` | Pairing code + device info |
| S→C | `PairResult` | Pairing result |
| S→C | `Challenge` | Nonce for a known device |
| C→S | `ChallengeResponse` | Device signature |
| S→C | `Welcome` | Authentication success, session info |
| S→C | `AuthError` | Authentication failure |
| C→S | `Subscribe` | Subscription/filter/replay |
| S→C | `Subscribed` | Subscription active |
| C→S | `Unsubscribe` | Remove subscription |
| C→S | `ReplayRequest` | Explicit replay |
| S→C | `HistoryTruncated` | Requested history is outside the backlog |
| S→C | `ChatEvent` | Chat event |
| C→S | `SendRequest` | Raw or structured send |
| S→C | `SendResult` | Send status |
| C→S | `SendCancel` | Cancel request (currently limited) |
| C→S/S→C | `Ping` / `Pong` | Latency measurement |
| S→C | `Error` | Request/connection error |
| S→C | `RateLimitInfo` | Rate-limit information |
| S→C | `SessionEnded` | Logout/session end |
| S→C | `ServerShutdown` | Server shutdown |

## Capabilities

`ServerInfo.capabilities.features` / `Welcome.enabled_features` use dotted
strings:

```text
chat.segments
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

Clients should only use features advertised by both sides; ignore unknown
features.

## Current implementation

- Protocol version: `freechat.v1`.
- Default subprotocol: `freechat.v1.protobuf`.
- `freechat.v1.json` is available only when JSON debug is enabled in the plugin.
- The server sets `ChatEvent.origin_request_id` on outgoing echoes, but
  **does not currently emit a separate `SendResult(ECHOED)`**.
- `SendResult(SUBMITTED)` means the game chat box accepted the input; it does
  not prove delivery.
- The server keeps only the current game session backlog and does not persist
  chat history.
- The server listens on loopback by default; LAN access must be enabled in the
  plugin settings.

## Security notes

- Always pin the server P-256 public-key fingerprint. Do not trust all
  certificates.
- Never log or forward pairing codes or device private keys.
- Send only content the user explicitly requested. Do not auto-broadcast to
  public channels or linkshells.
- The server blocks account/session commands by default: `/logout`, `/quit`,
  `/shutdown`, `/exit`, `/delete`, `/resign`.
