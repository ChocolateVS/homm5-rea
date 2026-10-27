using System.Globalization;
using System.Xml.Linq;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Rmg;

// Typed views of the generator's data, read from the stock archives:
//   RMG/Templates/*.xdb                     (SRMGTemplate, loader 0x004a2340)
//   RMG/Params/Default.xdb                  (SRMGParameters, loader 0x004a2470)
//   GameMechanics/RefTables/RMGPresetTable  (per-race object pools and tiles, loader 0x004a35a0)
// Field offsets in comments are the exe's struct offsets used by the generator code.

public sealed class RmgZoneDef
{
    public int Index;                 // +0x04
    public string Setting = "";       // +0x08 race setting (RACE_RANDOM_TYPE, RACE_HEAVEN, ...)
    public bool CanBeWater;           // +0x0c
    public int Size;                  // +0x10
    public bool CanBePlayerStart;     // +0x14
    public bool Town;                 // +0x15
    public int TownGuardStrength;     // +0x18
    public bool Shipyard;             // +0x1c
    public int[] Mines = new int[7];  // +0x20 (wood, ore, mercury, crystal, sulfur, gem, gold)
    public int AbandonedMines;        // +0x2c
    public int[] Dwellings = new int[7]; // +0x30
    public int UpgBuildingsDensity;   // +0x3c
    public int TreasureDensity;       // +0x40
    public int TreasureChestDensity;  // +0x44
    public int Prisons;               // +0x48
    public int LandCartographer;      // +0x4c
    public int ShopPoints;            // +0x50
    public int ShrinePoints;          // +0x54
    public int LuckMoralBuildingsDensity; // +0x58
    public int ResourceBuildingsDensity;  // +0x5c
    public int TreasureBuildingPoints;    // +0x60
    public int TreasureBlocksTotalValue;  // +0x64
    public int DenOfThieves;          // +0x68
    public int RedwoodObservatoryDensity; // +0x6c
    public int BuffPoints;            // +0x70
}

public sealed class RmgConnectionDef
{
    public int Source, Dest;          // +0x04, +0x08
    public bool TwoWay;               // +0x0c
    public int GuardStrength;         // +0x10
    public bool Guarded;              // +0x14
    public bool Wide;                 // +0x15
}

public sealed class RmgTemplate
{
    public string Path = "";
    public string Name = "";
    public List<RmgZoneDef> Zones = new();             // +0x5c
    public List<RmgConnectionDef> Connections = new(); // +0x68
    public bool GraalOnMap;            // +0x74
    public int MinPlayers, MaxPlayers; // +0x78, +0x7c
    public int MinMapSize, MaxMapSize; // +0x80, +0x84 (size units, see RmgSizes)
    public bool Underground;           // +0x88
    public bool TestTemplate;          // +0x89

    /// <summary>
    /// Whether the RMG screen lists this template for the given settings (exe 0x0090c880, the template
    /// list of IRandomMapGenerator vf3, which fills the template combo and picks the random template).
    /// Test templates never; a fixed size compares its units (x0.7 rounded with water, x2 with a fixed
    /// underground) with [MinMapSize, MaxMapSize]; a fixed underground also needs MaxMapSize > 9 (also
    /// with a random size); a fixed player count must lie in [MinPlayers, MaxPlayers].
    /// </summary>
    public bool ListedFor(int? sizeIndex, bool? underground, bool water, int? players)
    {
        if (TestTemplate) return false;
        bool fixedUg = underground == true;
        if (sizeIndex is int s)
        {
            int units = RmgSizes.Units(s);
            if (water) units = (int)MathF.Round(units * 0.7f);
            int u = fixedUg ? units * 2 : units;
            if (u < MinMapSize || u > MaxMapSize) return false;
        }
        if (fixedUg && MaxMapSize <= 9) return false;
        if (players is int p && (p < MinPlayers || p > MaxPlayers)) return false;
        return true;
    }

    public override string ToString() => Name;
}

