# FreeChat Protocol v1

> Status: Phase 0 draft (pending review)
> Version: 1.0.0-draft
> Date: 2026-10-20
> Package: `freechat.v1`
> WebSocket subprotocols: `freechat.v1.protobuf` (default) / `freechat.v1.json` (debug only)
>
> Language: **English** · [简体中文](../../zh-CN/protocol/freechat-v1.md)

---

## 1. Overview

FreeChat is a **game-side server** that runs inside a Dalamud plugin. It pushes
FFXIV chat-log events to clients on the local network and accepts text/command
send requests from those clients.

This document defines only the WebSocket protocol between the plugin and a
client. It does not define client UI, game-internal data formats, or
XIVChatNext compatibility.

### 1.1 Goals

- Multi-language clients (Windows/iOS/Android/macOS/CLI) can share one `.proto`.
- Chat events carry as much information as practical: plain text + raw SeString
  + structured segments.
- Clients can subscribe/filter per connection without stalling the game thread.
- Reconnect/replay is supported within the current game session; nothing is
  persisted.
- Sending is equivalent to typing in the game chat box, with queueing, rate
  limits, receipts, and a command blocklist.
- WSS + self-signed certificate + public-key pinning; QR/deep link for first
  pairing.
- The protocol is versioned and can be checked with `buf breaking`.

### 1.2 Non-goals

- Browser/Web clients (not in v1; future ADR).
- Public relay / NAT traversal.
- Multiple game instances / multi-boxing.
- Server-side chat persistence or offline messages.
- Capturing text that is being typed but not yet sent.
- Screenshots, friends, presence, item/map lookup.
- Character automation, anti-AFK, auto-reply.

---

## 2. Roles and data flow

```
+---------------------+          WSS / WebSocket          +-------------------+
| FFXIV + Dalamud     | <===============================> | Client            |
| FreeChat plugin     |   freechat.v1 (protobuf/json)    | Win/iOS/Android   |
| (game-side server)  |                                  | macOS/CLI         |
+---------------------+                                  +-------------------+
```

**Receive:**

```
IChatGui event
  -> game-thread capture (copy raw SeString)
  -> background enrichment (segments)
  -> server backlog (sequence)
  -> per-client filter + queue
  -> WebSocket
```

**Send:**

```
WebSocket SendRequest
  -> validate (length/command/target/rate)
  -> send queue
  -> IFramework.Update (one command per frame)
  -> ProcessChatBoxEntry
  -> SendResult(SUBMITTED)
  -> outgoing ChatEvent -> SendResult(ECHOED)
```

---

## 3. Transport

### 3.1 WebSocket

- Transport: WebSocket over TLS (`wss://`).
- Default port: `14777`.
- Listens on loopback by default; LAN must be explicitly enabled in the plugin UI.
- Path: `/ws` (clients should accept any path returned by the server, but the
  default is `/ws`).
- Each WebSocket **message** (not a TCP fragment) carries exactly one `Envelope`.
- Binary messages are protobuf; text messages are debug JSON only.
- The client must offer a subprotocol in the HTTP Upgrade:

```
Sec-WebSocket-Protocol: freechat.v1.protobuf
```

or, only when the server has debug mode enabled:

```
Sec-WebSocket-Protocol: freechat.v1.json
```

- The server selects the first supported subprotocol and echoes it in the `101`
  response. If none is supported, it returns HTTP `400` or closes with `1003`.
- Clients must not put multiple `Envelope`s in one WebSocket message; the server
  must not concatenate multiple `Envelope`s into one message.
- The server should support WebSocket ping/pong. Application-level `Ping`/`Pong`
  is for latency measurement; both may coexist.

### 3.2 Limits

| Item | Default | Notes |
|---|---|---|
| HTTP request headers | 8 KiB | Reject larger requests |
| Handshake timeout | 5 s | From TCP accept to `101` |
| One WebSocket message | 1 MiB | Close `1009` if exceeded |
| Connections | 32 | Configurable |
| Concurrent handshakes | 8 | Prevent handshake exhaustion |
| Send queue | 128 messages / 64 KiB (global) | 32 per connection |
| Client outbound queue | 512 messages / 8 MiB | Slow-consumer policy in 10.4 |
| Application keepalive | 20 s | Disconnect after 60 s without response |

### 3.3 Close

- Normal: `1000`.
- Plugin unload/restart: `ServerShutdown` + `1001` or `1012`.
- Auth failure: `AuthError` + `4001`.
- Pairing required: `PairingRequired` + `4002` (or keep the connection open and
  wait for pairing; see section 5).
- Device revoked: `4003`.
- Protocol/encoding mismatch: `4004`.
- Slow consumer: `4005`.
- Server shutdown: `4006`.
- Message too large: `1009`.
- Unexpected error: `1011`.

Before closing, the server should make a best effort to send the corresponding
`Error`/`AuthError`/`ServerShutdown`, but must not wait indefinitely.

---

## 4. Security and pairing

### 4.1 TLS

