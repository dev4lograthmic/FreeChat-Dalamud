# FreeChat CLI

CLI 是 FreeChat v1 协议的小型参考客户端，用于测试和排错，不是产品客户端。
支持配对、订阅、发送、ping 和 envelope 解码。

源码：`src/FreeChat.Cli/`；可执行文件：`freechat`。

## 构建与运行

```powershell
dotnet build src\FreeChat.Cli\FreeChat.Cli.csproj -c Release
dotnet run --project src\FreeChat.Cli\FreeChat.Cli.csproj -- --help
```

输出 DLL：`src\FreeChat.Cli\bin\Release\net10.0\freechat.dll`。

## 获取配对链接

打开插件 **FreeChat → 配对**，复制深链接：

```text
freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
```

链接包含主机、端口、server id、公钥指纹和一次性配对码。CLI 用 `--uri`
接收，不需要分别复制这些字段。

## 配对

```powershell
dotnet run --project src\FreeChat.Cli -- pair --uri "<深链接>"
```

插件必须正在运行，客户端必须能访问到它。在插件 **配对** 标签里确认请求。
CLI 会把设备密钥和 server id 保存到状态文件，并打印 device id。

也可以显式传字段：

```powershell
dotnet run --project src\FreeChat.Cli -- pair `
  --host 127.0.0.1 --port 14777 `
  --fingerprint <64 hex> --code <6 digits>
```

指纹是必需的，它是服务端原始 P-256 公钥的 SHA-256，不是证书哈希。
指纹错误会在发送 `ClientHello` 之前中止 TLS 连接。

## 订阅

配对后：

```powershell
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<深链接>"
```

实时打印聊天，直到 `Ctrl+C`。链接里的配对码在设备已受信任后不再需要，
但保留也无害。

过滤与回放：

```powershell
# 只订阅 post 来源，按关键字过滤，排除自己的消息
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<链接>" `
  --sources post --keyword "raid" --exclude-own

# 从 sequence 41 之后回放
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<链接>" --from 41

# 收到 10 条后退出（方便脚本）
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<链接>" --count 10
```

选项：

| 选项 | 含义 |
|---|---|
| `--sources post,pre,structured,all` | 来源选择，默认 `post` |
| `--keyword text` | 大小写敏感的子串过滤 |
| `--regex pattern` | 非回溯正则过滤 |
| `--exclude-own` | 排除自己发出的消息 |
| `--include-battle` | 包含战斗事件 |
| `--from sequence` | 回放该 sequence 之后的消息 |
| `--count n` | 收到 `n` 条 `ChatEvent` 后退出 |

## 发送

原始文本或命令：

```powershell
dotnet run --project src\FreeChat.Cli -- send --uri "<链接>" --raw "/echo FreeChat CLI"
```

结构化频道：

```powershell
dotnet run --project src\FreeChat.Cli -- send --uri "<链接>" `
  --channel party --text "Hello from the CLI"

dotnet run --project src\FreeChat.Cli -- send --uri "<链接>" `
  --channel tell --target "Friend@Typhon" --text "Hi"
```

频道名：`say`、`party`、`shout`、`yell`、`alliance`、`fc`、
`ls1`…`ls8`、`cwls1`…`cwls8`、`novice`、`pvp`、`tell`。

`--raw` 最多 500 UTF-8 字节，并受服务端命令黑名单限制。`--channel`/`--text`
由服务端按每段 500 字节拆分。`SUBMITTED` 只表示游戏接受了输入，不代表对方
已经收到。

## Ping

```powershell
dotnet run --project src\FreeChat.Cli -- ping --uri "<链接>"
```

## 解码 envelope

```powershell
# 从文件读取 protobuf
dotnet run --project src\FreeChat.Cli -- decode --input message.bin

# Base64 protobuf
dotnet run --project src\FreeChat.Cli -- decode --base64 "<base64>"

# Proto3 JSON 示例
dotnet run --project src\FreeChat.Cli -- decode `
  --input docs\shared\protocol\examples\chat_event_say.json --encoding json
```

输出为格式化的 proto3 JSON。int64 字段按 proto3 JSON 规范输出为字符串。

## JSON 调试传输

插件的 JSON 调试开关打开后，服务端才接受 `freechat.v1.json`：

```powershell
dotnet run --project src\FreeChat.Cli -- pair --uri "<链接>" --json
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<链接>" --json --print-json
```

- `--json`：使用 JSON 子协议而不是 protobuf。
- `--print-json`：把收到的 envelope 以 proto3 JSON 打印，而不是人类可读摘要。

## 状态文件

默认：

```text
<ApplicationData>/FreeChat/cli-state.json
```

可用 `--state <path>` 覆盖。文件包含 device id、P-256 私钥、设备名和
server id。Unix 下权限为 `0600`；Windows 下是普通用户文件。把它当作凭据，
不要提交到仓库或分享。

## 退出码

| 码 | 含义 |
|---|---|
| `0` | 成功 |
| `1` | 连接、协议、认证或发送错误 |
| `2` | 需要配对 / 缺少配对码或 server id |
| `130` | `Ctrl+C` 取消 |

## 安全说明

- TLS pinning 是强制的，没有 `--insecure` 开关。
- 深链接包含一次性配对码，不要公开发布。
- CLI 同时校验 TLS 公钥 pin 和 `ServerInfo` 里的指纹。
- CLI 不持久化聊天记录，只打印收到的事件。
- CLI 是参考实现；第三方客户端应直接使用协议文档和生成的绑定。

## 相关

- [协议快速开始](../protocol/quickstart.md)
- [协议规范](../protocol/freechat-v1.md)
- [一致性测试](../protocol/conformance.md)
