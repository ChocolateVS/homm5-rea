using System.Xml.Linq;
using H5.Core.Data;

namespace H5.Adventure.Map;

// Static, immutable description of an adventure map as authored (map.xdb + terrain).
// Dynamic game state (owners, remaining stacks, visited flags…) lives in State/.

/// <summary>Map tile coordinate on one floor (0 = surface, 1 = underground).</summary>
public readonly record struct Tile(int X, int Y, int Floor = 0)
{
    public Tile Offset(int dx, int dy) => new(X + dx, Y + dy, Floor);
    public override string ToString() => Floor == 0 ? $"({X},{Y})" : $"({X},{Y},u)";
}

public enum ObjectKind
{
    Static, Treasure, Monster, Building, Artifact, Mine, AbandonedMine, Dwelling, Shrine, Town, Hero,
    Prison, Garrison, Shipyard, Tent, Sign, HillFort, SeerHut, DwarvenWarren, Cartographer, Sphinx, Stand, Unknown,
}

/// <summary>Terrain classes that matter to rules (fan manual p.260, p.299).</summary>
public enum TerrainClass { Grass, Dirt, Sand, Lava, Snow, Taiga, Subterranean, DwarvenSubterranean, Water, River, Road, Unknown }

public sealed class AdvMap
{
    public string Id = "";
    public string Source = "";
    public string Name = "";
    public string Description = "";
    public int Version;
    public int TilesX, TilesY;
    public bool HasSurface = true, HasUnderground;
    /// <summary>map.xdb BorderSize (exe 00a0ed80, clamped to 1..20): the impassable frame of every floor.</summary>
    public int BorderSize = 1;
    public int InitialFloor;
    public bool CustomGameMap;
    public int HeroMaxLevel;
    public bool CustomTeams;
    public bool RandomMoons;
    /// <summary>The map's moons (position, step) used when RandomMoons is false (0041bdd0).</summary>
    public List<(int Pos, int Step)> Moons = new();
    /// <summary>The map's own SMoonCalendar (game path) or null for the default calendar.</summary>
    public string? MoonCalendar;
    /// <summary>MoonCalendarModifications/BlockMonstersWeeks: creature weeks become WEEK_OF_NOTHING.</summary>
    public bool BlockMonstersWeeks;
    /// <summary>Objectives/Primary/Common/DieInWeekWithoutTowns: losing all towns eliminates after 7 days.</summary>
    public bool DieInWeekWithoutTowns;
    public List<MapPlayer> Players = new();
    public List<MapObject> Objects = new();
    public List<MapObjective> Objectives = new();
    /// <summary>Floor index → terrain (null if the floor does not exist).</summary>
    public MapFloor?[] Floors = new MapFloor?[2];
    public string? ScriptPath;
    public string? ScriptSource;
    /// <summary>Spells/artifacts banned or enabled at map level (raw ids from map.xdb).</summary>
    public List<string> SpellIds = new(), ArtifactIds = new(), AvailableHeroes = new();
    public List<string> Warnings = new();

    public IEnumerable<MapObject> OfKind(ObjectKind k) => Objects.Where(o => o.Kind == k);
    public MapFloor? Floor(int f) => f >= 0 && f < Floors.Length ? Floors[f] : null;
}

public sealed class MapPlayer
{
    public int Index;
    public bool Active;
    public int Team;
    public bool CanBeHuman, CanBeComputer;
    public string Race = "";
    public string Colour = "";
    public string Behaviour = "";
    public string? MainTownUid;
    public string? MainHeroUid;
    public bool HeroInTown;
    public string DefaultBonus = "";
    /// <summary>Map "CanBeDisabled" / "CanChangeBonus": the custom-game setup may close the slot / change its bonus.</summary>
    public bool CanBeDisabled, CanChangeBonus;
    public List<string> ReserveHeroes = new();
    /// <summary>Editor-defined reserve heroes (DeployReserveHero puts them on the map).</summary>
    public List<HeroInstance> ReserveHeroInstances = new();
    public List<string> BannedHeroes = new(), AllowedHeroes = new(), BannedHeroRaces = new();
}

