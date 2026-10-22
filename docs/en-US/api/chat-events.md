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
    "chunks": [],
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
| `segments` | Legacy structured segments; may be empty on parse failure |
| `chunks` | Preferred rich-text runs; text and link metadata in one run |
| `outgoing` | Whether the local player sent it |
| `tellPeer` | Tell peer (`TELL`) |
| `channelName` | Linkshell/CWLS name when known |
| `originRequestId` | Game echo correlation for a `SendRequest` |
| `originalRawSender` | Original sender SeString; present when it differs from the current stage |
| `originalRawMessage` | Original message SeString; present when it differs from the current stage |
| `originalText` | Original plain text; present when it differs from the current stage |
| `contentMode` | `CURRENT` or `ORIGINAL` |

`sender.worldId` is `0` when the sender's world is unknown; `sender.worldName`
is then empty. Do not substitute a placeholder world name.

Same-world tells and other local player chat often carry only the character
name, with no world at all. `sender.isPlayer` is still `true` for player chat
channels. When the display text is `Name@World`, `sender.worldName` is filled
but `sender.worldId` may remain `0`.

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
| `PRE_HANDLE` | `ChatMessage` stage; other plugin handlers may already have modified it |
| `POST_HANDLE` | Final display; recommended default |
| `STRUCTURED` | `LogMessage` structured system/battle logs |

`POST_HANDLE` and `STRUCTURED` may overlap. Deduplicate by:

```text
timestamp + log_kind + sender + text
```

## 4. Segments (legacy)

`Segment` uses a oneof:

| Type | Fields |
|---|---|
| `TextSegment` | `text` + `TextStyle` (foreground, glow, italic) |
| `IconSegment` | `iconId` |
| `ItemSegment` | item ID, HQ, name, description, icon, item level, equip level, rarity, category, materia slots, advanced melding, stats |
| `MapSegment` | map ID, territory, coordinates, place name, map filename, zoom |
| `AutoTranslateSegment` | auto-translate text |
| `PlayerSegment` | player name, world, ContentId |
| `StatusSegment` | status ID, name, icon |
| `PartyFinderSegment` | listing ID, link type |
| `QuestSegment` | quest ID, name |

`segments` is kept for backward compatibility. It does not describe where a
link appears inside `text`; a standalone link segment and a following
`TextSegment` may both contain display text. New clients should use `chunks`.

## 5. Chunks (recommended)

`chunks` is the canonical rich-text representation. Each chunk is one drawable
run. A text chunk contains its own display text and, optionally, the link
metadata for exactly that text:

```json
{
  "chunks": [
    { "text": { "text": "Meet at " } },
    {
      "text": {
        "text": "Limsa Lominsa (12.3, 45.6)",
        "map": {
          "mapId": 123,
          "territoryId": 456,
          "x": 12.3,
          "y": 45.6,
          "placeName": "Limsa Lominsa"
        }
      }
    },
    { "text": { "text": "." } }
  ]
}
```

Rules:

- Render chunks in order.
- `ChatTextChunk.text` is the exact display text; if a link is present, that
  text is the clickable label.
- `ChatIconChunk.iconId` is a standalone icon run.
- `style` applies to the chunk; reuse `TextStyle`.
- `ChatTextChunk.link` is one of `item`, `map`, `partyFinder`, `quest`,
  `player`, `status`, or `autoTranslate`.
- **No character offsets are needed.** The link position is the chunk's text.
- `ChatEvent.text` remains the plain-text search/notification fallback.
- If the server cannot derive display text for a link, it emits a readable
  fallback label (item name, map name + coordinates, quest name, or a numeric
  ID).
- `chunks` may be empty on parse failure. Fall back in this order:
  `chunks` → `segments` → `text` → `rawMessage`.

`PartyFinderSegment` only guarantees `listingId` and `linkType`; it does not
embed full party-finder details. `QuestSegment` includes the quest name when
the game sheet is available.

Capability names:

```text
chat.chunks
chat.chunks.party_finder
chat.chunks.quest
```

## 6. SeString and raw data

- `rawSender` / `rawMessage` are raw game SeString bytes.
- `text` is server-extracted plain text; it does not preserve styles/links.
- `segments` and `chunks` are server-parsed; item/map metadata may differ by
  game language and data version.
- `original_*` captures the message before plugin modifications. It is emitted
  only when the original differs from `text`/`raw_message`; `text` always
  describes the current stage (`PRE_HANDLE` or `POST_HANDLE`).
- If parsing fails, the server still sends `text` + `raw_*` and does not drop
  the event.
- When another plugin modifies a message:
  - `POST_HANDLE` + `CURRENT`: what the player finally sees.
  - `POST_HANDLE` + `ORIGINAL`: original content.
  - `includeOriginal=true` is a legacy request flag; `original_*` is now
    emitted automatically when it differs from the current stage.

## 7. Echo correlation

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
- `SUBMITTED` is terminal, and the server may additionally send a best-effort,
  non-terminal `SendResult(ECHOED)` to the originating session.

## 8. Examples

- [Say event](../../shared/protocol/examples/chat_event_say.json)
- [Tell event](../../shared/protocol/examples/chat_event_tell.json)
- [Map link chunks](../../shared/protocol/examples/chat_event_map.json)
- [SessionEnded](../../shared/protocol/examples/session_ended.json)
