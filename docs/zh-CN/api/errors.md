# 错误与关闭

## 1. Error

```json
{
  "requestId": "send-3",
  "error": {
    "code": "ERROR_CODE_COMMAND_BLOCKED",
    "message": "Command is blocked by server policy.",
    "retryAfterMs": 0
  }
}
```

- `requestId` 关联原请求；推送型错误可以没有。
- 客户端应以 `code` 为准，`message` 仅用于显示/排错。
- `retryAfterMs` 用于限流后的等待提示。

## 2. ErrorCode

| 错误码 | 含义 | 建议 |
|---|---|---|
| `ERROR_CODE_INVALID_REQUEST` | 参数/状态不合法 | 修正请求 |
| `ERROR_CODE_UNAUTHENTICATED` | 未认证 | 重新认证 |
| `ERROR_CODE_PAIRING_REQUIRED` | 需要配对 | 带配对码重试 |
| `ERROR_CODE_PAIRING_INVALID` | 配对码错误 | 检查配对码 |
| `ERROR_CODE_PAIRING_EXPIRED` | 配对码过期 | 重新生成 |
| `ERROR_CODE_PAIRING_RATE_LIMITED` | 配对尝试过多 | 等待后重试 |
| `ERROR_CODE_DEVICE_REVOKED` | 设备被撤销 | 重新配对 |
| `ERROR_CODE_PROTOCOL_MISMATCH` | 协议/子协议不匹配 | 更新客户端 |
| `ERROR_CODE_UNSUPPORTED_ENCODING` | 编码不支持 | 用 protobuf |
| `ERROR_CODE_UNSUPPORTED_VERSION` | 协议版本不支持 | 升级客户端 |
| `ERROR_CODE_NOT_SUBSCRIBED` | 未订阅 | 先 `Subscribe` |
| `ERROR_CODE_FILTER_INVALID` | 过滤器非法 | 检查 filter |
| `ERROR_CODE_REGEX_INVALID` | 正则非法/过长/超时 | 修正 regex |
| `ERROR_CODE_TEXT_TOO_LONG` | raw_text 超过 500 字节 | 缩短文本 |
| `ERROR_CODE_COMMAND_BLOCKED` | 命令黑名单 | 不要重试 |
| `ERROR_CODE_NOT_LOGGED_IN` | 游戏未登录 | 登录后重试 |
| `ERROR_CODE_CHANNEL_UNAVAILABLE` | 频道不可用 | 检查队伍/FC |
| `ERROR_CODE_TELL_TARGET_INVALID` | 私聊目标无效 | 提供 Name@World |
| `ERROR_CODE_QUEUE_FULL` | 发送队列满 | 等待后重试 |
| `ERROR_CODE_RATE_LIMITED` | 触发限流 | 按 `retryAfterMs` 等待 |
| `ERROR_CODE_SLOW_CONSUMER` | 客户端消费太慢 | 减少订阅/过滤 |
| `ERROR_CODE_INTERNAL` | 服务端内部错误 | 记录日志并重连 |
| `ERROR_CODE_SHUTTING_DOWN` | 服务端关闭 | 重连 |
| `ERROR_CODE_HISTORY_UNAVAILABLE` | 历史不可用 | 从 oldest 重新订阅 |
| `ERROR_CODE_FEATURE_DISABLED` | 用户关闭了该功能 | 提示用户开启 |
| `ERROR_CODE_PLAYER_LIST_BUSY` | 好友刷新正在进行 | 稍后重试 |
| `ERROR_CODE_PLAYER_LIST_TIMEOUT` | 游戏未及时响应 | 重试或显示不可用 |

## 3. SendStatus

| 状态 | 含义 |
|---|---|
| `SEND_STATUS_UNSPECIFIED` | 未指定 |
| `SEND_STATUS_QUEUED` | 协议保留；当前不发送 |
| `SEND_STATUS_SUBMITTED` | 终止状态；游戏接受输入，不代表送达 |
| `SEND_STATUS_ECHOED` | 可选的尽力而为回显通知 |
| `SEND_STATUS_FAILED` | 失败 |
| `SEND_STATUS_BLOCKED` | 命令黑名单 |
| `SEND_STATUS_RATE_LIMITED` | 限流 |
| `SEND_STATUS_QUEUE_FULL` | 队列满 |
| `SEND_STATUS_CANCELLED` | 取消 |

## 4. CloseCode

| 关闭码 | 含义 |
|---|---|
| `1000` | 正常关闭 |
| `1001` | 服务端/客户端离开 |
| `1002` | 协议错误 |
| `1003` | 不支持的数据 |
| `1008` | 策略违规 |
| `1009` | 消息过大 |
| `1011` | 内部错误 |
| `1013` | 稍后重试 |
| `4001` | 认证失败 |
| `4002` | 需要配对 |
| `4003` | 设备被撤销 |
| `4004` | 协议/版本不匹配 |
| `4005` | 慢消费者 |
| `4006` | 服务端关闭 |

关闭前服务端会尽量发送对应的 `Error` / `AuthError` / `SessionEnded` /
`ServerShutdown`，但不要依赖关闭帧之前一定收到应用层消息。

## 5. 限流与背压

- 配对：失败 5 次后锁定 5 分钟。
- 发送：默认 4 条/秒，burst 4；可在插件设置调整。
- 客户端出站队列：默认 512 条；慢消费者会被关闭 `4005`。
- 单个慢客户端不应影响其他客户端或游戏线程。
- 客户端应快速读取 WebSocket；UI 卡顿时减少订阅来源、关闭 battle、缩小过滤范围。

## 6. 重试建议

| 情况 | 动作 |
|---|---|
| `UNAUTHENTICATED` | 重新 challenge/配对 |
| `DEVICE_REVOKED` | 重新配对，不要循环重试 |
| `RATE_LIMITED` | 等待 `retryAfterMs` |
| `QUEUE_FULL` | 等待后重试，避免放大 |
| `SESSION_ENDED` / `SERVER_SHUTDOWN` | 重新连接，按新 session 重新订阅 |
| `HISTORY_TRUNCATED` | 从 `oldest_sequence` 重新回放 |