- The server uses an **ECDSA P-256 + SHA-256 self-signed certificate**.
- TLS 1.2/1.3; no client certificate is required.
- The private key is encrypted with Windows DPAPI (CurrentUser) and stored in
  the plugin configuration.
- Certificate validity: 10 years suggested. SANs may include `localhost`, the
  hostname, and current LAN IPs, but **identity is established by public-key
  pinning**, not by CA/hostname validation.
- The client **must** compare the pin during the TLS handshake. On mismatch it
  must abort immediately and must not send `ClientHello` or the pairing code.
- Browsers are out of scope for v1; they cannot bypass self-signed certificate
  errors.

### 4.2 Public-key fingerprint

- Public-key encoding: P-256 **X9.63 uncompressed point**, 65 bytes:
  `0x04 || X(32) || Y(32)`, big-endian.
- Fingerprint: `SHA-256(65-byte public key)`, 32 bytes.
- Presentation/QR: 64 lowercase hex characters (`fp`).
- This encoding is easy to export from a certificate public key or device key
  on iOS/Android/Windows/macOS.
- Pin the public key, not the whole certificate: certificate renewal/SAN changes
  keep the pin; only a key change requires re-pairing.

### 4.3 Pairing QR / deep link

The plugin UI displays a QR code containing:

```
freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
```

Optional:

```
&name=<display name>
```

- `host`: LAN IP or hostname; IPv6 uses brackets, e.g. `[fe80::1]`.
- `sid`: `server_id`, for display/de-duplication.
- `fp`: server public-key fingerprint.
- `code`: one-time pairing code, 6 digits, valid for 5 minutes, single use.
- `name`: optional display name; do not put sensitive information here.

A desktop client may copy the same deep link or import
`freechat-pair.json`:

```json
{
  "v": 1,
  "host": "192.168.1.10",
  "port": 14777,
  "sid": "srv-1a2b",
  "fp": "0ed3a6ab957ff6f59a9630a473d31a7d04fcb46548b89016196980c7e96584f6",
  "code": "482913"
}
```

### 4.4 Pairing flow

```
Client                                          Server
  |  TCP + TLS (pin fp)                            |
  |----------------------------------------------->|
  |  HTTP Upgrade (freechat.v1.protobuf)           |
  |<-----------------------------------------------|
  |  Envelope{ ClientHello }                       |
  |----------------------------------------------->|
  |                                                | unknown device
  |  Envelope{ PairingRequired }                   |
  |<-----------------------------------------------|
  |                                                | user reads code in plugin UI
  |  Envelope{ PairRequest(code, device_pubkey) }  |
  |----------------------------------------------->|
  |                                                | plugin UI confirms device
  |  Envelope{ PairResult(ok, device_id) }         |
  |<-----------------------------------------------|
  |  Envelope{ Welcome }                           |
  |<-----------------------------------------------|
  |  Envelope{ Subscribe }                         |
  |----------------------------------------------->|
```

Rules:

- `PairingRequired` **does not contain the pairing code**. The code can only be
  read/scanned from the plugin UI.
- The user must confirm the device in the plugin UI (device name, platform,
  public-key fingerprint).
- On success, the server stores `device_id` + device public key; the client
  stores the server `fp` + its device private key.
- The pairing code expires after 5 minutes, is single-use, and is rate-limited
  (default 5 attempts/minute/IP; 5-minute lockout after that).
- The server may revoke a device at any time. The next request from a revoked
  device returns `DEVICE_REVOKED` and closes with `4003`.

### 4.5 Known-device authentication

```
Server -> Challenge { nonce(32), algorithm, expires_at }
Client -> ChallengeResponse { signature }
Server -> Welcome
```

- `algorithm` is fixed to `ECDSA_P256_SHA256_P1363`.
- Signing input (UTF-8, fields separated by `0x00`):

```
"FreeChat/v1 challenge" 0x00 server_id 0x00 device_id 0x00 nonce
```

- Hash the bytes above with SHA-256, then sign with the device private key.
  Signature format is IEEE P1363 (64 bytes, `r || s`).
- The server verifies with the stored device public key. On failure it returns
  `AuthError{UNAUTHENTICATED}` and closes with `4001`.
- The nonce is 32 random bytes, valid for 60 seconds, single-use; replay must be
  rejected.
- The device private key is generated and stored by the client (iOS Keychain /
  Android Keystore / Windows DPAPI / macOS Keychain).

### 4.6 Certificate/key rotation

- Replacing only the certificate while reusing the key: the pin does not change;
  clients do not need to re-pair.
- Replacing the key: all devices must re-pair; the plugin UI must warn clearly.
- Lost device key: remove the device and pair again.

### 4.7 Transport security modes

| Scenario | Transport | Notes |
|---|---|---|
| LAN | `wss://` | Pin required; default |
| Loopback debug | `ws://127.0.0.1` | Explicit opt-in only; not for LAN |
| Browser | Not supported | Self-signed WSS is incompatible; future ADR |

