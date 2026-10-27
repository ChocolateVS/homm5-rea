using H5.Adventure.Map;
using H5.Assets.Terrain;

namespace H5.Rmg;

/// <summary>Output of a generator run plus conversion into the adventure engine's <see cref="AdvMap"/>.</summary>
public sealed class RmgResult
{
    public RmgGenerator Generator { get; }
    public List<RmgObject> Objects { get; } = new();
    /// <summary>Per floor: AdvMapTile path per tile, row-major (y·W + x).</summary>
    public List<string[]> TileLayers { get; } = new();
    /// <summary>Connection end points: (floor, zone, other zone, x, y).</summary>
    public List<(int Floor, int Zone, int Other, int X, int Y)> Passages { get; } = new();
    public Dictionary<int, RmgObject> StartTowns { get; } = new();
    /// <summary>The free tile a hero uses to visit each placed object.</summary>
    public Dictionary<RmgObject, (int X, int Y)> Access { get; } = new(ReferenceEqualityComparer.Instance);
    public List<string> Warnings { get; } = new();
    /// <summary>Objects whose visit tile cannot be reached from the zone hub (should be 0).</summary>
    public int Unreachable;

    public RmgResult(RmgGenerator g) => Generator = g;

    internal bool StartTownAccess(RmgZone z, out (int X, int Y) access)
    {
        access = (-1, -1);
        var town = Objects.FirstOrDefault(o => o.Note == "town" && o.Zone == z.Id && o.Floor == z.Floor);
        return town != null && Access.TryGetValue(town, out access) && access.X >= 0;
    }

    public IEnumerable<RmgObject> OfKind(string sharedTag) => Objects.Where(o => o.Def.Tag == sharedTag);

    public string Describe()
    {
        var g = Generator;
        return $"This is a random map based on template {g.Template.Name} created with options: {RmgSizes.Names[g.SizeIndex]} size, " +
               $"{(g.HasUnderground ? "with" : "without")} underground, {(g.HasWater ? "with" : "without")} water. " +
               $"The map is for {g.Players} players, start seed is {g.Seed}";
    }

    static readonly string[] PlayerColours = { "PCOLOR_RED", "PCOLOR_BLUE", "PCOLOR_GREEN", "PCOLOR_YELLOW", "PCOLOR_ORANGE", "PCOLOR_TEAL", "PCOLOR_PURPLE", "PCOLOR_TAN" };

    /// <summary>Builds the engine's map model (objects with shared descriptors, players, floors with terrain and blocking).</summary>
    public AdvMap ToAdvMap(SharedCatalog catalog)
    {
        var g = Generator;
        var m = new AdvMap
        {
            Id = $"RMG_{g.Template.Name}_{g.Seed}",
            Source = $"rmg:{g.Template.Name}:{g.SizeIndex}:{g.Seed}",
            Name = $"{g.Template.Name} {g.Seed}",
            Description = Describe(),
            Version = 3,
            TilesX = g.W, TilesY = g.H,
            HasSurface = true, HasUnderground = g.HasUnderground,
            CustomGameMap = true,
            RandomMoons = true,
        };
        for (int p = 0; p < 8; p++)
        {
            var player = new MapPlayer
            {
                Index = p, Active = p < g.Players, Team = p, CanBeHuman = true, CanBeComputer = true,
                Colour = PlayerColours[p], Behaviour = "PB_RANDOM", HeroInTown = p < g.Players, DefaultBonus = "PLAYER_BONUS_RANDOM",
            };
            if (StartTowns.TryGetValue(p, out var town))
            {
                player.Race = town.Def.TownType ?? "";
                player.MainTownUid = UidOf(Objects.IndexOf(town), town);
            }
            m.Players.Add(player);
        }
        m.Objectives.Add(new MapObjective { Primary = true, Player = -1, Name = "RMG", Kind = "OBJECTIVE_KIND_DEFEAT_ALL" });

        int index = 0;
        foreach (var o in Objects)
        {
            var kind = KindOf(o.Def.Tag);
            var mo = new MapObject
            {
                Index = index, Uid = UidOf(index++, o), Kind = kind, Tag = o.Def.Tag.Replace("Shared", ""),
                Pos = new Tile(o.X, o.Y, o.Floor), Rot = o.Rot, SharedPath = o.Shared, Shared = o.Def, Owner = o.Owner,
            };
            switch (kind)
            {
                case ObjectKind.Monster:
                    mo.Creature = o.Creature ?? o.Def.Creature;
                    mo.Amount = o.Amount;
                    mo.CustomAmount = true;
                    mo.AdditionalStacks.AddRange(o.AdditionalStacks.Select(a => new H5.Core.Data.ArmySlot(a.Creature, a.Count)));
                    mo.Mood = "MONSTER_MOOD_AGGRESSIVE";
                    mo.Courage = "MONSTER_COURAGE_CAN_FLEE_JOIN";
                    break;
                case ObjectKind.Artifact: mo.ArtifactId = o.Def.ArtifactId; break;
                case ObjectKind.Town: mo.TownType = o.Def.TownType; break;
            }
            m.Objects.Add(mo);
        }

        for (int f = 0; f < TileLayers.Count; f++)
            m.Floors[f] = BuildFloor(f, TileLayers[f], catalog, m);
        // Static blocking, as the loader computes it for authored maps.
        foreach (var o in m.Objects)
        {
            var fl = m.Floor(o.Pos.Floor);
            if (fl == null) continue;
            var active = new HashSet<Tile>(o.ActiveTiles);
            foreach (var b in o.BlockedTiles)
                if (fl.InBounds(b.X, b.Y) && !active.Contains(b)) fl.Blocked[b.Y * fl.Width + b.X] = true;
        }
        return m;
    }

