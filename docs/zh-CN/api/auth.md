# 认证与配对

## 1. 深链接

插件 **Pairing** 标签生成：

```text
freechat://pair?v=1&host=192.168.1.10&port=14777&sid=<server_id>&fp=<64 hex>&code=<6 digits>
```

| 参数 | 说明 |
|---|---|
| `v` | 协议版本，当前为 `1` |
| `host` | 服务端地址；IPv6 使用方括号 |
| `port` | WSS 端口 |
| `sid` | `server_id`，用于校验重连目标 |
| `fp` | 服务端 P-256 公钥的 SHA-256 指纹 |
| `code` | 一次性配对码，6 位数字，5 分钟有效 |

配对码只显示在插件 UI，**不会**出现在 `PairingRequired` 或任何服务端消息中。

## 2. 连接流程

```text
Client                                      Server
  | TCP + TLS（pin fp）                        |
  |------------------------------------------->|
  | HTTP Upgrade /ws, freechat.v1.protobuf     |
  |<-------------------------------------------|
  | Envelope{ ClientHello }                    |
  |------------------------------------------->|
  |                                            | 未知设备
  | Envelope{ PairingRequired }                |
  |<-------------------------------------------|
  | Envelope{ PairRequest(code, pubkey) }      |
  |------------------------------------------->|
  |                                            | 插件 UI 确认
  | Envelope{ PairResult(ok, device_id) }      |
  |<-------------------------------------------|
  | Envelope{ Welcome }                        |
  |<-------------------------------------------|
  | Envelope{ Subscribe }                      |
  |------------------------------------------->|
```

已知设备跳过配对，服务端直接发送 `Challenge`。

## 3. ClientHello

第一条客户端消息：

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

| 字段 | 必填 | 说明 |
|---|---|---|
| `protocolVersion` | 是 | 当前为 `1` |
| `clientName` | 是 | 客户端名称 |
| `clientVersion` | 是 | 客户端版本 |
| `platform` | 是 | `windows` / `ios` / `android` / `macos` / `cli` / `other` |
| `deviceId` | 是 | 客户端安装级稳定 ID |
| `devicePublicKey` | 是 | P-256 X9.63 65 字节公钥 |
| `pairingCode` | 否 | 首次配对时可带；服务端以 `PairRequest.code` 为准 |
| `resumeFromSequence` | 否 | 续传提示；实际回放由 `Subscribe.from_sequence` 决定 |
| `capabilities` | 否 | 客户端支持的能力 |

服务端在收到 `ClientHello` 后：

- 如果设备未知：返回 `PairingRequired`。
- 如果设备已知且公钥匹配：返回 `Challenge`。
- 如果协议版本不匹配：返回 `AuthError{UNSUPPORTED_VERSION}` 并关闭。

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

客户端应：

- 校验 `server.publicKeyFingerprint` 与深链接 `fp` 一致。
- 校验 `server.serverId` 与深链接 `sid` 一致。
- `server.character.worldId` 为 `0` 表示世界未知，此时 `worldName` 为空。
- 把设备名/平台/公钥指纹展示给用户。
- 让用户从插件 UI 读取配对码；不要尝试从服务端获取。

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

配对规则：

- `code` 为 6 位数字，5 分钟有效，只能成功使用一次。
- 失败次数过多会触发限流：默认 5 次失败后锁定 5 分钟。
- 服务端保存 `device_id`、设备名、平台、公钥、创建时间、最后在线时间。
- 配对成功后服务端发送 `PairResult`，然后直接进入认证并发送 `Welcome`。

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

失败时：

```json
{
  "pairResult": {
    "ok": false,
    "error": "ERROR_CODE_PAIRING_INVALID",
    "message": "The pairing code is invalid."
  }
}
```

客户端应在成功后保存 `device_id`。

## 7. Challenge

已知设备重连：

```json
{
  "challenge": {
    "nonce": "AAECAwQ...",
    "algorithm": "ECDSA_P256_SHA256_P1363",
    "expiresAt": "2026-10-20T12:35:00Z"
  }
}
```

签名输入：

```text
"FreeChat/v1 challenge" || 0x00 || server_id || 0x00 || device_id || 0x00 || nonce
```

- 使用设备私钥，ECDSA P-256 + SHA-256。
- 签名格式：IEEE P1363，64 字节 `r || s`。
- nonce 32 字节随机，60 秒有效，一次性。
- `server_id` 必须来自深链接或上次配对保存的状态；不能凭空猜测。
- 重放、过期 nonce、错误签名都会返回 `AuthError{UNAUTHENTICATED}`。

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

客户端应保存：

- `device_id`
- 设备私钥（安全存储）
- `server_id`
- 当前 `session_id`
- 最近一次 `sequence`

## 10. AuthError

```json
{
  "authError": {
    "code": "ERROR_CODE_UNAUTHENTICATED",
    "message": "Challenge signature is invalid."
  }
}
```

常见原因：

| 错误 | 原因 |
|---|---|
| `UNSUPPORTED_VERSION` | `ClientHello.protocol_version` 不匹配 |
| `UNAUTHENTICATED` | 签名错误、nonce 过期/重放 |
| `DEVICE_REVOKED` | 设备已被插件撤销 |
| `PAIRING_INVALID` | 配对码错误 |
| `PAIRING_EXPIRED` | 配对码过期 |
| `PAIRING_RATE_LIMITED` | 配对尝试过多 |

## 11. 证书重置

如果插件证书损坏，UI 可以显式“重置证书”：

- 旧证书文件会被隔离为 `freechat.pfx.corrupt-<timestamp>`。
- 服务端生成新的 TLS 密钥和新的 `fp`。
- 设备存储不变，`device_id` 和公钥仍然有效。
- 客户端只需要更新 pin/深链接；之后仍可用原设备密钥通过 `Challenge`。

客户端不能假设 `fp` 永久不变；应把 pin 和 `device_id` 分开保存。
