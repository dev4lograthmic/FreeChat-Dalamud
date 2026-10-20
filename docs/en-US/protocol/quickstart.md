# Protocol quick start

[简体中文](../../zh-CN/protocol/quickstart.md)

This is the shortest path from a pairing link to a live chat subscription.
The CLI is the executable reference implementation; the full semantics are in
the [protocol spec](freechat-v1.md).

## 0. Prerequisites

- A running FreeChat plugin and a reachable `wss://host:port/ws`.
- The pairing deep link from **FreeChat → Pairing**:
  `freechat://pair?v=1&host=...&port=...&sid=...&fp=...&code=...`.
- A P-256 device key. Genuine clients generate it once per install and store
  the private key securely.
- A protobuf/JSON codec generated from `proto/freechat/v1/`. C# users can
  reference `FreeChat.Protocol` and call `EnvelopeCodec`.

Try the reference client first:

```powershell
dotnet run --project src\FreeChat.Cli -- pair --uri "<deep link>"
dotnet run --project src\FreeChat.Cli -- subscribe --uri "<deep link>"
```

## 1. Connect and pin

Use WSS, not plain WS. Offer exactly one subprotocol:

```text
Sec-WebSocket-Protocol: freechat.v1.protobuf
```

`freechat.v1.json` is debug-only and must be enabled in the plugin. Validate
the server certificate by pinning:

```text
SHA-256( 0x04 || X || Y ) == fp from the deep link
```

Do **not** rely on the self-signed certificate chain or hostname. Compare the
raw 65-byte P-256 public key, not the certificate hash.

## 2. Send ClientHello

```csharp
var hello = new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    ClientHello = new ClientHello
    {
        ProtocolVersion = 1,
        ClientName = "my-client",
        ClientVersion = "0.1.0",
        Platform = "windows", // windows / ios / android / macos / cli / other
        DeviceId = deviceId,
        DevicePublicKey = ByteString.CopyFrom(publicKey), // 0x04 || X || Y
        PairingCode = pairingCode, // only for first pairing
    },
};
await SendAsync(EnvelopeCodec.Encode(hello, json: false));
```

The server replies with one of:

- `PairingRequired` — unknown device; send `PairRequest` with the code from
  the deep link. The server never sends the code back.
- `Challenge` — known device; sign the challenge.
- `AuthError` — fail and close.

## 3. Pair or answer the challenge

Pairing:

```csharp
var pair = new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    PairRequest = new PairRequest
    {
        Code = pairingCode,
        DeviceName = "My Phone",
        Platform = "ios",
        DevicePublicKey = ByteString.CopyFrom(publicKey),
    },
};
```

Challenge:

```text
input = "FreeChat/v1 challenge" || 0x00 || server_id || 0x00 || device_id || 0x00 || nonce
signature = ECDSA_P256_SHA256_P1363(input)
```

Send `ChallengeResponse.signature` (64 bytes, `r || s`). On success the server
sends `Welcome` with `session_id`, sequence range, and server info. Persist
`device_id`, the private key, and `server_id` for reconnects.

## 4. Subscribe

```csharp
var subscribe = new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    Subscribe = new Subscribe
    {
        Filter = new ChatFilter(),          // no restrictions
        Sources = { ChatSource.PostHandle }, // default
        ContentMode = ContentMode.Current,
        FromSequence = 0,                    // live only
    },
};
```

The server replies `Subscribed`, then pushes `ChatEvent` messages. `from_sequence
> 0` replays history after that sequence; if it is too old, expect
`HistoryTruncated` first.

Useful filters: `include_categories`, `exclude_categories`, `keyword`,
`regex`, `include_own` (optional; unset means include), `include_battle`.

## 5. Receive

Each `ChatEvent` carries:

- `sequence` and `message_id` (`server_id:session_id:sequence`)
- `channel`, `log_kind`, `source`
- `sender`, `text`, `raw_sender`, `raw_message`
- `segments` (text, icon, item, map, auto-translate, player, status)
- `outgoing`, `tell_peer`, `channel_name`
- `origin_request_id` when it is the game echo of a `SendRequest`

Render `segments` when possible; fall back to `text` or `raw_message`.

## 6. Send

Raw text or command:

```csharp
new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    SendRequest = new SendRequest { RawText = "/echo hello" },
};
```

Structured channel:

```csharp
new Envelope
{
    RequestId = Guid.NewGuid().ToString("N"),
    SendRequest = new SendRequest
    {
        Chat = new ChatSend
        {
            Channel = ChatChannel.Party,
            Text = "hello",
        },
    },
};
```

Watch `SendResult` for the request id. `SUBMITTED` means the game accepted the
input; it does not prove delivery. The server currently emits
`origin_request_id` on the echo `ChatEvent` but not a separate `ECHOED`
`SendResult`.

## 7. Keepalive and shutdown

- Reply to `Ping` with `Pong` carrying the same nonce.
- Send `Ping` yourself for latency checks.
- On `SessionEnded` or `ServerShutdown`, stop and reconnect later.
- Close with code `1000` for a normal client shutdown.

## Minimal C# skeleton

```csharp
using System.Net.WebSockets;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;

var ws = new ClientWebSocket();
ws.Options.AddSubProtocol(ProtocolConstants.ProtobufSubProtocol);
ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
    PinMatches(cert, expectedFingerprint); // implement with ECDsa + SHA256

await ws.ConnectAsync(new Uri($"wss://{host}:{port}/ws"), ct);

// send EnvelopeCodec.Encode(envelope, json: false) as binary frames
// receive binary frames and call EnvelopeCodec.Decode(bytes, json: false)
```

## Where to go next

- [Protocol spec](freechat-v1.md) — message fields, filters, errors, close codes.
- [CLI](../development/cli.md) — reference commands and examples.
- [Conformance](conformance.md) — cases and current status.
- [Examples](../../shared/protocol/examples/) — proto3 JSON envelopes.
