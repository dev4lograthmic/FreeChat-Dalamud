# FreeChat.Dalamud plugin (English)

[简体中文](../../zh-CN/development/plugin.md)

The Dalamud adapter for FreeChat. It owns the plugin lifecycle, captures chat
events, feeds them into `FreeChatServer`, and provides the pairing/device UI.

## Build

```powershell
dotnet build src\FreeChat.Dalamud\FreeChat.Dalamud.csproj -c Debug
```

Output:

```
src\FreeChat.Dalamud\bin\Debug\FreeChat.Dalamud.dll
```

## Load as a dev plugin

1. `/xlsettings` → Experimental → Dev Plugin Locations.
2. Add `E:\Projects\FreeChat-Dalamud\src\FreeChat.Dalamud\bin\Debug\FreeChat.Dalamud.dll`.
3. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable **FreeChat**.
4. Run `/freechat` to open the window.

The window is tabbed: **Status** (running state, character, capture counters,
online clients, test send), **Pairing** (code, fingerprint, deep link, QR,
pending approvals), **Devices**, **Settings** (language, port, LAN, backlog,
limits, command blocklist), and **Logs**.

## Architecture

| Type | Role |
|---|---|
| `Plugin` | Dalamud lifecycle, services, commands, login/logout |
| `ChatCapture` | Subscribes to the three `IChatGui` sources |
| `ChatEventFactory` | Converts `IChatMessage` / `ILogMessage` to `ChatEvent` |
| `ChatEnricher` | Converts SeString payloads to protocol segments |
| `ServerHost` | Starts/stops `FreeChatServer`, updates character |
| `PairingApprovalService` | Bridges pairing requests to the UI |
| `ChatSendService` | `ProcessChatBoxEntry`, splitting, blocklist, echo tracking |
| `Localizer` | Embedded `en-US` / `zh-CN` JSON UI catalogs |
| `UiLog` | In-memory plugin log shown in the Logs tab |
| `MainWindow` | Tabbed status, pairing, devices, settings, test send, logs |

## Current limitations

- `SendResult(ECHOED)` is not emitted yet; outgoing `ChatEvent.origin_request_id`
  is populated for clients that need correlation.
- `ChatEvent.Original*` fields are not populated yet.
- Linkshell/CWLS display names are not resolved yet.
- The QR code encodes the deep link; pair from the **Pairing** tab or copy the
  link into a client.

## In-game test

1. Load the plugin and run `/freechat`.
2. Open **Pairing**, copy the code/fingerprint/deep link, or scan the QR code.
3. Run a FreeChat client (or the integration test harness) against
   `wss://<game-pc>:14777/ws`.
4. Approve the device in the **Pairing** tab.
5. Chat normally; the client should receive `PostHandle` events.
6. Use the **Status** tab's test send with `/echo FreeChat test`; the game
   should echo it, and **Last send** / **Last echo** should update.
7. Switch **Settings → Language** to `zh-CN` and confirm the tabs and labels
   change immediately.

## Related

- [Server](server.md)
- [Protocol project](protocol.md)
