# FreeChat.Dalamud plugin

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
pending requests), **Devices**, **Settings** (language, port, LAN, replay,
rate limits, command blocklist), and **Logs**.

## Structure

| Type | Role |
|---|---|
| `Plugin` | Dalamud lifecycle, services, commands, login/logout |
| `ChatCapture` | Subscribes to the three `IChatGui` sources |
| `ChatEventFactory` | Converts `IChatMessage` / `ILogMessage` to `ChatEvent` |
| `ChatEnricher` | Converts SeString payloads to protocol segments/chunks |
| `GameSocialData` | Framework-thread linkshell names, friend list, presence |
| `ServerHost` | Starts/stops `FreeChatServer`, updates character, injects social data |
| `PairingApprovalService` | Bridges pairing requests to the UI |
| `ChatSendService` | `ProcessChatBoxEntry`, splitting, blocklist, echo tracking |
| `Localizer` | Embedded `en-US` / `zh-CN` JSON UI catalogs |
| `UiLog` | In-memory log shown in the Logs tab |
| `MainWindow` | Status, pairing, devices, settings, test send, logs |

## Current limitations

- `SUBMITTED` is terminal. `SendResult(ECHOED)` is a best-effort, non-terminal
  notification; outgoing `ChatEvent.origin_request_id` is also populated.
- `ChatEvent.Original*` fields are not populated yet.
- Linkshell/CWLS names are filled into `ChatEvent.channel_name`.
- Friend list and presence are opt-in; see the social API docs.
- The QR code encodes the deep link; scan it from the **Pairing** tab or copy the
  link into a client.
