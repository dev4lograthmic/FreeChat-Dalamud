namespace FreeChat.Cli;

internal sealed class CliException : Exception
{
    public CliException(string message, int exitCode = 1) : base(message) => this.ExitCode = exitCode;

    public int ExitCode { get; }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                throw new CliException($"Unexpected argument: {arg}");

            var name = arg[2..];
            string? value = null;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            options.values[name] = value;
        }

        return options;
    }

    public bool Has(string name) => this.values.ContainsKey(name);

    public string? Get(string name) =>
        this.values.TryGetValue(name, out var value) ? value : null;

    public string Get(string name, string fallback) => this.Get(name) ?? fallback;

    public int GetInt(string name, int fallback)
    {
        var value = this.Get(name);
        if (value is null)
            return fallback;

        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new CliException($"Invalid integer for --{name}: {value}");
    }

    public long GetLong(string name, long fallback)
    {
        var value = this.Get(name);
        if (value is null)
            return fallback;

        return long.TryParse(value, out var parsed)
            ? parsed
            : throw new CliException($"Invalid integer for --{name}: {value}");
    }

    public string Require(string name) =>
        this.Get(name) ?? throw new CliException($"Missing required option --{name}.");
}
