# FreeChat 协议文档（简体中文）

[English](../../en-US/protocol/README.md)

- [FreeChat v1 协议](freechat-v1.md) — 传输、安全、配对、消息、过滤、发送、错误。
- [示例](../../shared/protocol/examples/) — debug 编码使用的 proto3 JSON 信封。
- [一致性测试](conformance.md) — 用例清单与当前状态。
- [开发文档](../development/server.md) — 服务端核心和协议项目说明。
- [ADR](../../../.opencode/adr/) — 架构决策记录。

## 协议源文件

[`proto/freechat/v1/`](../../../proto/freechat/v1/) 下的 `.proto` 是唯一源文件。
Markdown 文档解释语义；如果两者冲突，同时修正文档和 proto，然后运行：

```bash
buf lint
buf build
python3 scripts/validate_examples.py
```

## 生成 C#

```bash
buf generate
```

输出到 `src/FreeChat.Protocol/Generated/`，由 `FreeChat.Protocol` 项目配合
`Google.Protobuf` 运行时使用；见[协议项目](../development/protocol.md)。
生成文件不要手工修改。

## 版本

- 包名：`freechat.v1`。
- 子协议：`freechat.v1.protobuf`（默认）、`freechat.v1.json`（调试）。
- 只做加法；删除字段必须 `reserved`；首个协议提交进入 base branch 后启用
  `buf breaking` CI。
