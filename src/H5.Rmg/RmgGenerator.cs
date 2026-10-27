namespace H5.Rmg;

/// <summary>Generation options as chosen on the original RMG screen (sRMGProps.InitialParams).</summary>
public sealed class RmgOptions
{
    public string Template = "";
    /// <summary>Size index 0..6 (tiny .. impossible); ignored when <see cref="RandomSize"/>.</summary>
    public int Size = 1;
    public bool RandomSize;
    public int Players = 2;
    public bool RandomPlayers;
    public bool Underground;
    public bool RandomUnderground;
    /// <summary>0 = none, 2 = water (the exe stores 0/2).</summary>
    public int WaterAmount;
    public bool RandomWater;
    /// <summary>0 weak, 1 medium, 2 strong, 3 very strong, 4 impossible.</summary>
    public int MonsterLevel = 2;
    public bool RandomMonsterLevel;
    /// <summary>0 = time-based seed, like the original.</summary>
    public int Seed;
    /// <summary>Races chosen per player on the setup screen (preset index, 1 = random). Missing entries are random.</summary>
    public List<int> PlayerRaces = new();
    /// <summary>
    /// The screen's EP_GRAAL_ON_MAP check box (InitialParams.Grail, generator +0xb5, saved as
    /// "sRMGProps.InitialParams.Grail"): a Tear of Asha in one random zone and "obelisks" in every zone.
    /// </summary>
    public bool Grail;
    /// <summary>The screen's Resources combo (InitialParams.ResourceMultiplier, RESOURCE_MISERABLE..MUCH = 0..4):
    /// treasure density x 0.2/0.5/1/2/4. 2 is what the screen sends for an unset combo (0x00799230).</summary>
    public int ResourceLevel = 2;
    /// <summary>The screen's Experience combo (InitialParams.ExpMultiplier, EXP_MISERABLE..MUCH = 0..4): chest and
    /// upgrade-building densities x 0.2/0.5/1/2/4.</summary>
    public int ExpLevel = 2;
    /// <summary>
    /// The screen's EP_TRUE_RANDOM_TOWNS check box (InitialParams +0x95, generator +0xa5, saved as
    /// "sRMGProps.InitialParams.RandomTowns"): towns are /MapObjects/RandomTown (0x00923e50, 0x00ff9b24) and dwellings
    /// /MapObjects/Random/RandomDwelling1..7 (0x0091e910, table 0x00ff9b48); the screen then sends no PlayersInfo.
    /// </summary>
    public bool RandomTowns;
}

/// <summary>The zone classes of 0x0090bd80 (vtables 0x00e58134, 0x00e827b4, 0x00e583cc, 0x00e58374).</summary>
public enum RmgZoneClass { Game, SubInferno, Subterra, Dwarven }

