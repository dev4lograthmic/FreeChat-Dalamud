# 聊天事件

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

| 字段 | 说明 |
|---|---|
| `messageId` | `server_id:session_id:sequence` |
| `sequence` | 当前 session 单调递增 |
| `timestamp` | 服务端 UTC 时间 |
| `channel` | 归一化频道 |
| `logKind` | 原始 `XivChatType` / `LogKind`；未知频道时使用 |
| `source` | `PRE_HANDLE` / `POST_HANDLE` / `STRUCTURED` |
| `sender` | 发送者；系统/NPC 可能 `isPlayer=false` |
| `text` | 纯文本，适合搜索/通知 |
| `rawSender` | 原始 SeString 字节 |
| `rawMessage` | 原始 SeString 字节 |
| `segments` | 结构化片段；解析失败时可能为空 |
| `outgoing` | 是否本地玩家发出 |
| `tellPeer` | 私聊对端（`TELL`） |
| `channelName` | 通讯贝/CWLS 名称（若已知） |
| `originRequestId` | 若是某次 `SendRequest` 的游戏回显，带该请求 ID |
| `originalRawSender` | 可选的原始 SeString |
| `originalRawMessage` | 可选的原始 SeString |
| `originalText` | 可选的原始文本 |
| `contentMode` | `CURRENT` 或 `ORIGINAL` |

## 2. 频道与 log_kind

`ChatChannel` 是归一化显示/过滤字段；`logKind` 是游戏原始值。

客户端应：

- 已知频道用 `channel` 显示。
- 未知频道显示为“其他”，用 `logKind` 精确过滤。
- 不要假设 `ChatChannel` 枚举永远完整；游戏补丁可能增加新值。

常见频道：`SAY`、`SHOUT`、`YELL`、`TELL`、`PARTY`、`ALLIANCE`、
`FREE_COMPANY`、`LINKSHELL_1..8`、`CROSS_LINKSHELL_1..8`、
`NOVICE_NETWORK`、`PVP_TEAM`、`SYSTEM`、`ERROR`、`NPC`、`BATTLE`、
`LOOT`、`PROGRESS`、`ECHO`、`EMOTE`、`OTHER`。

## 3. 来源与去重

| 来源 | 说明 |
|---|---|
| `PRE_HANDLE` | 其他插件处理前；可能被后续插件 suppress/修改 |
| `POST_HANDLE` | 最终显示；推荐默认 |
| `STRUCTURED` | `LogMessage` 结构化系统/战斗日志 |

`POST_HANDLE` 与 `STRUCTURED` 可能重复。建议按：

```text
timestamp + log_kind + sender + text
```

做客户端去重。

## 4. Segments

`Segment` 使用 oneof 表示片段类型：

| 类型 | 字段 |
|---|---|
| `TextSegment` | `text` + `TextStyle`（前景色、描边、斜体） |
| `IconSegment` | `iconId` |
| `ItemSegment` | 物品 ID、HQ、名称、描述、图标、等级、品级、稀有度、类别、禁断孔、属性 |
| `MapSegment` | 地图 ID、区域 ID、坐标、地名、地图文件名、缩放 |
| `AutoTranslateSegment` | 自动翻译文本 |
| `PlayerSegment` | 玩家名、世界、ContentId |
| `StatusSegment` | 状态 ID、名称、图标 |

客户端不认识的片段应回退到 `text` 或 `rawMessage`。

## 5. SeString 与原始数据

- `rawSender` / `rawMessage` 是游戏原始 SeString 字节。
- `text` 是服务端提取的纯文本，不保留样式/链接。
- `segments` 是服务端解析结果；物品/地图元数据可能因游戏语言和数据版本不同。
- 解析失败时，服务端仍应发送 `text` + `raw_*`，不阻断事件。
- 其他插件修改消息时：
  - `POST_HANDLE` + `CURRENT`：玩家最终看到的内容。
  - `POST_HANDLE` + `ORIGINAL`：原始内容。
  - `includeOriginal=true`：同时带 `original_*`。

## 6. 回显关联

发送请求后，游戏产生的 outgoing `ChatEvent` 可能带：

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

客户端应使用 `originRequestId` 关联原来的 `SendRequest.request_id`，避免“乐观显示 + 回显”重复。

当前实现说明：

- 服务端会设置 `originRequestId`。
- 服务端暂不额外发送 `SendResult(ECHOED)`；发送回执通常停在 `SUBMITTED`。

## 7. 示例

- [Say 事件](../../shared/protocol/examples/chat_event_say.json)
- [Tell 事件](../../shared/protocol/examples/chat_event_tell.json)
- [SessionEnded](../../shared/protocol/examples/session_ended.json)
