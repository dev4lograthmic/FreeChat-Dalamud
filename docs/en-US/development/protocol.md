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

## Chat chunks

`ChatEvent.chunks` is the preferred rich-text representation. Each
`ChatTextChunk` contains its own display text and an optional link; clients
render the chunks in order and do not need character offsets. `segments`
remains for backward compatibility, and both are produced from one SeString
traversal.

## Related

- [Plugin API](../api/README.md)
- [Protocol source](../../../proto/freechat/v1/)
