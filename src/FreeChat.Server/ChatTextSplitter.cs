using System.Text;

namespace FreeChat.Server;

public static class ChatTextSplitter
{
    public const int MaxBytes = 500;

    /// <summary>
    /// Splits a chat body into parts that fit the game's UTF-8 byte limit,
    /// repeating the command prefix on every part.
    /// </summary>
    public static string[] SplitWithPrefix(string prefix, string text, int maxBytes = MaxBytes)
    {
        if (text.IndexOf('\0') >= 0)
            throw new ArgumentException("Embedded NUL in chat text.", nameof(text));
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Chat text is empty.", nameof(text));

        var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        if (prefixBytes >= maxBytes)
            throw new ArgumentException("Chat command prefix leaves no room for text.", nameof(prefix));

        var bodyBudget = maxBytes - prefixBytes;
        if (Encoding.UTF8.GetByteCount(text) <= bodyBudget)
            return [prefix + text];

        var parts = new List<string>();
        var builder = new StringBuilder(prefix);
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > bodyBudget)
            {
                parts.Add(builder.ToString());
                builder.Clear().Append(prefix);
                used = 0;
            }

            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }

        if (used > 0)
            parts.Add(builder.ToString());

        return parts.ToArray();
    }
}
