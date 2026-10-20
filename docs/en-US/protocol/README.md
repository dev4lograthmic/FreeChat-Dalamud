# FreeChat protocol docs (English)

[简体中文](../../zh-CN/protocol/README.md)

- [FreeChat v1 protocol](freechat-v1.md) — transport, security, pairing, messages, filters, sending, errors.
- [Examples](../../shared/protocol/examples/) — proto3 JSON envelopes for the debug encoding.
- [Conformance](conformance.md) — test case checklist and current status.
- [ADRs](../../../.opencode/adr/) — architecture decision records.

## Source of truth

The `.proto` files in [`proto/freechat/v1/`](../../../proto/freechat/v1/) are the
source of truth. The Markdown spec explains semantics; if they disagree, fix
the spec **and** the proto together, then run:

```bash
buf lint
buf build
python3 scripts/validate_examples.py
```

## Generating C#

```bash
buf generate
```

This writes C# bindings to `src/FreeChat.Protocol/Generated/`. The
`FreeChat.Protocol` project consumes them with the `Google.Protobuf` runtime;
the project file is added in Phase 2. Generated files must not be edited by
hand.

## Versioning

- Package: `freechat.v1`.
- Subprotocols: `freechat.v1.protobuf` (default), `freechat.v1.json` (debug).
- Additive changes only; reserve removed field numbers; run `buf breaking`
  against the base branch once the first protocol commit has landed.
