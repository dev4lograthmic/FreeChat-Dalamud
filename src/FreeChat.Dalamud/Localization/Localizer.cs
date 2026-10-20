using System.Globalization;
using System.Text.Json;

namespace FreeChat.Dalamud.Localization;

public sealed class Localizer
{
    public static readonly IReadOnlyList<string> AvailableLanguages = ["en-US", "zh-CN"];

    private readonly Dictionary<string, Dictionary<string, string>> catalogs = new(StringComparer.OrdinalIgnoreCase);
    private string language;

    public Localizer(string language)
    {
        foreach (var code in AvailableLanguages)
            this.catalogs[code] = LoadCatalog(code);

        this.language = this.catalogs.ContainsKey(language) ? language : "en-US";
    }

    public event Action? LanguageChanged;

    public string Language => this.language;

    public string this[string key] => this.Get(key);

    public string Get(string key, params object?[] args)
    {
        var value = this.Find(key);
        if (value is null)
            return key;

        return args.Length == 0
            ? value
            : string.Format(CultureInfo.CurrentCulture, value, args);
    }

    public void SetLanguage(string language)
    {
        if (!this.catalogs.ContainsKey(language))
            language = "en-US";

        if (string.Equals(this.language, language, StringComparison.OrdinalIgnoreCase))
            return;

        this.language = language;
        this.LanguageChanged?.Invoke();
    }

    private string? Find(string key)
    {
        if (this.catalogs.TryGetValue(this.language, out var current)
            && current.TryGetValue(key, out var value))
            return value;

        return this.catalogs.TryGetValue("en-US", out var fallback)
               && fallback.TryGetValue(key, out value)
            ? value
            : null;
    }

    private static Dictionary<string, string> LoadCatalog(string language)
    {
        var assembly = typeof(Localizer).Assembly;
        var resourceName = $"FreeChat.Dalamud.Localization.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