Debug JSON is an **encoding** switch, not a transport-security switch;
`freechat.v1.json` still runs over WSS.

---

## 5. Handshake state machine

States:

```
CONNECTED
  -> HELLO_RECEIVED
  -> PAIRING_PENDING  (unknown device)
  -> CHALLENGE_PENDING (known device)
  -> AUTHENTICATED
  -> SUBSCRIBED
  -> CLOSED
```

Rules:

1. After the WebSocket is established, the client must send `ClientHello`
   within 5 seconds.
2. The server decides from `device_id` / `device_public_key`:
   - Unknown: send `PairingRequired`, wait for `PairRequest` (up to 60 seconds).
   - Known: send `Challenge`, wait for `ChallengeResponse` (up to 10 seconds).
3. On success: send `Welcome`.
4. The client should send `Subscribe` within 10 seconds of `Welcome`; otherwise
   the server may keep the connection open without pushing chat, or close it
   according to configuration.
5. Any auth failure: send `AuthError` and close with the corresponding code.
6. Any message before authentication: `Error{UNAUTHENTICATED}`; the server may
   close with `4001`.
7. Unsupported `ClientHello.protocol_version`:
   `AuthError{UNSUPPORTED_VERSION}` + `4004`.
8. On logout/character change/plugin unload: `SessionEnded`, close 1001; the
   client should reconnect and subscribe on the new session.

### 5.1 Timeouts

| Phase | Timeout |
|---|---|
| WebSocket handshake | 5 s |
| `ClientHello` | 5 s |
| Pairing confirmation | 60 s |
| Challenge response | 10 s |
| First `Subscribe` | 10 s (configurable) |
| Application ping | 20 s interval; disconnect after 60 s without response |

---

## 6. Envelope and messages

Every message is wrapped in `Envelope`:

```proto
message Envelope {
  string request_id = 1;
  oneof payload { ... }
}
```

- `request_id`: opaque client-generated string for request/response correlation;
  the server echoes it in the response.
- Push messages (`ChatEvent`, `Pong`, `Error`, `RateLimitInfo`, etc.) may omit
  `request_id`.
- `request_id` should be unique per connection; UUID/ULID is recommended.
- Unknown `oneof` fields: clients/servers must ignore them (protobuf unknown
  field semantics).
- Unknown enum values: preserve the raw number; clients should fall back to
  `*_UNSPECIFIED` or use `log_kind`.

### 6.1 Message inventory

| Direction | Message | Notes |
|---|---|---|
| C→S | `ClientHello` | Protocol version/client info/device key/pairing code/resume sequence |
| S→C | `PairingRequired` | Unknown device, waiting for pairing |
| C→S | `PairRequest` | Pairing code + device info |
| S→C | `PairResult` | Pairing result |
| S→C | `Challenge` | Nonce for a known device |
| C→S | `ChallengeResponse` | Device signature |
| S→C | `Welcome` | Auth success + session info |
| S→C | `AuthError` | Auth failure |
| C→S | `Subscribe` | Subscribe/filter/resume |
| S→C | `Subscribed` | Subscription active + replay range |
| C→S | `Unsubscribe` | Remove subscription |
| C→S | `ReplayRequest` | Explicit replay |
| S→C | `HistoryTruncated` | Requested sequence is too old |
| S→C | `ChatEvent` | Chat event |
| C→S | `SendRequest` | Send text/command |
| S→C | `SendResult` | Send status |
| C→S | `SendCancel` | Cancel a queued send |
| C→S/S→C | `Ping` / `Pong` | Latency measurement |
| S→C | `Error` | Request/connection error |
| S→C | `RateLimitInfo` | Rate-limit information |
| S→C | `SessionEnded` | Logout/character change/plugin unload |
| S→C | `ServerShutdown` | Server shutdown |

---

## 7. Session and subscription

### 7.1 Identifiers

- `server_id`: stable per installation (generated on first run).
- `session_id`: generated per game login/plugin load; changes on logout, relog,
  or restart.
- `device_id`: stable per client installation.
- `message_id`: `server_id:session_id:sequence`.
- `sequence`: starts at 1 and increases monotonically per session. **Every
  message captured by the server** consumes a sequence, independent of any
  client's filter.

### 7.2 Subscribe

- One active subscription per connection; a new `Subscribe` replaces the old one.
- Empty `sources`: default `[CHAT_SOURCE_POST_HANDLE]`.
- Unspecified `content_mode`: default `CONTENT_MODE_CURRENT`.
- `include_original`: only meaningful when `content_mode=CURRENT`; when true,
  events also carry `original_*` fields.
- `from_sequence`:
  - `<= 0`: live only.
  - `> 0`: replay messages with `sequence > from_sequence`, then switch to live.
  - If `from_sequence < oldest_sequence - 1`: send `HistoryTruncated`, then
    replay from `oldest_sequence`.
- Replayed messages are subject to the active filter.
- `Subscribe` itself does not produce `ChatEvent`s; the server sends
  `Subscribed`, then replay, then live.

