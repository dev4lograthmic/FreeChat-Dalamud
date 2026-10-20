# FreeChat Protocol v1

> 状态：Phase 0 草案（待评审）
> 版本：1.0.0-draft
> 日期：2026-10-20
> 协议包：`freechat.v1`
> WebSocket 子协议：`freechat.v1.protobuf`（默认）/ `freechat.v1.json`（仅调试）
>
> 语言：[English](../../en-US/protocol/freechat-v1.md) · **简体中文**

---

## 1. 概述

FreeChat 是一个运行在 Dalamud 插件内的**游戏侧服务端**。它把 FFXIV 聊天日志以结构化事件推送给局域网内的客户端，并接受客户端发送文本/命令的请求。

本协议只定义**插件与客户端之间**的 WebSocket 协议，不定义客户端 UI、不定义游戏内数据格式、不定义 XIVChatNext 兼容性。

### 1.1 设计目标

- 多语言客户端（Windows/iOS/Android/macOS/CLI）可用同一份 `.proto` 生成代码。
- 聊天事件信息尽量完整：纯文本 + 原始 SeString + 结构化片段。
- 客户端可按连接订阅/过滤，不把游戏线程拖垮。
- 当前游戏会话内支持断线重连补发；不持久化。
- 发送等价于在游戏聊天框输入，带队列、限流、回执、黑名单。
- WSS + 自签证书 + 公钥指纹 pinning；二维码/深链接完成首次配对。
- 协议可版本化、可做 breaking-change 检查。

### 1.2 非目标

- 浏览器/Web 客户端（v1 不做；未来另立 ADR）。
- 公网 relay / NAT 穿透。
- 多游戏实例/多开。
- 服务端聊天持久化、离线消息。
- 输入框“正在输入”内容。
- 截图、好友、在线状态、物品/地图查询。
- 自动操作角色、防 AFK、自动回复。

---

## 2. 角色与数据流

```
+---------------------+          WSS / WebSocket          +-------------------+
| FFXIV + Dalamud     | <===============================> | Client            |
| FreeChat plugin     |   freechat.v1 (protobuf/json)    | Win/iOS/Android   |
| (game-side server)  |                                  | macOS/CLI         |
+---------------------+                                  +-------------------+
```

**接收：**

```
IChatGui event
  -> game-thread capture (copy raw SeString)
  -> background enrichment (segments)
  -> server backlog (sequence)
  -> per-client filter + queue
  -> WebSocket
```

**发送：**

```
WebSocket SendRequest
  -> validate (length/command/target/rate)
  -> send queue
  -> IFramework.Update (one command per frame)
  -> ProcessChatBoxEntry
  -> SendResult(SUBMITTED)
  -> outgoing ChatEvent -> SendResult(ECHOED)
```

---

## 3. 传输

### 3.1 WebSocket

- 传输：WebSocket over TLS（`wss://`）。
- 默认端口：`14777`。
- 默认只监听 loopback；LAN 必须在插件 UI 显式开启。
- 路径：`/ws`（客户端应接受服务器返回的任意路径，但默认连接 `/ws`）。
- 每个 WebSocket 消息（message，而不是 TCP 分片）承载**一个** `Envelope`。
- 二进制消息用于 protobuf；文本消息仅用于 debug JSON。
- 客户端必须在 HTTP Upgrade 中提供子协议：

```
Sec-WebSocket-Protocol: freechat.v1.protobuf
```

或（仅服务端开启 debug 时）：

```
Sec-WebSocket-Protocol: freechat.v1.json
```

- 服务端选择第一个支持的子协议并在 `101` 响应中回显；不支持则返回 HTTP `400` 或关闭 `1003`。
- 客户端不得在一条 WebSocket 消息里放多个 `Envelope`；服务端也不得把多个 `Envelope` 拼成一条消息。
- 服务端应支持 WebSocket ping/pong；应用层 `Ping`/`Pong` 用于测量延迟，两者可共存。

### 3.2 限制

| 项 | 默认值 | 说明 |
|---|---|---|
| HTTP 请求头 | 8 KiB | 超过直接拒绝 |
| 握手超时 | 5 s | 从 TCP 接受到 `101` |
| 单条 WebSocket 消息 | 1 MiB | 超过关闭 `1009` |
| 连接数 | 32 | 可配置 |
| 并发握手 | 8 | 防止握手耗尽 |
| 发送队列 | 128 条 / 64 KiB（全局） | 每连接 32 条 |
| 客户端发送队列 | 512 条 / 8 MiB | 慢消费者策略见 10.4 |
| 应用层保活 | 20 s | 60 s 无响应断开 |

### 3.3 关闭

- 正常关闭：`1000`。
- 服务端卸载/重启：`ServerShutdown` + `1001` 或 `1012`。
- 认证失败：`AuthError` + `4001`。
- 需要配对：`PairingRequired` + `4002`（或保持连接等待配对，见 5）。
- 设备撤销：`4003`。
- 协议/编码不匹配：`4004`。
- 慢消费者：`4005`。
- 服务端关闭：`4006`。
- 消息过大：`1009`。
- 非预期异常：`1011`。

