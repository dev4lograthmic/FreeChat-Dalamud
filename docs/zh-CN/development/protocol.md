# FreeChat.Protocol

[English](../../en-US/development/protocol.md)

FreeChat v1 的 C# 绑定和共享协议辅助代码。

- `Generated/` 由 `buf generate` 从 `proto/freechat/v1/*.proto` 生成，禁止手改。
- `Codec/EnvelopeCodec.cs`：把一个 `Envelope` 编码/解码为二进制 protobuf
  或 proto3 JSON。
- `ProtocolConstants.cs`：版本、子协议名、capability 常量。
- 项目使用 `Google.Protobuf` 运行时，目标框架 `net10.0`。

## 重新生成

```bash
buf generate
```

生成的 C# 类型使用 `FreeChat.Protocol.V1` 命名空间。

## 相关

- [协议规范](../protocol/freechat-v1.md)
- [服务端项目](server.md)
