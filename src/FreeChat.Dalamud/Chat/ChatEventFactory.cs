using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FreeChat.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace FreeChat.Dalamud.Chat;

public sealed class ChatEventFactory
{
    private readonly ChatEnricher enricher;
    private readonly Func<ChatChannel, string?>? channelName;
    private readonly IPlayerState? playerState;

    public ChatEventFactory(
        ChatEnricher enricher,
        Func<ChatChannel, string?>? channelName = null,
        IPlayerState? playerState = null)
    {
        this.enricher = enricher;
        this.channelName = channelName;
        this.playerState = playerState;
    }

    public ChatEvent Create(IChatMessage message, ChatSource source, string? localPlayerName)
    {
        var kind = (ushort)message.LogKind;
        var channel = MapChannel(kind);
        var sender = CreateCharacter(
            message.Sender,
            localPlayerName,
            IsPlayerKind(kind),
            channel == ChatChannel.Tell);
        var chatEvent = new ChatEvent
        {
            Channel = channel,
            LogKind = kind,
            Source = source,
            Sender = sender,
            ChannelName = this.channelName?.Invoke(channel) ?? string.Empty,
            Text = ExtractText(message.Message),
            RawSender = ByteString.CopyFrom(message.Sender.Encode()),
            RawMessage = ByteString.CopyFrom(message.Message.Encode()),
            Outgoing = kind == (ushort)XivChatType.TellOutgoing
                       || (localPlayerName is not null && sender.Name == localPlayerName),
            ContentMode = ContentMode.Current,
            Timestamp = message.Timestamp <= 0
                ? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                : Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(message.Timestamp)),
        };

        try
        {
            var enrichment = this.enricher.Enrich(message.Message);
            chatEvent.Segments.AddRange(enrichment.Segments);
            chatEvent.Chunks.AddRange(enrichment.Chunks);
        }
        catch
        {
            // Keep text/raw even if a payload cannot be enriched.
        }

        SetOriginalFields(chatEvent, message);

        if (kind is (ushort)XivChatType.TellIncoming or (ushort)XivChatType.TellOutgoing
            && sender.IsPlayer)
        {
            chatEvent.TellPeer = sender.Clone();
        }

