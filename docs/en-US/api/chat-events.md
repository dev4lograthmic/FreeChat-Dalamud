# Chat events

## 1. ChatEvent

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
      "isPlayer": true
    },
    "text": "Hello",
    "rawSender": "AikDAQ...",
    "rawMessage": "AikDAQ...",
    "segments": [],
    "outgoing": false,
    "contentMode": "CONTENT_MODE_CURRENT"
  }
}
```

| Field | Meaning |
|---|---|
| `messageId` | `server_id:session_id:sequence` |
| `sequence` | Monotonic per session |
| `timestamp` | Server UTC time |
| `channel` | Normalized channel |
| `logKind` | Raw `XivChatType` / `LogKind`; use for unknown channels |
| `source` | `PRE_HANDLE` / `POST_HANDLE` / `STRUCTURED` |
| `sender` | Sender; system/NPC messages may have `isPlayer=false` |
| `text` | Plain text for search/notifications |
| `rawSender` | Raw SeString bytes |
| `rawMessage` | Raw SeString bytes |
| `segments` | Structured segments; may be empty on parse failure |
| `outgoing` | Whether the local player sent it |
| `tellPeer` | Tell peer (`TELL`) |
| `channelName` | Linkshell/CWLS name when known |
| `originRequestId` | Game echo correlation for a `SendRequest` |
| `originalRawSender` | Optional original SeString |
| `originalRawMessage` | Optional original SeString |
| `originalText` | Optional original text |
| `contentMode` | `CURRENT` or `ORIGINAL` |

## 2. Channels and log_kind

`ChatChannel` is the normalized display/filter field; `logKind` is the raw game
value.

Clients should:

- use `channel` for known channels;
- display unknown channels as "other" and filter by `logKind`;
- not assume the `ChatChannel` enum is exhaustive; game patches may add values.

Common channels: `SAY`, `SHOUT`, `YELL`, `TELL`, `PARTY`, `ALLIANCE`,
`FREE_COMPANY`, `LINKSHELL_1..8`, `CROSS_LINKSHELL_1..8`, `NOVICE_NETWORK`,
`PVP_TEAM`, `SYSTEM`, `ERROR`, `NPC`, `BATTLE`, `LOOT`, `PROGRESS`, `ECHO`,
`EMOTE`, `OTHER`.

## 3. Sources and deduplication

| Source | Description |
|---|---|
| `PRE_HANDLE` | Before other plugins; may be modified or suppressed |
| `POST_HANDLE` | Final display; recommended default |
| `STRUCTURED` | `LogMessage` structured system/battle logs |

`POST_HANDLE` and `STRUCTURED` may overlap. Deduplicate by:

```text
timestamp + log_kind + sender + text
```

## 4. Segments

`Segment` uses a oneof:

| Type | Fields |
|---|---|
| `TextSegment` | `text` + `TextStyle` (foreground, glow, italic) |
| `IconSegment` | `iconId` |
| `ItemSegment` | item ID, HQ, name, description, icon, level, item level, rarity, category, materia slots, stats |
| `MapSegment` | map ID, territory, coordinates, place name, map filename, zoom |
| `AutoTranslateSegment` | auto-translate text |
| `PlayerSegment` | player name, world, ContentId |
| `StatusSegment` | status ID, name, icon |

Clients that do not understand a segment should fall back to `text` or
`rawMessage`.

## 5. SeString and raw data

- `rawSender` / `rawMessage` are raw game SeString bytes.
- `text` is server-extracted plain text; it does not preserve styles/links.
- `segments` are server-parsed; item/map metadata may differ by game language
  and data version.
- If parsing fails, the server still sends `text` + `raw_*` and does not drop
  the event.
- When another plugin modifies a message:
  - `POST_HANDLE` + `CURRENT`: what the player finally sees.
  - `POST_HANDLE` + `ORIGINAL`: original content.
  - `includeOriginal=true`: include `original_*` alongside the current fields.

## 6. Echo correlation

After sending, the game's outgoing `ChatEvent` may include:

```json
{
  "chatEvent": {
    "channel": "CHAT_CHANNEL_ECHO",
    "text": "FreeChat agent test",
    "outgoing": false,
    "originRequestId": "29d1e0830e824f329b8bf894c027012d"
  }
}
```

Clients should use `originRequestId` to match the original
`SendRequest.request_id` and avoid duplicate optimistic rendering.

Current implementation:

- the server sets `originRequestId`;
- the server does **not** currently send an additional `SendResult(ECHOED)`;
  send results usually stop at `SUBMITTED`.

## 7. Examples

- [Say event](../../shared/protocol/examples/chat_event_say.json)
- [Tell event](../../shared/protocol/examples/chat_event_tell.json)
- [SessionEnded](../../shared/protocol/examples/session_ended.json)
