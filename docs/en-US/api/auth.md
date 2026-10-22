# Authentication and pairing

## 1. Deep link

The plugin **Pairing** tab generates:

```text
freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
```

| Parameter | Meaning |
|---|---|
| `v` | Protocol version, currently `1` |
| `host` | Server address; use brackets for IPv6 |
| `port` | WSS port |
| `sid` | `server_id`; used to verify the reconnect target |
| `fp` | SHA-256 fingerprint of the server P-256 public key |
| `code` | One-time pairing code, 6 digits, valid for 5 minutes |

The pairing code is displayed only in the plugin UI. It is **never** included in
`PairingRequired` or any server message.

## 2. Connection flow

```text
Client                                      Server
  | TCP + TLS (pin fp)                        |
  |------------------------------------------->|
  | HTTP Upgrade /ws, freechat.v1.protobuf     |
  |<-------------------------------------------|
  | Envelope{ ClientHello }                    |
  |------------------------------------------->|
  |                                            | unknown device
  | Envelope{ PairingRequired }                |
  |<-------------------------------------------|
  | Envelope{ PairRequest(code, pubkey) }      |
  |------------------------------------------->|
  |                                            | plugin UI approval
  | Envelope{ PairResult(ok, device_id) }      |
  |<-------------------------------------------|
  | Envelope{ Welcome }                        |
  |<-------------------------------------------|
  | Envelope{ Subscribe }                      |
  |------------------------------------------->|
```

Known devices skip pairing and receive `Challenge` directly.

## 3. ClientHello

The first client message:

```json
{
  "requestId": "c-1",
  "clientHello": {
    "protocolVersion": 1,
    "clientName": "FreeChat CLI",
    "clientVersion": "0.2.0",
    "platform": "cli",
    "deviceId": "dev-7f3c2a",
    "devicePublicKey": "BAECAwQ...",
    "pairingCode": "",
    "resumeFromSequence": "0",
    "capabilities": ["chat.segments", "filters.regex"]
  }
}
```

| Field | Required | Meaning |
|---|---|---|
| `protocolVersion` | yes | Currently `1` |
| `clientName` | yes | Client name |
| `clientVersion` | yes | Client version |
| `platform` | yes | `windows` / `ios` / `android` / `macos` / `cli` / `other` |
| `deviceId` | yes | Stable per-install device ID |
| `devicePublicKey` | yes | P-256 X9.63 65-byte public key |
| `pairingCode` | no | May be sent on first pairing; the server uses `PairRequest.code` |
| `resumeFromSequence` | no | Resume hint; actual replay is controlled by `Subscribe.from_sequence` |
| `capabilities` | no | Client-supported capabilities |

Server behavior:

- Unknown device: return `PairingRequired`.
- Known device with matching public key: return `Challenge`.
- Unsupported protocol version: return `AuthError{UNSUPPORTED_VERSION}` and close.

## 4. PairingRequired

```json
{
  "pairingRequired": {
    "server": {
      "serverId": "srv-1a2b",
      "serverName": "FreeChat",
      "character": {
        "name": "Someone",
        "worldId": 86,
        "worldName": "Typhon",
        "isPlayer": true,
        "isLocal": true
      },
      "protocolVersion": 1,
      "serverVersion": "0.1.0",
      "publicKeyFingerprint": "DtOmq5V/9vWaljCkc9MafQT8tGVIuJAWGWmAx+llhPY=",
      "capabilities": {
        "features": ["chat.segments", "filters.regex"],
        "maxMessageBytes": 1048576,
        "maxClients": 32,
        "backlogMessages": 5000,
        "backlogBytes": 33554432
      }
    },
    "reason": "unknown_device",
    "expiresAt": "2026-10-20T12:40:00Z",
    "codeLength": 6
  }
}
```

Clients should:

- verify `server.publicKeyFingerprint` matches the deep link `fp`;
- verify `server.serverId` matches the deep link `sid`;
- treat `server.character.worldId == 0` as an unknown world; `worldName` is then
  empty;
