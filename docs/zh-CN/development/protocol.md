# FreeChat.Protocol

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

## 聊天 chunks

`ChatEvent.chunks` 是首选的富文本表示。每个 `ChatTextChunk` 自带显示文本和
可选链接；客户端按顺序渲染，不需要字符 offset。`segments` 继续用于兼容旧
客户端，两者由同一次 SeString 遍历生成。

## 相关

- [插件 API](../api/README.md)
- [协议源文件](../../../proto/freechat/v1/)