/// <summary>A zone during generation (NRMG::CGameZone; offsets are the exe's).</summary>
public sealed class RmgZone
{
    /// <summary>The class 0x0090bd80 created: surface zones CGameZone; underground zones CGameDwarvenZone when builder +0x8c
    /// is set, else CGameSubInfernoZone (Next(2) == 0) or CGameSubterraZone.</summary>
    public RmgZoneClass Class;
    /// <summary>
    /// +0x20, the race preset the zone's objects, statics and terrain come from (vf 0x1c): CGameZone 0x00918a50 the zone
    /// race (3 = Haven for a Dungeon zone on the surface), CGameSubInfernoZone 0x00baa380 preset 0, CGameDwarvenZone
    /// 0x00926d40 preset 2, CGameSubterraZone 0x00928b90 preset 6. +0x1c (the zone race's preset) gives the town.
    /// </summary>
    public int Preset;
    /// <summary>+0x148: the dwarven zone's placed statics (0x00926e80; the constructor 0x00928a30 clears it).</summary>
    public List<(float X, float Y)> DwarvenPoints = new();
    public required RmgZoneDef Def;
    public int Id => Def.Index;          // +0xec: value written into the zone map
    public int Floor;                    // +0xf4
    public int Race;                     // race preset index
    public int Player;                   // +0xf0: 0 = none, else 1-based player number
    public int ZoneSeed;                 // +0x13c: draw kept by the zone constructor
    public int Radius;                   // +0x140
    public float CenterX, CenterY;       // +0xe4, +0xe8
    public List<(int X, int Y)> Tiles = new(); // +0xcc
    public bool HasTown => Def.Town || Player != 0;
    public (int X, int Y)? TownPos;      // +0x0c/+0x10 once placed (town anchor, or tile centroid)
    public bool AnchorOnly;              // TownPos is the centroid of a zone without a town
    public bool TownPlaced;              // +0xf8: set by 0x00923e50 once the town is committed
    // Point lists of the road stages (0x0091bdb0, 0x0091bf90), positions as floats like the exe's.
    public List<(float X, float Y)> RoadNodes = new();        // +0x68: visit tiles of committed objects, connection points
    public List<(float X, float Y)> Network = new();          // +0x74: main-road tiles (flag 8)
    public List<(float X, float Y)> MineNetwork = new();      // +0x80: mine-road tiles (flag 0x10)
    public List<(float X, float Y)> ObjectPaths = new();      // +0x8c: object-path tiles (flag 0x20)
    public List<(float X, float Y)> ConnectionPoints = new(); // +0xc0
    public List<(float X, float Y)> GuardPoints = new();      // +0x98: connection guard points (0x00925a00)
    public List<(float X, float Y)> MinePoints = new();       // +0x11c: visit tiles of the zone's mines
    public List<(float X, float Y)> BlockedList = new();      // +0x5c: blocked tiles of stamped objects (0x0091a420)
    public List<(float X, float Y)> BigStaticPoints = new();  // +0xb4: lake centres and big statics (0x00924940, 0x009253b0)
    public List<int> ConnectedZones = new();                  // +0xd8: neighbours already joined (0x00925a00, 0x0091a950)
    public override string ToString() => $"zone {Id} floor {Floor} race {Race} player {Player} R={Radius} c=({CenterX},{CenterY})";
}

/// <summary>Per-floor generation grids (the 0x120-byte floor records of the map builder).</summary>
public sealed class RmgFloor
{
    public readonly int Index, W, H;
    /// <summary>+0xc4: zone id per tile, -1 = unassigned.</summary>
    public readonly int[] Zone;
    /// <summary>+0xe4: distance to the zone border (0 on border tiles, -1 outside zones).</summary>
    public readonly int[] BorderDist;
    /// <summary>+0xf4: the distance field of 0x009191a0 (last written by any zone of the floor).</summary>
    public readonly int[] Dist;
    /// <summary>+0xd4: the tile flags (<see cref="TileFlags"/>).</summary>
    public readonly int[] Flags;
    /// <summary>
    /// +0x24: the per-vertex height level grid ((W+1) x (H+1), indexed [y][x]; terrain chunk 7). 0x00912940 (from
    /// 0x00912ce0: floor 0 with flag 1, floor 1 with flag 0) fills the surface with 0x10, the underground with 0x20, then
    /// on the underground the vertices with row or column below 3 get 16 + (3 − min)·16/3 and the others below
    /// size − size % 3 (both coordinates) 0x10. The rock fill 0x009159a0 raises it to 0x20 and smooths it (0x009131d0);
    /// 0x00915d90 (level &gt; 0x10) and the dwarven statics (level != 0x10) read it.
    /// </summary>
    public readonly byte[] Level;
    /// <summary>
    /// +0x14: vertex heights, same layout (terrain chunk 5, world units). 0x00912940: surface 3·2 = 6, underground
    /// 18·2 = 36 with ((3 − min)/3 + 1)·2·9 on the edge band and 2·9 = 18 inside; the rock fill writes 36 and its
    /// smoothing moves a vertex by (new − old level) x 1.125; the surface gets 0x00918180 (<see cref="RmgGenerator"/>).
    /// </summary>
    public readonly float[] Heights;
    /// <summary>+0x68: per-vertex byte grid (terrain chunk 15), 1 from 0x00912940; 0x009198f0 clears it (0x00915d20).</summary>
    public readonly byte[] Chunk15;
    public int Lv(int y, int x) => y * (W + 1) + x;
    public readonly NivalHashMap<RmgZone> Zones = new();

    public RmgFloor(int index, int w, int h)
    {
        Index = index; W = w; H = h;
        Zone = Enumerable.Repeat(-1, w * h).ToArray();
        BorderDist = Enumerable.Repeat(-1, w * h).ToArray();
        Flags = new int[w * h];
        Dist = Enumerable.Repeat(-1, w * h).ToArray();
        Level = new byte[(w + 1) * (h + 1)];
        Heights = new float[(w + 1) * (h + 1)];
        Chunk15 = Enumerable.Repeat((byte)1, (w + 1) * (h + 1)).ToArray();
        InitTerrain(index != 0);
    }

