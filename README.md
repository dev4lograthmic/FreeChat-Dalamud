# FreeChat-Dalamud

**English** · [简体中文](#简体中文)

A Dalamud plugin that runs a game-side WebSocket server for FINAL FANTASY XIV.
Trusted clients on the same machine or LAN can receive structured chat events
and send messages or commands through the game chat box.

> Status: Phase 3 (Dalamud receive adapter + UI). Sending is wired in Phase 4.
> The protocol is `freechat.v1`; clients are third-party/separate projects.

## Repository layout

| Path | Purpose |
|---|---|
| `proto/freechat/v1/` | Protocol source of truth (protobuf) |
| `src/FreeChat.Protocol` | Generated C# bindings, codecs, constants |
| `src/FreeChat.Server` | WSS transport, pairing, sessions, filters, replay |
| `src/FreeChat.Dalamud` | Dalamud plugin, chat capture, pairing UI |
| `tests/FreeChat.Server.Tests` | Server/protocol integration tests |
| `docs/` | Bilingual protocol and development documentation |
| `FreeChat.slnx` | Buildable solution (Protocol, Server, Plugin, Tests) |

## Build and test

Windows PowerShell:

```powershell
dotnet build FreeChat.slnx -c Release
dotnet test FreeChat.slnx -c Release
```

Expected: 0 warnings, 0 errors; 15 server/protocol tests pass.

## Build the plugin

```powershell
dotnet build src\FreeChat.Dalamud\FreeChat.Dalamud.csproj -c Debug
```

Output:

```
src\FreeChat.Dalamud\bin\Debug\FreeChat.Dalamud.dll
```

Load it as a Dalamud dev plugin:

1. `/xlsettings` → Experimental → Dev Plugin Locations
2. Add the DLL path above.
3. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable **FreeChat**
4. Run `/freechat` to open the server window.

## Current capabilities

- WSS server with self-signed ECDSA P-256 certificate and public-key pinning
- QR/deep-link-compatible pairing code, device approval, challenge-response
- Receive `ChatMessage`, `ChatMessageUnhandled`, and `LogMessage`
- Structured `ChatEvent` with raw SeString, text, styles, item/map/player/status
- Per-connection filters, current-session replay, send rate limiting
- Plugin UI: status, fingerprint, pairing code, devices, capture counters

## Not implemented yet

- Sending real chat/commands (Phase 4)
- Localized UI and full log panel (Phase 5)
- CLI/reference client and conformance runner (Phase 6)
- Browser clients, relay, multi-instance (future)

## Documentation

- [English docs](docs/en-US/protocol/README.md)
- [简体中文文档](docs/zh-CN/protocol/README.md)
- [Development docs](docs/en-US/development/README.md)

## License

AGPL-3.0; see [LICENSE.md](LICENSE.md).

---

# 简体中文

[English](#freechat-dalamud)

一个 Dalamud 插件：在游戏进程内运行 WebSocket 服务端，让本机或局域网内的受信
客户端接收结构化聊天事件，并通过游戏聊天框发送消息/命令。

> 状态：Phase 3（Dalamud 接收适配 + UI）。发送在 Phase 4 接入。
> 协议为 `freechat.v1`；客户端由第三方或独立项目实现。

## 仓库结构

| 路径 | 用途 |
|---|---|
| `proto/freechat/v1/` | 协议源文件（protobuf） |
| `src/FreeChat.Protocol` | 生成的 C# 绑定、编解码、常量 |
| `src/FreeChat.Server` | WSS 传输、配对、会话、过滤、回放 |
| `src/FreeChat.Dalamud` | Dalamud 插件、聊天采集、配对 UI |
| `tests/FreeChat.Server.Tests` | 服务端/协议集成测试 |
| `docs/` | 中英文协议与开发文档 |
| `FreeChat.slnx` | 可构建解决方案（Protocol、Server、Plugin、Tests） |

## 构建与测试

Windows PowerShell：

```powershell
dotnet build FreeChat.slnx -c Release
dotnet test FreeChat.slnx -c Release
```

预期：0 warning、0 error；15 个服务端/协议测试通过。

## 构建插件

```powershell
dotnet build src\FreeChat.Dalamud\FreeChat.Dalamud.csproj -c Debug
```

输出：

```
src\FreeChat.Dalamud\bin\Debug\FreeChat.Dalamud.dll
```

作为 Dalamud Dev Plugin 加载：

1. `/xlsettings` → Experimental → Dev Plugin Locations
2. 添加上面的 DLL 路径
3. `/xlplugins` → Dev Tools → Installed Dev Plugins → 启用 **FreeChat**
4. 运行 `/freechat` 打开服务端窗口

## 当前能力

- WSS 服务端、ECDSA P-256 自签证书、公钥 pinning
- 配对码/深链接、设备确认、challenge-response
- 接收 `ChatMessage`、`ChatMessageUnhandled`、`LogMessage`
- 结构化 `ChatEvent`：原始 SeString、文本、样式、物品/地图/玩家/状态
- 每连接过滤、当前会话回放、发送限流
- 插件 UI：状态、指纹、配对码、设备、捕获计数

## 尚未实现

- 真实聊天/命令发送（Phase 4）
- UI 本地化、完整日志面板（Phase 5）
- CLI/参考客户端、一致性测试 runner（Phase 6）
- 浏览器客户端、relay、多实例（以后）

## 文档

- [English docs](docs/en-US/protocol/README.md)
- [简体中文文档](docs/zh-CN/protocol/README.md)
- [开发文档](docs/zh-CN/development/README.md)

## 许可证

AGPL-3.0；见 [LICENSE.md](LICENSE.md)。
