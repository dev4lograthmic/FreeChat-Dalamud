using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FreeChat.Protocol.V1;
using Lumina.Excel.Sheets;

namespace FreeChat.Dalamud.Chat;

/// <summary>
/// Converts a game SeString into both the legacy segment list and the
/// link-aware chunk list. Chunks are the preferred representation: text and
/// its link metadata live in the same run, so clients do not need offsets.
/// </summary>
public sealed class ChatEnricher
{
    private readonly IDataManager data;

    public ChatEnricher(IDataManager data) => this.data = data;

    public ChatEnrichment Enrich(SeString message)
    {
        var segments = new List<Segment>();
        var chunks = new List<ChatChunk>();
        var italic = false;
        var foreground = new Stack<uint>();
        var glow = new Stack<uint>();
        PendingLink? pending = null;

        TextStyle CurrentStyle() => new()
        {
            ForegroundRgba = foreground.Count > 0 ? foreground.Peek() : 0,
            GlowRgba = glow.Count > 0 ? glow.Peek() : 0,
            Italic = italic,
        };

        void AddText(string text, PendingLink? link = null)
        {
            if (string.IsNullOrEmpty(text))
                return;

            segments.Add(new Segment
            {
                Style = CurrentStyle(),
                Text = new TextSegment { Text = text },
            });

            var chunk = new ChatChunk
            {
                Style = CurrentStyle(),
                Text = new ChatTextChunk { Text = text },
            };
            link?.Apply(chunk.Text);
            chunks.Add(chunk);

            if (link is not null)
                link.HasText = true;
        }

        void FlushPendingFallback()
        {
            if (pending is null || pending.HasText)
                return;

            var text = pending.FallbackText();
            if (!string.IsNullOrEmpty(text))
            {
                var chunk = new ChatChunk
                {
                    Style = CurrentStyle(),
                    Text = new ChatTextChunk { Text = text },
                };
                pending.Apply(chunk.Text);
                chunks.Add(chunk);
            }
        }

        void SetPending(PendingLink link)
        {
            FlushPendingFallback();
            pending = link;
        }

        foreach (var payload in message.Payloads)
        {
            switch (payload)
            {
                case EmphasisItalicPayload emphasis:
                    italic = emphasis.IsEnabled;
                    break;

                case UIForegroundPayload fg:
                    if (fg.IsEnabled)
                        foreground.Push(fg.UIColor.Value.Dark);
                    else if (foreground.Count > 0)
                        foreground.Pop();
                    break;

                case UIGlowPayload gl:
                    if (gl.IsEnabled)
                        glow.Push(gl.UIColor.Value.Light);
                    else if (glow.Count > 0)
                        glow.Pop();
                    break;

                case IconPayload icon:
                    segments.Add(new Segment
                    {
                        Style = CurrentStyle(),
                        Icon = new IconSegment { IconId = (uint)icon.Icon },
                    });
                    chunks.Add(new ChatChunk
                    {
                        Style = CurrentStyle(),
                        Icon = new ChatIconChunk { IconId = (uint)icon.Icon },
                    });
                    break;

                case AutoTranslatePayload auto:
                {
                    // Auto-translate is display text with its own metadata; it
                    // does not wait for a separate link label.
                    if (!string.IsNullOrEmpty(auto.Text))
                    {
                        segments.Add(new Segment
                        {
                            Style = CurrentStyle(),
                            Text = new TextSegment { Text = auto.Text },
                        });

                        var chunk = new ChatChunk
                        {
                            Style = CurrentStyle(),
                            Text = new ChatTextChunk
                            {
                                Text = auto.Text,
                                AutoTranslate = new AutoTranslateSegment { Text = auto.Text },
                            },
                        };
                        chunks.Add(chunk);
                    }

                    break;
                }

                case ItemPayload item:
                {
                    var itemSegment = CreateItemSegment(item);
                    segments.Add(new Segment
                    {
                        Style = CurrentStyle(),
                        Item = itemSegment,
                    });
                    SetPending(PendingLink.ForItem(itemSegment));
                    break;
                }

                case MapLinkPayload map:
                {
                    var mapSegment = CreateMapSegment(map);
                    segments.Add(new Segment
                    {
                        Style = CurrentStyle(),
                        Map = mapSegment,
                    });
                    SetPending(PendingLink.ForMap(mapSegment));
                    break;
                }

                case PartyFinderPayload partyFinder:
                {
                    var partyFinderSegment = new PartyFinderSegment
                    {
                        ListingId = partyFinder.ListingId,
                        LinkType = MapPartyFinderLinkType(partyFinder.LinkType),
                    };
                    segments.Add(new Segment
                    {
                        Style = CurrentStyle(),
                        PartyFinder = partyFinderSegment,
                    });
                    SetPending(PendingLink.ForPartyFinder(partyFinderSegment));
                    break;
                }

                case QuestPayload quest:
                {
                    var questSegment = CreateQuestSegment(quest);
                    segments.Add(new Segment
                    {
                        Style = CurrentStyle(),
                        Quest = questSegment,
                    });
                    SetPending(PendingLink.ForQuest(questSegment));
                    break;
                }

                case PlayerPayload player:
                {
                    var playerSegment = new PlayerSegment
                    {
                        Name = player.PlayerName,
                        WorldId = player.World.RowId,
                        WorldName = player.World.IsValid
                            ? player.World.Value.Name.ExtractText()
                            : string.Empty,
                    };
                    segments.Add(new Segment
                    {
                        Style = CurrentStyle(),
                        Player = playerSegment,
                    });
                    SetPending(PendingLink.ForPlayer(playerSegment));
                    break;
                }

                case StatusPayload status:
                {
                    var statusSegment = new StatusSegment
                    {
                        StatusId = status.Status.RowId,
                        Name = status.Status.IsValid
                            ? status.Status.Value.Name.ExtractText()
                            : string.Empty,
                        IconId = status.Status.IsValid ? status.Status.Value.Icon : 0,
                    };
                    segments.Add(new Segment
                    {
                        Style = CurrentStyle(),
                        Status = statusSegment,
                    });
                    SetPending(PendingLink.ForStatus(statusSegment));
                    break;
                }

                case RawPayload raw when IsLinkTerminator(raw):
                    FlushPendingFallback();
                    pending = null;
                    break;

                case ITextProvider text:
                    AddText(text.Text, pending);
                    break;
            }
        }

        FlushPendingFallback();
        return new ChatEnrichment(segments, chunks);
    }