    /// <summary>exe 0x00912940 (this = floor terrain, size, size, flag): flag 1 for the surface, 0 for the underground.</summary>
    void InitTerrain(bool underground)
    {
        float fill = 2f * (underground ? 18f : 3f);                              // 0x00f46678 x (0x00e44678 | 0x00e13280)
        Array.Fill(Heights, fill);
        Array.Fill(Level, (byte)(underground ? 0x20 : 0x10));
        if (!underground) return;
        for (int r = 0; r < W; r++)          // the vertex loops stop one short of the grid (param_2 / param_3)
            for (int c = 0; c < H; c++)
            {
                if (r < 3 || c < 3)
                {
                    int m = Math.Min(r, c);
                    Level[Lv(r, c)] = (byte)((3 - m) * 0x10 / 3 + 0x10);
                    Heights[Lv(r, c)] = ((3 - m) * 0.33333334f + 1f) * 2f * 9f;   // 0x00e19950, 0x00e0a7e4, 0x00e4d7a4
                }
                else if (r < W - W % 3 && c < H - H % 3)
                {
                    Level[Lv(r, c)] = 0x10;
                    Heights[Lv(r, c)] = 2f * 9f;
                }
            }
    }

    public bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
    public int At(int x, int y) => x * H + y;  // the exe stores [x][y]
}

public sealed partial class RmgGenerator
{
    readonly RmgDatabase _db;
    readonly RmgTemplate _t;
    readonly RmgOptions _o;
    public RmgRandom Rng { get; }
    public int W { get; private set; }
    public int H { get; private set; }
    public int SizeIndex { get; private set; }
    public int Players { get; private set; }
    public bool HasUnderground { get; private set; }
    public bool HasWater { get; private set; }
    public int MonsterLevel { get; private set; }
    public int Seed { get; private set; }
    /// <summary>The map builder's random angle (+0x5c, 0x00911500); FireDot statics take it (0x009140f0).</summary>
    public float BuilderAngle { get; private set; }
    public uint BuilderDraw { get; private set; }
    public List<RmgFloor> Floors { get; } = new();
    public List<RmgZone> Zones { get; } = new();
    /// <summary>Player race per player (race preset index, 1 = random until a start zone picks it).</summary>
    public List<int> PlayerRaces { get; } = new();
    public List<string> Log { get; } = new();
    /// <summary>Draw counter at the end of each stage, as the original logs it.</summary>
    public List<(string Stage, int Counter)> Counters { get; } = new();

