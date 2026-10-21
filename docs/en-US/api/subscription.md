# Subscriptions and replay

After authentication, clients send `Subscribe` before receiving chat events.

## 1. Subscribe

```json
{
  "requestId": "sub-1",
  "subscribe": {
    "filter": {
      "includeCategories": [],
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

| Field | Meaning |
|---|---|
| `filter` | Optional; empty filter means no restrictions |
| `sources` | Empty defaults to `POST_HANDLE` |
| `contentMode` | Defaults to `CURRENT` |
| `fromSequence` | `<= 0` is live only; `> 0` replays after that sequence |
| `includeOriginal` | Only meaningful for `CURRENT`; when true, events include `original_*` |

Each connection has one active subscription. Sending `Subscribe` again replaces
the previous subscription.

## 2. Sources

| Source | Meaning |
|---|---|
| `CHAT_SOURCE_PRE_HANDLE` | `IChatGui.ChatMessage` before other plugins handle it |
| `CHAT_SOURCE_POST_HANDLE` | `IChatGui.ChatMessageUnhandled`, what the player finally sees; recommended default |
| `CHAT_SOURCE_STRUCTURED` | `IChatGui.LogMessage`, structured system/battle logs |

`PRE_HANDLE` messages may be modified or suppressed by other plugins.
`POST_HANDLE` is closer to what the player sees. `STRUCTURED` may duplicate
`POST_HANDLE`; clients should deduplicate by `timestamp`, `log_kind`, and
content.

## 3. Filters

All conditions are ANDed:

| Condition | Semantics |
|---|---|
| `includeCategories` non-empty | Message category must be included |
| `excludeCategories` includes category | Drop |
| `includeLogKinds` non-empty | Raw `log_kind` must be included |
| `excludeLogKinds` includes `log_kind` | Drop |
| `keyword` non-empty | `ChatEvent.text` must contain the substring |
| `regex` non-empty | Must match `ChatEvent.text` |
| `caseSensitive` | Controls keyword/regex case sensitivity |
| `includeOwn = false` | Drop `outgoing=true` messages |
| `includeBattle = false` | Drop battle categories |

Rules:

- Empty include means no restriction; exclude wins.
- `includeOwn` is optional; unset is equivalent to `true`.
- `includeBattle` defaults to `false`.
- Regex uses `RegexOptions.NonBacktracking`, max 256 characters, 50 ms timeout.
- Invalid regex returns `ERROR_CODE_REGEX_INVALID`; invalid filters return
  `ERROR_CODE_FILTER_INVALID`.
- Filtering runs on the server background, not the game thread.
- Changing filters does not replay history; send `ReplayRequest` when needed.

### Categories

Common `ChatCategory` values:

```text
CHAT_CATEGORY_PLAYER_CHAT
CHAT_CATEGORY_TELL
CHAT_CATEGORY_PARTY
CHAT_CATEGORY_ALLIANCE
CHAT_CATEGORY_FREE_COMPANY
CHAT_CATEGORY_LINKSHELL
CHAT_CATEGORY_CROSS_LINKSHELL
CHAT_CATEGORY_NOVICE_NETWORK
CHAT_CATEGORY_PVP_TEAM
CHAT_CATEGORY_SYSTEM
CHAT_CATEGORY_ERROR
CHAT_CATEGORY_NPC
CHAT_CATEGORY_BATTLE
CHAT_CATEGORY_LOOT
CHAT_CATEGORY_PROGRESS
CHAT_CATEGORY_ECHO
CHAT_CATEGORY_EMOTE
CHAT_CATEGORY_OTHER
```

## 4. Subscribed

```json
{
  "requestId": "sub-1",
  "subscribed": {
    "effectiveFilter": {},
    "effectiveSources": ["CHAT_SOURCE_POST_HANDLE"],
    "effectiveContentMode": "CONTENT_MODE_CURRENT",
    "oldestSequence": "1",
    "latestSequence": "42",
    "replayTruncated": false,
    "replayFromSequence": "1"
  }
}
```

Clients start receiving `ChatEvent` after `Subscribed`.

## 5. Replay

When `fromSequence > 0`:

1. The server sends `Subscribed`.
2. It replays events with `sequence > fromSequence` in ascending order.
3. It then continues with live events.

Replay applies both the current `ChatFilter` and the effective `sources`.
An explicit `ReplayRequest` also uses the current subscription sources.

If the requested range is too old:

```json
{
  "historyTruncated": {
    "requestedFromSequence": "1",
    "oldestSequence": "100",
    "latestSequence": "150"
  }
}
```

Replay then starts at `oldestSequence`.

### Explicit ReplayRequest

```json
{
  "requestId": "replay-1",
  "replayRequest": {
    "fromSequence": "100",
    "toSequence": "120",
    "filter": null
  }
}
```

- `toSequence <= 0` means up to `latest_sequence`.
- An empty `filter` uses the active subscription filter; a non-empty filter
  overrides it for this replay.
- Replay does not change the live sequence.

## 6. Unsubscribe

```json
{
  "requestId": "unsub-1",
  "unsubscribe": {}
}
```

After unsubscribing, no more chat events are pushed; the connection remains
open.

## 7. Sessions and sequence

- `server_id`: stable per installation.
- `session_id`: generated per login/plugin load; changes after logout, reload,
  or restart.
- `message_id`: `server_id:session_id:sequence`.
- `sequence`: starts at 1 per session and increases monotonically.
- Every captured message consumes a sequence, even if a client filter drops it.
- After `SessionEnded`, clear the old session's `last_sequence`.

## 8. Examples

- [Subscribe](../../shared/protocol/examples/subscribe.json)
- [Chat event: Say](../../shared/protocol/examples/chat_event_say.json)
- [Chat event: Tell](../../shared/protocol/examples/chat_event_tell.json)
