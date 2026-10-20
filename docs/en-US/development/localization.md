# Plugin localization (English)

[简体中文](../../zh-CN/development/localization.md)

The plugin UI uses a small flat JSON catalog.

## Files

| File | Purpose |
|---|---|
| `src/FreeChat.Dalamud/Localization/en-US.json` | Source strings |
| `src/FreeChat.Dalamud/Localization/zh-CN.json` | Simplified Chinese |
| `src/FreeChat.Dalamud/Localization/Localizer.cs` | Loader, fallback, formatting |

The JSON files are embedded in the plugin assembly. Add a new language by
copying `en-US.json`, translating the values, keeping every key, and adding
the language code to `Localizer.AvailableLanguages`.

## Rules

- Keys are stable; do not rename keys for grammar changes.
- Keep placeholders such as `{0}` and `{1}` in the translated value.
- Keep command names, protocol terms, and IDs untranslated.
- Missing keys fall back to `en-US`; they are not silently dropped.
- UI code should call `Localizer.Get(key, args...)` or the indexer, never
  hard-code English text.

## Crowdin

`crowdin.yml` maps the English source to translated files:

```text
/src/FreeChat.Dalamud/Localization/en-US.json
  -> /src/FreeChat.Dalamud/Localization/%locale%.json
```

Set `CROWDIN_PROJECT_ID` and `CROWDIN_PERSONAL_TOKEN` in the environment, then
run the Crowdin CLI. Do not commit the token. Pull translations before a
release and review the generated JSON diff.

## Adding a string

1. Add the key to `en-US.json` and `zh-CN.json`.
2. Use it from `MainWindow` (or another UI type).
3. Rebuild the plugin and check both languages in the **Settings** tab.
4. If the string is an error message, keep the underlying protocol error code
   unchanged; only the display text is localized.
