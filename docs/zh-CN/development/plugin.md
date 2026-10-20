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

窗口使用标签页：**状态**（运行状态、角色、捕获计数、在线客户端、测试发送）、
**配对**（配对码、指纹、深链接、二维码、待确认请求）、**设备**、**设置**
（语言、端口、局域网、回放、限流、命令黑名单）、**日志**。

## 结构

| 类型 | 作用 |
|---|---|
| `Plugin` | Dalamud 生命周期、服务、命令、登录/登出 |
| `ChatCapture` | 订阅三个 `IChatGui` 来源 |
| `ChatEventFactory` | 把 `IChatMessage` / `ILogMessage` 转成 `ChatEvent` |
| `ChatEnricher` | 把 SeString payload 转成协议 segments |
| `ServerHost` | 启停 `FreeChatServer`、更新角色 |
| `PairingApprovalService` | 把配对请求接到 UI |
| `ChatSendService` | `ProcessChatBoxEntry`、分段、黑名单、echo 跟踪 |
| `Localizer` | 内嵌 `en-US` / `zh-CN` JSON UI 文案 |
| `UiLog` | 内存日志，显示在“日志”标签 |
| `MainWindow` | 状态、配对、设备、设置、测试发送、日志 |

## 当前限制

- 暂不发送 `SendResult(ECHOED)`；outgoing `ChatEvent.origin_request_id` 已填充，
  客户端可自行关联。
- `ChatEvent.Original*` 字段暂未填充。
- 通讯贝/CWLS 名称暂未解析。
- 二维码编码的是深链接；可在 **配对** 标签扫码，或复制链接到客户端。

## 游戏内测试

1. 加载插件，运行 `/freechat`。
2. 打开 **配对**，复制配对码/指纹/深链接，或直接扫码。
3. 用 FreeChat 客户端（或集成测试工具）连接
   `wss://<游戏电脑>:14777/ws`。
4. 在 **配对** 标签里确认设备。
5. 正常聊天；客户端应收到 `PostHandle` 事件。
6. 在 **状态** 标签的测试发送里填 `/echo FreeChat test`；游戏应回显，
   **上次发送** / **上次回显** 应更新。
7. 在 **设置 → 语言** 切换到 `zh-CN`，确认标签和文案立即变化。

## 相关

- [服务端](server.md)
- [协议项目](protocol.md)