    static string UidOf(int index, RmgObject o) => $"item_RMG{index:D5}_{o.X}_{o.Y}_{o.Floor}";

    static ObjectKind KindOf(string sharedTag) => sharedTag switch
    {
        "AdvMapStaticShared" => ObjectKind.Static,
        "AdvMapTreasureShared" => ObjectKind.Treasure,
        "AdvMapMonsterShared" => ObjectKind.Monster,
        "AdvMapBuildingShared" => ObjectKind.Building,
        "AdvMapArtifactShared" => ObjectKind.Artifact,
        "AdvMapMineShared" => ObjectKind.Mine,
        "AdvMapAbanMineShared" => ObjectKind.AbandonedMine,
        "AdvMapDwellingShared" => ObjectKind.Dwelling,
        "AdvMapShrineShared" => ObjectKind.Shrine,
        "AdvMapTownShared" => ObjectKind.Town,
        "AdvMapPrisonShared" => ObjectKind.Prison,
        "AdvMapCartographerShared" => ObjectKind.Cartographer,
        _ => ObjectKind.Unknown,
    };

    MapFloor BuildFloor(int index, string[] tiles, SharedCatalog catalog, AdvMap m)
    {
        int w = Generator.W, h = Generator.H;
        var f = new MapFloor
        {
            Index = index, Width = w, Height = h,
            Terrain = new TerrainClass[w * h], TileType = new string[w * h], Blocked = new bool[w * h],
        };
        for (int i = 0; i < w * h; i++)
        {
            f.TileType[i] = catalog.TileType(tiles[i]);
            f.Terrain[i] = SharedCatalog.Classify(f.TileType[i]);
        }
        f.Data = BuildTerrain(tiles, w, h);
        return f;
    }

    /// <summary>Flat terrain with one blend layer per tile type: a vertex weight is the share of its four tiles using that type.</summary>
    static TerrainData BuildTerrain(string[] tiles, int w, int h)
    {
        var t = new TerrainData
        {
            TilesX = w, TilesY = h,
            Heights = new Grid<float>(w + 1, h + 1, new float[(w + 1) * (h + 1)]),
        };
        var types = tiles.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        for (int li = 0; li < types.Count; li++)
        {
            var weights = new byte[(w + 1) * (h + 1)];
            for (int vy = 0; vy <= h; vy++)
                for (int vx = 0; vx <= w; vx++)
                {
                    if (li == 0) { weights[vy * (w + 1) + vx] = 255; continue; }
                    int n = 0, c = 0;
                    for (int dy = -1; dy <= 0; dy++)
                        for (int dx = -1; dx <= 0; dx++)
                        {
                            int x = vx + dx, y = vy + dy;
                            if (x < 0 || y < 0 || x >= w || y >= h) continue;
                            c++;
                            if (tiles[y * w + x] == types[li]) n++;
                        }
                    weights[vy * (w + 1) + vx] = (byte)(c == 0 ? 0 : 255 * n / c);
                }
            t.Layers.Add(new TerrainLayer { TilePath = types[li], Weights = new Grid<byte>(w + 1, h + 1, weights) });
        }
        return t;
    }
}