### 7.3 Filter semantics

All `ChatFilter` conditions are ANDed:

1. `include_categories` non-empty: the message's `ChatCategory` must be in it.
2. `exclude_categories` contains the category: drop.
3. `include_log_kinds` non-empty: `ChatEvent.log_kind` must be in it.
4. `exclude_log_kinds` contains `log_kind`: drop.
5. `keyword` non-empty: `ChatEvent.text` must contain the substring (case
   sensitivity controlled by `case_sensitive`).
6. `regex` non-empty: must match `ChatEvent.text`.
7. `include_own=false`: drop messages with `outgoing=true` (unset defaults to
   including own messages).
8. `include_battle=false`: drop `ChatCategory.BATTLE` (and structured battle
   messages).

Rules:

- Empty include = no restriction; exclude always wins.
- Empty keyword/regex = no restriction.
- Regex uses `RegexOptions.NonBacktracking`, max 256 characters, 50 ms match
  timeout; invalid/timeout returns `FILTER_INVALID`/`REGEX_INVALID`.
- Filtering runs on a background thread; never run regex on the game thread.
- Filter changes do not replay history automatically; use `ReplayRequest`.

### 7.4 Replay

- `ReplayRequest` can request history while subscribed.
- `from_sequence`/`to_sequence` follow `Subscribe` semantics; `to_sequence<=0`
  means up to `latest_sequence`.
- Replay is ascending by sequence and subject to `filter` (optional override).
- Out-of-buffer requests: `HistoryTruncated`, then replay from
  `oldest_sequence`.
- Replay does not change the live sequence.

### 7.5 Session end

- Logout: `SessionEnded{reason="logout"}`, close 1001.
- Character change: `SessionEnded{reason="character_changed"}`, close 1001.
- Plugin unload: `SessionEnded{reason="plugin_unloaded"}`, close 1001.
- Server restart: `ServerShutdown{reason, retry_after_ms}`, close 1012/4006.
- Clients should reconnect automatically; if `session_id` changes, clear the
  per-session `last_sequence`.

---

## 8. Chat event model

### 8.1 ChatEvent

| Field | Notes |
|---|---|
| `message_id` | Stable ID |
| `sequence` | Monotonic within the session |
| `timestamp` | Server UTC time |
| `channel` | Normalized channel |
| `log_kind` | Raw XivChatType/LogKind; use for unknown channels |
| `source` | `PRE_HANDLE` / `POST_HANDLE` / `STRUCTURED` |
| `sender` | Sender identity; system/NPC may have `is_player=false` |
| `text` | Plain text, suitable for search/notifications |
| `raw_sender` | Raw SeString bytes |
| `raw_message` | Raw SeString bytes |
| `segments` | Structured segments; empty on parse failure, text/raw remain valid |
| `outgoing` | Whether the local player sent it |
| `tell_peer` | Tell peer (TELL) |
| `channel_name` | Linkshell/CWLS name when known |
| `origin_request_id` | Set when this is the game echo of a SendRequest |
| `original_*` | Optional original content (see 7.2) |
| `content_mode` | Whether fields represent current or original content |

### 8.2 Channels

`ChatChannel` is the normalized enum; `log_kind` is the raw value. Clients
should:

- use `channel` for known channels;
- display unknown channels as "Other" and use `log_kind` for exact filtering;
- not treat `ChatChannel` as an exhaustive list.

### 8.3 Sources

- `PRE_HANDLE`: `IChatGui.ChatMessage`, before other plugins handle it; may be
  suppressed later.
- `POST_HANDLE`: `IChatGui.ChatMessageUnhandled`, final displayed message;
  recommended default.
- `STRUCTURED`: `IChatGui.LogMessage`, structured battle/system entries; may
  duplicate `POST_HANDLE`; clients should de-duplicate by
  `timestamp`/`log_kind`/content.

### 8.4 Segments

`Segment` `oneof`:

- `TextSegment`: text + `TextStyle` (foreground/glow/italic).
- `IconSegment`: game icon ID.
- `ItemSegment`: item ID/HQ/name/description/icon/level/item level/rarity/
  category/materia slots/stats.
- `MapSegment`: map ID/territory/coordinates/place name/map filename/scale.
- `AutoTranslateSegment`: auto-translate text.
- `PlayerSegment`: player name/world/ContentId.
- `StatusSegment`: status ID/name/icon.

Unknown segment kinds should fall back to `ChatEvent.text` or `raw_message`.

### 8.5 SeString and text

- `raw_sender`/`raw_message` are raw game SeString bytes; clients may parse them
  with game data.
- `text` is server-extracted plain text; styling/links are not guaranteed.
- `segments` is the server-parsed representation; item/map metadata may vary by
  game language/data version.
- On parse failure, the server sends only `text` + `raw_*`; it must not drop the
  message.
- When another plugin modified the message:
  - `POST_HANDLE` + `CURRENT`: what the player finally sees.
  - `POST_HANDLE` + `ORIGINAL`: original content
    (`OriginalSender`/`OriginalMessage`).
  - `include_original=true`: include `original_*` alongside current fields.