关闭前服务端应尽量发送对应的 `Error`/`AuthError`/`ServerShutdown`，但不得无限等待。

---

## 4. 安全与配对

### 4.1 TLS

- 服务端使用 **ECDSA P-256 + SHA-256 自签证书**。
- TLS 1.2/1.3；不要求客户端证书。
- 私钥用 Windows DPAPI（CurrentUser）加密后保存在插件配置中。
- 证书有效期建议 10 年；SAN 可包含 `localhost`、主机名、当前 LAN IP，但**身份验证以公钥 pin 为准**，不以 CA/主机名为准。
- 客户端**必须**在 TLS 握手阶段比对 pin；不匹配立即断开，不得发送 `ClientHello`/配对码。
- 浏览器不在 v1 范围；浏览器无法绕过自签证书错误。

### 4.2 公钥指纹

- 公钥编码：P-256 **X9.63 uncompressed point**，65 字节：`0x04 || X(32) || Y(32)`，大端。
- 指纹：`SHA-256(公钥 65 字节)`，32 字节。
- 展示/二维码：64 位小写十六进制字符串（`fp`）。
- 该编码在 iOS/Android/Windows/macOS 上都易于从证书公钥或设备公钥导出。
- pin 公钥而不是整张证书：证书续期/换 SAN 不改变 pin；换密钥才需要重新配对。

### 4.3 配对二维码 / 深链接

插件 UI 显示二维码，内容为：

```
freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
```

可选：

```
&name=<display name>
```

- `host`：LAN IP 或主机名；IPv6 用方括号，如 `[fe80::1]`。
- `sid`：`server_id`，用于客户端显示/去重。
- `fp`：服务端公钥指纹。
- `code`：一次性配对码，6 位数字，5 分钟有效，只能成功使用一次。
- `name`：可选显示名；不要放敏感信息。

桌面客户端可复制同样的深链接，或导入 `freechat-pair.json`：

```json
{
  "v": 1,
  "host": "192.168.1.10",
  "port": 14777,
  "sid": "srv-1a2b",
  "fp": "0ed3a6ab957ff6f59a9630a473d31a7d04fcb46548b89016196980c7e96584f6",
  "code": "482913"
}
```

### 4.4 配对流程

```
Client                                          Server
  |  TCP + TLS（pin fp）                            |
  |----------------------------------------------->|
  |  HTTP Upgrade (freechat.v1.protobuf)           |
  |<-----------------------------------------------|
  |  Envelope{ ClientHello }                       |
  |----------------------------------------------->|
  |                                                | 未知设备
  |  Envelope{ PairingRequired }                   |
  |<-----------------------------------------------|
  |                                                | 用户在插件 UI 读取 code
  |  Envelope{ PairRequest(code, device_pubkey) }  |
  |----------------------------------------------->|
  |                                                | 插件 UI 弹窗确认设备
  |  Envelope{ PairResult(ok, device_id) }         |
  |<-----------------------------------------------|
  |  Envelope{ Welcome }                           |
  |<-----------------------------------------------|
  |  Envelope{ Subscribe }                         |
  |----------------------------------------------->|
```

规则：

- `PairingRequired` **不包含配对码**；配对码只能由用户在插件 UI 读取/扫码。
- 用户必须在插件 UI 确认设备（显示设备名、平台、公钥指纹）。
- 配对成功后，服务端保存 `device_id` + 设备公钥；客户端保存服务端 `fp` + 设备私钥。
- 配对码 5 分钟过期、一次性、失败限流（默认 5 次/分钟/IP，超过锁定 5 分钟）。
- 服务端可随时撤销设备；撤销后该设备的下一次请求返回 `DEVICE_REVOKED` 并关闭 `4003`。

### 4.5 已知设备认证

```
Server -> Challenge { nonce(32), algorithm, expires_at }
Client -> ChallengeResponse { signature }
Server -> Welcome
```

- `algorithm` 固定为 `ECDSA_P256_SHA256_P1363`。
- 签名输入（UTF-8，字段间用 `0x00` 分隔）：

```
"FreeChat/v1 challenge" 0x00 server_id 0x00 device_id 0x00 nonce
```

- 对上述字节做 SHA-256，然后用设备私钥签名；签名格式为 IEEE P1363（64 字节，`r || s`）。
- 服务端用保存的设备公钥验签；失败返回 `AuthError{UNAUTHENTICATED}` 并关闭 `4001`。
- nonce 32 字节随机、60 秒有效、一次性；重放必须拒绝。
- 设备私钥由客户端生成并安全保存（iOS Keychain / Android Keystore / Windows DPAPI / macOS Keychain）。

### 4.6 证书/密钥轮换

- 仅换证书、复用同一密钥：pin 不变，客户端无需重新配对。
- 换密钥：所有设备必须重新配对；插件 UI 应明确警告。
- 设备私钥丢失：删除设备后重新配对。

### 4.7 传输安全模式

| 场景 | 传输 | 说明 |
|---|---|---|
| LAN | `wss://` | 必须 pin；默认 |
| loopback 调试 | `ws://127.0.0.1` | 仅显式开启；不用于 LAN |
| 浏览器 | 不支持 | 自签 WSS 不兼容；未来另立 ADR |

