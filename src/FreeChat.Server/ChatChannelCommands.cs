using FreeChat.Protocol.V1;

namespace FreeChat.Server;

public static class ChatChannelCommands
{
    public static string Prefix(ChatChannel channel) => channel switch
    {
        ChatChannel.Say => "/say ",
        ChatChannel.Shout => "/shout ",
        ChatChannel.Yell => "/yell ",
        ChatChannel.Party => "/party ",
        ChatChannel.Alliance => "/alliance ",
        ChatChannel.FreeCompany => "/freecompany ",
        ChatChannel.Linkshell1 => "/l1 ",
        ChatChannel.Linkshell2 => "/l2 ",
        ChatChannel.Linkshell3 => "/l3 ",
        ChatChannel.Linkshell4 => "/l4 ",
        ChatChannel.Linkshell5 => "/l5 ",
        ChatChannel.Linkshell6 => "/l6 ",
        ChatChannel.Linkshell7 => "/l7 ",
        ChatChannel.Linkshell8 => "/l8 ",
        ChatChannel.CrossLinkshell1 => "/cwl1 ",
        ChatChannel.CrossLinkshell2 => "/cwl2 ",
        ChatChannel.CrossLinkshell3 => "/cwl3 ",
        ChatChannel.CrossLinkshell4 => "/cwl4 ",
        ChatChannel.CrossLinkshell5 => "/cwl5 ",
        ChatChannel.CrossLinkshell6 => "/cwl6 ",
        ChatChannel.CrossLinkshell7 => "/cwl7 ",
        ChatChannel.CrossLinkshell8 => "/cwl8 ",
        ChatChannel.NoviceNetwork => "/n ",
        ChatChannel.PvpTeam => "/pvpteam ",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel cannot be used for sending."),
    };

    public static string TellPrefix(string name, string world)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
            throw new ArgumentException("Tell target requires a name and world.");

        return $"/tell {name}@{world} ";
    }
}
