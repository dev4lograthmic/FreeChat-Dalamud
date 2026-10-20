# 发送消息

发送等价于在游戏聊天框输入。服务端负责校验、排队、限流和回执。

## 1. 两种发送模式

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

- 可包含任意游戏命令。
- 最大 **500 UTF-8 字节**；超过返回 `ERROR_CODE_TEXT_TOO_LONG`，不会自动拆分。
- CR/LF 归一化为空格；NUL 拒绝。
- 命令名取第一个 token（`/` 到空格/行尾），大小写不敏感。
- 默认黑名单：

  ```text
  /logout
  /quit
  /shutdown
  /exit
  /delete
  /resign
  ```

  命中返回 `ERROR_CODE_COMMAND_BLOCKED`。
- 全角斜杠 `／` 会按 `/` 归一化后检查，避免绕过黑名单。
- 其他命令由插件 UI 的黑名单配置决定。

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
      "text": "马上来"
    }
  }
}
```

- `channel` 必填。
- `target` 只对 `CHAT_CHANNEL_TELL` 必填。
- 当前实现要求 tell 提供 `target.name` + `target.world_name`；只给 `world_id`
  不能解析为命令前缀。
- 服务端负责生成频道前缀；客户端不需要拼 `/party`、`/tell` 等命令。
- 服务端按 500 UTF-8 字节（含前缀）自动分段；每段保留频道/私聊前缀。

## 2. 频道与前缀

| ChatChannel | 服务端前缀 |
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

频道不可用（不在队、不在 FC 等）由游戏处理；服务端只能报告输入是否提交，不能保证对方收到。

## 3. 队列与限流

- 网络线程只入队；`IFramework.Update` 每帧最多提交一段。
- 默认发送间隔 250 ms；可在插件设置调整。
- 默认限流 4 条/秒，burst 4；可在插件设置调整。
- 超过限流返回：

  ```json
  {
    "sendResult": {
      "status": "SEND_STATUS_RATE_LIMITED",
      "error": "ERROR_CODE_RATE_LIMITED",
      "message": "Send rate limit exceeded."
    }
  }
  ```

- 队列满返回 `ERROR_CODE_QUEUE_FULL`。
- 未登录/角色不可用时返回 `ERROR_CODE_NOT_LOGGED_IN`。

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

| 状态 | 含义 |
|---|---|
| `SEND_STATUS_QUEUED` | 已入队（当前实现通常直接返回 Submitted） |
| `SEND_STATUS_SUBMITTED` | 游戏聊天框接受了输入，不代表送达 |
| `SEND_STATUS_ECHOED` | 观察到游戏回显（协议保留；当前实现未发送） |
| `SEND_STATUS_FAILED` | 游戏 API 失败/异常 |
| `SEND_STATUS_BLOCKED` | 命令黑名单 |
| `SEND_STATUS_RATE_LIMITED` | 限流 |
| `SEND_STATUS_QUEUE_FULL` | 队列满 |
| `SEND_STATUS_CANCELLED` | 取消 |

`SUBMITTED` 不是送达回执；私聊没有已读/送达确认。

## 5. 多段发送

- 结构化发送超过 500 字节时会拆成多段。
- 每段保留频道/私聊前缀。
- 当前实现：所有段提交后返回一个 `Submitted`，带 `partCount`；暂不逐段发送
  `SendResult`。
- 某段失败时后续段不会继续提交。

## 6. 回显关联

游戏回显事件可能带：

```json
{
  "chatEvent": {
    "channel": "CHAT_CHANNEL_ECHO",
    "text": "FreeChat test",
    "originRequestId": "send-2"
  }
}
```

客户端用 `originRequestId` 关联发送请求。当前实现不发送额外的
`SendResult(ECHOED)`；不要假设一定收到该状态。

## 7. SendCancel

```json
{
  "requestId": "cancel-1",
  "sendCancel": {
    "targetRequestId": "send-2"
  }
}
```

当前实现只返回：

```json
{
  "sendResult": {
    "status": "SEND_STATUS_CANCELLED",
    "message": "Only queued sends can be cancelled; this request is already in flight."
  }
}
```

即：尚未实现真正的排队段取消语义；不要把 `SendCancel` 当作可靠撤回。

## 8. 安全与使用建议

- 只发送用户明确要求的内容。
- 不要自动向公开频道、通讯贝或跨服通讯贝广播。
- 不要实现自动回复、防 AFK 或批量刷屏。
- 发送前先检查用户是否登录、目标频道是否可用。
- 生产客户端应显示 `Blocked`/`RateLimited`/`QueueFull` 等状态，而不是静默重试。