Debug JSON 是**编码**开关，不是传输安全开关；`freechat.v1.json` 仍然跑在 WSS 上。

---

## 5. 握手状态机

状态：

```
CONNECTED
  -> HELLO_RECEIVED
  -> PAIRING_PENDING  (unknown device)
  -> CHALLENGE_PENDING (known device)
  -> AUTHENTICATED
  -> SUBSCRIBED
  -> CLOSED
```

规则：

1. WebSocket 建立后，客户端必须在 5 秒内发送 `ClientHello`。
2. 服务端根据 `device_id` / `device_public_key` 判断：
   - 未知：发送 `PairingRequired`，等待 `PairRequest`（最多 60 秒）。
   - 已知：发送 `Challenge`，等待 `ChallengeResponse`（最多 10 秒）。
3. 认证成功：发送 `Welcome`。
4. 客户端应在收到 `Welcome` 后 10 秒内发送 `Subscribe`；否则服务端可保持连接但不推送聊天，或按配置关闭。
5. 任何认证失败：发送 `AuthError`，关闭对应 close code。
6. 客户端在认证前发送其他消息：`Error{UNAUTHENTICATED}`，可关闭 `4001`。
7. `ClientHello.protocol_version` 不支持：`AuthError{UNSUPPORTED_VERSION}` + `4004`。
8. 服务端登出/角色切换/插件卸载：`SessionEnded`，关闭 1001；客户端应自动重连并在新 session 上重新订阅。

### 5.1 超时

| 阶段 | 超时 |
|---|---|
| WebSocket 握手 | 5 s |
| `ClientHello` | 5 s |
| 配对确认 | 60 s |
| challenge 响应 | 10 s |
| 首条 `Subscribe` | 10 s（可配置） |
| 应用层 ping | 20 s 间隔，60 s 无响应断开 |

---

## 6. Envelope 与消息

所有消息都包在 `Envelope` 中：

```proto
message Envelope {
  string request_id = 1;
  oneof payload { ... }
}
```

- `request_id`：客户端生成的不透明字符串，用于请求/响应关联；服务端在响应中原样回显。
- 推送型消息（`ChatEvent`、`Pong`、`Error`、`RateLimitInfo` 等）可以不带 `request_id`。
- 同一个连接上 `request_id` 应唯一；建议 UUID/ULID。
- 未知 `oneof` 字段：客户端/服务端必须忽略（protobuf 未知字段语义）。
- 未知枚举值：保留原始数字；客户端应降级到 `*_UNSPECIFIED` 或使用 `log_kind`。

### 6.1 消息清单

| 方向 | 消息 | 说明 |
|---|---|---|
| C→S | `ClientHello` | 协议版本/客户端信息/设备公钥/配对码/续传 sequence |
| S→C | `PairingRequired` | 未知设备，等待配对 |
| C→S | `PairRequest` | 配对码 + 设备信息 |
| S→C | `PairResult` | 配对结果 |
| S→C | `Challenge` | 已知设备 nonce |
| C→S | `ChallengeResponse` | 设备签名 |
| S→C | `Welcome` | 认证成功 + session 信息 |
| S→C | `AuthError` | 认证失败 |
| C→S | `Subscribe` | 订阅/过滤/续传 |
| S→C | `Subscribed` | 订阅生效 + 回放范围 |
| C→S | `Unsubscribe` | 取消订阅 |
| C→S | `ReplayRequest` | 显式回放 |
| S→C | `HistoryTruncated` | 请求的 sequence 太旧 |
| S→C | `ChatEvent` | 聊天事件 |
| C→S | `SendRequest` | 发送文本/命令 |
| S→C | `SendResult` | 发送状态 |
| C→S | `SendCancel` | 取消排队中的发送 |
| C→S/S→C | `Ping` / `Pong` | 延迟测量 |
| S→C | `Error` | 请求/连接错误 |
| S→C | `RateLimitInfo` | 限流信息 |
| S→C | `SessionEnded` | 登出/角色切换/插件卸载 |
| S→C | `ServerShutdown` | 服务端关闭 |

---

## 7. 会话与订阅

### 7.1 标识

- `server_id`：安装级稳定 ID（插件首次运行生成）。
- `session_id`：本次游戏登录/插件加载生成；登出/重登/重启后变化。
- `device_id`：客户端安装级稳定 ID。
- `message_id`：`server_id:session_id:sequence`。
- `sequence`：每个 session 从 1 开始单调递增；**所有被服务端捕获的消息**都消耗一个 sequence，与单个客户端的过滤无关。

### 7.2 Subscribe

- 每个连接同一时间只有一个活动订阅；再次 `Subscribe` 会替换旧订阅。
- `sources` 为空：默认 `[CHAT_SOURCE_POST_HANDLE]`。
- `content_mode` 未指定：默认 `CONTENT_MODE_CURRENT`。
- `include_original`：仅当 `content_mode=CURRENT` 时有效；为 true 时事件额外带 `original_*` 字段。
- `from_sequence`：
  - `<= 0`：只收实时。
  - `> 0`：先回放 `sequence > from_sequence` 的消息，再切换实时。
  - 如果 `from_sequence < oldest_sequence - 1`：先发 `HistoryTruncated`，然后从 `oldest_sequence` 开始回放。
