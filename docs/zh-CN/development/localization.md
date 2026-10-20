# 插件本地化（简体中文）

[English](../../en-US/development/localization.md)

插件 UI 使用一个扁平 JSON 文案表。

## 文件

| 文件 | 用途 |
|---|---|
| `src/FreeChat.Dalamud/Localization/en-US.json` | 源文案 |
| `src/FreeChat.Dalamud/Localization/zh-CN.json` | 简体中文 |
| `src/FreeChat.Dalamud/Localization/Localizer.cs` | 加载、回退、格式化 |

JSON 文件会嵌入插件程序集。新增语言时复制 `en-US.json`，翻译值并保留所有
键，然后把语言代码加入 `Localizer.AvailableLanguages`。

## 规则

- 键保持稳定；不要因为语法变化重命名键。
- 保留 `{0}`、`{1}` 等占位符。
- 命令名、协议术语和 ID 不翻译。
- 缺失的键回退到 `en-US`，不会静默丢弃。
- UI 代码使用 `Localizer.Get(key, args...)` 或索引器，不硬编码英文。

## Crowdin

`crowdin.yml` 把英文源文件映射到翻译文件：

```text
/src/FreeChat.Dalamud/Localization/en-US.json
  -> /src/FreeChat.Dalamud/Localization/%locale%.json
```

在环境变量里设置 `CROWDIN_PROJECT_ID` 和 `CROWDIN_PERSONAL_TOKEN`，然后运行
Crowdin CLI。不要把 token 提交到仓库。发布前拉取翻译并检查生成的 JSON diff。

## 新增文案

1. 在 `en-US.json` 和 `zh-CN.json` 里加入同一个键。
2. 在 `MainWindow`（或其他 UI 类型）里使用。
3. 重新构建插件，在 **设置** 标签里检查两种语言。
4. 错误文案只本地化显示文本，底层的协议错误码保持不变。