    /// <summary>Legacy segments for clients that do not understand chunks.</summary>
    public List<Segment> Convert(SeString message) => this.Enrich(message).Segments;

    /// <summary>Link-aware chunks. Prefer these over <see cref="Convert"/>.</summary>
    public List<ChatChunk> ConvertChunks(SeString message) => this.Enrich(message).Chunks;

    private ItemSegment CreateItemSegment(ItemPayload item)
    {
        var itemRow = this.data.GetExcelSheet<Item>().GetRowOrDefault(item.ItemId);
        var segment = new ItemSegment
        {
            ItemId = item.ItemId,
            Hq = item.IsHQ,
            ItemKind = (uint)item.Kind,
            Name = itemRow?.Name.ExtractText() ?? string.Empty,
            Description = Truncate(itemRow?.Description.ExtractText() ?? string.Empty),
            IconId = itemRow?.Icon ?? 0,
        };

        if (itemRow is not { } row)
            return segment;

        segment.ItemLevel = row.LevelItem.RowId;
        segment.EquipLevel = row.LevelEquip;
        segment.Rarity = row.Rarity;
        segment.Category = row.ItemUICategory.IsValid
            ? row.ItemUICategory.Value.Name.ExtractText()
            : string.Empty;
        segment.MateriaSlots = row.MateriaSlotCount;
        segment.AdvancedMelding = row.IsAdvancedMeldingPermitted;

        for (var i = 0; i < row.BaseParam.Count && i < row.BaseParamValue.Count; i++)
        {
            var value = row.BaseParamValue[i];
            var parameter = row.BaseParam[i];
            if (value == 0 || !parameter.IsValid)
                continue;

            var name = parameter.Value.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;

            segment.Stats.Add($"{name} {value:+0;-0}");
        }

        return segment;
    }

