# FreeChat.Dalamud 插件（简体中文）

[English](../../en-US/development/plugin.md)

FreeChat 的 Dalamud 适配层：负责插件生命周期、聊天采集、把事件喂给
`FreeChatServer`，以及配对/设备 UI。

## 构建

```powershell
dotnet build src\FreeChat.Dalamud\FreeChat.Dalamud.csproj -c Debug
```

输出：

```
src\FreeChat.Dalamud\bin\Debug\FreeChat.Dalamud.dll
```

## 作为 Dev Plugin 加载

1. `/xlsettings` → Experimental → Dev Plugin Locations。
2. 添加 `E:\Projects\FreeChat-Dalamud\src\FreeChat.Dalamud\bin\Debug\FreeChat.Dalamud.dll`。
3. `/xlplugins` → Dev Tools → Installed Dev Plugins → 启用 **FreeChat**。
4. 运行 `/freechat` 打开窗口。

窗口显示监听地址、角色、证书指纹、配对码、待确认设备、受信设备和基础设置。

## 结构

| 类型 | 作用 |
|---|---|
| `Plugin` | Dalamud 生命周期、服务、命令、登录/登出 |
| `ChatCapture` | 订阅三个 `IChatGui` 来源 |
| `ChatEventFactory` | 把 `IChatMessage` / `ILogMessage` 转成 `ChatEvent` |
| `ChatEnricher` | 把 SeString payload 转成协议 segments |
| `ServerHost` | 启停 `FreeChatServer`、更新角色 |
| `PairingApprovalService` | 把配对请求接到 UI |
| `MainWindow` | 状态、配对、设备、设置 |

## 当前限制

- 发送尚未接入；`UnavailableChatSink` 在 Phase 4 前返回
  `ERROR_CODE_NOT_LOGGED_IN`。
- UI 文案尚未本地化；Phase 5 处理。
- `ChatEvent.Original*` 字段暂未填充。
- 通讯贝/CWLS 名称暂未解析。

## 游戏内测试

1. 加载插件，运行 `/freechat`。
2. 复制配对码和指纹。
3. 用 FreeChat 客户端（或集成测试工具）连接
   `wss://<游戏电脑>:14777/ws`。
4. 在插件窗口里确认设备。
5. 正常聊天；客户端应收到 `PostHandle` 事件。

## 相关

- [服务端](server.md)
- [协议项目](protocol.md)
