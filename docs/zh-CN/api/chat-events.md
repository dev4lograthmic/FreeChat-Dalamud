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
    "chunks": [],
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
| `segments` | 旧版结构化片段；解析失败时可能为空 |
| `chunks` | 推荐的富文本 run；文本和链接元数据在同一个 run 中 |
| `outgoing` | 是否本地玩家发出 |
| `tellPeer` | 私聊对端（`TELL`） |
| `channelName` | 通讯贝/CWLS 名称（若已知） |
| `originRequestId` | 若是某次 `SendRequest` 的游戏回显，带该请求 ID |
| `originalRawSender` | 插件修改前的原始发送者 SeString；与当前阶段不同时存在 |
| `originalRawMessage` | 插件修改前的原始消息 SeString；与当前阶段不同时存在 |
| `originalText` | 插件修改前的原始纯文本；与当前阶段不同时存在 |
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
| `PRE_HANDLE` | `ChatMessage` 阶段；其他插件 handler 可能已修改 |
| `POST_HANDLE` | 最终显示；推荐默认 |
| `STRUCTURED` | `LogMessage` 结构化系统/战斗日志 |

`POST_HANDLE` 与 `STRUCTURED` 可能重复。建议按：

```text
timestamp + log_kind + sender + text
```

做客户端去重。

## 4. Segments（旧版）

`Segment` 使用 oneof 表示片段类型：

| 类型 | 字段 |
|---|---|
| `TextSegment` | `text` + `TextStyle`（前景色、描边、斜体） |
| `IconSegment` | `iconId` |
| `ItemSegment` | 物品 ID、HQ、名称、描述、图标、品级、装备等级、稀有度、类别、禁断孔、禁忌镶嵌、属性 |
| `MapSegment` | 地图 ID、区域 ID、坐标、地名、地图文件名、缩放 |
| `AutoTranslateSegment` | 自动翻译文本 |
| `PlayerSegment` | 玩家名、世界、ContentId |
| `StatusSegment` | 状态 ID、名称、图标 |
| `PartyFinderSegment` | 招募 ID、链接类型 |
| `QuestSegment` | 任务 ID、任务名 |

`segments` 仅为兼容旧客户端保留。它不能表示链接出现在 `text` 的第几个字符；
独立的链接片段和后面的 `TextSegment` 可能都包含显示文本。新客户端应使用
`chunks`。

## 5. Chunks（推荐）

`chunks` 是富文本的权威表示。每个 chunk 是一个可绘制 run。文本 chunk 自带
显示文本，并可携带**只作用于这段文本**的链接元数据：

```json
{
  "chunks": [
    { "text": { "text": "来 " } },
    {
      "text": {
        "text": "利姆萨·罗敏萨 (12.3, 45.6)",
        "map": {
          "mapId": 123,
          "territoryId": 456,
          "x": 12.3,
          "y": 45.6,
          "placeName": "利姆萨·罗敏萨"
        }
      }
    },
    { "text": { "text": " 吧" } }
  ]
}
```

规则：

- 按 `chunks` 顺序渲染。
- `ChatTextChunk.text` 就是精确的显示文本；有链接时，这段文本就是可点击标签。
- `ChatIconChunk.iconId` 是独立图标 run。
- `style` 作用于该 chunk，复用 `TextStyle`。
- `ChatTextChunk.link` 可以是 `item`、`map`、`partyFinder`、`quest`、
  `player`、`status` 或 `autoTranslate`。
- **不需要字符 offset。** 链接位置就是 chunk 的文本。
- `ChatEvent.text` 仍然是搜索/通知用的纯文本兜底。
- 无法取得链接显示文本时，服务端生成可读 fallback（物品名、地名+坐标、
  任务名或数字 ID）。
- 解析失败时 `chunks` 可能为空。客户端按以下顺序回退：
  `chunks` → `segments` → `text` → `rawMessage`。

`PartyFinderSegment` 只保证 `listingId` 和 `linkType`，不包含完整招募详情。
`QuestSegment` 在游戏数据可用时包含任务名。

能力名称：

```text
chat.chunks
chat.chunks.party_finder
chat.chunks.quest
```

## 6. SeString 与原始数据

- `rawSender` / `rawMessage` 是游戏原始 SeString 字节。
- `text` 是服务端提取的纯文本，不保留样式/链接。
- `segments` 和 `chunks` 是服务端解析结果；物品/地图元数据可能因游戏语言和
  数据版本不同。
- `original_*` 表示插件修改前的内容；只有与当前 `text`/`raw_message` 不同时
  才会出现。`text` 始终表示当前阶段（`PRE_HANDLE` 或 `POST_HANDLE`）的内容。
- 解析失败时，服务端仍应发送 `text` + `raw_*`，不阻断事件。
- 其他插件修改消息时：
  - `POST_HANDLE` + `CURRENT`：玩家最终看到的内容。
  - `POST_HANDLE` + `ORIGINAL`：原始内容。
  - `includeOriginal=true` 是旧请求标志；`original_*` 现在会在与当前阶段
    不同时自动出现。

## 7. 回显关联

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
- `SUBMITTED` 是终止结果；服务端还可能向原 session 发送一条尽力而为、非终止的
  `SendResult(ECHOED)`。

## 8. 示例

- [Say 事件](../../shared/protocol/examples/chat_event_say.json)
- [Tell 事件](../../shared/protocol/examples/chat_event_tell.json)
- [地图链接 chunks](../../shared/protocol/examples/chat_event_map.json)
- [SessionEnded](../../shared/protocol/examples/session_ended.json)
