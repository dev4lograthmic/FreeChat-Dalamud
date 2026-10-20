# FreeChat v1 一致性测试

[English](../../en-US/protocol/conformance.md)

本文档跟踪协议一致性用例，以及当前自动化和实机测试状态。

## 当前状态

- [x] `.proto` 可编译（`buf build`）。
- [x] `buf lint` 通过。
- [x] JSON 示例通过校验（`python3 scripts/validate_examples.py`）。
- [x] protobuf/JSON 等价性（`ConformanceTests`）。
- [ ] 固定二进制 protobuf 向量。
- [x] 自动化客户端/服务端集成测试（配对、challenge、订阅、回放、JSON 调试）。
- [ ] 用 CLI 对游戏实例做完整实机一致性运行。
- [ ] `buf breaking` CI 检查（首个协议提交进入 base branch 后启用）。

## 如何运行

```bash
buf lint
buf build
python3 scripts/validate_examples.py
```

## 用例

| ID | 用例 | 预期 | 状态 |
|---|---|---|---|
| C-01 | WebSocket 子协议协商 | 服务端选择 `freechat.v1.protobuf`；不支持时 400/1003 | 部分（protobuf + JSON 已测） |
| C-02 | 未知设备 | `ClientHello` -> `PairingRequired`（payload 不含配对码） | 通过（集成） |
| C-03 | 错误 TLS pin | 客户端在 `ClientHello` 前断开 | 通过（集成） |
| C-04 | 配对成功 | `PairRequest` -> `PairResult(ok)` -> `Welcome` | 通过（集成） |
| C-05 | 已知设备认证 | `Challenge` -> `ChallengeResponse` -> `Welcome` | 通过（集成） |
| C-06 | 错误 challenge 签名 | `AuthError{UNAUTHENTICATED}` + 4001 | TODO |
| C-07 | 过期/重放 nonce | `AuthError{UNAUTHENTICATED}` | TODO |
| C-08 | 订阅 + 回放 | `Subscribed` 后按 sequence 顺序收到 `ChatEvent` | 通过（集成） |
| C-09 | 回放过旧 | `HistoryTruncated` 后从 `oldest_sequence` 回放 | TODO |
| C-10 | include/exclude 过滤 | 被排除的频道不投递 | 通过（集成） |
| C-11 | keyword/regex 过滤 | 只投递匹配的 `text` | TODO |
| C-12 | 关闭自己消息 | `include_own=false` 时不投递 `outgoing=true` | TODO |
| C-13 | 关闭战斗日志 | `include_battle=false` 时不投递 `CHAT_CATEGORY_BATTLE` | TODO |
| C-14 | raw 发送 | `QUEUED` -> `SUBMITTED` -> `ECHOED`，带 `origin_message_id` | 部分（`SUBMITTED` + echo tag） |
| C-15 | 结构化私聊 | `CHAT_CHANNEL_TELL` + target -> 正确前缀 | TODO |
| C-16 | 多段发送 | `part_count`/`part_index` 正确；失败取消后续 | TODO |
| C-17 | 命令黑名单 | `/logout` -> `SEND_STATUS_BLOCKED` + `COMMAND_BLOCKED` | TODO |
| C-18 | 限流 | `RATE_LIMITED` + `retry_after_ms` | TODO |
| C-19 | 队列满 | `QUEUE_FULL` | TODO |
| C-20 | 取消发送 | 排队段 -> `CANCELLED`；已提交段不受影响 | TODO |
| C-21 | 设备撤销 | `DEVICE_REVOKED` + 4003 | TODO |
| C-22 | 慢消费者 | 关闭 4005；其他客户端不受影响 | TODO |
| C-23 | 超大消息 | 关闭 1009 | TODO |
| C-24 | JSON/protobuf 等价 | 同一信封两种编码可互转 | 通过（`ConformanceTests`） |
| C-25 | 未知字段/枚举 | 不崩溃；未知枚举优雅降级 | TODO |
| C-26 | 登出/插件卸载 | `SessionEnded` + 1001 | TODO |
| C-27 | Ping/Pong | nonce 回显；可测延迟 | 通过（集成） |

## 说明

- JSON 示例位于 `docs/shared/protocol/examples/`。
- 二进制向量必须从同一份 `.proto` 生成，不要手写两套 schema。
- Phase 1 必须先验证 `IChatGui` 事件覆盖范围，再勾选聊天来源相关用例。
- `FreeChat.Cli` 是可执行的参考客户端；见 [CLI](../development/cli.md)。
