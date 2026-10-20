# FreeChat CLI

The CLI is a small reference client for the FreeChat v1 protocol. It is a test
and debugging tool, not a product client. It supports pairing, subscriptions,
sending, ping, and envelope decoding.

Source: `src/FreeChat.Cli/`; executable: `freechat`.

## Build and run

```powershell
dotnet build src\FreeChat.Cli\FreeChat.Cli.csproj -c Release
dotnet run --project src\FreeChat.Cli\FreeChat.Cli.csproj -- --help
```

The built DLL is `src\FreeChat.Cli\bin\Release\net10.0\freechat.dll`.

## Get the pairing link

Open **FreeChat → Pairing** in the plugin. Copy the deep link:

```text
freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
```

The link contains the host, port, server id, public-key fingerprint, and
one-time code. The CLI accepts it with `--uri`, so you do not have to copy the
fields separately.

## Pair

```powershell
dotnet run --project src\FreeChat.Cli -- pair --uri "<deep link>"
```

The plugin must be running and the client must be reachable. Approve the
request in the plugin's **Pairing** tab. The CLI saves the device key and
server id in its state file and prints the device id.

Without `--uri`, pass the fields explicitly:

```powershell
dotnet run --project src\FreeChat.Cli -- pair `
  --host 127.0.0.1 --port 14777 `
  --fingerprint <64 hex> --code <6 digits>
```

The fingerprint is required. It is the SHA-256 of the server's raw P-256
public key, not the certificate hash. A wrong fingerprint aborts the TLS
connection before `ClientHello`.

## Subscribe

After pairing:

```powershell
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<deep link>"
```

Live chat is printed until `Ctrl+C`. The URI still carries the pairing code;
after the device is trusted the code is not needed, but it is harmless.

Filters and replay:

```powershell
# Only party and tell channels, excluding your own messages
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<link>" `
  --sources post --keyword "raid" --exclude-own

# Replay after sequence 41
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<link>" --from 41

# Exit after 10 chat events (useful for scripts)
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<link>" --count 10
```

Options:

| Option | Meaning |
|---|---|
| `--sources post,pre,structured,all` | Source selection; default `post` |
| `--keyword text` | Case-sensitive substring filter |
| `--regex pattern` | Non-backtracking regex filter |
| `--exclude-own` | Drop outgoing messages |
| `--include-battle` | Include battle events |
| `--from sequence` | Replay after a sequence |
| `--count n` | Stop after `n` `ChatEvent`s |

## Send

Raw text or slash command:

```powershell
dotnet run --project src\FreeChat.Cli -- send --uri "<link>" --raw "/echo FreeChat CLI"
```

Structured channels:

```powershell
dotnet run --project src\FreeChat.Cli -- send --uri "<link>" `
  --channel party --text "Hello from the CLI"

dotnet run --project src\FreeChat.Cli -- send --uri "<link>" `
  --channel tell --target "Friend@Typhon" --text "Hi"
```

Supported channel names: `say`, `party`, `shout`, `yell`, `alliance`, `fc`,
`ls1`…`ls8`, `cwls1`…`cwls8`, `novice`, `pvp`, `tell`.

`--raw` is limited to 500 UTF-8 bytes and is subject to the server command
blocklist. `--channel`/`--text` is split by the server at 500 bytes per part.
A `SUBMITTED` result means the game accepted the input; it does not prove the
other player received it.

## Ping

```powershell
dotnet run --project src\FreeChat.Cli -- ping --uri "<link>"
```

## Decode an envelope

```powershell
# Protobuf from a file
dotnet run --project src\FreeChat.Cli -- decode --input message.bin

# Base64 protobuf
dotnet run --project src\FreeChat.Cli -- decode --base64 "<base64>"

# Proto3 JSON example
dotnet run --project src\FreeChat.Cli -- decode `
  --input docs\shared\protocol\examples\chat_event_say.json --encoding json
```

Output is pretty proto3 JSON. Int64 fields are printed as strings, as required
by proto3 JSON.

## JSON debug transport

The server enables `freechat.v1.json` only when the plugin setting is on:

```powershell
dotnet run --project src\FreeChat.Cli -- pair --uri "<link>" --json
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<link>" --json --print-json
```

- `--json`: use the JSON subprotocol instead of protobuf.
- `--print-json`: print received envelopes as proto3 JSON instead of the
  human-readable summary.

## State file

Default:

```text
<ApplicationData>/FreeChat/cli-state.json
```

Override with `--state <path>`. It contains the device id, P-256 private key,
device name, and server id. On Unix the file is written with mode `0600`; on
Windows it is a normal user file. Treat it like a credential. Do not commit it
to a repository or share it.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Connection, protocol, authentication, or send error |
| `2` | Pairing required / missing pairing code or server id |
| `130` | Cancelled with `Ctrl+C` |

## Security notes

- TLS pinning is mandatory; there is no `--insecure` switch.
- The deep link contains a one-time pairing code; do not post it publicly.
- The CLI verifies both the TLS public-key pin and the fingerprint advertised
  in `ServerInfo`.
- The CLI does not persist chat history; it only prints received events.
- The CLI is a reference implementation. Third-party clients should use the
  protocol spec and generated bindings directly.

## Related

- [Protocol quick start](../protocol/quickstart.md)
- [Protocol spec](../protocol/freechat-v1.md)
- [Conformance](../protocol/conformance.md)
