using FreeChat.Protocol.V1;
using FreeChat.Server;
using Xunit;

namespace FreeChat.Server.Tests;

public sealed class SendHelperTests
{
    [Fact]
    public void SplitWithPrefix_RepeatsPrefixAndHonorsUtf8Limit()
    {
        var parts = ChatTextSplitter.SplitWithPrefix("/p ", new string('a', 1200));

        Assert.True(parts.Length >= 3);
        Assert.All(parts, part => Assert.EndsWith("a", part));
        Assert.All(parts, part => Assert.True(System.Text.Encoding.UTF8.GetByteCount(part) <= 500));
        Assert.All(parts, part => Assert.StartsWith("/p ", part));
    }

    [Fact]
    public void SplitWithPrefix_HandlesMultiByteRunes()
    {
        var parts = ChatTextSplitter.SplitWithPrefix("/s ", string.Concat(Enumerable.Repeat("你好", 300)));

        Assert.All(parts, part => Assert.True(System.Text.Encoding.UTF8.GetByteCount(part) <= 500));
        Assert.All(parts, part => Assert.StartsWith("/s ", part));
        Assert.Contains("你", parts[0]);
    }

    [Fact]
    public void CommandPolicy_BlocksDefaultAccountCommandsOnly()
    {
        Assert.True(CommandPolicy.IsBlocked("/logout"));
        Assert.True(CommandPolicy.IsBlocked("  /QUIT now"));
        Assert.True(CommandPolicy.IsBlocked("\t/logout"));
        Assert.True(CommandPolicy.IsBlocked("/logout now"));
        Assert.True(CommandPolicy.IsBlocked("／logout"));
        Assert.False(CommandPolicy.IsBlocked("/party hello"));
        Assert.False(CommandPolicy.IsBlocked("hello /logout"));
        Assert.Equal("/tell", CommandPolicy.GetCommandName("/tell Someone@Typhon hi"));
    }

    [Fact]
    public void ChannelPrefix_MapsCommonChannels()
    {
        Assert.Equal("/say ", ChatChannelCommands.Prefix(ChatChannel.Say));
        Assert.Equal("/party ", ChatChannelCommands.Prefix(ChatChannel.Party));
        Assert.Equal("/cwl1 ", ChatChannelCommands.Prefix(ChatChannel.CrossLinkshell1));
        Assert.Equal("/tell Friend@Typhon ", ChatChannelCommands.TellPrefix("Friend", "Typhon"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChatChannelCommands.Prefix(ChatChannel.Tell));
    }
}