public sealed class RmgParams
{
    public int Mine1LevelMinRadius, Mine1LevelMaxRadius, Mine2LevelMinRadius, Mine2LevelMaxRadius,
        Mine3LevelMinRadius, Mine3LevelMaxRadius;          // +0x48..+0x5c
    public int BasicLevelGuardPower;                       // +0x60
    public int ConnectionGuardLevel, Mine1LevelGuardLevel, Mine2LevelGuardLevel, MineGoldGuardLevel; // +0x64..+0x70
    public int JunctionMinBorderDistance, TeleportMinBorderDistance, TeleportMaxBorderDistance;      // +0x74..+0x7c
    public int DistBetweenLakes, DistBetweenTreasureBlocks;                                          // +0x80, +0x84
    public int CreatureMinStackAmount, CreatureMaxStackAmount;                                        // +0x88, +0x8c
    public int MinDistanceBetweenBigObjects, MinDistanceBetweenTreasureBlocks;                        // +0x90, +0x94
    public string DefaultSurfaceTile = "", DefaultSubterraTile = "", DeepWaterTile = "", DeepWaterBottom = "", DefaultTransitiveTile = "";
    public int TransitiveTileIntensity;
    /// <summary>PointLightParams zMin / zMax (+0xb4 / +0xb8) and LightRadiusMin / Max (+0xbc / +0xc0): the underground
    /// zones' point lights draw Next(zMax − zMin) and Next(LightRadiusMax − LightRadiusMin) (0x00bab980 etc.).</summary>
    public int LightZMin, LightZMax, LightRadiusMin, LightRadiusMax;
    public List<string> GroundTerrainLights = new();       // +0x1f4
    public string? Obelisk, Grail;
    public List<string> WaterTreasures = new();
}

/// <summary>One weighted pool entry: a building with a point cost and guard level (NewShopBuildings etc.).</summary>
public sealed record RmgValuedObject(string Shared, int Value, int GuardStrength);

public sealed class RmgRacePreset
{
    public int Index;                 // index in Table_RMGPreset_Race (3 = RACE_HEAVEN ... 10 = RACE_STRONGHOLD)
    public string Id = "";
    public bool Valid;
    public string? TownProto;
    public List<string> HeroPool = new();
    public List<RmgValuedObject> NewDwellings = new();
    /// <summary>Preset +0x28 (after TownProto +0x14 and HeroPool +0x1c): the dwellings 0x0091e910 places, by tier.</summary>
    public List<string> Dwellings = new();
    public string DefaultTile = "", RoadTile = "", SecondaryRoadTile = "", WaterTile = "", WaterBottomTile = "", WaterCoastTile = "";
    public List<string> OneTileSmallBlockers = new(), OneTileSmallNonblockers = new(), OneTileBigObjects = new(),
        BigStatics = new(), Mountains = new(), OverTownCenterObjects = new();
    /// <summary>Preset +0xcc / +0xd8 (0x009225b0, 0x00922760); empty items kept ("" = a null reference, its draws still taken).</summary>
    public List<string> OverLakeCenterObjects = new(), OverLakeOneTileRandomObjects = new();
    public List<RmgValuedObject> LuckMorale = new(), Shops = new(), ResourceGivers = new(), Upgrades = new(),
        Shrines = new(), Treasuries = new(), Buffs = new();
    public List<string> Mines = new();
    public string? AbandonedMine;
}

/// <summary>A creature in Table_Creature_CreatureType order (the exe iterates creatures by enum index).</summary>
public sealed record RmgCreature(int Index, string Id, string Town, int Tier, int Power, string? MonsterShared);

/// <summary>An artifact the generator may place: CanBeGeneratedToSell with an ArtifactShared; <see cref="Index"/> is its ArtifactEffect value (table slot).</summary>
public sealed record RmgArtifact(string Id, string Shared, string Class, int Cost, int Index = -1);

/// <summary>
/// One SRMGSimpleCustomArmyTemplate of the guard template group (exe 0x00ba71b0): used when its
/// [MinPower, MaxPower] (+0x50/+0x54) contains the guard value; Stacks (+0x44, 12-byte records:
/// creature +4, coefficient +8) give the creatures and their relative counts.
/// </summary>
public sealed record RmgArmyTemplate(string Path, int MinPower, int MaxPower, List<(int Creature, int Coef)> Stacks);