- 回放消息同样应用当前 filter。
- `Subscribe` 本身不产生 `ChatEvent`；服务端先发 `Subscribed`，再发回放，再发实时。

### 7.3 过滤语义

`ChatFilter` 条件全部 AND：

1. `include_categories` 非空：消息的 `ChatCategory` 必须在其中。
2. `exclude_categories` 包含消息分类：丢弃。
3. `include_log_kinds` 非空：`ChatEvent.log_kind` 必须在其中。
4. `exclude_log_kinds` 包含 `log_kind`：丢弃。
5. `keyword` 非空：`ChatEvent.text` 必须包含该子串（大小写由 `case_sensitive` 决定）。
6. `regex` 非空：必须匹配 `ChatEvent.text`。
7. `include_own=false`：丢弃 `outgoing=true` 的消息（未设置时默认包含自己发的消息）。
8. `include_battle=false`：丢弃 `ChatCategory.BATTLE`（以及 `source=STRUCTURED` 且分类为战斗的消息）。

规则：

- include 为空 = 不限制；exclude 优先。
- keyword/regex 为空 = 不限制。
- regex 使用 `RegexOptions.NonBacktracking`，最大 256 字符，匹配超时 50 ms；非法/超时返回 `FILTER_INVALID`/`REGEX_INVALID`。
- 过滤在后台线程执行；不得在游戏线程跑正则。
- 过滤器变更不会自动重放历史；需要历史时显式 `ReplayRequest`。

### 7.4 Replay

- `ReplayRequest` 可在已订阅状态下请求一段历史。
- `from_sequence`/`to_sequence` 与 `Subscribe` 语义一致；`to_sequence<=0` 表示到 `latest_sequence`。
- 回放按 sequence 升序；受 `filter`（可选覆盖）约束。
- 超过 buffer 范围：先 `HistoryTruncated`，再从 `oldest_sequence` 开始。
- 回放不改变实时流的 sequence。

### 7.5 会话结束

- 登出：`SessionEnded{reason="logout"}`，关闭 1001。
- 角色切换：`SessionEnded{reason="character_changed"}`，关闭 1001。
- 插件卸载：`SessionEnded{reason="plugin_unloaded"}`，关闭 1001。
- 服务端重启：`ServerShutdown{reason, retry_after_ms}`，关闭 1012/4006。
- 客户端应自动重连；发现 `session_id` 变化时清空本 session 的 last_sequence。

---

## 8. 聊天事件模型

### 8.1 ChatEvent

| 字段 | 说明 |
|---|---|
| `message_id` | 稳定 ID |
| `sequence` | 本 session 单调递增 |
| `timestamp` | 服务器 UTC 时间 |
| `channel` | 归一化频道 |
| `log_kind` | 原始 XivChatType/LogKind；未知频道时使用 |
| `source` | `PRE_HANDLE` / `POST_HANDLE` / `STRUCTURED` |
| `sender` | 发送者身份；系统/NPC 可能 `is_player=false` |
| `text` | 纯文本，适合搜索/通知 |
| `raw_sender` | 原始 SeString 字节 |
| `raw_message` | 原始 SeString 字节 |
| `segments` | 结构化片段；解析失败时为空，但 text/raw 仍有效 |
| `outgoing` | 是否本地玩家发出 |
| `tell_peer` | 私聊对端（TELL） |
| `channel_name` | 通讯贝/CWLS 名称（若已知） |
| `origin_request_id` | 若是某次 SendRequest 的游戏回显，带该请求 ID |
| `original_*` | 可选，原始内容（见 7.2） |
| `content_mode` | 本事件字段代表 current 还是 original |

### 8.2 频道

`ChatChannel` 是归一化枚举；`log_kind` 是原始值。客户端应：

- 已知频道用 `channel` 显示/过滤；
- 未知频道显示为“其他”，并用 `log_kind` 做精确过滤；
- 不要把 `ChatChannel` 当作穷举列表。

### 8.3 数据来源

- `PRE_HANDLE`：`IChatGui.ChatMessage`，其他插件处理前；可能被后续插件 suppress。
- `POST_HANDLE`：`IChatGui.ChatMessageUnhandled`，最终显示；推荐默认。
- `STRUCTURED`：`IChatGui.LogMessage`，结构化战斗/系统日志；可能与 `POST_HANDLE` 重复，客户端按 `timestamp`/`log_kind`/内容去重。

### 8.4 片段

`Segment` 的 `oneof`：

- `TextSegment`：文本 + `TextStyle`（前景色/描边/斜体）。
- `IconSegment`：游戏图标 ID。
- `ItemSegment`：物品 ID/HQ/名称/描述/图标/等级/品级/稀有度/类别/禁断孔/属性。
- `MapSegment`：地图 ID/区域/坐标/地名/地图文件名/缩放。
- `AutoTranslateSegment`：自动翻译文本。
- `PlayerSegment`：玩家名/世界/ContentId。
- `StatusSegment`：状态 ID/名称/图标。

