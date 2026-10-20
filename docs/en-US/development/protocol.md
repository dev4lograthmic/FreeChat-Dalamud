# FreeChat.Protocol

[简体中文](../../zh-CN/development/protocol.md)

C# bindings and shared protocol helpers for FreeChat v1.

- `Generated/` is produced by `buf generate` from `proto/freechat/v1/*.proto`.
  Do not edit these files by hand.
- `Codec/EnvelopeCodec.cs` encodes/decodes one `Envelope` as binary protobuf
  or proto3 JSON.
- `ProtocolConstants.cs` holds the version, subprotocol names, and capability
  identifiers.
- The project references the `Google.Protobuf` runtime and targets `net10.0`.

## Regenerate

```bash
buf generate
```

The generated C# types use the `FreeChat.Protocol.V1` namespace.

## Related

- [Protocol specification](../protocol/freechat-v1.md)
- [Server project](server.md)