public sealed class RmgDatabase
{
    public Dictionary<string, RmgTemplate> Templates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RmgParams Params { get; private set; } = new();
    public List<RmgRacePreset> Races { get; } = new();
    public List<RmgCreature> Creatures { get; } = new();
    public List<RmgArtifact> Artifacts { get; } = new();
    /// <summary>The two-stack guard templates (exe 0x00ba7e30: the group named at 0x00e568c0), in group order.</summary>
    public List<RmgArmyTemplate> ArmyTemplates { get; } = new();
    /// <summary>
    /// The town specializations of RMG/TownRandomSpecGroup.xdb (global ref 0x00ff9944, STownRandomSpecGroup, link +0x44)
    /// in group order: path, TownType (+0x6c) and RandomTown (+0x70; 0 = TOWN_RANDOM, 1 = TOWN_SCRIPT_ONLY).
    /// </summary>
    public List<(string Path, string TownType, string RandomTown)> TownSpecs { get; } = new();
    public const string TownSpecGroupPath = "RMG/TownRandomSpecGroup.xdb";
    public XdbLoader Xdb { get; }

    public const string ParamsPath = "RMG/Params/Default.xdb";
    public const string PresetPath = "GameMechanics/RefTables/RMGPresetTable.xdb";

    RmgDatabase(XdbLoader xdb) => Xdb = xdb;