    public RmgGenerator(RmgDatabase db, RmgOptions options)
    {
        _db = db;
        _o = options;
        if (!db.Templates.TryGetValue(options.Template, out var t))
            throw new ArgumentException($"Unknown RMG template '{options.Template}'");
        _t = t;
        Seed = options.Seed != 0 ? options.Seed : (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Rng = new RmgRandom(Seed);
    }

    public RmgTemplate Template => _t;
    public RmgDatabase Database => _db;

    void Mark(string stage) => Counters.Add((stage, Rng.Counter));

    /// <summary>Runs the whole pipeline (exe 0x0090e7e0, "CreateMap") and returns the generated map.</summary>
    public RmgResult Generate()
    {
        ResolveOptions();
        Mark("CreateMap");
        CreateMapDraws();
        Mark("LoadTemplate");
        BuildZones();
        Mark("GenerateGameZones");
        PlaceZoneCenters();
        Mark("FillZones");
        FillZones();
        Mark("CalcBorderTiles");
        ComputeBorderDistances();
        Mark("FillTerrain");
        // 0x00915dc0 (builder +0x8c set): with an underground floor 0x009133e0 draws Next(8) for the floor's +0x78 cell grid
        // (byte 1 = 1, byte 2 = Next(8) + 8). TODO(exe): that grid is not modelled (terrain).
        if ((BuilderDraw & 1) != 0 && Floors.Count > 1) Rng.Next(8);
        var result = new RmgResult(this);
        PaintTerrain(result);
        Mark("PlaceTowns");
        PlaceTowns(result);
        Mark("FillDistToTownsTable");
        Mark("ZoneConnections");
        CreateConnections(result);
        Mark("MainObjects");
        PlaceMainObjects(result);
        Mark("Roads");
        CreateRoads(result);
        PlaceStatics(result);
        Mark("AdditionalObjects");
        PlaceUndergroundTreasures(result);
        PlaceTreasureBlocks(result);
        FinishMap(result);
        Finish(result);
        Mark("Finished");
        return result;
    }

    // ---- stage 1: options (exe 0x0090e7e0 prologue) -------------------------------------------

    void ResolveOptions()
    {
        bool randomUg = _o.RandomUnderground;
        bool ug = _o.Underground;
        // Exactly one draw for underground, one for size and one for players, whatever the options.
        if (_t.MaxMapSize == 1) { ug = false; randomUg = false; Rng.Advance(); }
        else if (_t.MinMapSize > RmgSizes.Units(6)) { ug = true; randomUg = false; Rng.Advance(); }
        else if (randomUg) { ug = Rng.Next(2) != 0; randomUg = false; }
        else Rng.Advance();

        int size = _o.Size;
        if (_o.RandomSize)
        {
            int units = Rng.Next(_t.MaxMapSize - _t.MinMapSize + 1) + _t.MinMapSize;
            if (!randomUg && ug) units /= 2;
            size = RmgSizes.IndexOf(units);
        }
        else Rng.Advance();

        int players = _o.Players;
        if (_o.RandomPlayers) players = Rng.Next(_t.MaxPlayers - _t.MinPlayers + 1) + _t.MinPlayers;
        else Rng.Advance();
        if (players > _t.MaxPlayers || players < _t.MinPlayers) players = _t.MinPlayers;

        // The template's minimum area wins over the chosen size (doubling with underground).
        if (RmgSizes.Units(size) * (ug ? 2 : 1) < _t.MinMapSize)
        {
            if (RmgSizes.IndexOf(_t.MinMapSize) > 5)
            {
                int half = RmgSizes.IndexOf(_t.MinMapSize / 2);
                size = half < 4 ? half : 4;
                ug = true;
            }
            else size = RmgSizes.IndexOf(_t.MinMapSize);
        }

        SizeIndex = Math.Clamp(size, 0, 6);
        W = H = RmgSizes.Tiles[SizeIndex];
        Players = players;
        HasUnderground = ug;
        for (int i = 0; i < players; i++) PlayerRaces.Add(i < _o.PlayerRaces.Count ? _o.PlayerRaces[i] : 1);
        Log.Add($"template {_t.Name}, size {RmgSizes.Names[SizeIndex]} ({W}x{H}), players {players}, underground {ug}, seed {Seed}");
    }

    // ---- stage 2: CreateMap draws (exe 0x0090cb00) ---------------------------------------------

    void CreateMapDraws()
    {
        MonsterLevel = _o.MonsterLevel;
        if (_o.RandomMonsterLevel) MonsterLevel = Rng.Next(3) + 1; else Rng.Advance();
        HasWater = _o.WaterAmount != 0;
        if (_o.RandomWater) HasWater = Rng.Next(2) != 0; else Rng.Advance();
        // Map builder construction (0x00911500): a random angle and one discarded draw.
        BuilderAngle = Rng.Float(0, 6.2831855f);   // builder +0x5c
        BuilderDraw = Rng.Advance();               // builder +0x88; +0x8c = odd (dwarven underground zones, 0x0090bd80)
        // Ambient light choice and the random-moons roll.
        if (_db.Params.GroundTerrainLights.Count == 0) Rng.Advance();
        else Rng.Next(_db.Params.GroundTerrainLights.Count);
        Rng.Next(10);
        for (int f = 0; f < (HasUnderground ? 2 : 1); f++) Floors.Add(new RmgFloor(f, W, H));
    }

    // ---- stage 3: zones (exe 0x0090bd80, "LoadTemplate") ---------------------------------------

    static readonly int[] SurfaceRaces = { 3, 4, 5, 9, 8, 7, 10 };
    static readonly int[] UndergroundRaces = { 6, 8, 9, 7 };

    static int RaceIndexOf(string setting) => setting switch
    {
        "RACE_SPECIAL" => 0, "RACE_RANDOM_TYPE" => 1, "RACE_NO_TYPE" => 2, "RACE_HEAVEN" => 3, "RACE_PRESERVE" => 4,
        "RACE_ACADEMY" => 5, "RACE_DUNGEON" => 6, "RACE_NECROMANCY" => 7, "RACE_INFERNO" => 8, "RACE_DWARF" => 9,
        "RACE_STRONGHOLD" => 10, _ => 1,
    };

    void BuildZones()
    {
        // Records {size, floor, zone} in template order, sorted by size (merge sort with the
        // exe's tie order, 0x0090fd60), split between floors, then re-sorted by index (0x0090fe60).
        var recs = _t.Zones.Select(z => (Size: z.Size, Floor: 0, Zone: z)).ToList();
        recs = StridedMergeSort(recs, (l, r) => r.Size < l.Size);
        var surface = SurfaceRaces.ToList();
        var under = UndergroundRaces.ToList();
        if (!HasUnderground) surface.Add(6);
        if (HasUnderground)
        {
            int s0 = 0, s1 = 0;
            for (int i = 0; i < recs.Count; i++)
            {
                int floor;
                if (Math.Abs(s0 - s1) < 2) floor = Rng.Next(2);
                else { floor = s1 < s0 ? 1 : 0; Rng.Advance(); }
                if (floor == 0) s0 += recs[i].Size; else s1 += recs[i].Size;
                recs[i] = recs[i] with { Floor = floor };
            }
        }
        recs = StridedMergeSort(recs, (l, r) => l.Zone.Index < r.Zone.Index);
        // 0x0090bd80: one Next(2) chooses the class of every underground zone when builder +0x8c (the odd builder draw,
        // set in 0x0090e7e0) does not make them dwarven.
        bool subterra = Rng.Next(2) != 0;
        bool dwarven = (BuilderDraw & 1) != 0;

        int starts = 0;
        foreach (var rec in recs)
        {
            int setting = RaceIndexOf(rec.Zone.Setting);
            int race = setting;
            int player = 0;
            if (setting is 0 or 1 or 2)
            {
                var list = rec.Floor == 0 ? surface : under;
                race = list[Rng.Next(list.Count)];
                if (rec.Zone.CanBePlayerStart && starts < PlayerRaces.Count)
                {
                    Rng.Advance();
                    if (PlayerRaces[starts] == 1) PlayerRaces[starts] = race; else race = PlayerRaces[starts];
                    starts++;
                    player = starts;
                }
                else
                {
                    Rng.Advance();
                    if (rec.Zone.CanBePlayerStart && starts < Players) { player = starts + 1; starts++; }
                }
            }
            else
            {
                Rng.Advance();
                if (rec.Zone.CanBePlayerStart && starts < Players)
                {
                    if (starts < PlayerRaces.Count && PlayerRaces[starts] == 1) PlayerRaces[starts] = race;
                    player = starts + 1;
                    starts++;
                }
            }
            // Zone construction (base ctor 0x00925850) keeps one draw as the zone's own seed (+0x13c).
            int zoneSeed = (int)Rng.Advance();
            var cls = rec.Floor == 0 ? RmgZoneClass.Game : dwarven ? RmgZoneClass.Dwarven : subterra ? RmgZoneClass.Subterra : RmgZoneClass.SubInferno;
            int preset = cls switch
            {
                RmgZoneClass.SubInferno => 0, RmgZoneClass.Dwarven => 2, RmgZoneClass.Subterra => 6,
                _ => race == 6 && rec.Floor == 0 ? 3 : race,
            };
            var zone = new RmgZone { Def = rec.Zone, Floor = rec.Floor, Race = race, Player = player, ZoneSeed = zoneSeed, Class = cls, Preset = preset };
            Zones.Add(zone);
            Floors[rec.Floor].Zones.Add(zone.Id, zone);
        }
    }

    /// <summary>
    /// The exe's bottom-up strided merge (0x0090f6b0/0x0090f7d0) is a stable-order-breaking
    /// variant: when the comparator says "left first" it takes left, otherwise right, so equal
    /// keys come out right-first within each merge.
    /// </summary>
    static List<T> StridedMergeSort<T>(List<T> items, Func<T, T, bool> leftFirst)
    {
        int n = items.Count;
        if (n < 2) return items.ToList();
        var a = items.ToList();
        var b = new T[n];
        int width = 0x40000000;
        while (n <= width) width >>= 1;
        var src = a.ToArray();
        for (; width != 0; width >>= 1)
        {
            // Merge pass at stride `width`: element streams i, i+2w, ... merged with i+w, i+3w, ...
            var dst = new T[n];
            if (n < width * 2)
                for (int k = n - width; k < width; k++) dst[k] = src[k];
            int lanes = Math.Min(width, n - width);
            for (int lane = 0; lane < lanes; lane++)
            {
                int l = lane, r = lane + width, o = lane;
                while (true)
                {
                    if (r >= n) { while (l < n) { dst[o] = src[l]; l += width * 2; o += width; } break; }
                    if (l >= n) { while (r < n) { dst[o] = src[r]; r += width * 2; o += width; } break; }
                    if (leftFirst(src[l], src[r])) { dst[o] = src[l]; l += width * 2; o += width; }
                    else { dst[o] = src[r]; r += width * 2; o += width; }
                }
            }
            src = dst;
        }
        return src.ToList();
    }

    // ---- stage 4: zone centres (exe 0x00905070, "GenerateGameZones") ---------------------------

    void PlaceZoneCenters()
    {
        float area = (float)(W * H);
        float k = 0.9f;
        float total = 0;
        foreach (var z in _t.Zones) total += z.Size;
        var points = new List<(float X, float Y)>();
        for (int i = W * H / 100; i > 0; i--)
        {
            int ry = Rng.Next(H);
            int rx = Rng.Next(W);
            points.Add((rx, ry));
        }
        // Zone centres start at (0,0) and are NOT reset between attempts: unplaced zones keep
        // their previous centre and still reject nearby points, exactly like the original.
        double inv = 1.0 / total;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (points.Count > 0) Rng.Shuffle(points);
            foreach (var f in Floors)
                foreach (var z in f.Zones.Values)
                {
                    z.Radius = (int)Math.Round(Math.Sqrt(z.Def.Size * inv * area * k) * 0.3333333432674408, MidpointRounding.ToEven);
                    if (HasUnderground) z.Radius = (int)Math.Round(z.Radius * 1.4142135381698608, MidpointRounding.ToEven);
                }
            bool ok = true;
            foreach (var f in Floors)
            {
                if (points.Count > 0) Rng.Shuffle(points);
                foreach (var z in f.Zones.Values)
                {
                    if (!ok) break;
                    bool placed = false;
                    foreach (var p in points)
                    {
                        if (p.X < z.Radius || p.X > W - z.Radius || p.Y < z.Radius || p.Y > H - z.Radius) continue;
                        bool fits = true;
                        foreach (var g in Floors)
                            foreach (var other in g.Zones.Values)
                            {
                                if (other == z) continue;
                                float dx = other.CenterX - p.X, dy = other.CenterY - p.Y;
                                if (MathF.Sqrt(dy * dy + dx * dx) < other.Radius + z.Radius && other.Floor == z.Floor) { fits = false; break; }
                            }
                        if (!fits) continue;
                        z.CenterX = p.X; z.CenterY = p.Y;
                        Log.Add($"Zone #{z.Id}, started at {(int)p.X}:{(int)p.Y}, k == {k:0.00}, R = {z.Radius}");
                        placed = true;
                        break;
                    }
                    if (!placed) ok = false;
                }
            }
            if (ok) return;
            k *= 0.97f;
        }
        throw new InvalidOperationException("RMG: could not place zone centres");
    }

