# Plugin localization

The plugin UI uses a flat JSON string catalog.

## Files

| File | Purpose |
|---|---|
| `src/FreeChat.Dalamud/Localization/en-US.json` | Source strings |
| `src/FreeChat.Dalamud/Localization/zh-CN.json` | Simplified Chinese |
| `src/FreeChat.Dalamud/Localization/Localizer.cs` | Loader, fallback, formatting |

The JSON files are embedded in the plugin assembly. To add a language, copy
`en-US.json`, translate the values while keeping every key, and add the language
code to `Localizer.AvailableLanguages`.

## Rules

- Keep keys stable; do not rename keys for grammar changes.
- Preserve placeholders such as `{0}` and `{1}`.
- Do not translate command names, protocol terms, or IDs.
- Missing keys fall back to `en-US`; they are never silently dropped.
- UI code uses `Localizer.Get(key, args...)` or the indexer; do not hard-code
  English text.

## Crowdin

`crowdin.yml` maps the English source to translated files:

```text
/src/FreeChat.Dalamud/Localization/en-US.json
  -> /src/FreeChat.Dalamud/Localization/%locale%.json
```

Set `CROWDIN_PROJECT_ID` and `CROWDIN_PERSONAL_TOKEN` in the environment, then
run the Crowdin CLI. Do not commit the token. Pull translations before a release
and review the generated JSON diff.

## Adding a string

1. Add the key to `en-US.json` and `zh-CN.json`.
2. Use it from `MainWindow` (or another UI type).
3. Rebuild the plugin and check both languages in the **Settings** tab.
4. For error text, localize only the display message; keep the underlying
   protocol error code unchanged.