        return chatEvent;
    }

    /// <summary>
    /// Captures the message/sender before other plugins modified them. The
    /// original fields are only emitted when they differ from the current
    /// stage, so unmodified events keep their existing shape.
    /// </summary>
    private static void SetOriginalFields(ChatEvent chatEvent, IChatMessage message)
    {
        var currentMessage = message.Message.Encode();
        var originalMessage = (ReadOnlySpan<byte>)message.OriginalMessage;
        if (!currentMessage.AsSpan().SequenceEqual(originalMessage))
        {
            chatEvent.OriginalText = message.OriginalMessage.ExtractText();
            chatEvent.OriginalRawMessage = ByteString.CopyFrom(originalMessage.ToArray());
        }

        var currentSender = message.Sender.Encode();
        var originalSender = (ReadOnlySpan<byte>)message.OriginalSender;
        if (!currentSender.AsSpan().SequenceEqual(originalSender))
            chatEvent.OriginalRawSender = ByteString.CopyFrom(originalSender.ToArray());
    }

    public ChatEvent CreateStructured(ILogMessage message, string? localPlayerName)
    {
        var sourceEntity = message.SourceEntity;
        var sender = new CharacterRef
        {
            Name = sourceEntity?.Name.ToString() ?? string.Empty,
            WorldId = sourceEntity?.HomeWorldId ?? 0,
            WorldName = sourceEntity is { IsPlayer: true }
                ? WorldNames.Resolve(sourceEntity.HomeWorld)
                : string.Empty,
            IsPlayer = sourceEntity?.IsPlayer ?? false,
            IsLocal = localPlayerName is not null && sourceEntity?.Name.ToString() == localPlayerName,
        };

        var text = message.FormatLogMessageForDebugging().ExtractText();
        var chatEvent = new ChatEvent
        {
            Channel = sourceEntity is not null || message.TargetEntity is not null
                ? ChatChannel.Battle
                : ChatChannel.System,
            LogKind = 0,
            Source = ChatSource.Structured,
            Sender = sender,
            Text = text,
            Outgoing = false,
            ContentMode = ContentMode.Current,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        if (!string.IsNullOrEmpty(text))
        {
            chatEvent.Chunks.Add(new ChatChunk
            {
                Text = new ChatTextChunk { Text = text },
            });
        }

        return chatEvent;
    }

    private CharacterRef CreateCharacter(
        SeString sender,
        string? localPlayerName,
        bool isPlayerKind,
        bool isTell)
    {
        var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is not null)
        {
            var worldId = player.World.RowId;
            var worldName = WorldNames.Resolve(player.World);
            if (isTell && worldId == 0 && worldName.Length == 0)
                (worldId, worldName) = this.LocalWorld();

            return new CharacterRef
            {
                Name = player.PlayerName,
                WorldId = worldId,
                WorldName = worldName,
                IsPlayer = true,
                IsLocal = localPlayerName is not null && player.PlayerName == localPlayerName,
            };
        }

        var text = ExtractText(sender).Trim();
        var name = text;
        var worldNameFromText = string.Empty;
        if (isPlayerKind)
        {
            // Same-world tells usually carry only the character name. Some
            // senders still render as "Name@World"; keep the world text even
            // when the id is unknown.
            var at = text.LastIndexOf('@');
            if (at > 0 && at < text.Length - 1)
            {
                name = text[..at].Trim();
                worldNameFromText = text[(at + 1)..].Trim();
            }
        }

        var senderWorldId = 0u;
        var senderWorldName = worldNameFromText;
        if (isTell && senderWorldName.Length == 0)
            (senderWorldId, senderWorldName) = this.LocalWorld();

        return new CharacterRef
        {
            Name = name,
            WorldId = senderWorldId,
            WorldName = senderWorldName,
            IsPlayer = isPlayerKind,
            IsLocal = isPlayerKind && localPlayerName is not null && name == localPlayerName,
        };
    }

    private (uint Id, string Name) LocalWorld()
    {
        if (this.playerState is not { IsLoaded: true })
            return (0, string.Empty);

        var world = this.playerState.CurrentWorld;
        return (world.RowId, WorldNames.Resolve(world));
    }

    /// <summary>
    /// Whether the chat type is produced by a player character. Used when the
    /// game omits PlayerPayload (for example, same-world tells).
    /// </summary>
    private static bool IsPlayerKind(ushort kind) => (XivChatType)kind is
        XivChatType.Say
        or XivChatType.Shout
        or XivChatType.Yell
        or XivChatType.TellIncoming
        or XivChatType.TellOutgoing
        or XivChatType.GmTell
        or XivChatType.Party
        or XivChatType.CrossParty
        or XivChatType.GmParty
        or XivChatType.Alliance
        or XivChatType.FreeCompany
        or XivChatType.GmFreeCompany
        or XivChatType.Ls1
        or XivChatType.Ls2
        or XivChatType.Ls3
        or XivChatType.Ls4
        or XivChatType.Ls5
        or XivChatType.Ls6
        or XivChatType.Ls7
        or XivChatType.Ls8
        or XivChatType.CrossLinkShell1
        or XivChatType.CrossLinkShell2
        or XivChatType.CrossLinkShell3
        or XivChatType.CrossLinkShell4
        or XivChatType.CrossLinkShell5
        or XivChatType.CrossLinkShell6
        or XivChatType.CrossLinkShell7
        or XivChatType.CrossLinkShell8
        or XivChatType.NoviceNetwork
        or XivChatType.PvPTeam
        or XivChatType.CustomEmote
        or XivChatType.StandardEmote;

    private static string ExtractText(SeString value) =>
        string.Concat(value.Payloads.OfType<ITextProvider>().Select(x => x.Text));

    private static ChatChannel MapChannel(ushort kind) => (XivChatType)kind switch
    {
        XivChatType.Say => ChatChannel.Say,
        XivChatType.Shout => ChatChannel.Shout,
        XivChatType.Yell => ChatChannel.Yell,
        XivChatType.TellIncoming or XivChatType.TellOutgoing or XivChatType.GmTell => ChatChannel.Tell,
        XivChatType.Party or XivChatType.CrossParty or XivChatType.GmParty => ChatChannel.Party,
        XivChatType.Alliance => ChatChannel.Alliance,
        XivChatType.FreeCompany or XivChatType.GmFreeCompany or XivChatType.FreeCompanyAnnouncement
            or XivChatType.FreeCompanyLoginLogout => ChatChannel.FreeCompany,
        XivChatType.Ls1 => ChatChannel.Linkshell1,
        XivChatType.Ls2 => ChatChannel.Linkshell2,
        XivChatType.Ls3 => ChatChannel.Linkshell3,
        XivChatType.Ls4 => ChatChannel.Linkshell4,
        XivChatType.Ls5 => ChatChannel.Linkshell5,
        XivChatType.Ls6 => ChatChannel.Linkshell6,
        XivChatType.Ls7 => ChatChannel.Linkshell7,
        XivChatType.Ls8 => ChatChannel.Linkshell8,
        XivChatType.CrossLinkShell1 => ChatChannel.CrossLinkshell1,
        XivChatType.CrossLinkShell2 => ChatChannel.CrossLinkshell2,
        XivChatType.CrossLinkShell3 => ChatChannel.CrossLinkshell3,
        XivChatType.CrossLinkShell4 => ChatChannel.CrossLinkshell4,
        XivChatType.CrossLinkShell5 => ChatChannel.CrossLinkshell5,
        XivChatType.CrossLinkShell6 => ChatChannel.CrossLinkshell6,
        XivChatType.CrossLinkShell7 => ChatChannel.CrossLinkshell7,
        XivChatType.CrossLinkShell8 => ChatChannel.CrossLinkshell8,
        XivChatType.NoviceNetwork or XivChatType.NoviceNetworkSystem => ChatChannel.NoviceNetwork,
        XivChatType.PvPTeam or XivChatType.PvpTeamAnnouncement or XivChatType.PvpTeamLoginLogout
            => ChatChannel.PvpTeam,
        XivChatType.SystemMessage or XivChatType.GatheringSystemMessage or XivChatType.Sign
            or XivChatType.RetainerSale or XivChatType.PeriodicRecruitmentNotification
            or XivChatType.Orchestrion or XivChatType.MessageBook => ChatChannel.System,
        XivChatType.SystemError or XivChatType.ErrorMessage => ChatChannel.Error,
        XivChatType.NPCDialogue or XivChatType.NPCDialogueAnnouncements => ChatChannel.Npc,
        XivChatType.Damage or XivChatType.Miss or XivChatType.Action or XivChatType.Item
            or XivChatType.Healing or XivChatType.GainBuff or XivChatType.GainDebuff
            or XivChatType.LoseBuff or XivChatType.LoseDebuff => ChatChannel.Battle,
        XivChatType.LootNotice or XivChatType.LootRoll => ChatChannel.Loot,
        XivChatType.Progress => ChatChannel.Progress,
        XivChatType.Echo => ChatChannel.Echo,
        XivChatType.CustomEmote or XivChatType.StandardEmote => ChatChannel.Emote,
        _ => ChatChannel.Other,
    };
}