客户端不认识的片段应回退到 `ChatEvent.text` 或 `raw_message`。

### 8.5 SeString 与文本

- `raw_sender`/`raw_message` 是游戏原始 SeString 字节；客户端可用游戏数据自行解析。
- `text` 是服务端提取的纯文本；不保证保留样式/链接。
- `segments` 是服务端解析结果；物品/地图元数据可能因游戏语言/数据版本不同。
- 服务端解析失败时只发 `text` + `raw_*`，不阻断消息。
- 其他插件修改过消息时：
  - `POST_HANDLE` + `CURRENT`：玩家最终看到的内容。
  - `POST_HANDLE` + `ORIGINAL`：原始内容（`OriginalSender`/`OriginalMessage`）。
  - `include_original=true`：同时带 `original_*`。

---

## 9. 发送

### 9.1 SendRequest

两种模式：

```proto
message SendRequest {
  oneof content {
    string raw_text = 1;
    ChatSend chat = 2;
  }
}
```

#### raw_text

- 等价于在游戏聊天框输入，可包含 `/command`。
- 最大 500 UTF-8 字节；超过返回 `TEXT_TOO_LONG`（不自动拆分，避免拆坏命令）。
- CR/LF 归一化为空格；NUL 拒绝。
- 命令名解析：第一个 token（`/` 后到空格/行尾），大小写不敏感。
- 默认黑名单：`/logout`、`/quit`、`/shutdown`、`/exit`、`/delete`、`/resign`；命中返回 `COMMAND_BLOCKED`。
- 其他命令由用户在插件 UI 中添加/移除。

#### ChatSend

- `channel` + 可选 `target` + `text`。
- 服务端负责拼前缀（如 `/party`、`/tell Name@World`），客户端不需要记命令。
- `CHAT_CHANNEL_TELL` 必须提供 `target.name` + `target.world_name`/`world_id`；否则 `TELL_TARGET_INVALID`。
- 服务端按 500 UTF-8 字节（含前缀）自动分段；`part_count`/`part_index` 报告。
- 分段保持前缀；私聊每段都带完整 `Name@World`。
- 频道不可用（不在队/不在 FC 等）由游戏返回错误；服务端只能报告 `SUBMITTED`，并把游戏错误作为普通 `ChatEvent` 推回。

### 9.2 发送队列与限流

- 网络线程只入队；`IFramework.Update` 每帧最多执行一条。
- 队列：全局 128 条 / 64 KiB；每连接 32 条。
- 限流建议默认：
  - 全局 4 条/秒，burst 4。
  - 公开频道/私聊 1 条/秒。
  - 可配置。
- 超过限流：`SendResult{RATE_LIMITED}` + `Error{RATE_LIMITED, retry_after_ms}`。
- 队列满：`SendResult{QUEUE_FULL}`。
- 发送前校验登录状态/角色/session；不满足返回 `NOT_LOGGED_IN`/`CHANNEL_UNAVAILABLE`。

### 9.3 回执状态机

```
QUEUED -> SUBMITTED -> ECHOED
   |          |
   +-> FAILED / BLOCKED / RATE_LIMITED / QUEUE_FULL / CANCELLED
```

- `QUEUED`：已入队，带 `queue_position`。
- `SUBMITTED`：`ProcessChatBoxEntry` 调用成功（游戏接受输入，不代表对方收到）。
- `ECHOED`：观察到对应的 outgoing `ChatEvent`；带 `origin_message_id`。
- `FAILED`：游戏 API 不可用/异常。
- `BLOCKED`：命令黑名单。
- `RATE_LIMITED`/`QUEUE_FULL`：限流/队列满。
- `CANCELLED`：`SendCancel` 或前一段失败导致后续取消。
- 多段发送：每段独立 `SendResult`；`part_index` 从 0 开始；第一段失败时后续标记 `CANCELLED`。
- tell 没有送达回执；`ECHOED` 只代表游戏聊天框接受了这条消息。

### 9.4 回显关联

- 服务端提交发送后，记录待匹配项（频道、目标、文本、时间窗口）。
- 收到 outgoing `ChatEvent` 且匹配时：
  - 在该事件上设置 `origin_request_id`；
  - 发送 `SendResult{ECHOED, origin_message_id}`。
- 匹配窗口建议 5 秒；超时只保留 `SUBMITTED`，不再发 `ECHOED`。
- 客户端应使用 `origin_request_id` 去重，避免“乐观显示 + 回显”重复。

### 9.5 SendCancel

- `SendCancel.target_request_id` 指向要取消的 `SendRequest` 的 `Envelope.request_id`。
- 只能取消尚未提交的排队段；已提交的无法撤回。
- 取消成功：对应段 `SendResult{CANCELLED}`。

---

## 10. 错误、限流、背压

### 10.1 Error

```proto
message Error {
  ErrorCode code = 1;
  string message = 2;
  uint32 retry_after_ms = 3;
}
```

- `Envelope.request_id` 关联请求；推送型错误可无 `request_id`。
- `message` 仅供人读；客户端应以 `code` 为准。

### 10.2 错误码

