# FreeChat v1 conformance

[简体中文](../../zh-CN/protocol/conformance.md)

This document tracks protocol conformance cases and the current automated and
live-test status.

## Current status

- [x] `.proto` compiles (`buf build`).
- [x] `buf lint` passes.
- [x] JSON examples validate (`python3 scripts/validate_examples.py`).
- [x] Binary protobuf/JSON equivalence (`ConformanceTests`).
- [ ] Fixed binary protobuf vectors.
- [x] Automated client/server integration tests (pairing, challenge, subscribe, replay, JSON debug).
- [ ] Full live conformance run with the CLI against a game instance.
- [ ] `buf breaking` CI check (enabled after the first protocol commit lands on the base branch).

## How to run

```bash
buf lint
buf build
python3 scripts/validate_examples.py
```

## Cases

| ID | Case | Expected | Status |
|---|---|---|---|
| C-01 | WebSocket subprotocol negotiation | server selects `freechat.v1.protobuf`; rejects unsupported with 400/1003 | PARTIAL (protobuf + JSON tested) |
| C-02 | Unknown device | `ClientHello` -> `PairingRequired` (no code in payload) | PASS (integration) |
| C-03 | Wrong TLS pin | client aborts before `ClientHello` | PASS (integration) |
| C-04 | Pairing success | `PairRequest` -> `PairResult(ok)` -> `Welcome` | PASS (integration) |
| C-05 | Known device auth | `Challenge` -> `ChallengeResponse` -> `Welcome` | PASS (integration) |
| C-06 | Bad challenge signature | `AuthError{UNAUTHENTICATED}` + close 4001 | TODO |
| C-07 | Expired/replayed nonce | `AuthError{UNAUTHENTICATED}` | TODO |
| C-08 | Subscribe + replay | `Subscribed` then `ChatEvent` in sequence order | PASS (integration) |
| C-09 | Replay too old | `HistoryTruncated` then replay from `oldest_sequence` | TODO |
| C-10 | Filter include/exclude | excluded channels never delivered | PASS (integration) |
| C-11 | Keyword/regex filter | only matching `text` delivered | TODO |
| C-12 | Own messages off | `outgoing=true` not delivered when `include_own=false` | TODO |
| C-13 | Battle off | `CHAT_CATEGORY_BATTLE` not delivered when `include_battle=false` | TODO |
| C-14 | Raw send | `QUEUED` -> `SUBMITTED` -> `ECHOED` with `origin_message_id` | PARTIAL (`SUBMITTED` + echo tag) |
| C-15 | Structured tell | `CHAT_CHANNEL_TELL` + target -> correct prefix | TODO |
| C-16 | Multi-part send | `part_count`/`part_index` correct; failure cancels remaining | TODO |
| C-17 | Blocked command | `/logout` -> `SEND_STATUS_BLOCKED` + `COMMAND_BLOCKED` | TODO |
| C-18 | Rate limit | `RATE_LIMITED` + `retry_after_ms` | TODO |
| C-19 | Queue full | `QUEUE_FULL` | TODO |
| C-20 | Send cancel | queued part -> `CANCELLED`; submitted part unaffected | TODO |
| C-21 | Device revoked | `DEVICE_REVOKED` + close 4003 | TODO |
| C-22 | Slow consumer | close 4005; other clients unaffected | TODO |
| C-23 | Oversized message | close 1009 | TODO |
| C-24 | JSON/protobuf equivalence | same envelope encodes/decodes in both | PASS (`ConformanceTests`) |
| C-25 | Unknown fields/enums | no crash; unknown enum degrades gracefully | TODO |
| C-26 | Logout/plugin unload | `SessionEnded` + close 1001 | TODO |
| C-27 | Ping/Pong | nonce echoed; latency measurable | PASS (integration) |

## Notes

- JSON examples live in `docs/shared/protocol/examples/`.
- The binary vectors should be generated from the same `.proto`; do not
  hand-write two schemas.
- Phase 1 must verify `IChatGui` event coverage before marking chat-source
  cases complete.
