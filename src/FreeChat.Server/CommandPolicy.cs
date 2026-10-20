namespace FreeChat.Server;

public static class CommandPolicy
{
    public static readonly IReadOnlySet<string> DefaultBlocked = new HashSet<string>(
        new[]
        {
            "/logout",
            "/quit",
            "/shutdown",
            "/exit",
            "/delete",
            "/resign",
        },
        StringComparer.OrdinalIgnoreCase);

    public static string? GetCommandName(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return null;
        if (trimmed[0] == '\uFF0F')
            trimmed = "/" + trimmed[1..];
        if (trimmed[0] != '/')
            return null;

        var end = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return end < 0 ? trimmed : trimmed[..end];
    }

    public static bool IsBlocked(string text, IEnumerable<string>? blocked = null)
    {
        var command = GetCommandName(text);
        if (command is null)
            return false;

        var set = blocked as IReadOnlySet<string>
                  ?? new HashSet<string>(blocked ?? DefaultBlocked, StringComparer.OrdinalIgnoreCase);
        return set.Contains(command);
    }
}