| 错误码 | 含义 | 可重试 |
|---|---|---|
| `INVALID_REQUEST` | 参数/状态不合法 | 否 |
| `UNAUTHENTICATED` | 未认证 | 重新认证 |
| `PAIRING_REQUIRED` | 需要配对 | 是（带码） |
| `PAIRING_INVALID` | 配对码错误 | 是（限流） |
| `PAIRING_EXPIRED` | 配对码过期 | 重新生成 |
| `PAIRING_RATE_LIMITED` | 配对尝试过多 | 是（等） |
| `DEVICE_REVOKED` | 设备被撤销 | 否（重新配对） |
| `PROTOCOL_MISMATCH` | 协议/子协议不匹配 | 否 |
| `UNSUPPORTED_ENCODING` | JSON debug 未开启/编码不支持 | 否 |
| `UNSUPPORTED_VERSION` | 协议版本不支持 | 否 |
| `NOT_SUBSCRIBED` | 未订阅 | 先订阅 |
| `FILTER_INVALID` | 过滤器非法 | 否 |
| `REGEX_INVALID` | 正则非法/超时 | 否 |
| `TEXT_TOO_LONG` | 超过 500 字节（raw_text） | 否 |
| `COMMAND_BLOCKED` | 命令黑名单 | 否 |
| `NOT_LOGGED_IN` | 游戏未登录 | 是 |
| `CHANNEL_UNAVAILABLE` | 频道不可用 | 可能 |
| `TELL_TARGET_INVALID` | 私聊目标无效 | 否 |
| `QUEUE_FULL` | 发送队列满 | 是（等） |
| `RATE_LIMITED` | 触发限流 | 是（等） |
| `SLOW_CONSUMER` | 客户端消费太慢 | 否（被断开） |
| `INTERNAL` | 服务端内部错误 | 可能 |
| `SHUTTING_DOWN` | 服务端关闭 | 是（重连） |
| `HISTORY_UNAVAILABLE` | 请求的历史不可用 | 从 oldest 重新订阅 |

### 10.3 限流

- 配对：5 次/分钟/IP；失败锁定 5 分钟。
- 连接：32 个并发；8 个并发握手。
- 发送：见 9.2。
- 聊天推送：不主动限流；通过每客户端队列和慢消费者策略保护。
- 触发限流时发送 `RateLimitInfo` + `Error`。

### 10.4 背压与慢消费者

- 每客户端独立有界队列（512 条 / 8 MiB）。
- 队列满时：
  1. 优先丢弃 `BATTLE`/`STRUCTURED` 低优先级消息；
  2. 仍满则关闭该连接 `4005 SLOW_CONSUMER`；
  3. 不影响其他客户端。
- 服务端不得为慢客户端阻塞游戏线程或其他客户端。
- 客户端应尽快读取；如果 UI 卡顿，应减少订阅（关闭 battle、缩小过滤）。

---

## 11. 版本与能力

### 11.1 版本

- `ClientHello.protocol_version = 1`。
- 包名 `freechat.v1`；WebSocket 子协议 `freechat.v1.protobuf` / `freechat.v1.json`。
- 只做加法：新增字段/消息/枚举值；不改变已有字段语义。
- 删除字段必须 `reserved`；不复用字段号。
- `buf breaking` 在 CI 中对 main 检查。
- 破坏性变更 -> `freechat.v2` + 新子协议。

### 11.2 能力

`ServerCapabilities.features` / `ClientCapabilities.features` 使用点分字符串：

```
chat.segments
chat.raw
chat.original
chat.sources.pre_handle
chat.sources.post_handle
chat.sources.structured
filters.regex
replay.sequence
send.raw
send.structured
send.cancel
send.echo
pairing.qr
pairing.device_keys
tls.pinning.sha256
```

- 客户端应只使用双方都声明支持的能力。
- 服务端在 `Welcome.enabled_features` 中返回本次连接启用的能力子集。
- 未知能力忽略。

---

## 12. proto3 JSON（仅调试）

- 仅当插件 UI 开启 `Enable JSON debug transport` 时，服务端接受 `freechat.v1.json`。
- 一条 WebSocket 文本消息 = 一个 `Envelope` 的 proto3 JSON。
- 使用 protobuf 标准 JSON 映射：
  - 字段名 lowerCamelCase（`request_id` -> `requestId`）。
  - `int64`/`uint64` 输出为字符串（如 `"42"`）。
  - `bytes` 输出为 base64。
  - 枚举输出为名字（如 `"CHAT_CHANNEL_SAY"`）。
  - `google.protobuf.Timestamp` 输出 RFC3339（如 `"2026-10-20T12:34:56.789Z"`）。
  - 默认值通常省略；客户端解析时应接受缺失字段。
- 服务端/客户端应同时接受 lowerCamelCase 和原始 snake_case 字段名（Google.Protobuf JsonParser 支持）。
- JSON 模式与 protobuf 模式数据完全一致；不支持 `Any`。
- 生产环境默认关闭；开启时插件日志应给出警告。

---

## 13. 示例

以下均为 proto3 JSON（debug）示例；protobuf 二进制字段相同。

### 13.1 ClientHello

