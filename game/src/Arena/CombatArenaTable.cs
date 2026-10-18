using System.Xml.Linq;
using H5.Core.Xdb;

namespace H5Reborn.Arena;

/// <summary>
/// GameMechanics/CombatArenaTypes/CombatArenaTypes.xdb — the table H5_Game.exe picks
/// combat arenas from (FUN_008547a0, NDb::SCombatArenaTypes): one entry per terrain type
/// (index = terrain type: 0 dirt, 1 grass, 2 sand, 3 snow, 4 lava, 5 subterranean, 6 water,
/// 7 dwarven, 8–9 orcish), each with small/average/large lists of "new", water-side and
/// deep-water arenas. Entries are either references to Scenes/CombatArenas scenes or
/// inline AdventureFlybyScene definitions (which carry their own MountPoint).
/// </summary>
public static class CombatArenaTable
{
    public const string Path = "GameMechanics/CombatArenaTypes/CombatArenaTypes.xdb";
    public const int TerrainGrass = 1;

    public enum Size { Small, Average, Large }
    public enum Kind { New, Water, DeepWater }

    /// <summary>An arena choice: either a scene path or an inline scene element.</summary>
    public sealed record Entry(string? ScenePath, XElement? InlineScene, string Label);

    public static IReadOnlyList<Entry> Arenas(int terrain, Size size, Kind kind = Kind.New)
    {
        var root = Game.Instance.Xdb.Load(Path).Root!;
        var items = root.Element("arenas")?.Elements("Item").ToList() ?? new();
        if (terrain < 0 || terrain >= items.Count) return Array.Empty<Entry>();
        var sizeEl = items[terrain].Element(size switch { Size.Small => "small", Size.Large => "large", _ => "average" });
        var list = sizeEl?.Element(kind switch { Kind.Water => "waterArenas", Kind.DeepWater => "deepWaterArenas", _ => "newArenas" });
        var result = new List<Entry>();
        foreach (var item in list?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var href = (string?)item.Attribute("href");
            if (string.IsNullOrEmpty(href)) continue;
            if (href.StartsWith("#n:inline"))
            {
                var scene = item.Element("AdventureFlybyScene");
                if (scene == null) continue;
                var map = XdbRef.Parse((string?)scene.Element("Scenery")?.Attribute("href"), Path).Path;
                result.Add(new Entry(null, scene, System.IO.Path.GetFileNameWithoutExtension(map)));
            }
            else
            {
                var r = XdbRef.Parse(href, Path);
                if (Game.Instance.Fs.Exists(r.Path)) result.Add(new Entry(r.Path, null, System.IO.Path.GetFileNameWithoutExtension(r.Path)));
            }
        }
        return result;
    }

    /// <summary>
    /// Arenas for a battle on the given terrain: the requested size's list, falling back to
    /// "average" when a terrain defines no list for that size (grass, snow, …).
    /// </summary>
    public static IReadOnlyList<Entry> ForBattle(int terrain, Size size = Size.Average)
    {
        var list = Arenas(terrain, size);
        return list.Count > 0 ? list : Arenas(terrain, Size.Average);
    }
}