    private static MapSegment CreateMapSegment(MapLinkPayload map)
    {
        var segment = new MapSegment
        {
            MapId = map.Map.RowId,
            TerritoryId = map.TerritoryType.RowId,
            X = map.XCoord,
            Y = map.YCoord,
        };

        if (!map.Map.IsValid)
            return segment;

        var row = map.Map.Value;
        segment.PlaceName = row.PlaceName.IsValid
            ? row.PlaceName.Value.Name.ExtractText()
            : string.Empty;
        segment.MapFilenameId = row.Id.ExtractText();
        segment.SizeFactor = row.SizeFactor;
        return segment;
    }

    private static QuestSegment CreateQuestSegment(QuestPayload payload)
    {
        var quest = payload.Quest;
        return new QuestSegment
        {
            QuestId = quest.RowId,
            Name = quest.IsValid ? quest.Value.Name.ExtractText() : string.Empty,
        };
    }

    private static PartyFinderLinkType MapPartyFinderLinkType(
        PartyFinderPayload.PartyFinderLinkType linkType) => linkType switch
    {
        PartyFinderPayload.PartyFinderLinkType.LimitedToHomeWorld
            => PartyFinderLinkType.LimitedToHomeWorld,
        PartyFinderPayload.PartyFinderLinkType.PartyFinderNotification
            => PartyFinderLinkType.Notification,
        _ => PartyFinderLinkType.Unspecified,
    };

    private static bool IsLinkTerminator(RawPayload payload)
    {
        var terminator = RawPayload.LinkTerminator.Data;
        return payload.Data.AsSpan().SequenceEqual(terminator);
    }

    private static string Truncate(string value, int maxLength = 1024) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed class PendingLink
    {
        public ItemSegment? Item { get; init; }
        public MapSegment? Map { get; init; }
        public PartyFinderSegment? PartyFinder { get; init; }
        public QuestSegment? Quest { get; init; }
        public PlayerSegment? Player { get; init; }
        public StatusSegment? Status { get; init; }
        public bool HasText { get; set; }

        public static PendingLink ForItem(ItemSegment item) => new() { Item = item };
        public static PendingLink ForMap(MapSegment map) => new() { Map = map };
        public static PendingLink ForPartyFinder(PartyFinderSegment partyFinder) => new()
        {
            PartyFinder = partyFinder,
        };
        public static PendingLink ForQuest(QuestSegment quest) => new() { Quest = quest };
        public static PendingLink ForPlayer(PlayerSegment player) => new() { Player = player };
        public static PendingLink ForStatus(StatusSegment status) => new() { Status = status };

        public void Apply(ChatTextChunk chunk)
        {
            if (this.Item is not null)
                chunk.Item = this.Item.Clone();
            else if (this.Map is not null)
                chunk.Map = this.Map.Clone();
            else if (this.PartyFinder is not null)
                chunk.PartyFinder = this.PartyFinder.Clone();
            else if (this.Quest is not null)
                chunk.Quest = this.Quest.Clone();
            else if (this.Player is not null)
                chunk.Player = this.Player.Clone();
            else if (this.Status is not null)
                chunk.Status = this.Status.Clone();
        }

        public string FallbackText()
        {
            if (this.Item is not null)
                return string.IsNullOrEmpty(this.Item.Name)
                    ? $"Item #{this.Item.ItemId}"
                    : this.Item.Name;

            if (this.Map is not null)
            {
                var label = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0} ({1:0.0}, {2:0.0})",
                    this.Map.PlaceName,
                    this.Map.X,
                    this.Map.Y);
                return string.IsNullOrWhiteSpace(this.Map.PlaceName)
                    ? $"Map #{this.Map.MapId}"
                    : label.TrimStart();
            }

            if (this.PartyFinder is not null)
                return $"Party Finder #{this.PartyFinder.ListingId}";

            if (this.Quest is not null)
                return string.IsNullOrEmpty(this.Quest.Name)
                    ? $"Quest #{this.Quest.QuestId}"
                    : this.Quest.Name;

            if (this.Player is not null)
                return this.Player.Name;

            if (this.Status is not null)
                return this.Status.Name;

            return string.Empty;
        }
    }
}

public sealed record ChatEnrichment(List<Segment> Segments, List<ChatChunk> Chunks);