```json
{
  "requestId": "c-1",
  "clientHello": {
    "protocolVersion": 1,
    "clientName": "FreeChat CLI",
    "clientVersion": "0.1.0",
    "platform": "cli",
    "deviceId": "dev-7f3c2a",
    "devicePublicKey": "BAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=",
    "pairingCode": "482913",
    "resumeFromSequence": "0",
    "capabilities": ["chat.segments", "filters.regex"]
  }
}
```

### 13.2 PairingRequired

```json
{
  "pairingRequired": {
    "server": {
      "serverId": "srv-1a2b",
      "serverName": "FreeChat",
      "character": {
        "name": "Someone",
        "worldId": 86,
        "worldName": "Typhon",
        "contentId": "12345678901234567",
        "isPlayer": true,
        "isLocal": true
      },
      "protocolVersion": 1,
      "serverVersion": "0.1.0",
      "publicKeyFingerprint": "DtOmq5V/9vWaljCkc9MafQT8tGVIuJAWGWmAx+llhPY=",
      "capabilities": {
        "features": ["chat.segments", "filters.regex"],
        "maxMessageBytes": 1048576,
        "maxClients": 32,
        "maxSendQueue": 128,
        "backlogMessages": 5000,
        "backlogBytes": 33554432
      }
    },
    "reason": "unknown_device",
    "codeLength": 6,
    "expiresAt": "2026-10-20T12:40:00Z"
  }
}
```

### 13.3 PairRequest

```json
{
  "requestId": "c-2",
  "pairRequest": {
    "code": "482913",
    "deviceName": "My iPhone",
    "platform": "ios",
    "devicePublicKey": "BAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=",
    "capabilities": { "features": ["chat.segments"] }
  }
}
```

### 13.4 Challenge / ChallengeResponse

```json
{
  "challenge": {
    "nonce": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
    "algorithm": "ECDSA_P256_SHA256_P1363",
    "expiresAt": "2026-10-20T12:35:00Z"
  }
}
```

```json
{
  "requestId": "c-3",
  "challengeResponse": {
    "signature": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw=="
  }
}
```

### 13.5 Welcome

```json
{
  "welcome": {
    "server": { "serverId": "srv-1a2b", "protocolVersion": 1 },
    "sessionId": "sess-9f8e",
    "oldestSequence": "1",
    "latestSequence": "42",
    "deviceId": "dev-7f3c2a",
    "enabledFeatures": ["chat.segments", "filters.regex"]
  }
}
```

### 13.6 Subscribe

```json
{
  "requestId": "c-4",
  "subscribe": {
    "filter": {
      "includeCategories": ["CHAT_CATEGORY_PLAYER_CHAT", "CHAT_CATEGORY_TELL"],
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

### 13.7 ChatEvent (Say)

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
      "isPlayer": true,
      "isLocal": false
    },
    "text": "Hello",
    "rawSender": "AikDAQIDSGVsbG8=",
    "rawMessage": "AikDAQIDSGVsbG8=",
    "segments": [
      {
        "style": { "foregroundRgba": 4294967295, "glowRgba": 0, "italic": false },
        "text": { "text": "Hello" }
      }
    ],
    "outgoing": false,
    "contentMode": "CONTENT_MODE_CURRENT"
  }
}
```

### 13.8 ChatEvent (Tell)

```json
{
  "chatEvent": {
    "messageId": "srv-1a2b:sess-9f8e:43",
    "sequence": "43",
    "timestamp": "2026-10-20T12:35:10Z",
    "channel": "CHAT_CHANNEL_TELL",
    "logKind": 13,
    "source": "CHAT_SOURCE_POST_HANDLE",
    "sender": { "name": "Friend", "worldId": 86, "worldName": "Typhon", "isPlayer": true },
    "text": "Hello",
    "rawMessage": "AikDAQIDSGVsbG8=",
    "segments": [],
    "outgoing": false,
    "tellPeer": { "name": "Friend", "worldId": 86, "worldName": "Typhon", "isPlayer": true },
    "contentMode": "CONTENT_MODE_CURRENT"
  }
}
```

### 13.9 SendRequest (raw)

```json
{
  "requestId": "send-1",
  "sendRequest": { "rawText": "/p 等一下" }
}
```

### 13.10 SendRequest (structured)

```json
{
  "requestId": "send-2",
  "sendRequest": {
    "chat": {
      "channel": "CHAT_CHANNEL_TELL",
      "target": { "name": "Friend", "worldId": 86, "worldName": "Typhon", "isPlayer": true },
      "text": "马上来"
    }
  }
}
```

### 13.11 SendResult

```json
{
  "requestId": "send-2",
  "sendResult": {
    "status": "SEND_STATUS_SUBMITTED",
    "partIndex": 0,
    "partCount": 1
  }
}
```

```json
{
  "requestId": "send-2",
  "sendResult": {
    "status": "SEND_STATUS_ECHOED",
    "partIndex": 0,
    "partCount": 1,
    "originMessageId": "srv-1a2b:sess-9f8e:44"
  }
}
```

### 13.12 Error

