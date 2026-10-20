using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FreeChat.Protocol.V1;
using Lumina.Excel.Sheets;

namespace FreeChat.Dalamud.Chat;

public sealed class ChatEnricher
{
    private readonly IDataManager data;

    public ChatEnricher(IDataManager data) => this.data = data;

    public List<Segment> Convert(SeString message)
    {
        var segments = new List<Segment>();
        var italic = false;
        var foreground = new Stack<uint>();
        var glow = new Stack<uint>();

        void AddText(string text)
        {
            segments.Add(new Segment
            {
                Style = new TextStyle
                {
                    ForegroundRgba = foreground.Count > 0 ? foreground.Peek() : 0,
                    GlowRgba = glow.Count > 0 ? glow.Peek() : 0,
                    Italic = italic,
                },
                Text = new TextSegment { Text = text },
            });
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
                        Icon = new IconSegment { IconId = (uint)icon.Icon },
                    });
                    break;
                case AutoTranslatePayload auto:
                    AddText(auto.Text);
                    break;
                case ItemPayload item:
                    var itemRow = this.data.GetExcelSheet<Item>().GetRowOrDefault(item.ItemId);
                    segments.Add(new Segment
                    {
                        Item = new ItemSegment
                        {
                            ItemId = item.ItemId,
                            Hq = item.IsHQ,
                            ItemKind = (uint)item.Kind,
                            Name = itemRow?.Name.ExtractText() ?? string.Empty,
                            Description = itemRow?.Description.ExtractText() ?? string.Empty,
                            IconId = itemRow?.Icon ?? 0,
                        },
                    });
                    break;
                case MapLinkPayload map:
                    segments.Add(new Segment
                    {
                        Map = new MapSegment
                        {
                            MapId = map.Map.RowId,
                            TerritoryId = map.TerritoryType.RowId,
                            X = map.XCoord,
                            Y = map.YCoord,
                            PlaceName = map.Map.Value.PlaceName.Value.Name.ExtractText(),
                            SizeFactor = map.Map.Value.SizeFactor,
                        },
                    });
                    break;
                case PlayerPayload player:
                    segments.Add(new Segment
                    {
                        Player = new PlayerSegment
                        {
                            Name = player.PlayerName,
                            WorldId = player.World.RowId,
                            WorldName = player.World.Value.Name.ExtractText(),
                        },
                    });
                    break;
                case StatusPayload status:
                    segments.Add(new Segment
                    {
                        Status = new StatusSegment
                        {
                            StatusId = status.Status.RowId,
                            Name = status.Status.Value.Name.ExtractText(),
                            IconId = status.Status.Value.Icon,
                        },
                    });
                    break;
                case ITextProvider text:
                    AddText(text.Text);
                    break;
            }
        }

        return segments;
    }
}