    public static RmgDatabase Load(XdbLoader xdb)
    {
        var db = new RmgDatabase(xdb);
        foreach (var p in xdb.FileSystem.Enumerate("RMG/Templates/").Where(p => p.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var t = ParseTemplate(xdb.Load(p).Root!, p);
            if (t != null) db.Templates[t.Name] = t;
        }
        db.Params = ParseParams(xdb.Load(ParamsPath).Root!, ParamsPath);
        db.LoadRaces();
        db.LoadCreatures();
        db.LoadArtifacts();
        db.LoadArmyTemplates();
        db.LoadTownSpecs();
        return db;
    }

    public RmgRacePreset Race(int index) => Races[index];

    // ---- parsing helpers -------------------------------------------------------------

    static int I(XElement? e, string n) =>
        int.TryParse(e?.Element(n)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    static bool B(XElement? e, string n) => string.Equals(e?.Element(n)?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    static string S(XElement? e, string n) => e?.Element(n)?.Value?.Trim() ?? "";
    static string Ref(XElement? e, string current) =>
        XdbRef.Parse((string?)e?.Attribute("href"), current).Path ?? "";
    static List<string> Refs(XElement? list, string current) =>
        list?.Elements("Item").Select(i => Ref(i, current)).Where(p => p.Length > 0).ToList() ?? new();
    static List<string> RefsAll(XElement? list, string current) =>
        list?.Elements("Item").Select(i => Ref(i, current)).ToList() ?? new();
    static int[] Ints(XElement? list, int n)
    {
        var v = new int[n];
        int i = 0;
        foreach (var it in list?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            if (i >= n) break;
            int.TryParse(it.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i++]);
        }
        return v;
    }

    static RmgTemplate? ParseTemplate(XElement r, string path)
    {
        if (r.Name.LocalName != "RMGTemplate") return null;
        var t = new RmgTemplate
        {
            Path = path,
            Name = S(r, "Name") is { Length: > 0 } n ? n : System.IO.Path.GetFileNameWithoutExtension(path),
            GraalOnMap = B(r, "GraalOnMap"),
            MinPlayers = I(r, "MinPlayers"), MaxPlayers = I(r, "MaxPlayers"),
            MinMapSize = I(r, "MinMapSize"), MaxMapSize = I(r, "MaxMapSize"),
            Underground = B(r, "Underground"), TestTemplate = B(r, "TestTemplate"),
        };
        foreach (var z in r.Element("Zones")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            t.Zones.Add(new RmgZoneDef
            {
                Index = I(z, "Index"), Setting = S(z, "Setting"), CanBeWater = B(z, "CanBeWater"), Size = I(z, "Size"),
                CanBePlayerStart = B(z, "CanBePlayerStart"), Town = B(z, "Town"), TownGuardStrength = I(z, "TownGuardStrenght"),
                Shipyard = B(z, "Shipyard"), Mines = Ints(z.Element("Mines"), 7), AbandonedMines = I(z, "AbandonedMines"),
                Dwellings = Ints(z.Element("Dwellings"), 7), UpgBuildingsDensity = I(z, "UpgBuildingsDensity"),
                TreasureDensity = I(z, "TreasureDensity"), TreasureChestDensity = I(z, "TreasureChestDensity"),
                Prisons = I(z, "Prisons"), LandCartographer = I(z, "LandCartographer"), ShopPoints = I(z, "ShopPoints"),
                ShrinePoints = I(z, "ShrinePoints"), LuckMoralBuildingsDensity = I(z, "LuckMoralBuildingsDensity"),
                ResourceBuildingsDensity = I(z, "ResourceBuildingsDensity"), TreasureBuildingPoints = I(z, "TreasureBuildingPoints"),
                TreasureBlocksTotalValue = I(z, "TreasureBlocksTotalValue"), DenOfThieves = I(z, "DenOfThieves"),
                RedwoodObservatoryDensity = I(z, "RedwoodObservatoryDensity"), BuffPoints = I(z, "BuffPoints"),
            });
        foreach (var c in r.Element("Connections")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            t.Connections.Add(new RmgConnectionDef
            {
                Source = I(c, "SourceZoneIndex"), Dest = I(c, "DestZoneIndex"), TwoWay = B(c, "TwoWay"),
                GuardStrength = I(c, "GuardStrenght"), Guarded = B(c, "Guarded"), Wide = B(c, "Wide"),
            });
        return t;
    }

    static RmgParams ParseParams(XElement r, string path) => new()
    {
        Mine1LevelMinRadius = I(r, "Mine1LevelMinRadius"), Mine1LevelMaxRadius = I(r, "Mine1LevelMaxRadius"),
        Mine2LevelMinRadius = I(r, "Mine2LevelMinRadius"), Mine2LevelMaxRadius = I(r, "Mine2LevelMaxRadius"),
        Mine3LevelMinRadius = I(r, "Mine3LevelMinRadius"), Mine3LevelMaxRadius = I(r, "Mine3LevelMaxRadius"),
        BasicLevelGuardPower = I(r, "BasicLeverGuardPower"), ConnectionGuardLevel = I(r, "ConnectionGuardLevel"),
        Mine1LevelGuardLevel = I(r, "Mine1LevelGuardLevel"), Mine2LevelGuardLevel = I(r, "Mine2LevelGuardLevel"),
        MineGoldGuardLevel = I(r, "MineGoldGuardLevel"), JunctionMinBorderDistance = I(r, "JunctionMinBorderDistance"),
        TeleportMinBorderDistance = I(r, "TeleportMinBorderDistance"), TeleportMaxBorderDistance = I(r, "TeleportMaxBorderDistance"),
        DistBetweenLakes = I(r, "DistBetweenLakes"), DistBetweenTreasureBlocks = I(r, "DistBetweenTreasureBlocks"),
        CreatureMinStackAmount = I(r, "CreatureMinStackAmount"), CreatureMaxStackAmount = I(r, "CreatureMaxStackAmount"),
        MinDistanceBetweenBigObjects = I(r, "MinDistanceBetweenBigObjects"),
        MinDistanceBetweenTreasureBlocks = I(r, "MinDistanceBetweenTreasureBlocks"),
        DefaultSurfaceTile = Ref(r.Element("DefaultSurfaceTile"), path), DefaultSubterraTile = Ref(r.Element("DefaultSubterraTile"), path),
        DeepWaterTile = Ref(r.Element("DeepWaterTile"), path), DeepWaterBottom = Ref(r.Element("DeepWaterBottom"), path),
        DefaultTransitiveTile = Ref(r.Element("DefaultTransitiveTile"), path),
        TransitiveTileIntensity = I(r, "TransitiveTileIntensity"),
        LightZMin = I(r.Element("PointLightParams"), "zMin"), LightZMax = I(r.Element("PointLightParams"), "zMax"),
        LightRadiusMin = I(r.Element("PointLightParams"), "LightRadiusMin"), LightRadiusMax = I(r.Element("PointLightParams"), "LightRadiusMax"),
        GroundTerrainLights = Refs(r.Element("GroundTerrainLights"), path),
        Obelisk = Ref(r.Element("Obelisk"), path) is { Length: > 0 } o ? o : null,
        Grail = Ref(r.Element("Grail"), path) is { Length: > 0 } g ? g : null,
        WaterTreasures = Refs(r.Element("WaterTreasures"), path),
    };

    static List<RmgValuedObject> Valued(XElement? list, string current) =>
        list?.Elements("Item").Select(i => new RmgValuedObject(Ref(i.Element("Building"), current), I(i, "Value"), I(i, "GuardStrenght")))
            .Where(v => v.Shared.Length > 0).ToList() ?? new();

    void LoadRaces()
    {
        var root = Xdb.Load(PresetPath).Root!;
        int index = 0;
        foreach (var item in root.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var o = item.Element("obj");
            var tiles = o?.Element("Tiles");
            Races.Add(new RmgRacePreset
            {
                Index = index++, Id = S(item, "ID"), Valid = B(o, "Valid"),
                TownProto = Ref(o?.Element("TownProto"), PresetPath) is { Length: > 0 } tp ? tp : null,
                HeroPool = Refs(o?.Element("HeroPool"), PresetPath),
                NewDwellings = Valued(o?.Element("NewDwellings"), PresetPath),
                Dwellings = Refs(o?.Element("Dwellings"), PresetPath),
                DefaultTile = Ref(tiles?.Element("DefaultTile"), PresetPath), RoadTile = Ref(tiles?.Element("RoadTile"), PresetPath),
                SecondaryRoadTile = Ref(tiles?.Element("SecondaryRoadTile"), PresetPath),
                WaterTile = Ref(tiles?.Element("WaterTile"), PresetPath), WaterBottomTile = Ref(tiles?.Element("WaterBottomTile"), PresetPath),
                WaterCoastTile = Ref(tiles?.Element("WaterCoastTile"), PresetPath),
                OneTileSmallBlockers = Refs(o?.Element("OneTileSmallBlockers"), PresetPath),
                OneTileSmallNonblockers = Refs(o?.Element("OneTileSmallNonblockers"), PresetPath),
                OneTileBigObjects = Refs(o?.Element("OneTileBigObjects"), PresetPath),
                BigStatics = Refs(o?.Element("BigStatics"), PresetPath), Mountains = Refs(o?.Element("Mountains"), PresetPath),
                OverTownCenterObjects = Refs(o?.Element("OverTownCenterObjects"), PresetPath),
                OverLakeCenterObjects = RefsAll(o?.Element("OverLakeCenterObjects"), PresetPath),
                OverLakeOneTileRandomObjects = RefsAll(o?.Element("OverLakeOneTileRandomObjects"), PresetPath),
                LuckMorale = Valued(o?.Element("NewLuckMoraleBuildings"), PresetPath),
                Shops = Valued(o?.Element("NewShopBuildings"), PresetPath),
                ResourceGivers = Valued(o?.Element("NewResourceGivers"), PresetPath),
                Upgrades = Valued(o?.Element("NewUpgradeBuildings"), PresetPath),
                Shrines = Valued(o?.Element("NewShrines"), PresetPath),
                Treasuries = Valued(o?.Element("NewTreasuryBuildings"), PresetPath),
                Buffs = Valued(o?.Element("NewBuffBuildings"), PresetPath),
                Mines = Refs(o?.Element("Mines"), PresetPath),
                AbandonedMine = Ref(o?.Element("AbandonedMine"), PresetPath) is { Length: > 0 } am ? am : null,
            });
        }
    }

    void LoadTownSpecs()
    {
        if (!Xdb.FileSystem.Exists(TownSpecGroupPath)) return;
        foreach (var p in Refs(Xdb.Load(TownSpecGroupPath).Root?.Element("link"), TownSpecGroupPath))
        {
            if (!Xdb.FileSystem.Exists(p)) continue;
            var e = Xdb.Load(p).Root;
            TownSpecs.Add((p, S(e, "TownType"), S(e, "RandomTown")));
        }
    }

    void LoadCreatures()
    {
        const string table = "GameMechanics/RefTables/Creatures.xdb";
        var root = Xdb.Load(table).Root!;
        int index = 0;
        foreach (var item in root.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var id = S(item, "ID");
            var path = Ref(item.Element("Obj"), table);
            int i = index++;
            if (!StockTables.Kept(Xdb.FileSystem, table, id)) continue;   // beyond the stock engine's 180 creatures
            if (path.Length == 0 || !Xdb.FileSystem.Exists(path)) { Creatures.Add(new RmgCreature(i, id, "", 0, 0, null)); continue; }
            var c = Xdb.Load(path).Root!;
            var monster = Ref(c.Element("MonsterShared"), path);
            Creatures.Add(new RmgCreature(i, id, S(c, "CreatureTown"), I(c, "CreatureTier"), I(c, "Power"),
                monster.Length > 0 && Xdb.FileSystem.Exists(monster) ? monster : null));
        }
    }

    public const string ArmyTemplateGroupPath =
        "RMG/CustomArmyTemplates/SimpleTemplates/AutoTemplates/TestTemplateGroup.(RMGSimpleCustomArmyTemplateGroup).xdb";

    void LoadArmyTemplates()
    {
        if (!Xdb.FileSystem.Exists(ArmyTemplateGroupPath)) return;
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in Creatures) ids.TryAdd(c.Id, c.Index);
        foreach (var path in Refs(Xdb.Load(ArmyTemplateGroupPath).Root!.Element("Templates"), ArmyTemplateGroupPath))
        {
            if (!Xdb.FileSystem.Exists(path)) continue;
            var t = Xdb.Load(path).Root!;
            var stacks = new List<(int, int)>();
            foreach (var it in t.Element("Stacks")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                stacks.Add((ids.TryGetValue(S(it, "Creature"), out var ci) ? ci : 0, I(it, "Coef")));
            ArmyTemplates.Add(new RmgArmyTemplate(path, I(t, "MinPower"), I(t, "MaxPower"), stacks));
        }
    }

    void LoadArtifacts()
    {
        const string table = "GameMechanics/RefTables/Artifacts.xdb";
        if (!Xdb.FileSystem.Exists(table)) return;
        var root = Xdb.Load(table).Root!;
        foreach (var item in StockTables.Items(Xdb.FileSystem, table, root))
        {
            var o = item.Element("obj");
            var shared = Ref(o?.Element("ArtifactShared"), table);
            if (shared.Length == 0 || !Xdb.FileSystem.Exists(shared) || !B(o, "CanBeGeneratedToSell")) continue;
            Artifacts.Add(new RmgArtifact(S(item, "ID"), shared, S(o, "Type"), I(o, "CostOfGold"),
                StockTables.EnumValue(Xdb.FileSystem, "ArtifactEffect", S(item, "ID")) ?? -1));
        }
    }
}

/// <summary>Map sizes: the generator's size index ↔ tiles ↔ template size units (exe 0x00e556a8, vtable+0x14/+0x18).</summary>
public static class RmgSizes
{
    /// <summary>Tiles per side for size index 0..6 (tiny .. impossible), table at 0x00e556a8.</summary>
    public static readonly int[] Tiles = { 72, 96, 136, 176, 216, 256, 320 };
    public static readonly string[] Names = { "tiny", "small", "normal", "large", "extra large", "huge", "impossible" };

    /// <summary>exe 0x00903cd0: template size units represented by a size index.</summary>
    public static int Units(int sizeIndex) => sizeIndex switch
    {
        0 => 5, 1 => 10, 2 => 18, 3 => 31, 4 => 47, 5 => 66, 6 => 102, _ => 0,
    };

    /// <summary>exe 0x00903d40: smallest size index for a number of template size units.</summary>
    public static int IndexOf(int units) =>
        units < 8 ? 0 : units < 15 ? 1 : units < 25 ? 2 : units < 40 ? 3 : units < 60 ? 4 : units < 90 ? 5 : 6;
}
