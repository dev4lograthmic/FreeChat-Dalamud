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

The window shows the listening address, character, certificate fingerprint,
pairing code, pending approvals, trusted devices, and basic settings.

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
| `MainWindow` | Server status, pairing, devices, settings, test send |

## Current limitations

- `SendResult(ECHOED)` is not emitted yet; outgoing `ChatEvent.origin_request_id`
  is populated for clients that need correlation.
- UI strings are not localized yet; localization is Phase 5.
- `ChatEvent.Original*` fields are not populated yet.
- Linkshell/CWLS display names are not resolved yet.

## In-game test

1. Load the plugin and run `/freechat`.
2. Copy the pairing code and fingerprint.
3. Run a FreeChat client (or the integration test harness) against
   `wss://<game-pc>:14777/ws`.
4. Approve the device in the plugin window.
5. Chat normally; the client should receive `PostHandle` events.
6. Use the **Test send** box with `/echo FreeChat test`; the game should echo
   it, and **Last send** / **Last echo** should update.

## Related

- [Server](server.md)
- [Protocol project](protocol.md)
