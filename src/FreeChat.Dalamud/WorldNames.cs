using Dalamud.Utility;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FreeChat.Dalamud;

/// <summary>
/// Resolves world ids to protocol display names.
/// World id 0 is a development placeholder row in the game's World sheet
/// (its name is "Dev"), so a zero id must be reported as an unknown world
/// instead of leaking the placeholder name to clients.
/// </summary>
internal static class WorldNames
{
    public static string Resolve(ExcelSheet<World> worlds, uint worldId)
    {
        if (worldId == 0)
            return string.Empty;

        return worlds.GetRowOrDefault(worldId)?.Name.ExtractText() ?? string.Empty;
    }

    public static string Resolve(RowRef<World> world)
    {
        if (world.RowId == 0 || !world.IsValid)
            return string.Empty;

        return world.Value.Name.ExtractText();
    }
}