public sealed class MapObjective
{
    public bool Primary;
    /// <summary>-1 for common objectives, else the player index.</summary>
    public int Player = -1;
    public string Name = "";
    public string Kind = "";
    public List<string> Parameters = new();
    public int Timeout = -1, Holdout = -1;
    public bool InstantVictory;
    public bool InitiallyActive = true, NeedComplete = true;
    public string? Caption;
}

/// <summary>One placed object. Typed fields are filled according to <see cref="Kind"/>.</summary>
public sealed class MapObject
{
    public int Index;
    /// <summary>The xdb item id ("item_GUID"), used by player MainTown/MainHero links and scripts.</summary>
    public string Uid = "";
    public ObjectKind Kind;
    public string Tag = "";
    public Tile Pos;
    public float Rot;
    /// <summary>Script name (the "Name" field), empty if none.</summary>
    public string Name = "";
    public string SharedPath = "";
    public SharedDef Shared = SharedDef.Empty;
    /// <summary>Owning player index, -1 for none.</summary>
    public int Owner = -1;
    public List<ArmySlot> Guards = new();
    /// <summary>The placed object's own text (AdvMapSign MessageFileRef, relative to the map).</summary>
    public string? MessageText;

    // Monster
    public string? Creature;
    public int MonsterRandomLevel;   // 0 = specific, 1..7 = random of tier, 8 = any
    public int Amount, Amount2;
    public string Mood = "", Courage = "";
    public bool DoesNotGrow, CustomAmount;
    public Resources? Reward;
    public string? RewardArtifact;
    public List<ArmySlot> AdditionalStacks = new();

    // Artifact / shrine / scroll
    public string? ArtifactId;
    public string? SpellId;

    // Town
    public string? TownType;
    public string? Specialization;
    public List<TownBuildingInit> Buildings = new();
    public List<string> TownSpells = new();
    /// <summary>Hero placed in the town's garrison in the editor (&lt;GarrisonHero&gt;), if any.</summary>
    public HeroInstance? GarrisonHero;

    // Hero
    public HeroInstance? Hero;
    public int OverrideMask;

    // Dwelling
    public bool[] CreaturesEnabled = Array.Empty<bool>();
    public bool RandomCreatures;

    // Random towns and dwellings (AdvMapTown +0x11c / +0x120 / +0x124, AdvMapDwelling +0xac / +0xb0 / +0xb4, readers
    // 00a0a490 / 00a09de0): where the race comes from — RND_NONE 0, RND_PLAYER 1, RND_TOWN 2, RND_OWN 3 (enum 009fc040).
    public int RndSource;
    /// <summary>LinkToPlayer: player index, -1 for PLAYER_NONE.</summary>
    public int LinkToPlayer = -1;
    /// <summary>LinkToTown: the linked town's item id, or null.</summary>
    public string? LinkToTownUid;
    /// <summary>The shared the map placed (a RandomTown / RandomDwelling one) before the game resolved it.</summary>
    public string? PlacedSharedPath;
    public SharedDef? PlacedShared;
    /// <summary>The town type the map placed (TOWN_RANDOM_TYPE for a random town).</summary>
    public string? PlacedTownType;
    /// <summary>A random dwelling's creature level (009a6a00 argument 6, dwelling +0x128): 1..7, or -1.</summary>
    public int DwellingTier;

    // Treasure
    public bool TreasureCustom;
    public int TreasureAmount;

    /// <summary>
    /// Shipyards and towns: the slipway where a newly built ship appears (&lt;ShipTile&gt;),
    /// as an offset from <see cref="Pos"/>. Null when the object defines none.
    /// </summary>
    public (int X, int Y)? ShipTile;

    /// <summary>Seer Hut quest (&lt;Quest&gt;): objective, description and award.</summary>
    public SeerQuest? Quest;

    /// <summary>Sphinx: draws from the random riddle pool (RandomRiddle), else asks <see cref="SphinxRiddle"/>.</summary>
    public bool SphinxRandomRiddle = true;
    public Data.SphinxRiddle? SphinxRiddle;

