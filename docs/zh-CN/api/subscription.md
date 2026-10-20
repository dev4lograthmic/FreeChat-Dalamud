# 订阅与回放

认证成功后，客户端发送 `Subscribe` 才会收到聊天事件。

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

| 字段 | 说明 |
|---|---|
| `filter` | 可选；空 filter 表示不限制 |
| `sources` | 为空时默认 `POST_HANDLE` |
| `contentMode` | 未指定时默认 `CURRENT` |
| `fromSequence` | `<= 0` 只收实时；`> 0` 先回放该 sequence 之后 |
| `includeOriginal` | 仅 `CURRENT` 时有意义；为 true 时事件附带 `original_*` |

每个连接同一时间只有一个活动订阅；再次发送 `Subscribe` 会替换旧订阅。

## 2. 来源

| 来源 | 含义 |
|---|---|
| `CHAT_SOURCE_PRE_HANDLE` | 其他插件处理前的 `IChatGui.ChatMessage` |
| `CHAT_SOURCE_POST_HANDLE` | 最终显示的 `IChatGui.ChatMessageUnhandled`，推荐默认 |
| `CHAT_SOURCE_STRUCTURED` | `IChatGui.LogMessage`，系统/战斗等结构化日志 |

`PRE_HANDLE` 的消息可能被其他插件修改/抑制；`POST_HANDLE` 更接近玩家最终看到的内容。`STRUCTURED` 与 `POST_HANDLE` 可能重复，客户端应按 `timestamp`、`log_kind` 和内容去重。

## 3. 过滤器

所有条件按 AND 组合：

| 条件 | 语义 |
|---|---|
| `includeCategories` 非空 | 消息分类必须在其中 |
| `excludeCategories` 包含分类 | 丢弃 |
| `includeLogKinds` 非空 | 原始 `log_kind` 必须在其中 |
| `excludeLogKinds` 包含 `log_kind` | 丢弃 |
| `keyword` 非空 | `ChatEvent.text` 必须包含该子串 |
| `regex` 非空 | 必须匹配 `ChatEvent.text` |
| `caseSensitive` | 控制 keyword/regex 大小写 |
| `includeOwn = false` | 丢弃 `outgoing=true` 的消息 |
| `includeBattle = false` | 丢弃战斗分类 |

规则：

- 空 include = 不限制；exclude 优先。
- `includeOwn` 是 optional：未设置时等同 `true`。
- `includeBattle` 默认 `false`。
- regex 使用 `RegexOptions.NonBacktracking`，最大 256 字符，匹配超时 50 ms。
- 非法 regex 返回 `ERROR_CODE_REGEX_INVALID`；过滤器校验失败返回
  `ERROR_CODE_FILTER_INVALID`。
- 过滤在服务端后台执行，不在游戏线程跑正则。
- 修改过滤器不会自动回放历史；需要历史时发送 `ReplayRequest`。

### 分类

常用 `ChatCategory`：

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

收到 `Subscribed` 后才开始接收 `ChatEvent`。

## 5. 回放

`fromSequence > 0`：

1. 服务端发送 `Subscribed`。
2. 按 sequence 升序回放 `sequence > fromSequence` 的事件。
3. 回放完成后继续实时推送。

如果请求过旧：

```json
{
  "historyTruncated": {
    "requestedFromSequence": "1",
    "oldestSequence": "100",
    "latestSequence": "150"
  }
}
```

然后从 `oldestSequence` 开始回放。

### 显式 ReplayRequest

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

- `toSequence <= 0` 表示到 `latest_sequence`。
- `filter` 为空时使用当前订阅 filter；非空时作为本次回放的覆盖。
- 回放不改变实时流的 sequence。

## 6. Unsubscribe

```json
{
  "requestId": "unsub-1",
  "unsubscribe": {}
}
```

取消后不再推送聊天事件；连接仍然保持。

## 7. 会话与 sequence

- `server_id`：安装级稳定 ID。
- `session_id`：本次登录/插件加载生成；登出、重载、重启后变化。
- `message_id`：`server_id:session_id:sequence`。
- `sequence`：每个 session 从 1 开始单调递增。
- 所有被服务端捕获的消息都消耗 sequence，即使客户端 filter 不匹配。
- `SessionEnded` 后应清空旧 session 的 `last_sequence`。

## 8. 示例

- [订阅](../../shared/protocol/examples/subscribe.json)
- [聊天事件 Say](../../shared/protocol/examples/chat_event_say.json)
- [聊天事件 Tell](../../shared/protocol/examples/chat_event_tell.json)