- show the device name, platform, and public-key fingerprint to the user;
- ask the user to read the pairing code from the plugin UI; never request it
  from the server.

## 5. PairRequest

```json
{
  "requestId": "c-2",
  "pairRequest": {
    "code": "482913",
    "deviceName": "My iPhone",
    "platform": "ios",
    "devicePublicKey": "BAECAwQ...",
    "capabilities": {
      "features": ["chat.segments", "filters.regex"]
    }
  }
}
```

Pairing rules:

- `code` is a 6-digit, one-time code valid for 5 minutes.
- Too many failures trigger a lockout: 5 failed attempts locks the client for
  5 minutes.
- The server stores `device_id`, device name, platform, public key, creation
  time, and last-seen time.
- On success the server sends `PairResult`, then authenticates and sends
  `Welcome`.

## 6. PairResult

```json
{
  "requestId": "c-2",
  "pairResult": {
    "ok": true,
    "deviceId": "c374bdb0b79e4d27aab7d6171ba39c88"
  }
}
```

Failure:

```json
{
  "pairResult": {
    "ok": false,
    "error": "ERROR_CODE_PAIRING_INVALID",
    "message": "The pairing code is invalid."
  }
}
```

The client should persist `device_id` after success.

## 7. Challenge

Known-device reconnect:

```json
{
  "challenge": {
    "nonce": "AAECAwQ...",
    "algorithm": "ECDSA_P256_SHA256_P1363",
    "expiresAt": "2026-10-20T12:35:00Z"
  }
}
```

Signature input:

```text
"FreeChat/v1 challenge" || 0x00 || server_id || 0x00 || device_id || 0x00 || nonce
```

- Use the device private key with ECDSA P-256 + SHA-256.
- Signature format: IEEE P1363, 64 bytes `r || s`.
- The nonce is 32 random bytes, valid for 60 seconds, single-use.
- `server_id` must come from the deep link or saved pairing state.
- Replayed/expired nonces and invalid signatures return
  `AuthError{UNAUTHENTICATED}`.

## 8. ChallengeResponse

```json
{
  "requestId": "c-3",
  "challengeResponse": {
    "signature": "AAECAwQ..."
  }
}
```

## 9. Welcome

```json
{
  "welcome": {
    "server": {
      "serverId": "srv-1a2b",
      "serverName": "FreeChat",
      "protocolVersion": 1,
      "publicKeyFingerprint": "DtOmq5V/9vWaljCkc9MafQT8tGVIuJAWGWmAx+llhPY="
    },
    "sessionId": "sess-9f8e",
    "oldestSequence": "1",
    "latestSequence": "42",
    "deviceId": "c374bdb0b79e4d27aab7d6171ba39c88",
    "enabledFeatures": ["chat.segments", "filters.regex"]
  }
}
```

Clients should persist:

- `device_id`
- device private key (secure storage)
- `server_id`
- current `session_id`
- last known `sequence`

## 10. AuthError

```json
{
  "authError": {
    "code": "ERROR_CODE_UNAUTHENTICATED",
    "message": "Challenge signature is invalid."
  }
}
```

Common causes:

| Error | Cause |
|---|---|
| `UNSUPPORTED_VERSION` | `ClientHello.protocol_version` mismatch |
| `UNAUTHENTICATED` | Bad signature, expired/replayed nonce |
| `DEVICE_REVOKED` | Device was revoked in the plugin |
| `PAIRING_INVALID` | Wrong pairing code |
| `PAIRING_EXPIRED` | Pairing code expired |
| `PAIRING_RATE_LIMITED` | Too many pairing attempts |

## 11. Certificate reset

If the plugin certificate is damaged, the UI can explicitly reset it:

- the old certificate is quarantined as `freechat.pfx.corrupt-<timestamp>`;
- the server creates a new TLS key and a new `fp`;
- the device store is unchanged; `device_id` and the device public key remain
  valid;
- clients only need the new pin/deep link; they can still authenticate with the
  existing device key via `Challenge`.

Clients must not assume `fp` is permanent; store the pin separately from
`device_id`.