    /// <summary>Hill Fort &lt;CreaturesUpgradesFilter&gt; (null when the instance has none, i.e. nothing forbidden).</summary>
    public UpgradesFilter? HillFortFilter;
    /// <summary>
    /// A town's &lt;CreaturesUpgradesFilter&gt; (AdvMapTown +0x130, copied to CAdvMapTown +0x2e8 / +0x2f4 / +0x300 / +0x30c by
    /// 00cd6bc0; used by the dwelling filter 00ccf130). TODO: not read by MapLoader yet (null = nothing forbidden).
    /// </summary>
    public UpgradesFilter? TownFilter;

    /// <summary>Scalar fields not modelled above (kept for scripts and later rules).</summary>
    public Dictionary<string, string> Props = new(StringComparer.Ordinal);

    /// <summary>Tiles blocked by this object after rotation (absolute).</summary>
    public IEnumerable<Tile> BlockedTiles => Shared.Blocked.Select(o => Footprint.Place(Pos, o, Rot));
    /// <summary>Tiles from which the object is visited (absolute).</summary>
    public IEnumerable<Tile> ActiveTiles => Shared.Active.Select(o => Footprint.Place(Pos, o, Rot));
    /// <summary>
    /// The tiles the pathfinder's object map lets a hero enter whatever the terrain kind there (H5_Game.exe 0045d910,
    /// built per path search by 00ceb670 → pathfinder +4, read by 009d1410 through vf 0 = 0045d690): an object with at
    /// most one blocked and one active tile registers its position (vf 0xa4, 0045d8a0), any other its active tiles
    /// (vf 0xb8, 0045d740).
    /// </summary>
    public IEnumerable<Tile> ActionTiles =>
        Shared.Blocked.Count <= 1 && Shared.Active.Count <= 1 ? new[] { Pos } : ActiveTiles;
    /// <summary>
    /// Whether the object's blocked tiles go to the pathfinder object map's blocked set (0045d910 → 0045d6d0, map
    /// +0x38, refused by 009d1410 through vf 0xc = 0045d670 before any other test): objects with two or more blocked or
    /// active tiles.
    /// </summary>
    public bool MultiTile => Shared.Blocked.Count > 1 || Shared.Active.Count > 1;

    public override string ToString() => $"#{Index} {Kind} {Shared.Type} {Pos}";
}

public sealed record TownBuildingInit(string Type, int Initial, int Max);

/// <summary>
/// A Hill Fort's CreaturesUpgradesFilter (the CAdvMapHillFort data at +8 read by H5_Game.exe 004bb440 / 004bbb10):
/// tiers whose normal (1st) or alternative (2nd) upgrade is forbidden, creatures that may not be upgraded and
/// upgraded creatures that may not be produced.
/// </summary>
public sealed record UpgradesFilter(IReadOnlySet<int> ForbiddenBasicUpgradeTiers, IReadOnlySet<int> ForbiddenAlterUpgradeTiers,
    IReadOnlySet<string> NotUpgradeable, IReadOnlySet<string> ForbiddenUpgrades);

/// <summary>A Seer Hut's quest: an objective (OBJECTIVE_KIND_*) with enum-index parameters and its award.</summary>
public sealed class SeerQuest
{
    public MapObjective Objective = new();
    public string? Description;
    public bool TakeContribution;
    public string AwardType = "AWARD_NONE";
    public int Experience;
    public int[] Resources = new int[7];
    public string Attribute = "";
    public int AttributeAmount;
    public string? Artifact, Spell, Creature, Skill;
    public int CreatureCount, SpellPoints, Morale, Luck;
    public string Mastery = "MASTERY_NONE";
}