---

## 9. Sending

### 9.1 SendRequest

Two modes:

```proto
message SendRequest {
  oneof content {
    string raw_text = 1;
    ChatSend chat = 2;
  }
}
```

#### raw_text

- Equivalent to typing in the game chat box; may contain `/commands`.
- Maximum 500 UTF-8 bytes; larger returns `TEXT_TOO_LONG` (no automatic split,
  to avoid breaking commands).
- CR/LF are normalized to spaces; NUL is rejected.
- Command name: first token (after `/`, up to space/end of line), case-insensitive.
- Default blocklist: `/logout`, `/quit`, `/shutdown`, `/exit`, `/delete`,
  `/resign`; matching returns `COMMAND_BLOCKED`.
- Users can add/remove commands in the plugin UI.

#### ChatSend

- `channel` + optional `target` + `text`.
- The server builds the prefix (e.g. `/party`, `/tell Name@World`); clients do
  not need to know commands.
- `CHAT_CHANNEL_TELL` requires `target.name` +
  `target.world_name`/`world_id`; otherwise `TELL_TARGET_INVALID`.
- The server splits at 500 UTF-8 bytes (including prefix); reports
  `part_count`/`part_index`.
- Splitting preserves the prefix; every tell part includes the full
  `Name@World`.
- If a channel is unavailable (not in party/FC), the game returns an error; the
  server can only report `SUBMITTED` and forwards the game error as a normal
  `ChatEvent`.

### 9.2 Send queue and rate limits

- Network threads only enqueue; `IFramework.Update` executes at most one command
  per frame.
- Queue: 128 messages / 64 KiB global; 32 per connection.
- Suggested defaults:
  - Global 4 messages/second, burst 4.
  - Public channels/tells 1 message/second.
  - Configurable.
- Over limit: `SendResult{RATE_LIMITED}` + `Error{RATE_LIMITED,
  retry_after_ms}`.
- Queue full: `SendResult{QUEUE_FULL}`.
- Validate login/character/session before sending; otherwise return
  `NOT_LOGGED_IN`/`CHANNEL_UNAVAILABLE`.

### 9.3 Receipt state machine

```
QUEUED -> SUBMITTED -> ECHOED
   |          |
   +-> FAILED / BLOCKED / RATE_LIMITED / QUEUE_FULL / CANCELLED
```

- `QUEUED`: enqueued, with `queue_position`.
- `SUBMITTED`: `ProcessChatBoxEntry` succeeded (the game accepted the input;
  this does not mean the peer received it).
- `ECHOED`: a matching outgoing `ChatEvent` was observed; carries
  `origin_message_id`.
- `FAILED`: game API unavailable/exception.
- `BLOCKED`: command blocklist.
- `RATE_LIMITED`/`QUEUE_FULL`: rate limit/queue full.
- `CANCELLED`: `SendCancel` or a previous part failed.
- Multi-part: each part gets its own `SendResult`; `part_index` starts at 0; if
  the first part fails, remaining parts are marked `CANCELLED`.
- Tells have no delivery receipt; `ECHOED` only means the game chat box accepted
  the message.

### 9.4 Echo correlation

- After submitting, the server records a pending match (channel, target, text,
  time window).
- On a matching outgoing `ChatEvent`:
  - set `origin_request_id` on that event;
  - send `SendResult{ECHOED, origin_message_id}`.
- Suggested match window: 5 seconds; after timeout, keep `SUBMITTED` and do not
  send `ECHOED`.
- Clients should use `origin_request_id` to de-duplicate optimistic messages.

### 9.5 SendCancel

- `SendCancel.target_request_id` refers to the `Envelope.request_id` of the
  `SendRequest` to cancel.
- Only queued, not-yet-submitted parts can be cancelled; submitted parts cannot
  be recalled.
- On success: the part gets `SendResult{CANCELLED}`.

---

## 10. Errors, rate limits, backpressure

### 10.1 Error

```proto
message Error {
  ErrorCode code = 1;
  string message = 2;
  uint32 retry_after_ms = 3;
}
```

- `Envelope.request_id` correlates the request; push errors may omit it.
- `message` is human-readable; clients must use `code`.

### 10.2 Error codes

