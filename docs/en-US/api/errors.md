# Errors and closing

## 1. Error

```json
{
  "requestId": "send-3",
  "error": {
    "code": "ERROR_CODE_COMMAND_BLOCKED",
    "message": "Command is blocked by server policy.",
    "retryAfterMs": 0
  }
}
```

- `requestId` correlates the error with a request; push errors may omit it.
- Clients should rely on `code`; `message` is for display/debugging.
- `retryAfterMs` is a suggested delay after rate limiting.

## 2. ErrorCode

| Code | Meaning | Suggested action |
|---|---|---|
| `ERROR_CODE_INVALID_REQUEST` | Invalid parameters/state | Fix the request |
| `ERROR_CODE_UNAUTHENTICATED` | Not authenticated | Re-authenticate |
| `ERROR_CODE_PAIRING_REQUIRED` | Pairing required | Retry with a pairing code |
| `ERROR_CODE_PAIRING_INVALID` | Wrong pairing code | Check the code |
| `ERROR_CODE_PAIRING_EXPIRED` | Pairing code expired | Generate a new code |
| `ERROR_CODE_PAIRING_RATE_LIMITED` | Too many pairing attempts | Wait and retry |
| `ERROR_CODE_DEVICE_REVOKED` | Device revoked | Pair again |
| `ERROR_CODE_PROTOCOL_MISMATCH` | Protocol/subprotocol mismatch | Update the client |
| `ERROR_CODE_UNSUPPORTED_ENCODING` | Encoding unsupported | Use protobuf |
| `ERROR_CODE_UNSUPPORTED_VERSION` | Protocol version unsupported | Upgrade the client |
| `ERROR_CODE_NOT_SUBSCRIBED` | Not subscribed | Send `Subscribe` first |
| `ERROR_CODE_FILTER_INVALID` | Invalid filter | Fix the filter |
| `ERROR_CODE_REGEX_INVALID` | Invalid/long/timed-out regex | Fix the regex |
| `ERROR_CODE_TEXT_TOO_LONG` | raw_text over 500 bytes | Shorten the text |
| `ERROR_CODE_COMMAND_BLOCKED` | Command blocklist | Do not retry |
| `ERROR_CODE_NOT_LOGGED_IN` | Game not logged in | Retry after login |
| `ERROR_CODE_CHANNEL_UNAVAILABLE` | Channel unavailable | Check party/FC state |
| `ERROR_CODE_TELL_TARGET_INVALID` | Invalid tell target | Provide Name@World |
| `ERROR_CODE_QUEUE_FULL` | Send queue full | Wait and retry |
| `ERROR_CODE_RATE_LIMITED` | Rate limited | Wait `retryAfterMs` |
| `ERROR_CODE_SLOW_CONSUMER` | Client consumed too slowly | Reduce subscription/filtering |
| `ERROR_CODE_INTERNAL` | Server internal error | Log and reconnect |
| `ERROR_CODE_SHUTTING_DOWN` | Server shutting down | Reconnect |
| `ERROR_CODE_HISTORY_UNAVAILABLE` | History unavailable | Resubscribe from oldest |
| `ERROR_CODE_FEATURE_DISABLED` | Feature disabled by the user | Ask the user to enable it |
| `ERROR_CODE_PLAYER_LIST_BUSY` | Friend refresh already in progress | Retry shortly |
| `ERROR_CODE_PLAYER_LIST_TIMEOUT` | Game did not answer in time | Retry or show unavailable |

## 3. SendStatus

| Status | Meaning |
|---|---|
| `SEND_STATUS_UNSPECIFIED` | Unspecified |
| `SEND_STATUS_QUEUED` | Reserved; not currently emitted |
| `SEND_STATUS_SUBMITTED` | Terminal; game accepted input, not delivery |
| `SEND_STATUS_ECHOED` | Optional best-effort echo notification |
| `SEND_STATUS_FAILED` | Failed |
| `SEND_STATUS_BLOCKED` | Command blocklist |
| `SEND_STATUS_RATE_LIMITED` | Rate limited |
| `SEND_STATUS_QUEUE_FULL` | Queue full |
| `SEND_STATUS_CANCELLED` | Cancelled |

## 4. CloseCode

| Code | Meaning |
|---|---|
| `1000` | Normal closure |
| `1001` | Server/client going away |
| `1002` | Protocol error |
| `1003` | Unsupported data |
| `1008` | Policy violation |
| `1009` | Message too large |
| `1011` | Internal error |
| `1013` | Try again later |
| `4001` | Authentication failed |
| `4002` | Pairing required |
| `4003` | Device revoked |
| `4004` | Protocol/version mismatch |
| `4005` | Slow consumer |
| `4006` | Server shutdown |

The server tries to send the matching `Error` / `AuthError` / `SessionEnded` /
`ServerShutdown` before closing, but clients must not rely on receiving an
application message before the close frame.

## 5. Rate limits and backpressure

- Pairing: 5 failures locks the client for 5 minutes.
- Sending: 4/second, burst 4 by default; configurable in the plugin.
- Client outgoing queue: 512 messages by default; slow consumers are closed
  with `4005`.
- One slow client must not block the game thread or other clients.
- Clients should read WebSockets promptly; if the UI stalls, reduce sources,
  disable battle, or narrow filters.

## 6. Retry guidance

| Situation | Action |
|---|---|
| `UNAUTHENTICATED` | Re-run challenge/pairing |
| `DEVICE_REVOKED` | Pair again; do not retry in a loop |
| `RATE_LIMITED` | Wait `retryAfterMs` |
| `QUEUE_FULL` | Wait and retry; avoid amplification |
| `SESSION_ENDED` / `SERVER_SHUTDOWN` | Reconnect and resubscribe on the new session |
| `HISTORY_TRUNCATED` | Replay from `oldest_sequence` |