/// <summary>Converts a footprint offset into an absolute tile, honouring the object's rotation.</summary>
public static class Footprint
{
    /// <summary>
    /// Rotation is in radians and snapped to quarter turns. Offsets are rotated
    /// counter-clockwise in map (x, y) space; this convention was chosen because it
    /// keeps the active (entrance) tiles of rotated objects unobstructed across all
    /// stock maps (see AdventureMapTests.RotatedEntrancesAreReachable).
    /// </summary>
    public static Tile Place(Tile origin, (int X, int Y) off, float rot)
    {
        int q = QuarterTurns(rot);
        var (x, y) = q switch
        {
            1 => (-off.Y, off.X),
            2 => (-off.X, -off.Y),
            3 => (off.Y, -off.X),
            _ => (off.X, off.Y),
        };
        return origin.Offset(x, y);
    }

    public static int QuarterTurns(float rot)
    {
        int q = (int)MathF.Round(rot / (MathF.PI / 2));
        return ((q % 4) + 4) % 4;
    }
}

/// <summary>Per-floor terrain: tile classes, heights, and static passability.</summary>
public sealed class MapFloor
{
    public int Index;
    public int Width, Height;
    public TerrainClass[] Terrain = Array.Empty<TerrainClass>();
    /// <summary>The tile type id of each tile's dominant layer (TT_GRASS, TT_COBBLESTONE_ROAD…).</summary>
    public string[] TileType = Array.Empty<string>();
    /// <summary>Tiles blocked by objects or terrain (static: ignores heroes, monsters and pickups).</summary>
    public bool[] Blocked = Array.Empty<bool>();
    /// <summary>The exe's tile kind from the terrain (<see cref="TerrainPassability"/>; empty until built).</summary>
    public TileKind[] Kind = Array.Empty<TileKind>();
    /// <summary>The exe's ground type id of each tile (passability map tile +0; 9 = TT_NONE refuses entry).</summary>
    public byte[] Ground = Array.Empty<byte>();
    /// <summary>
    /// The blocked tiles of multi-tile objects (<see cref="MapObject.MultiTile"/>): the pathfinder's object map refuses
    /// them before its action tiles (exe 009d1410 → 0045d670).
    /// </summary>
    public bool[] ObjectBlocked = Array.Empty<bool>();
    /// <summary>The AdvMapTile type id of each terrain layer (<see cref="TerrainPassability.TypeId"/>).</summary>
    public int[] LayerTypes = Array.Empty<int>();
    public H5.Assets.Terrain.TerrainData? Data;

    /// <summary>
    /// The terrain refuses the tile (exe 009d1410: kind 3 or 5, or ground type TT_NONE), whatever stands on it.
    /// </summary>
    public bool TerrainBlocked(int x, int y)
    {
        int i = y * Width + x;
        if (i >= Kind.Length) return false;
        return Kind[i] is TileKind.Impassable or TileKind.Edge || Ground[i] == TerrainPassability.TypeNone;
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public TerrainClass At(int x, int y) => Terrain[y * Width + x];
    public bool IsBlocked(int x, int y) => Blocked[y * Width + x];
    public bool IsWater(int x, int y) => Terrain[y * Width + x] == TerrainClass.Water;
}

/// <summary>A shared object descriptor (MapObjects/*.(AdvMapXxxShared).xdb).</summary>
public sealed class SharedDef
{
    public static readonly SharedDef Empty = new();

    public string Path = "";
    public string Tag = "";
    public string Type = "";
    public string? ModelPath;
    public List<(int X, int Y)> Blocked = new(), Active = new(), Passable = new(), Hole = new();
    public bool WaterBased, FlyPassable;
    public List<string> Messages = new();

    // Monster
    public string? Creature;
    // Artifact
    public string? ArtifactId;
    // Treasure
    public int MinResource, MaxResource;
    // Shrine
    public int ShrineLevel;
    public bool RunicMagic;
    public int MaxRunicMagicLevel;
    // Dwelling
    public List<string> DwellingCreatures = new();
    public string? DwellingRandomType;
    // Town
    public string? TownType;
    public List<string> TownBuildingPaths = new();
    // Tent / border guard colour
    public int Color;
    // Abandoned mine guard variants (chance, stacks)
    public List<(int Chance, List<ArmySlot> Guards)> GuardVariants = new();

    public XElement? Raw;
}