| Code | Meaning | Retryable |
|---|---|---|
| `INVALID_REQUEST` | Invalid arguments/state | No |
| `UNAUTHENTICATED` | Not authenticated | Re-authenticate |
| `PAIRING_REQUIRED` | Pairing required | Yes (with code) |
| `PAIRING_INVALID` | Wrong pairing code | Yes (rate-limited) |
| `PAIRING_EXPIRED` | Pairing code expired | Generate a new one |
| `PAIRING_RATE_LIMITED` | Too many pairing attempts | Yes (wait) |
| `DEVICE_REVOKED` | Device revoked | No (re-pair) |
| `PROTOCOL_MISMATCH` | Protocol/subprotocol mismatch | No |
| `UNSUPPORTED_ENCODING` | JSON debug disabled/encoding unsupported | No |
| `UNSUPPORTED_VERSION` | Unsupported protocol version | No |
| `NOT_SUBSCRIBED` | Not subscribed | Subscribe first |
| `FILTER_INVALID` | Invalid filter | No |
| `REGEX_INVALID` | Invalid regex/timeout | No |
| `TEXT_TOO_LONG` | Over 500 bytes (raw_text) | No |
| `COMMAND_BLOCKED` | Command blocklist | No |
| `NOT_LOGGED_IN` | Game not logged in | Yes |
| `CHANNEL_UNAVAILABLE` | Channel unavailable | Maybe |
| `TELL_TARGET_INVALID` | Invalid tell target | No |
| `QUEUE_FULL` | Send queue full | Yes (wait) |
| `RATE_LIMITED` | Rate limited | Yes (wait) |
| `SLOW_CONSUMER` | Client too slow | No (disconnected) |
| `INTERNAL` | Internal server error | Maybe |
| `SHUTTING_DOWN` | Server shutting down | Yes (reconnect) |
| `HISTORY_UNAVAILABLE` | Requested history unavailable | Resubscribe from oldest |

### 10.3 Rate limits

- Pairing: 5 attempts/minute/IP; 5-minute lockout on failure.
- Connections: 32 concurrent; 8 concurrent handshakes.
- Sending: see 9.2.
- Chat push: no proactive rate limit; per-client queue and slow-consumer policy
  protect the server.
- On rate limit, send `RateLimitInfo` + `Error`.

### 10.4 Backpressure and slow consumers

- Each client has an independent bounded queue (512 messages / 8 MiB).
- When full:
  1. Drop `BATTLE`/`STRUCTURED` low-priority messages first;
  2. If still full, close the connection with `4005 SLOW_CONSUMER`;
  3. Do not affect other clients.
- The server must never block the game thread or other clients for a slow
  client.
- Clients should read promptly; if the UI stalls, reduce the subscription
  (disable battle, narrow the filter).

---

## 11. Versioning and capabilities

### 11.1 Versioning

- `ClientHello.protocol_version = 1`.
- Package `freechat.v1`; WebSocket subprotocols `freechat.v1.protobuf` /
  `freechat.v1.json`.
- Additive only: new fields/messages/enum values; never change existing
  semantics.
- Removed fields must be `reserved`; never reuse field numbers.
- `buf breaking` runs in CI against main.
- Breaking changes -> `freechat.v2` + a new subprotocol.

### 11.2 Capabilities

`ServerCapabilities.features` / `ClientCapabilities.features` use dot-separated
strings:

```
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

- Clients should only use capabilities declared by both sides.
- The server returns the enabled subset in `Welcome.enabled_features`.
- Unknown capabilities are ignored.

---

## 12. proto3 JSON (debug only)

- The server accepts `freechat.v1.json` only when the plugin UI has
  `Enable JSON debug transport` enabled.
- One WebSocket text message = one `Envelope` in proto3 JSON.
- Standard protobuf JSON mapping:
  - Field names are lowerCamelCase (`request_id` -> `requestId`).
  - `int64`/`uint64` are strings (e.g. `"42"`).
  - `bytes` are base64.
  - Enums are names (e.g. `"CHAT_CHANNEL_SAY"`).
  - `google.protobuf.Timestamp` is RFC3339 (e.g.
    `"2026-10-20T12:34:56.789Z"`).
  - Default values are usually omitted; parsers must accept missing fields.
- Servers/clients should accept both lowerCamelCase and the original
  snake_case field names (Google.Protobuf JsonParser supports this).
- JSON and protobuf carry exactly the same data; `Any` is not used.
- Disabled by default in production; enabling it must log a warning.

---

## 13. Examples

All examples below are proto3 JSON (debug); protobuf uses the same fields.

### 13.1 ClientHello

```json
{
  "requestId": "c-1",
  "clientHello": {
    "protocolVersion": 1,
    "clientName": "FreeChat CLI",
    "clientVersion": "0.1.0",
    "platform": "cli",
    "deviceId": "dev-7f3c2a",
    "devicePublicKey": "BAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=",
    "pairingCode": "482913",
    "resumeFromSequence": "0",
    "capabilities": ["chat.segments", "filters.regex"]
  }
}
```

### 13.2 PairingRequired

```json
{
  "pairingRequired": {
    "server": {
      "serverId": "srv-1a2b",
      "serverName": "FreeChat",
      "character": {
        "name": "Someone",
        "worldId": 86,
        "worldName": "Typhon",
        "contentId": "12345678901234567",
        "isPlayer": true,
        "isLocal": true
      },
      "protocolVersion": 1,
      "serverVersion": "0.1.0",
      "publicKeyFingerprint": "DtOmq5V/9vWaljCkc9MafQT8tGVIuJAWGWmAx+llhPY=",
      "capabilities": {
        "features": ["chat.segments", "filters.regex"],
        "maxMessageBytes": 1048576,
        "maxClients": 32,
        "maxSendQueue": 128,
        "backlogMessages": 5000,
        "backlogBytes": 33554432
      }
    },
    "reason": "unknown_device",
    "codeLength": 6,
    "expiresAt": "2026-10-20T12:40:00Z"
  }
}
```

### 13.3 PairRequest

```json
{
  "requestId": "c-2",
  "pairRequest": {
    "code": "482913",
    "deviceName": "My iPhone",
    "platform": "ios",
    "devicePublicKey": "BAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=",
    "capabilities": { "features": ["chat.segments"] }
  }
}
```

### 13.4 Challenge / ChallengeResponse

```json
{
  "challenge": {
    "nonce": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
    "algorithm": "ECDSA_P256_SHA256_P1363",
    "expiresAt": "2026-10-20T12:35:00Z"
  }
}
```

```json
{
  "requestId": "c-3",
  "challengeResponse": {
    "signature": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw=="
  }
}
```

### 13.5 Welcome

```json
{
  "welcome": {
    "server": { "serverId": "srv-1a2b", "protocolVersion": 1 },
    "sessionId": "sess-9f8e",
    "oldestSequence": "1",
    "latestSequence": "42",
    "deviceId": "dev-7f3c2a",
    "enabledFeatures": ["chat.segments", "filters.regex"]
  }
}
```

### 13.6 Subscribe

```json
{
  "requestId": "c-4",
  "subscribe": {
    "filter": {
      "includeCategories": ["CHAT_CATEGORY_PLAYER_CHAT", "CHAT_CATEGORY_TELL"],
      "excludeCategories": [],
      "includeLogKinds": [],
      "excludeLogKinds": [],
      "keyword": "",
      "regex": "",
      "caseSensitive": false,
      "includeOwn": true,
      "includeBattle": false
    },
    "sources": ["CHAT_SOURCE_POST_HANDLE"],
    "contentMode": "CONTENT_MODE_CURRENT",
    "fromSequence": "0",
    "includeOriginal": false
  }
}
```

### 13.7 ChatEvent (Say)

```json
{
  "chatEvent": {
    "messageId": "srv-1a2b:sess-9f8e:42",
    "sequence": "42",
    "timestamp": "2026-10-20T12:34:56.789Z",
    "channel": "CHAT_CHANNEL_SAY",
    "logKind": 10,
    "source": "CHAT_SOURCE_POST_HANDLE",
    "sender": {
      "name": "Someone",
      "worldId": 86,
      "worldName": "Typhon",
      "contentId": "12345678901234567",
      "isPlayer": true,
      "isLocal": false
    },
    "text": "Hello",
    "rawSender": "AikDAQIDSGVsbG8=",
    "rawMessage": "AikDAQIDSGVsbG8=",
    "segments": [
      {
        "style": { "foregroundRgba": 4294967295, "glowRgba": 0, "italic": false },
        "text": { "text": "Hello" }
      }
    ],
    "outgoing": false,
    "contentMode": "CONTENT_MODE_CURRENT"
  }
}
```

### 13.8 ChatEvent (Tell)

```json
{
  "chatEvent": {
    "messageId": "srv-1a2b:sess-9f8e:43",
    "sequence": "43",
    "timestamp": "2026-10-20T12:35:10Z",
    "channel": "CHAT_CHANNEL_TELL",
    "logKind": 13,
    "source": "CHAT_SOURCE_POST_HANDLE",
    "sender": { "name": "Friend", "worldId": 86, "worldName": "Typhon", "isPlayer": true },
    "text": "Hello",
    "rawMessage": "AikDAQIDSGVsbG8=",
    "segments": [],
    "outgoing": false,
    "tellPeer": { "name": "Friend", "worldId": 86, "worldName": "Typhon", "isPlayer": true },
    "contentMode": "CONTENT_MODE_CURRENT"
  }
}
```

### 13.9 SendRequest (raw)

```json
{
  "requestId": "send-1",
  "sendRequest": { "rawText": "/p one moment" }
}
```

### 13.10 SendRequest (structured)

```json
{
  "requestId": "send-2",
  "sendRequest": {
    "chat": {
      "channel": "CHAT_CHANNEL_TELL",
      "target": { "name": "Friend", "worldId": 86, "worldName": "Typhon", "isPlayer": true },
      "text": "On my way"
    }
  }
}
```

### 13.11 SendResult

```json
{
  "requestId": "send-2",
  "sendResult": {
    "status": "SEND_STATUS_SUBMITTED",
    "partIndex": 0,
    "partCount": 1
  }
}
```

```json
{
  "requestId": "send-2",
  "sendResult": {
    "status": "SEND_STATUS_ECHOED",
    "partIndex": 0,
    "partCount": 1,
    "originMessageId": "srv-1a2b:sess-9f8e:44"
  }
}
```

### 13.12 Error

```json
{
  "requestId": "send-3",
  "error": {
    "code": "ERROR_CODE_COMMAND_BLOCKED",
    "message": "Command /logout is blocked by server policy."
  }
}
```

### 13.13 SessionEnded

```json
{
  "sessionEnded": {
    "reason": "logout",
    "endedAt": "2026-10-20T13:00:00Z"
  }
}
```

---

## 14. Conformance

Phase 0 provides JSON cases; Phase 1 generates protobuf binary vectors.

Coverage:

1. Subprotocol negotiation: protobuf / json / unsupported.
2. Unknown device: `ClientHello` -> `PairingRequired`.
3. Wrong pin: disconnect during TLS; no `ClientHello`.
4. Pairing: `PairRequest` -> `PairResult(ok)` -> `Welcome`.
5. Known device: `Challenge` -> `ChallengeResponse` -> `Welcome`.
6. Bad signature/expired nonce: `AuthError`.
7. `Subscribe` -> `Subscribed`, including `from_sequence`.
8. Filters: include/exclude/keyword/regex/own/battle.
9. Replay: normal, `HistoryTruncated`, `from_sequence` beyond latest.
10. Sending: raw/structured/tell/multi-part/`QUEUED`/`SUBMITTED`/`ECHOED`.
11. Blocklist: `/logout` -> `COMMAND_BLOCKED`.
12. Rate limit/queue full: `RATE_LIMITED`/`QUEUE_FULL`.
13. `SendCancel`: cancel a queued part.
14. `Ping`/`Pong` latency.
15. Device revocation: `DEVICE_REVOKED` + `4003`.
16. JSON and protobuf encode/decode equivalence.
17. Unknown fields/enum values do not crash.
18. Message larger than 1 MiB -> `1009`.
19. Slow consumer -> `4005`.
20. Logout/plugin unload -> `SessionEnded` + 1001.

---

## 15. Security and privacy

- Chat content is never logged, persisted, or uploaded.
- Device storage only keeps `device_id`, name, platform, public key,
  created/last-seen, and revocation state; no chat.
- The pairing QR is shown only in the plugin UI; do not send it over chat/email.
- One-time pairing code: 5 minutes, single use, rate-limited.
- Clients must pin the exact public-key fingerprint; never trust-all.
- The server listens on loopback by default; LAN requires explicit WSS.
- Debug JSON is off by default; enabling it logs only "enabled", not message
  content.
- Regex uses NonBacktracking + timeout to avoid DoS.
- The default command blocklist covers account/session commands; users can add
  more.
- The server does not automate gameplay, anti-AFK, or auto-reply.

---

## 16. Open issues

1. Actual coverage/duplication of `ChatMessageUnhandled` vs `LogMessage` needs a
   Phase 1 spike.
2. Battle log is off by default; whether replay includes it is TBD.
3. Exact channel prefix aliases (`/l1` vs `/linkshell1`, etc.) need Phase 1
   testing.
4. Multi-part `ChatSend` failure/rollback semantics need confirmation before
   implementation.
5. AGPL-3.0 impact on the third-party protocol ecosystem; whether `.proto`/docs
   need a separate license.
6. Future browser security layer (local CA/relay/application-layer encryption).
7. Multi-instance, relay, screenshots/friends, and other future features.

---

## Appendix A: Default command blocklist

```
/logout
/quit
/shutdown
/exit
/delete
/resign
```

- Only applies to the `raw_text` command path; `ChatSend` builds safe prefixes.
- Case-insensitive; command name is the token after `/` up to the first
  space/end of line.
- Users can add/remove entries in the plugin UI; the server should record the
  effective list.

## Appendix B: Channel-to-prefix mapping (implementation detail)

`ChatSend` prefixes are maintained by the server, not hard-coded in clients.
Suggested:

| ChatChannel | Prefix |
|---|---|
| SAY | `/say ` |
| SHOUT | `/shout ` |
| YELL | `/yell ` |
| TELL | `/tell <Name@World> ` |
| PARTY | `/party ` |
| ALLIANCE | `/alliance ` |
| FREE_COMPANY | `/freecompany ` |
| LINKSHELL_1..8 | `/l1 `..`/l8 ` (to be tested) |
| CROSS_LINKSHELL_1..8 | `/cwl1 `..`/cwl8 ` (to be tested) |
| NOVICE_NETWORK | `/n ` (to be tested) |
| PVP_TEAM | `/pvpteam ` (to be tested) |

Phase 1 must verify aliases on the current game version; the wire protocol only
exposes `ChatChannel`, so prefix changes do not affect clients.

## Appendix C: Signature and key encoding

- Public key: P-256 X9.63 uncompressed point, 65 bytes.
- Fingerprint: SHA-256(65-byte public key).
- Device signature: ECDSA P-256 + SHA-256, IEEE P1363, 64 bytes.
- Signing input:
  `"FreeChat/v1 challenge" 0x00 server_id 0x00 device_id 0x00 nonce`.
- Nonce: 32 random bytes, valid for 60 seconds, single use.
- Pairing code: 6 digits, valid for 5 minutes, single use.