```json
{
  "requestId": "send-3",
  "error": {
    "code": "ERROR_CODE_COMMAND_BLOCKED",
    "message": "Command /logout is blocked by server policy."
  }
}
```

### 13.13 SessionEnded

```json
{
  "sessionEnded": {
    "reason": "logout",
    "endedAt": "2026-10-20T13:00:00Z"
  }
}
```

---

## 14. 一致性测试

Phase 0 提供 JSON 用例；Phase 1 生成 protobuf 二进制向量。

必须覆盖：

1. 子协议协商：protobuf / json / 不支持。
2. 未知设备：`ClientHello` -> `PairingRequired`。
3. 错误 pin：TLS 阶段断开，不发送 `ClientHello`。
4. 配对：`PairRequest` -> `PairResult(ok)` -> `Welcome`。
5. 已知设备：`Challenge` -> `ChallengeResponse` -> `Welcome`。
6. 错误签名/过期 nonce：`AuthError`。
7. `Subscribe` -> `Subscribed`，含 `from_sequence`。
8. 过滤：include/exclude/keyword/regex/自己/战斗。
9. 回放：正常、`HistoryTruncated`、`from_sequence` 超过 latest。
10. 发送：raw/structured/tell/多段/`QUEUED`/`SUBMITTED`/`ECHOED`。
11. 黑名单：`/logout` -> `COMMAND_BLOCKED`。
12. 限流/队列满：`RATE_LIMITED`/`QUEUE_FULL`。
13. `SendCancel`：取消排队段。
14. `Ping`/`Pong` 延迟。
15. 设备撤销：`DEVICE_REVOKED` + `4003`。
16. JSON 与 protobuf 同一消息可互转。
17. 未知字段/未知枚举值不崩溃。
18. 超过 1 MiB 消息 -> `1009`。
19. 慢消费者 -> `4005`。
20. 登出/插件卸载 -> `SessionEnded` + 1001。

---

## 15. 安全与隐私

- 聊天内容不写日志、不落盘、不上传。
- 设备存储只保存 `device_id`、名称、平台、公钥、创建/最后 seen、撤销状态；不保存聊天。
- 配对二维码只显示在插件 UI；不要通过聊天/邮件发送。
- 一次性配对码 5 分钟、一次性、限流。
- 客户端必须 pin 精确公钥指纹；不得 trust-all。
- 服务端默认只监听 loopback；LAN 必须显式开启 WSS。
- Debug JSON 默认关闭；开启时日志只记录“已启用”，不记录消息内容。
- 正则使用 NonBacktracking + 超时，避免 DoS。
- 命令黑名单默认拦截账号/会话类；其他命令由用户配置。
- 服务端不执行自动操作/防 AFK/自动回复。

---

## 16. 未决问题

1. `ChatMessageUnhandled` 与 `LogMessage` 的实际覆盖/重复关系，需 Phase 1 spike。
2. 战斗日志默认关闭；回放是否包含战斗日志待定。
3. 频道前缀命令的精确别名（`/l1` vs `/linkshell1` 等）需 Phase 1 实测。
4. `ChatSend` 多段发送的失败回滚策略需在实现前确认。
5. AGPL-3.0 对第三方协议生态的影响；是否给 `.proto`/文档单独许可。
6. 未来浏览器支持的安全层方案（本地 CA/relay/应用层加密）。
7. 多实例/多开、relay、截图/好友等后续能力。

---

## 附录 A：默认命令黑名单

```
/logout
/quit
/shutdown
/exit
/delete
/resign
```

- 仅作用于 `raw_text` 的命令路径；`ChatSend` 只生成安全前缀。
- 大小写不敏感；命令名取 `/` 后到第一个空格/行尾。
- 用户可在插件 UI 中增删；服务端应记录最终生效列表。

## 附录 B：频道到前缀（实现细节）

`ChatSend` 的频道前缀由服务端维护，不在客户端硬编码。建议：

| ChatChannel | 前缀 |
|---|---|
| SAY | `/say ` |
| SHOUT | `/shout ` |
| YELL | `/yell ` |
| TELL | `/tell <Name@World> ` |
| PARTY | `/party ` |
| ALLIANCE | `/alliance ` |
| FREE_COMPANY | `/freecompany ` |
| LINKSHELL_1..8 | `/l1 `..`/l8 `（待实测） |
| CROSS_LINKSHELL_1..8 | `/cwl1 `..`/cwl8 `（待实测） |
| NOVICE_NETWORK | `/n `（待实测） |
| PVP_TEAM | `/pvpteam `（待实测） |

Phase 1 必须用当前游戏版本验证别名；wire 协议只暴露 `ChatChannel`，前缀变化不影响客户端。

## 附录 C：签名与密钥编码

- 公钥：P-256 X9.63 uncompressed point，65 字节。
- 指纹：SHA-256(65 字节公钥)。
- 设备签名：ECDSA P-256 + SHA-256，IEEE P1363，64 字节。
- 签名输入：`"FreeChat/v1 challenge" 0x00 server_id 0x00 device_id 0x00 nonce`。
- nonce：32 字节随机，60 秒有效，一次性。
- 配对码：6 位数字，5 分钟有效，一次性。
