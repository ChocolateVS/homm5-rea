using System.Xml.Linq;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Adventure.Map;

/// <summary>Loads and caches shared object descriptors and tile descriptors from the game archives.</summary>
public sealed class SharedCatalog(XdbLoader xdb)
{
    readonly Dictionary<string, SharedDef> _shared = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _tileTypes = new(StringComparer.OrdinalIgnoreCase);

    public XdbLoader Xdb => xdb;

    public SharedDef Get(string path, Func<string, XElement?>? localLoader = null)
    {
        path = PakFileSystem.Normalize(path);
        if (_shared.TryGetValue(path, out var d)) return d;
        XElement? root = null;
        if (xdb.FileSystem.Exists(path)) root = xdb.Load(path).Root;
        else root = localLoader?.Invoke(path);
        d = root == null ? new SharedDef { Path = path, Tag = "missing" } : Parse(root, path);
        _shared[path] = d;
        return d;
    }

    static List<(int, int)> Tiles(XElement r, string name) =>
        r.Items(name).Select(i => (i.Int("x"), i.Int("y"))).ToList();

    static SharedDef Parse(XElement r, string path)
    {
        var d = new SharedDef
        {
            Path = path, Tag = r.Name.LocalName, Raw = r,
            ModelPath = XdbRef.Parse(r.Href("Model"), path).Path is { Length: > 0 } m ? m : null,
            Blocked = Tiles(r, "blockedTiles"), Active = Tiles(r, "activeTiles"),
            Passable = Tiles(r, "passableTiles"), Hole = Tiles(r, "holeTiles"),
            WaterBased = r.Bool("WaterBased"), FlyPassable = r.Bool("FlyPassable"),
        };
        d.Messages.AddRange(r.Element("messagesFileRef")?.Elements("Item").Select(i => (string?)i.Attribute("href") ?? "") ?? Enumerable.Empty<string>());
        // "Type" appears once at the top level of each typed descriptor.
        d.Type = r.Elements("Type").LastOrDefault()?.Value.Trim() ?? "";
        switch (d.Tag)
        {
            case "AdvMapMonsterShared":
                d.Creature = r.Str("Creature") is { Length: > 0 } c && c != "CREATURE_UNKNOWN" ? c : null;
                break;
            case "AdvMapArtifactShared":
                d.ArtifactId = r.Str("ArtifactID") is { Length: > 0 } a && a != "ARTIFACT_NONE" ? a : null;
                break;
            case "AdvMapTreasureShared":
                d.MinResource = r.Int("MinResource"); d.MaxResource = r.Int("MaxResource");
                break;
            case "AdvMapShrineShared":
                d.ShrineLevel = r.Int("Level"); d.RunicMagic = r.Bool("RunicMagic"); d.MaxRunicMagicLevel = r.Int("MaxRunicMagicLevel");
                d.Type = d.RunicMagic ? "SHRINE_RUNIC" : $"SHRINE_LEVEL_{d.ShrineLevel}";
                break;
            case "AdvMapDwellingShared":
                d.DwellingCreatures = r.StrItems("creatures");
                d.DwellingRandomType = r.Str("RandomType");
                break;
            case "AdvMapTownShared":
                d.TownType = r.Str("Type");
                d.TownBuildingPaths = r.Element("buildings")?.Elements("Item")
                    .Select(i => XdbRef.Parse((string?)i.Attribute("href"), path).Path).Where(p => p.Length > 0).ToList() ?? new();
                break;
            case "AdvMapTentShared":
                d.Color = r.Int("Color");
                break;
            case "AdvMapAbanMineShared":
                d.Type = "BUILDING_ABANDONED_MINE";
                foreach (var v in r.Items("GuardsVariants"))
                    d.GuardVariants.Add((v.Int("ChanceOfVariant"), v.Items("Guards")
                        .Select(g => new ArmySlot(g.Str("Creature"), g.Int("Count"))).ToList()));
                break;
            case "AdvMapDwarvenWarrenShared": d.Type = "BUILDING_DWARVEN_WARREN"; break;
            case "AdvMapPrisonShared": d.Type = "BUILDING_PRISON"; break;
            case "AdvMapSeerHutShared": d.Type = "BUILDING_SEER_HUT"; break;
            case "AdvMapShipyardShared": d.Type = "BUILDING_SHIPYARD"; break;
            case "AdvMapSignShared": d.Type = "BUILDING_SIGN"; break;
            case "AdvMapSphinxShared": d.Type = "BUILDING_SPHINX"; break;
        }
        return d;
    }

    /// <summary>Tile type (TT_GRASS, TT_COBBLESTONE_ROAD…) of an (AdvMapTile) descriptor.</summary>
    public string TileType(string tilePath)
    {
        tilePath = PakFileSystem.Normalize(tilePath);
        if (_tileTypes.TryGetValue(tilePath, out var t)) return t;
        t = xdb.FileSystem.Exists(tilePath) ? xdb.Load(tilePath).Root.Str("Type") : "";
        _tileTypes[tilePath] = t;
        return t;
    }

    /// <summary>Movement class of a tile type (fan manual p.299: roads, native terrains, water).</summary>
    public static TerrainClass Classify(string tileType) => tileType switch
    {
        "TT_GRASS" => TerrainClass.Grass,
        "TT_DIRT" => TerrainClass.Dirt,
        "TT_SAND" => TerrainClass.Sand,
        "TT_LAVA" => TerrainClass.Lava,
        "TT_SNOW" => TerrainClass.Snow,
        "TT_TAIGA" => TerrainClass.Taiga,
        "TT_SUBTERRANEAN" => TerrainClass.Subterranean,
        "TT_DWARVENMINES" => TerrainClass.DwarvenSubterranean,
        "TT_BIG_WATER" => TerrainClass.Water,
        // Rivers, bogs and lava flows: painted over land, not sailable and not walkable.
        "TT_SMALL_WATER" => TerrainClass.River,
        "TT_COBBLESTONE_ROAD" or "TT_DIRT_ROAD" or "TT_GRAVEL_ROAD" => TerrainClass.Road,
        _ => TerrainClass.Unknown,
    };
}
