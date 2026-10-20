# FreeChat.Protocol

C# bindings and shared protocol helpers for FreeChat v1.

- `Generated/` is produced by `buf generate` from `proto/freechat/v1/*.proto`;
  do not edit it by hand.
- `Codec/EnvelopeCodec.cs` encodes/decodes an `Envelope` as binary protobuf or
  proto3 JSON.
- `ProtocolConstants.cs` contains the version, subprotocol names, and capability
  constants.
- The project uses the `Google.Protobuf` runtime and targets `net10.0`.

## Regenerate

```bash
buf generate
```

Generated C# types use the `FreeChat.Protocol.V1` namespace.

## Related

- [Plugin API](../api/README.md)
- [Protocol source](../../../proto/freechat/v1/)
