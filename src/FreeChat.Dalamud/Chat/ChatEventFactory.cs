using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FreeChat.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace FreeChat.Dalamud.Chat;

public sealed class ChatEventFactory
{
    private readonly ChatEnricher enricher;
    private readonly Func<ChatChannel, string?>? channelName;

    public ChatEventFactory(ChatEnricher enricher, Func<ChatChannel, string?>? channelName = null)
    {
        this.enricher = enricher;
        this.channelName = channelName;
    }

    public ChatEvent Create(IChatMessage message, ChatSource source, string? localPlayerName)
    {
        var kind = (ushort)message.LogKind;
        var channel = MapChannel(kind);
        var sender = CreateCharacter(message.Sender, localPlayerName);
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
                ? sourceEntity.HomeWorld.Value.Name.ExtractText()
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

    private static CharacterRef CreateCharacter(SeString sender, string? localPlayerName)
    {
        var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is not null)
        {
            return new CharacterRef
            {
                Name = player.PlayerName,
                WorldId = player.World.RowId,
                WorldName = player.World.Value.Name.ExtractText(),
                IsPlayer = true,
                IsLocal = localPlayerName is not null && player.PlayerName == localPlayerName,
            };
        }

        var text = ExtractText(sender);
        return new CharacterRef
        {
            Name = text,
            IsPlayer = false,
            IsLocal = false,
        };
    }

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
