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
| `MainWindow` | Server status, pairing, devices, settings |

## Current limitations

- Sending is not wired yet; `UnavailableChatSink` returns
  `ERROR_CODE_NOT_LOGGED_IN` until Phase 4.
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

## Related

- [Server](server.md)
- [Protocol project](protocol.md)
