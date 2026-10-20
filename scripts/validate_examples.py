#!/usr/bin/env python3
"""Validate the JSON examples used by the FreeChat v1 protocol spec.

This intentionally uses only the Python standard library so it can run in CI
without the protobuf runtime. Binary conformance vectors are generated later
from the same .proto files.
"""

from __future__ import annotations

import json
import pathlib
import sys

ENVELOPE_PAYLOADS = {
    "clientHello",
    "welcome",
    "pairingRequired",
    "pairRequest",
    "pairResult",
    "challenge",
    "challengeResponse",
    "authError",
    "subscribe",
    "subscribed",
    "unsubscribe",
    "replayRequest",
    "historyTruncated",
    "chatEvent",
    "sendRequest",
    "sendResult",
    "sendCancel",
    "ping",
    "pong",
    "error",
    "sessionEnded",
    "serverShutdown",
    "rateLimit",
}

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXAMPLES = ROOT / "docs" / "shared" / "protocol" / "examples"


def main() -> int:
    errors: list[str] = []
    files = sorted(EXAMPLES.glob("*.json"))
    if not files:
        errors.append(f"no JSON examples found in {EXAMPLES}")

    for path in files:
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception as exc:  # noqa: BLE001 - report all parse failures
            errors.append(f"{path.name}: invalid JSON: {exc}")
            continue

        if not isinstance(data, dict):
            errors.append(f"{path.name}: top-level value must be an object")
            continue

        if "requestId" in data and not isinstance(data["requestId"], str):
            errors.append(f"{path.name}: requestId must be a string")

        payloads = set(data) - {"requestId"}
        if len(payloads) != 1:
            errors.append(
                f"{path.name}: expected exactly one envelope payload, got {sorted(payloads)}"
            )
            continue

        payload = next(iter(payloads))
        if payload not in ENVELOPE_PAYLOADS:
            errors.append(f"{path.name}: unknown envelope payload {payload!r}")

        text = path.read_text(encoding="utf-8")
        if "..." in text:
            errors.append(f"{path.name}: contains a '...' placeholder")

    if errors:
        print("Example validation failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    print(f"Validated {len(files)} JSON examples.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
