using System.Text.RegularExpressions;
using FreeChat.Protocol.V1;

namespace FreeChat.Server;

public sealed class FilterValidationException : Exception
{
    public FilterValidationException(ErrorCode code, string message) : base(message) => this.Code = code;

    public ErrorCode Code { get; }
}

public static class ChatCategoryMapper
{
    public static ChatCategory FromChannel(ChatChannel channel) => channel switch
    {
        ChatChannel.Say or ChatChannel.Shout or ChatChannel.Yell => ChatCategory.PlayerChat,
        ChatChannel.Tell => ChatCategory.Tell,
        ChatChannel.Party => ChatCategory.Party,
        ChatChannel.Alliance => ChatCategory.Alliance,
        ChatChannel.FreeCompany => ChatCategory.FreeCompany,
        ChatChannel.Linkshell1 or ChatChannel.Linkshell2 or ChatChannel.Linkshell3
            or ChatChannel.Linkshell4 or ChatChannel.Linkshell5 or ChatChannel.Linkshell6
            or ChatChannel.Linkshell7 or ChatChannel.Linkshell8 => ChatCategory.Linkshell,
        ChatChannel.CrossLinkshell1 or ChatChannel.CrossLinkshell2 or ChatChannel.CrossLinkshell3
            or ChatChannel.CrossLinkshell4 or ChatChannel.CrossLinkshell5 or ChatChannel.CrossLinkshell6
            or ChatChannel.CrossLinkshell7 or ChatChannel.CrossLinkshell8 => ChatCategory.CrossLinkshell,
        ChatChannel.NoviceNetwork => ChatCategory.NoviceNetwork,
        ChatChannel.PvpTeam => ChatCategory.PvpTeam,
        ChatChannel.System => ChatCategory.System,
        ChatChannel.Error => ChatCategory.Error,
        ChatChannel.Npc => ChatCategory.Npc,
        ChatChannel.Battle => ChatCategory.Battle,
        ChatChannel.Loot => ChatCategory.Loot,
        ChatChannel.Progress => ChatCategory.Progress,
        ChatChannel.Echo => ChatCategory.Echo,
        ChatChannel.Emote => ChatCategory.Emote,
        _ => ChatCategory.Other,
    };

    public static ChatCategory FromLogKind(uint logKind) => logKind switch
    {
        10 or 11 or 30 => ChatCategory.PlayerChat, // Say / Shout / Yell
        12 or 13 or 80 => ChatCategory.Tell,
        14 or 32 or 84 => ChatCategory.Party,
        15 => ChatCategory.Alliance,
        36 => ChatCategory.PvpTeam,
        16 or 17 or 18 or 19 or 20 or 21 or 22 or 23
            or 86 or 87 or 88 or 89 or 90 or 91 or 92 or 93 => ChatCategory.Linkshell,
        24 or 85 => ChatCategory.FreeCompany,
        27 or 94 => ChatCategory.NoviceNetwork,
        28 or 29 => ChatCategory.Emote,
        37 or 101 or 102 or 103 or 104 or 105 or 106 or 107 => ChatCategory.CrossLinkshell,
        41 or 42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 or 58 => ChatCategory.Battle,
        55 or 57 or 59 or 71 or 72 or 73 or 75 or 76 => ChatCategory.System,
        60 => ChatCategory.Error,
        61 or 68 => ChatCategory.Npc,
        62 or 65 => ChatCategory.Loot,
        64 => ChatCategory.Progress,
        56 => ChatCategory.Echo,
        _ => ChatCategory.Other,
    };
}

public sealed class ChatFilterMatcher
{
    private readonly ChatFilter filter;
    private readonly HashSet<ChatCategory> includeCategories;
    private readonly HashSet<ChatCategory> excludeCategories;
    private readonly HashSet<uint> includeLogKinds;
    private readonly HashSet<uint> excludeLogKinds;
    private readonly Regex? regex;

    public ChatFilterMatcher(ChatFilter filter)
    {
        if (filter.Regex.Length > 256)
            throw new FilterValidationException(ErrorCode.RegexInvalid, "Regex is longer than 256 characters.");

        this.filter = filter.Clone();
        this.includeCategories = filter.IncludeCategories.ToHashSet();
        this.excludeCategories = filter.ExcludeCategories.ToHashSet();
        this.includeLogKinds = filter.IncludeLogKinds.ToHashSet();
        this.excludeLogKinds = filter.ExcludeLogKinds.ToHashSet();

        if (string.IsNullOrEmpty(filter.Regex))
        {
            this.regex = null;
        }
        else
        {
            try
            {
                var options = RegexOptions.NonBacktracking;
                if (!filter.CaseSensitive)
                    options |= RegexOptions.IgnoreCase;
                this.regex = new Regex(filter.Regex, options, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException ex)
            {
                throw new FilterValidationException(ErrorCode.RegexInvalid, ex.Message);
            }
        }
    }

    public ChatFilter Filter => this.filter;

    public bool Matches(ChatEvent chatEvent)
    {
        var category = chatEvent.Channel == ChatChannel.Unspecified
            ? ChatCategoryMapper.FromLogKind(chatEvent.LogKind)
            : ChatCategoryMapper.FromChannel(chatEvent.Channel);

        if (this.includeCategories.Count > 0 && !this.includeCategories.Contains(category))
            return false;
        if (this.excludeCategories.Contains(category))
            return false;

        if (this.includeLogKinds.Count > 0 && !this.includeLogKinds.Contains(chatEvent.LogKind))
            return false;
        if (this.excludeLogKinds.Contains(chatEvent.LogKind))
            return false;

        if (!this.filter.IncludeBattle && category == ChatCategory.Battle)
            return false;

        var includeOwn = this.filter.HasIncludeOwn ? this.filter.IncludeOwn : true;
        if (!includeOwn && chatEvent.Outgoing)
            return false;

        if (!string.IsNullOrEmpty(this.filter.Keyword))
        {
            var comparison = this.filter.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (!chatEvent.Text.Contains(this.filter.Keyword, comparison))
                return false;
        }

        if (this.regex is not null)
        {
            try
            {
                if (!this.regex.IsMatch(chatEvent.Text))
                    return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }
}
