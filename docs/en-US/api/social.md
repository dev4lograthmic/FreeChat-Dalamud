# Social data

FreeChat exposes three read-only social features:

1. **Linkshell names** in `ChatEvent.channel_name`.
2. **Friend list** snapshots (`PlayerListRequest` / `PlayerListSnapshot`).
3. **Friend presence** for ContentIds in the current friend snapshot.

The friend list and presence APIs are **disabled by default**. The user must
enable them in the plugin settings and restart the server. The server only
advertises `social.friend_list` / `social.friend_presence` when enabled.

## 1. Linkshell names

No new request is needed. When a linkshell or cross-world linkshell message is
captured, the server fills `ChatEvent.channel_name` with the current name. The
client can fall back to `Linkshell N` / `Cross-world Linkshell N` when empty.

Names are refreshed from the game every 5 seconds and cleared on logout or
character change.

## 2. Friend list

### Request

```json
{
  "requestId": "friends-1",
  "playerListRequest": {
    "type": "PLAYER_LIST_TYPE_FRIEND",
    "pageSize": 32
  }
}
```

Fields:

| Field | Meaning |
|---|---|
| `type` | Currently only `PLAYER_LIST_TYPE_FRIEND` |
| `expectedOwnerKey` | Optional; if set, must match the active character key |
| `expectedOwnerEpoch` | Optional; if set, must match the current session id |
| `pageSize` | `0` uses 32; server clamps to 1..200 |

`Welcome.session_id` is a suitable `expectedOwnerEpoch`. Clients that do not
know the owner key can leave both expected fields empty; the server then uses
the current character.

### Response

The server returns ordered pages, all sharing the same `snapshotId`,
`capturedAt`, and `pageCount`:

```json
{
  "requestId": "friends-1",
  "playerListSnapshot": {
    "type": "PLAYER_LIST_TYPE_FRIEND",
    "status": "PLAYER_LIST_STATUS_SUCCESS",
    "owner": {
      "name": "Someone",
      "worldId": 86,
      "worldName": "Typhon",
      "isPlayer": true,
      "isLocal": true
    },
    "ownerEpoch": "169cdb1edd9b466682a5b9ae14fbfd10",
    "snapshotId": "7d1f0c69e1b94c56a6f4ae6b4a8c5f21",
    "capturedAt": "2026-10-21T12:30:00.000Z",
    "pageIndex": 0,
    "pageCount": 2,
    "players": [
      {
        "contentId": "12345678901234567",
        "name": "Friend One",
        "homeWorldId": 86,
        "homeWorldName": "Typhon",
        "homeDataCenterId": 9,
        "homeDataCenterName": "Materia",
        "currentWorldId": 86,
        "currentWorldName": "Typhon",
        "currentDataCenterId": 9,
        "currentDataCenterName": "Materia",
        "freeCompanyTag": "FC",
        "statusBits": "1",
        "jobId": 19,
        "jobName": "Paladin",
        "territoryId": 129,
        "territoryName": "Limsa Lominsa",
        "grandCompany": 1,
        "languages": 3,
        "mainLanguage": 1,
        "identityUnavailable": false
      }
    ]
  }
}
```

- `pageIndex` is 0-based.
- `pageCount` is at least 1. An empty friend list returns one empty page.
- Assemble pages in order and only use the snapshot after all pages arrive.
- A non-success `status` is a terminal response with `pageCount = 1`.
- The server caches a snapshot for about 5 seconds.
- A request can be cancelled by closing the connection or cancelling the
  request; the server stops sending further pages.

### Status values

| Status | Meaning |
|---|---|
| `PLAYER_LIST_STATUS_SUCCESS` | Complete snapshot page(s) |
| `PLAYER_LIST_STATUS_NOT_LOGGED_IN` | No character is logged in |
| `PLAYER_LIST_STATUS_UNAVAILABLE` | Feature/API/data unavailable |
| `PLAYER_LIST_STATUS_BUSY` | Another refresh is in flight |
| `PLAYER_LIST_STATUS_TIMED_OUT` | Game did not answer within 10 seconds |
| `PLAYER_LIST_STATUS_IDENTITY_CHANGED` | Character or session changed |
| `PLAYER_LIST_STATUS_FAILED` | Invalid or unreadable friend data |

When the API is disabled, the server returns an `Error` with
`ERROR_CODE_FEATURE_DISABLED` instead of a snapshot.

### PlayerInfo

- `statusBits` is the raw `OnlineStatus` bit mask. Clients may interpret it as
  online/offline/mentor/AFK/etc., but should not assume every bit is stable.
- `identityUnavailable` means the **name** could not be resolved, matching the
  game's "unable to retrieve character information" rows. World fields are
  independent: an entry can have a name and current world but no home world
  (typical for friends in another data center), or a world but no name. Do not
  use this flag to discard the whole row.
- `homeWorld*` is the character's home world. `currentWorld*` is the world the
  character is currently on; this is what the game shows in the friend list's
  "Location" column for cross-world/cross-DC friends. Use `currentWorld` when
  it is non-zero and fall back to `homeWorld` otherwise.
- `homeDataCenter*` / `currentDataCenter*` describe the data center of the
  matching world. An id of `0` means unknown and the corresponding name is
  empty. Never display a zero id as a server or data center name.
- `contentId` is only returned to authenticated, paired clients. It may be `0`
  for an entry the game cannot identify; such an entry cannot be used with
  friend presence.

## 3. Friend presence

Presence is a single-friend query:

```json
{
  "requestId": "presence-1",
  "friendPresenceRequest": {
    "contentId": "12345678901234567"
  }
}
```

```json
{
  "requestId": "presence-1",
  "friendPresence": {
    "contentId": "12345678901234567",
    "state": "PRESENCE_STATE_ONLINE",
    "status": "PLAYER_LIST_STATUS_SUCCESS",
    "checkedAt": "2026-10-21T12:31:00.000Z"
  }
}
```

- Only ContentIds present in the current friend snapshot are accepted.
- The request is rejected with `PLAYER_LIST_STATUS_UNAVAILABLE` for any other
  ContentId.
- `PRESENCE_STATE_UNKNOWN` means the game did not return a reliable result
  (for example cross-DC or unresolved).
- `PRESENCE_STATE_OFFLINE` is a point-in-time answer, not a permanent state.
- Results are cached for about 30 seconds.
- The feature is available only when `social.friend_presence` is advertised.

## 4. Capabilities

```text
social.friend_list
social.friend_presence
```

Both are absent unless the user enables them. Linkshell names do not require a
capability; they are part of the normal chat stream.

## 5. Examples

- [Friend list request](../../shared/protocol/examples/player_list_request.json)
- [Friend list snapshot](../../shared/protocol/examples/player_list_snapshot.json)
- [Presence request](../../shared/protocol/examples/friend_presence_request.json)
- [Presence response](../../shared/protocol/examples/friend_presence.json)
