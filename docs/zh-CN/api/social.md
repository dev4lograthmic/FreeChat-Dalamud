# 社交数据

FreeChat 提供三类只读社交数据：

1. **通讯贝名字**：填充到 `ChatEvent.channel_name`。
2. **好友列表**：`PlayerListRequest` / `PlayerListSnapshot`。
3. **好友在线状态**：只允许查询当前好友快照中的 ContentId。

好友列表和在线状态默认关闭。用户必须在插件设置中启用并重启服务端；只有启用后，
服务端才会声明 `social.friend_list` / `social.friend_presence` 能力。

## 1. 通讯贝名字

不需要新增请求。捕获到通讯贝或跨服通讯贝消息时，服务端会把当前名字填入
`ChatEvent.channel_name`。客户端在为空时可回退显示“通讯贝 N”/“跨服通讯贝 N”。

名字每 5 秒从游戏刷新一次，登出或切换角色时清空。

## 2. 好友列表

### 请求

```json
{
  "requestId": "friends-1",
  "playerListRequest": {
    "type": "PLAYER_LIST_TYPE_FRIEND",
    "pageSize": 32
  }
}
```

| 字段 | 说明 |
|---|---|
| `type` | 当前只支持 `PLAYER_LIST_TYPE_FRIEND` |
| `expectedOwnerKey` | 可选；设置后必须与当前角色键一致 |
| `expectedOwnerEpoch` | 可选；设置后必须与当前 session id 一致 |
| `pageSize` | `0` 使用 32；服务端限制在 1..200 |

`Welcome.session_id` 可直接作为 `expectedOwnerEpoch`。客户端不知道 owner key 时，
两个 expected 字段都可以留空，服务端使用当前角色。

### 响应

服务端按顺序返回分页，所有页共享同一个 `snapshotId`、`capturedAt` 和
`pageCount`：

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
      "contentId": "12345678901234567",
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

- `pageIndex` 从 0 开始。
- `pageCount` 至少为 1；空好友列表也会返回一个空页。
- 客户端应按顺序组装，收齐所有页后再使用快照。
- 非成功 `status` 是终止响应，`pageCount = 1`。
- 服务端会缓存快照约 5 秒。
- 客户端断开或取消请求后，服务端停止发送后续页。

### 状态

| 状态 | 说明 |
|---|---|
| `PLAYER_LIST_STATUS_SUCCESS` | 成功的快照页 |
| `PLAYER_LIST_STATUS_NOT_LOGGED_IN` | 当前未登录 |
| `PLAYER_LIST_STATUS_UNAVAILABLE` | 功能/API/数据不可用 |
| `PLAYER_LIST_STATUS_BUSY` | 已有刷新在进行 |
| `PLAYER_LIST_STATUS_TIMED_OUT` | 游戏 10 秒内未响应 |
| `PLAYER_LIST_STATUS_IDENTITY_CHANGED` | 角色或 session 已变化 |
| `PLAYER_LIST_STATUS_FAILED` | 好友数据无效或读取失败 |

功能关闭时，服务端返回 `Error`，错误码为
`ERROR_CODE_FEATURE_DISABLED`，不会返回快照。

### PlayerInfo

- `statusBits` 是原始 `OnlineStatus` 位掩码。客户端可以解释在线/离线/导师/AFK 等，
  但不要假设所有位都稳定。
- `identityUnavailable` 表示**名字**无法解析，对应游戏里的“无法获得角色情报”行。
  世界字段是独立的：条目可能有名字和当前世界但没有本服（跨大区好友常见），也
  可能有世界但没有名字。不要因为这个标记就丢弃整行。
- `homeWorld*` 是角色本服；`currentWorld*` 是角色当前所在服务器，也就是游戏好友
  列表“所在地”列对跨服/跨大区好友显示的值。`currentWorld` 非 0 时优先使用它，
  否则回退到 `homeWorld`。
- `homeDataCenter*` / `currentDataCenter*` 是对应世界的大区。ID 为 `0` 表示未知，
  对应名称为空。客户端不要把它显示成服务器或大区名。
- `contentId` 只返回给已认证、已配对客户端。游戏无法识别的条目可能为 `0`，
  这类条目不能用于好友在线状态查询。

## 3. 好友在线状态

在线状态是单好友查询：

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

- 只接受当前好友快照中的 ContentId。
- 其他 ContentId 返回 `PLAYER_LIST_STATUS_UNAVAILABLE`。
- `PRESENCE_STATE_UNKNOWN` 表示游戏没有给出可靠结果（例如跨大区或无法解析）。
- `PRESENCE_STATE_OFFLINE` 只是当前查询结果，不是永久状态。
- 结果缓存约 30 秒。
- 只有声明 `social.friend_presence` 时可用。

## 4. 能力

```text
social.friend_list
social.friend_presence
```

只有用户启用后才会声明。通讯贝名字不需要能力，属于普通聊天事件的一部分。

## 5. 示例

- [好友列表请求](../../shared/protocol/examples/player_list_request.json)
- [好友列表快照](../../shared/protocol/examples/player_list_snapshot.json)
- [在线状态请求](../../shared/protocol/examples/friend_presence_request.json)
- [在线状态响应](../../shared/protocol/examples/friend_presence.json)