    // ---- stage 5: assign tiles to zones (exe 0x00907d90, "FillZones") --------------------------

    static readonly (int Dx, int Dy)[] Neighbours8 =
    {
        (0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1),
    };

    void FillZones()
    {
        foreach (var f in Floors)
        {
            // Seed: each tile goes to the first zone (hash order) whose circle contains it.
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                    foreach (var z in f.Zones.Values)
                    {
                        float dx = x - MathF.Round(z.CenterX), dy = y - MathF.Round(z.CenterY);
                        if (MathF.Sqrt(dy * dy + dx * dx) < z.Radius) { f.Zone[f.At(x, y)] = z.Id; break; }
                    }
            // Zone tile lists are only rebuilt at the end of each pass (0x0091bc80), so during the
            // first pass they are empty and no tile can be stolen (the ratio test sees x/0).

            int passes = 0;
            float limit = W * 1.7320508f;
            while (passes < limit)
            {
                passes++;
                var pending = new NivalHashMap<List<(int X, int Y)>>();
                var stolen = new NivalHashMap<List<(int X, int Y)>>();
                bool xUp = Rng.Next(2) != 0;
                bool yUp = Rng.Next(2) != 0;
                for (int x = xUp ? 0 : W - 1; xUp ? x < W : x >= 0; x += xUp ? 1 : -1)
                    for (int y = yUp ? 0 : H - 1; yUp ? y < H : y >= 0; y += yUp ? 1 : -1)
                    {
                        int own = f.Zone[f.At(x, y)];
                        var counts = new NivalHashMap<int>();
                        if (own == -1)
                        {
                            foreach (var (dx, dy) in Neighbours8)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (!f.In(nx, ny)) continue;
                                int v = f.Zone[f.At(nx, ny)];
                                if (v == own) continue;
                                counts[v] = (counts.TryGetValue(v, out var c) ? c : 0) + 1;
                            }
                            int best = -1, bestCount = -1;
                            foreach (var (key, c) in counts.Items())
                                if (bestCount < c && key != -1) { bestCount = c; best = key; }
                            if (best != -1 && bestCount > 2) ListOf(pending, best).Add((x, y));
                        }
                        else if (x > 5 && x <= W - 6 && y > 5 && y <= H - 6)
                        {
                            foreach (var (dx, dy) in Neighbours8)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (!f.In(nx, ny)) continue;
                                int v = f.Zone[f.At(nx, ny)];
                                if (v == own) continue;
                                counts[v] = (counts.TryGetValue(v, out var c) ? c : 0) + 1;
                            }
                            if (counts.ContainsKey(-1) || counts.Count == 0) continue;
                            int best = -1, bestCount = -1;
                            foreach (var (key, c) in counts.Items())
                                if (bestCount < c && key != -1) { best = key; bestCount = c; }
                            var mine = f.Zones[own];
                            if (best != -1 && bestCount > 2 && f.Zones.TryGetValue(best, out var other) && mine.Tiles.Count > 0 &&
                                (double)other.Tiles.Count / mine.Tiles.Count < (double)other.Def.Size / mine.Def.Size &&
                                Rng.Float(0, 1) > 0.4f)
                                ListOf(stolen, best).Add((x, y));
                        }
                    }
                foreach (var (id, tiles) in pending.Items())
                    foreach (var (x, y) in tiles) f.Zone[f.At(x, y)] = id;
                foreach (var (id, tiles) in stolen.Items())
                    foreach (var (x, y) in tiles) f.Zone[f.At(x, y)] = id;
                RecomputeZoneTiles(f);
            }
        }
    }

    static List<(int, int)> ListOf(NivalHashMap<List<(int, int)>> map, int key)
    {
        if (!map.TryGetValue(key, out var l)) { l = new List<(int, int)>(); map.Add(key, l); }
        return l;
    }

    /// <summary>exe 0x0091bc80: rebuild each zone's tile list (x-major scan).</summary>
    static void RecomputeZoneTiles(RmgFloor f)
    {
        foreach (var z in f.Zones.Values) z.Tiles.Clear();
        for (int x = 0; x < f.W; x++)
            for (int y = 0; y < f.H; y++)
            {
                int id = f.Zone[f.At(x, y)];
                if (id != -1 && f.Zones.TryGetValue(id, out var z)) z.Tiles.Add((x, y));
            }
    }

    // ---- stage 6: distance to zone border (exe 0x00904240) -------------------------------------

    void ComputeBorderDistances()
    {
        foreach (var f in Floors)
        {
            var border = new List<(int X, int Y)>();
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    int id = f.Zone[f.At(x, y)];
                    if (id == -1) continue;
                    bool edge = false;
                    foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (-1, 0), (0, -1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (!f.In(nx, ny) || f.Zone[f.At(nx, ny)] != id) { edge = true; break; }
                    }
                    if (edge) { f.BorderDist[f.At(x, y)] = 0; border.Add((x, y)); }
                }
            // Distance to the nearest border tile of the same zone (bucketed by zone for speed).
            var byZone = border.GroupBy(b => f.Zone[f.At(b.X, b.Y)]).ToDictionary(g => g.Key, g => g.ToArray());
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    int i = f.At(x, y);
                    if (f.BorderDist[i] == 0) continue;
                    if (f.Zone[i] == -1) { f.BorderDist[i] = 10000; continue; }
                    float best = 10000f;
                    if (byZone.TryGetValue(f.Zone[i], out var tiles))
                        foreach (var (bx, by) in tiles)
                        {
                            float dx = x - bx, dy = y - by;
                            float d = MathF.Sqrt(dy * dy + dx * dx);
                            if (d < best) best = d;
                        }
                    f.BorderDist[i] = (int)MathF.Round(best, MidpointRounding.ToEven);
                }
        }
    }
}
