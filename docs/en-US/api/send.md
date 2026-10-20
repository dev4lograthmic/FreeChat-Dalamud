# Sending messages

Sending is equivalent to typing into the game chat box. The server validates,
queues, rate-limits, and acknowledges requests.

## 1. Two send modes

```proto
message SendRequest {
  oneof content {
    string raw_text = 1;
    ChatSend chat = 2;
  }
}
```

### raw_text

```json
{
  "requestId": "send-1",
  "sendRequest": {
    "rawText": "/echo FreeChat test"
  }
}
```

- May contain arbitrary game commands.
- Maximum **500 UTF-8 bytes**; overflow returns `ERROR_CODE_TEXT_TOO_LONG`.
  It is not split automatically.
- CR/LF is normalized to spaces; NUL is rejected.
- The command name is the first token (`/` through the first space/end of
  line), case-insensitive.
- Default blocklist:

  ```text
  /logout
  /quit
  /shutdown
  /exit
  /delete
  /resign
  ```

  Blocked commands return `ERROR_CODE_COMMAND_BLOCKED`.
- A full-width slash `／` is normalized to `/` before the check to prevent
  bypassing the blocklist.
- Additional commands are controlled by the plugin's blocklist setting.

### ChatSend

```json
{
  "requestId": "send-2",
  "sendRequest": {
    "chat": {
      "channel": "CHAT_CHANNEL_TELL",
      "target": {
        "name": "Friend",
        "worldId": 86,
        "worldName": "Typhon"
      },
      "text": "On my way"
    }
  }
}
```

- `channel` is required.
- `target` is required only for `CHAT_CHANNEL_TELL`.
- The current implementation requires `target.name` + `target.world_name`; a
  `world_id` alone cannot be turned into a command prefix.
- The server generates the channel prefix; clients do not need to build
  `/party`, `/tell`, etc.
- The server splits at 500 UTF-8 bytes including the prefix; every part keeps
  the channel/tell prefix.

## 2. Channels and prefixes

| ChatChannel | Server prefix |
|---|---|
| `CHAT_CHANNEL_SAY` | `/say ` |
| `CHAT_CHANNEL_SHOUT` | `/shout ` |
| `CHAT_CHANNEL_YELL` | `/yell ` |
| `CHAT_CHANNEL_TELL` | `/tell <Name@World> ` |
| `CHAT_CHANNEL_PARTY` | `/party ` |
| `CHAT_CHANNEL_ALLIANCE` | `/alliance ` |
| `CHAT_CHANNEL_FREE_COMPANY` | `/freecompany ` |
| `CHAT_CHANNEL_LINKSHELL_1..8` | `/l1 `..`/l8 ` |
| `CHAT_CHANNEL_CROSS_LINKSHELL_1..8` | `/cwl1 `..`/cwl8 ` |
| `CHAT_CHANNEL_NOVICE_NETWORK` | `/n ` |
| `CHAT_CHANNEL_PVP_TEAM` | `/pvpteam ` |

Unavailable channels (not in a party, not in an FC, etc.) are handled by the
game. The server can only report whether input was submitted; it cannot
guarantee delivery.

## 3. Queue and rate limits

- Network threads only enqueue; `IFramework.Update` submits at most one part per
  frame.
- Default send interval: 250 ms; configurable in the plugin.
- Default rate limit: 4/second, burst 4; configurable in the plugin.
- Rate limit response:

  ```json
  {
    "sendResult": {
      "status": "SEND_STATUS_RATE_LIMITED",
      "error": "ERROR_CODE_RATE_LIMITED",
      "message": "Send rate limit exceeded."
    }
  }
  ```

- Full queue returns `ERROR_CODE_QUEUE_FULL`.
- Not logged in / character unavailable returns `ERROR_CODE_NOT_LOGGED_IN`.

## 4. SendResult

```json
{
  "requestId": "send-2",
  "sendResult": {
    "status": "SEND_STATUS_SUBMITTED",
    "partIndex": 0,
    "partCount": 1,
    "queuePosition": 0
  }
}
```

| Status | Meaning |
|---|---|
| `SEND_STATUS_QUEUED` | Queued (current implementation usually returns Submitted directly) |
| `SEND_STATUS_SUBMITTED` | The game chat box accepted the input; not delivery |
| `SEND_STATUS_ECHOED` | Game echo observed (protocol reserved; not emitted currently) |
| `SEND_STATUS_FAILED` | Game API failure/exception |
| `SEND_STATUS_BLOCKED` | Command blocklist |
| `SEND_STATUS_RATE_LIMITED` | Rate limited |
| `SEND_STATUS_QUEUE_FULL` | Queue full |
| `SEND_STATUS_CANCELLED` | Cancelled |

`SUBMITTED` is not a delivery receipt; tells have no read/delivery confirmation.

## 5. Multi-part sends

- Structured sends over 500 bytes are split.
- Every part keeps the channel/tell prefix.
- Current implementation submits all parts and returns one `Submitted` with
  `partCount`; it does not send per-part `SendResult` messages yet.
- If a part fails, later parts are not submitted.

## 6. Echo correlation

The game echo event may include:

```json
{
  "chatEvent": {
    "channel": "CHAT_CHANNEL_ECHO",
    "text": "FreeChat test",
    "originRequestId": "send-2"
  }
}
```

Clients correlate using `originRequestId`. The current implementation does not
send an additional `SendResult(ECHOED)`; do not assume it will always arrive.

## 7. SendCancel

```json
{
  "requestId": "cancel-1",
  "sendCancel": {
    "targetRequestId": "send-2"
  }
}
```

The current implementation returns:

```json
{
  "sendResult": {
    "status": "SEND_STATUS_CANCELLED",
    "message": "Only queued sends can be cancelled; this request is already in flight."
  }
}
```

Actual cancellation of queued parts is not implemented yet; do not treat
`SendCancel` as a reliable undo.

## 8. Safety guidance

- Send only content the user explicitly requested.
- Do not auto-broadcast to public channels, linkshells, or cross-world
  linkshells.
- Do not implement auto-replies, anti-AFK, or message flooding.
- Check login/channel availability before sending.
- Production clients should surface `Blocked` / `RateLimited` / `QueueFull`
  instead of silently retrying.
