namespace H5.Rmg;

/// <summary>
/// Statics (exe 0x00906130, after the roads): every template zone in order, its zone's vf 0x34 ("at %g big statics in
/// zone %d set") then vf 0x30 ("one tile statics"). Surface zones are CGameZone (0x009253b0 / 0x00922f70); underground
/// zones CGameSubInfernoZone (0x00baa4a0 / 0x00baa900), CGameSubterraZone (0x00928cb0 / 0x00929110) or
/// CGameDwarvenZone (0x00926d60 / 0x00926e80), chosen in 0x0090bd80.
/// </summary>
public sealed partial class RmgGenerator
{
    void PlaceStatics(RmgResult r)
    {
        for (int zi = 0; zi < _t.Zones.Count; zi++)
        {
            var z = Zones.FirstOrDefault(zz => zz.Def == _t.Zones[zi]);
            if (z == null) continue;
            switch (z.Class)
            {
                case RmgZoneClass.Game:
                    BigStatics(r, z, lakesAndMountains: z.Floor != 1);
                    OneTileStatics(r, z);
                    break;
                case RmgZoneClass.WaterBordered:       // vf 0x34 is 0x009253b0 (its tests through vf 0x44 0x00bac810)
                    BigStatics(r, z, lakesAndMountains: z.Floor != 1);
                    OneTileStatics(r, z, water: true);
                    break;
                case RmgZoneClass.SubInferno:          // 0x00baa4a0: no lakes or mountains, vf 0x3c after each static
                    BigStatics(r, z, lakesAndMountains: false);
                    UndergroundOneTileStatics(r, z);
                    break;
                case RmgZoneClass.Subterra:            // 0x00928cb0: vf 0x40 first, no vf 0x3c on big statics
                    RockFill(z);
                    BigStatics(r, z, lakesAndMountains: false);
                    UndergroundOneTileStatics(r, z);   // 0x00929110 is 0x00baa900 instruction for instruction
                    break;
                case RmgZoneClass.Dwarven:             // 0x00926d60: vf 0x40, then 0x009191a0(0x3c); no big statics
                    RockFill(z);
                    DistanceField(z, 0x3c);
                    DwarvenOneTileStatics(r, z);
                    break;
            }
        }
    }

    /// <summary>
    /// vf 0x3c, called by the underground classes' static placers after an object is created (CGameZone 0x00918b50 does
    /// nothing): CGameSubInfernoZone 0x00bab980 lights "Crater" (path longer than 5), "Lavacrack" / "Hellpikes" (longer
    /// than 8) statics (0x00914460 → 0x009142d0), CGameSubterraZone 0x0092a190 "Crystal" (longer than 6, 0x009143a0),
    /// CGameDwarvenZone 0x00927f90 "Fakel" (longer than 4) or "FireColumn" (longer than 9) (0x00914420 / 0x00914440).
    /// A lit static gets pointLights.[0] with Pos.z = zMin + Next(zMax − zMin), a radius from Next(LightRadiusMax −
    /// LightRadiusMin) and the PointLightParams colour of the zone index. TODO(exe): the light is not emitted.
    /// </summary>
    void StaticLight(RmgZone z, string shared)
    {
        bool Has(int longer, string part) => shared.Length > longer && NameHas(shared, part);
        bool lit = z.Class switch
        {
            RmgZoneClass.SubInferno => Has(5, "Crater") || Has(8, "Lavacrack") || Has(8, "Hellpikes"),
            RmgZoneClass.Subterra => Has(6, "Crystal"),
            RmgZoneClass.Dwarven => Has(4, "Fakel") || Has(9, "FireColumn"),
            _ => false,
        };
        if (!lit) return;
        var p = _db.Params;
        Rng.Next(p.LightZMax - p.LightZMin);
        Rng.Next(p.LightRadiusMax - p.LightRadiusMin);
    }

    static bool NameHas(string shared, string part) => shared.Contains(part, StringComparison.Ordinal);

    /// <summary>
    /// exe 0x009253b0 (CGameZone vf 0x34): off the underground floor the lakes (0x00924940) and mountains (0x00922a10)
    /// first; then 0x009191a0(0x3c) and the zone tiles with distance &gt; 1. For each of the preset's BigStatics (+0xb4)
    /// in order and each such tile: a static with more than 10 blocked tiles skips a tile closer than 15 (0x00e44668)
    /// to an earlier big static (+0xb4 of the zone) when its name holds "Crater" (0x009141b0), and tries the 4 quarter
    /// turns in order, a smaller one one Next(4) turn; the first turn passing 0x00919a50 draws Float(0,1) and places the
    /// static when it is below 1 / (blocked tiles + 1) (0x009145b0, 0x0091a420; a big one's tile joins +0xb4).
    /// The underground copies 0x00baa4a0 (CGameSubInfernoZone, vf 0x3c after each creation) and 0x00928cb0
    /// (CGameSubterraZone) are this loop without the lakes, the mountains and 0x00915e00.
    /// A placed static whose name holds "Mountain" (0x009143e0 → 0x00914150, name longer than 7) with more than 15
    /// blocked tiles raises the ground under it (0x00915e00, <see cref="MountainHeights"/>).
    /// </summary>
    void BigStatics(RmgResult r, RmgZone z, bool lakesAndMountains)
    {
        var race = _db.Race(z.Preset);
        if (lakesAndMountains)
        {
            Lakes(r, z, race);
            Mountains(r, z, race);
        }
        DistanceField(z, 0x3c);
        var f = Floors[z.Floor];
        var tiles = z.Tiles.Where(t => f.Dist[f.At(t.X, t.Y)] > 1).ToList();
        foreach (var s in race.BigStatics)
        {
            var d = Catalog.Get(s);
            bool big = d.Blocked.Count > 10;
            bool crater = NameHas(s, "Crater");
            foreach (var (x, y) in tiles)
            {
                if (big && crater && z.BigStaticPoints.Any(p => MathF.Sqrt((p.Y - y) * (p.Y - y) + (p.X - x) * (p.X - x)) < 15f)) continue;
                int turns = big ? 4 : 1;
                for (int t = 0; t < turns; t++)
                {
                    float a = QuarterAngle(big ? t : Rng.Next(4));
                    if (!StaticFits(z, x, y, a, d)) continue;
                    double chance = 1.0 / (d.Blocked.Count + 1);
                    if (Rng.Float(0, 1) < chance)
                    {
                        CreateObject(r, z, s, x, y, a, "static");
                        if (z.Class == RmgZoneClass.SubInferno) StaticLight(z, s);   // 0x00baa4a0 only
                        if (big) z.BigStaticPoints.Add((x, y));
                        Stamp(z, x, y, a, d, null);
                        if (s.Length > 7 && NameHas(s, "Mountain") && d.Blocked.Count > 15) MountainHeights(z, x, y, a, d);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// exe 0x00924940, for Haven, Sylvan, Necropolis, Inferno, Fortress and Stronghold zones on the surface:
    /// 0x009191a0(0x3e); a lake centre is a zone tile with distance and border distance &gt; 5, no 8-neighbour farther
    /// out, Float(0,1) &lt; 0.4 and no earlier centre closer than 20 (0x00e531bc); it joins +0xb4 and gets flag 2. A
    /// level grid grows from the centres for levels 0..12 (x 1..W−2 outer, y inner): orthogonal neighbours with flags
    /// 0, distance and border distance &gt; 2 get level + 2, diagonal ones + 3, both flag 0x80; a pass that found fewer
    /// than its level (but some) tiles ends it. The lake's tiles are those with a level and distance &gt; 3; then
    /// 0x009225b0 per centre and 0x00922760 over the tiles.
    /// The lake tiles go to 0x00916f80 (<see cref="LakeTerrain"/>) before the objects.
    /// </summary>
    void Lakes(RmgResult r, RmgZone z, RmgRacePreset race)
    {
        if (z.Race is not (3 or 4 or 7 or 8 or 10 or 9) || z.Floor != 0) return;
        var f = Floors[z.Floor];
        DistanceField(z, 0x3e);
        var centres = new List<(int X, int Y)>();
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Dist[i] <= 5 || f.BorderDist[i] <= 5) continue;
            bool higher = false;
            foreach (var (dx, dy) in Around8)
            {
                int nx = x + dx, ny = y + dy;
                if (f.In(nx, ny) && f.Dist[i] < f.Dist[f.At(nx, ny)]) { higher = true; break; }
            }
            if (higher) continue;
            if (!(Rng.Float(0, 1) < 0.4f)) continue;                                           // 0x00e12fe4
            if (centres.Any(c => MathF.Sqrt((c.Y - y) * (c.Y - y) + (c.X - x) * (c.X - x)) < 20f)) continue;
            centres.Add((x, y));
            z.BigStaticPoints.Add((x, y));
        }
        var level = new float[W * H];
        Array.Fill(level, 1000f);
        foreach (var (x, y) in centres)
        {
            level[f.At(x, y)] = 0;
            f.Flags[f.At(x, y)] = TileFlags.Blocked;
        }
        for (int l = 0; l < 13; l++)
        {
            int count = 0;
            for (int x = 1; x < W - 1; x++)
                for (int y = 1; y < H - 1; y++)
                {
                    int i = f.At(x, y);
                    if (f.Zone[i] != z.Id || level[i] != l) continue;
                    count++;
                    for (int t = 0; t < 2; t++)
                        foreach (var (dx, dy) in t == 0 ? Ortho4 : Diag4)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (!f.In(nx, ny)) continue;
                            int j = f.At(nx, ny);
                            if (f.Flags[j] != 0 || f.Dist[j] <= 2 || f.BorderDist[j] <= 2) continue;
                            level[j] = l + 2 + t;
                            f.Flags[j] = TileFlags.Lake;
                        }
                }
            if (count != 0 && count < l) break;
        }
        var lake = new List<(int X, int Y)>();
        for (int x = 1; x < W - 1; x++)
            for (int y = 1; y < H - 1; y++)
                if (level[f.At(x, y)] != 1000f && f.Dist[f.At(x, y)] > 3) lake.Add((x, y));
        LakeTerrain(r, z, race, lake);
        foreach (var c in centres) LakeCentreObjects(r, z, race, c.X, c.Y);
        LakeTileObjects(r, z, race, lake);
    }

    /// <summary>
    /// exe 0x00916f80 (builder +0x60; the lake tiles, zone id, zone race +0x18), called by 0x00924940 before the lake
    /// objects. Per lake tile (x, y) in order: n = its orthogonal neighbours (0x00f46718) in the list; the +0x20 preset's
    /// WaterTile (+0x64) at 0x96 on its four vertices; with distance &gt; 3 and n &gt; 2 the depth grid's 4 x 4 cells (rows
    /// 2y .. 2y + 3, columns 2x .. 2x + 3) get m·0x3c − 0x3c, or 0xff when that is not below 0xff, m = min(distance,
    /// border distance); the WaterBottomTile (+0x70) on the four vertices with strength min((m − k)·s, 200), k = 4 and
    /// s = 0xf for race 7 (Necropolis), else 2 and 0x1e. Then twice over the list (entry i % n, no bounds tests) the
    /// /6 smoothing of 0x00916790 on those cells (±2, then ±1); then every tile of 1 .. size − 2 with more than two of
    /// its 8 neighbours (0x00f466d8) flagged exactly 0x80 gets flags 0x82.
    /// TODO(exe): the vertex strengths (0x00913510) and the WaterBottomTile are not modelled: the tile's one layer entry
    /// is the WaterTile.
    /// </summary>
    void LakeTerrain(RmgResult r, RmgZone z, RmgRacePreset race, List<(int X, int Y)> lake)
    {
        var f = Floors[z.Floor];
        var lakeSet = new HashSet<(int, int)>(lake);
        int cols = 2 * f.H + 1;
        var d = f.Depth;
        foreach (var (x, y) in lake)
        {
            int n = 0;
            foreach (var (dx, dy) in Ortho4) if (lakeSet.Contains((x + dx, y + dy))) n++;
            if (race.WaterTile.Length > 0) r.TileLayers[z.Floor][y * W + x] = race.WaterTile;
            int i = f.At(x, y);
            if (f.Dist[i] > 3 && n > 2)
            {
                int m = Math.Min(f.Dist[i], f.BorderDist[i]);
                byte c = m * 0x3c - 0x3c < 0xff ? (byte)(m * 0x3c - 0x3c) : (byte)0xff;
                for (int k = 0; k < 4; k++)
                    for (int j = 0; j < 4; j++) d[(2 * y + k) * cols + 2 * x + j] = c;
            }
        }
        for (int k = 0; k < 2 * lake.Count; k++)
        {
            var (x, y) = lake[k % lake.Count];
            for (int a = 0; a < 4; a++)
                for (int j = 0; j < 4; j++)
                {
                    int at = (2 * y + a) * cols + 2 * x + j;
                    d[at] = (byte)((d[at - 2 * cols] + d[at + 2 * cols] + d[at - 2] + d[at + 2] + d[at] * 2) / 6);
                }
            for (int a = 0; a < 4; a++)
                for (int j = 0; j < 4; j++)
                {
                    int at = (2 * y + a) * cols + 2 * x + j;
                    d[at] = (byte)((d[at - cols] + d[at + cols] + d[at - 1] + d[at + 1] + d[at] * 2) / 6);
                }
        }
        var ring = new List<int>();
        for (int x = 1; x < W - 1; x++)
            for (int y = 1; y < H - 1; y++)
            {
                int n = 0;
                foreach (var (dx, dy) in Around8) if (f.Flags[f.At(x + dx, y + dy)] == TileFlags.Lake) n++;
                if (n > 2) ring.Add(f.At(x, y));
            }
        foreach (int i in ring) f.Flags[i] = TileFlags.Lake | TileFlags.Blocked;
    }

    /// <summary>exe 0x009225b0: Next(3) + 1 of the preset's OverLakeCenterObjects (+0xcc), each turned by Next(4) at (x − 2 + Next(5), y − 2 + Next(5)) (y offset drawn first).</summary>
    void LakeCentreObjects(RmgResult r, RmgZone z, RmgRacePreset race, int cx, int cy)
    {
        var list = race.OverLakeCenterObjects;
        if (list.Count == 0) return;
        int n = Rng.Next(3) + 1;
        for (int k = 0; k < n; k++)
        {
            float a = QuarterAngle(Rng.Next(4));
            int oy = Rng.Next(5);
            int ox = Rng.Next(5);
            var s = list[Rng.Next(list.Count)];
            if (s.Length > 0) CreateObject(r, z, s, cx - 2 + ox, cy - 2 + oy, a, "lake centre");
        }
    }

    /// <summary>exe 0x00922760: per lake tile, Next(10) &lt; 6 places one of the preset's OverLakeOneTileRandomObjects (+0xd8; Next(4) turn, Next(n) pick; an empty reference places nothing).</summary>
    void LakeTileObjects(RmgResult r, RmgZone z, RmgRacePreset race, List<(int X, int Y)> tiles)
    {
        var list = race.OverLakeOneTileRandomObjects;
        if (list.Count == 0) return;
        foreach (var (x, y) in tiles)
        {
            if ((uint)Rng.Next(10) >= 6) continue;
            float a = QuarterAngle(Rng.Next(4));
            var s = list[Rng.Next(list.Count)];
            if (s.Length > 0) CreateObject(r, z, s, x, y, a, "lake tile");
        }
    }

    /// <summary>
    /// exe 0x00922a10: the zone tiles whose current distance field value is &gt; 4; per tile without an earlier mountain
    /// closer than 4 (0x00e11c80): one of the preset's Mountains (+0xc0) by Next(n), a Next(4) turn, 0x00919a50, then
    /// 0x009145b0; its rotated blocked tiles get flag 0x100 (2 once all mountains stand), then 0x00915e00 raises the
    /// ground under it (<see cref="MountainHeights"/>). No 0x0091a420.
    /// </summary>
    void Mountains(RmgResult r, RmgZone z, RmgRacePreset race)
    {
        if (race.Mountains.Count == 0) return;
        var f = Floors[z.Floor];
        var cands = z.Tiles.Where(t => f.Dist[f.At(t.X, t.Y)] > 4).ToList();
        var placed = new List<(int X, int Y)>();
        var tiles = new List<(int X, int Y)>();
        foreach (var (x, y) in cands)
        {
            if (placed.Any(p => MathF.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)) < 4f)) continue;
            var s = race.Mountains[Rng.Next(race.Mountains.Count)];
            var d = Catalog.Get(s);
            float a = QuarterAngle(Rng.Next(4));
            if (!StaticFits(z, x, y, a, d)) continue;
            CreateObject(r, z, s, x, y, a, "mountain");
            int q = ExeQuarter(a);
            foreach (var b in d.Blocked)
            {
                var (ox, oy) = Rotate(b, q);
                int tx = x + ox, ty = y + oy;
                if (f.In(tx, ty)) f.Flags[f.At(tx, ty)] = TileFlags.Mountain;
                tiles.Add((tx, ty));
            }
            MountainHeights(z, x, y, a, d);
            placed.Add((x, y));
        }
        foreach (var (tx, ty) in tiles)
            if (f.In(tx, ty)) f.Flags[f.At(tx, ty)] = TileFlags.Blocked;
    }

    /// <summary>
    /// exe 0x00915e00 (position, angle, blocked tiles; disassembly, the decompile is pruned): for each rotated blocked
    /// offset (dx, dy) (0x00b0be60) with r = √(dx² + dy²) and 3.5 − r &gt; 0 (0x00e57d10, 0x00e0be10), 0x009127b0 adds
    /// 2·(3.5 − r) to the floor-0 height of vertex (row chop(y + dy), column chop(x + dx)). Returns false.
    /// </summary>
    void MountainHeights(RmgZone z, int px, int py, float angle, H5.Adventure.Map.SharedDef d)
    {
        var f = Floors[0];
        int q = ExeQuarter(angle);
        foreach (var b in d.Blocked)
        {
            var (ox, oy) = Rotate(b, q);
            double r = Math.Sqrt((double)oy * oy + (double)ox * ox);
            if (!(3.5 - r > 0.0)) continue;
            float add = (float)((3.5 - r) * 2.0);
            f.Heights[f.Lv(py + oy, px + ox)] += add;
        }
    }

    /// <summary>
    /// exe 0x00922f70 (CGameZone vf 0x30). 0x009191a0(0x3c); the zone's tiles with flags 0 and border distance != 0
    /// fall into three lists by distance: &gt; 4, 3..4, 2. Then, with a Next(4) turn drawn for every tile, each border tile
    /// (border distance 0) with flags 0, 1, 8, 0x10 or 0x20 gets, by Float(0,1) &lt; 0.4 (or when there are no
    /// OneTileBigObjects), one of the OneTileSmallBlockers (+0x90), else one of the OneTileBigObjects (+0xa8); flag 2.
    /// Distance 2: Float &lt; 0.15 a big object (flag 2), else Float &lt; 0.4 a small blocker (flag 2), else Float &lt; 0.6 one of
    /// the OneTileSmallNonblockers (+0x9c, flag 1). Distance 3..4: Float &lt; 0.3 a big object, else Float &lt; 0.5 a small
    /// blocker. Distance &gt; 4: Float ≤ 0.5 a big object. "FireDot" statics take the builder's angle (0x009140f0).
    /// Objects are created by 0x009145b0 without footprint tests.
    /// </summary>
    /// <remarks>
    /// <paramref name="water"/>: exe 0x00bad6c0 (CGameWaterBorderedZone vf 0x30) is this function without the border
    /// pass, its lists taking the zone tiles with flags 0 and border distance ≥ 3 (distance &gt; 4, 3..4, 2); the three
    /// list loops are instruction for instruction the same (0.15 / 0.4 / 0.6, 0.3 / 0.5, ≤ 0.5; coordinates chopped).
    /// </remarks>
    void OneTileStatics(RmgResult r, RmgZone z, bool water = false)
    {
        var race = _db.Race(z.Preset);
        var f = Floors[z.Floor];
        DistanceField(z, 0x3c);
        var far = new List<(int X, int Y)>();
        var mid = new List<(int X, int Y)>();
        var near = new List<(int X, int Y)>();
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Flags[i] != 0 || (water ? f.BorderDist[i] < 3 : f.BorderDist[i] == 0)) continue;
            int d = f.Dist[i];
            if (d > 4) far.Add((x, y));
            else if (d > 2) mid.Add((x, y));
            else if (d > 1) near.Add((x, y));
        }
        var blockers = race.OneTileSmallBlockers;
        var nonblockers = race.OneTileSmallNonblockers;
        var bigs = race.OneTileBigObjects;

        void Put(List<string> list, int x, int y, float a, bool fireDot, int flag)
        {
            if (list.Count == 0) return;   // no stock preset lacks both lists
            var s = list[Rng.Next(list.Count)];
            if (fireDot && NameHas(s, "FireDot")) a = BuilderAngle;
            CreateObject(r, z, s, x, y, a, "static");
            f.Flags[f.At(x, y)] = flag;
        }

        foreach (var (x, y) in water ? new List<(int X, int Y)>() : z.Tiles)
        {
            float a = QuarterAngle(Rng.Next(4));
            int i = f.At(x, y);
            if (f.BorderDist[i] != 0 || f.Flags[i] is not (0 or 1 or 8 or 0x10 or 0x20)) continue;
            float roll = Rng.Float(0, 1);
            if ((roll < 0.4f && blockers.Count > 0) || bigs.Count == 0) Put(blockers, x, y, a, true, TileFlags.Blocked);
            else Put(bigs, x, y, a, false, TileFlags.Blocked);
        }
        foreach (var (x, y) in near)
        {
            float a = QuarterAngle(Rng.Next(4));
            if (Rng.Float(0, 1) < 0.15f && bigs.Count > 0) { Put(bigs, x, y, a, false, TileFlags.Blocked); continue; }   // 0x00e5807c
            if (blockers.Count > 0 && Rng.Float(0, 1) < 0.4f) { Put(blockers, x, y, a, true, TileFlags.Blocked); continue; }
            if (nonblockers.Count > 0 && Rng.Float(0, 1) < 0.6f) Put(nonblockers, x, y, a, true, TileFlags.Nonblocker);      // 0x00e18580
        }
        foreach (var (x, y) in mid)
        {
            float a = QuarterAngle(Rng.Next(4));
            if (Rng.Float(0, 1) < 0.3f && bigs.Count > 0) { Put(bigs, x, y, a, false, TileFlags.Blocked); continue; }      // 0x00e1a70c
            if (blockers.Count > 0 && Rng.Float(0, 1) < 0.5f) Put(blockers, x, y, a, true, TileFlags.Blocked);              // 0x00e0ab70
        }
        foreach (var (x, y) in far)
        {
            float a = QuarterAngle(Rng.Next(4));
            if (Rng.Float(0, 1) <= 0.5f && bigs.Count > 0) Put(bigs, x, y, a, false, TileFlags.Blocked);
        }
    }

    /// <summary>
    /// exe 0x00baa900 (CGameSubInfernoZone vf 0x30; CGameSubterraZone's 0x00929110 is the same code). 0x009191a0(0x3c);
    /// zone tiles with flags 0, border distance != 0, level not above 0x10 (0x00915d90) and 1 ≤ x, y &lt; size − 1
    /// (0x009157b0) fall into the lists distance &gt; 4, 3..4 and 2. Border pass over the zone tiles (level not above
    /// 0x10, inside the 1-tile frame): a Next(4) turn, then for border tiles with flags 0/1/8/0x10/0x20 and
    /// Float(0,1) ≥ 0.7 (0x00e15698) the CGameZone choice (Float &lt; 0.4 a small blocker with vf 0x3c, else a big
    /// object; flag 2). Distance 2: Float ≥ 0.6 (0x00e18580), Next(4), Float &lt; 0.15 a big object, else Float &lt; 0.4 a
    /// small blocker, else Float &lt; 0.6 a nonblocker (flag 1). Distance 3..4: Float ≥ 0.9 (0x00e1a704), Next(4), Float &lt;
    /// 0.3 a big object, else Float &lt; 0.5 a small blocker. Distance &gt; 4: Float ≥ 0.9, Next(4), Float ≤ 0.5 a big object.
    /// Blockers and nonblockers take the builder angle when "FireDot" (0x009140f0) and get vf 0x3c; big objects neither.
    /// </summary>
    void UndergroundOneTileStatics(RmgResult r, RmgZone z)
    {
        var race = _db.Race(z.Preset);
        var f = Floors[z.Floor];
        DistanceField(z, 0x3c);
        bool High(int x, int y) => f.Level[f.Lv(y, x)] > 0x10;                               // 0x00915d90 / 0x009128e0
        bool Inner(int x, int y) => x >= 1 && x < W - 1 && y >= 1 && y < H - 1;              // 0x009157b0
        var far = new List<(int X, int Y)>();
        var mid = new List<(int X, int Y)>();
        var near = new List<(int X, int Y)>();
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Flags[i] != 0 || f.BorderDist[i] == 0 || High(x, y) || !Inner(x, y)) continue;
            int d = f.Dist[i];
            if (d > 4) far.Add((x, y));
            else if (d > 2) mid.Add((x, y));
            else if (d > 1) near.Add((x, y));
        }
        var blockers = race.OneTileSmallBlockers;
        var nonblockers = race.OneTileSmallNonblockers;
        var bigs = race.OneTileBigObjects;

        void Put(List<string> list, int x, int y, float a, bool lit, int flag)
        {
            if (list.Count == 0) return;
            var s = list[Rng.Next(list.Count)];
            if (lit && NameHas(s, "FireDot")) a = BuilderAngle;
            CreateObject(r, z, s, x, y, a, "static");
            if (lit) StaticLight(z, s);
            f.Flags[f.At(x, y)] = flag;
        }

        foreach (var (x, y) in z.Tiles)
        {
            if (High(x, y) || !Inner(x, y)) continue;
            float a = QuarterAngle(Rng.Next(4));
            int i = f.At(x, y);
            if (f.BorderDist[i] != 0 || f.Flags[i] is not (0 or 1 or 8 or 0x10 or 0x20)) continue;
            if (Rng.Float(0, 1) < 0.7f) continue;
            float roll = Rng.Float(0, 1);
            if ((roll < 0.4f && blockers.Count > 0) || bigs.Count == 0) Put(blockers, x, y, a, true, TileFlags.Blocked);
            else Put(bigs, x, y, a, false, TileFlags.Blocked);
        }
        foreach (var (x, y) in near)
        {
            if (Rng.Float(0, 1) < 0.6f) continue;
            float a = QuarterAngle(Rng.Next(4));
            if (Rng.Float(0, 1) < 0.15f && bigs.Count > 0) { Put(bigs, x, y, a, false, TileFlags.Blocked); continue; }
            if (blockers.Count > 0 && Rng.Float(0, 1) < 0.4f) { Put(blockers, x, y, a, true, TileFlags.Blocked); continue; }
            if (nonblockers.Count > 0 && Rng.Float(0, 1) < 0.6f) Put(nonblockers, x, y, a, true, TileFlags.Nonblocker);
        }
        foreach (var (x, y) in mid)
        {
            if (Rng.Float(0, 1) < 0.9f) continue;
            float a = QuarterAngle(Rng.Next(4));
            if (Rng.Float(0, 1) < 0.3f && bigs.Count > 0) { Put(bigs, x, y, a, false, TileFlags.Blocked); continue; }
            if (blockers.Count > 0 && Rng.Float(0, 1) < 0.5f) Put(blockers, x, y, a, true, TileFlags.Blocked);
        }
        foreach (var (x, y) in far)
        {
            if (Rng.Float(0, 1) < 0.9f) continue;
            float a = QuarterAngle(Rng.Next(4));
            if (Rng.Float(0, 1) <= 0.5f && bigs.Count > 0) Put(bigs, x, y, a, false, TileFlags.Blocked);
        }
    }

    /// <summary>
    /// vf 0x40 of the underground classes (0x00baa3a0 / 0x00928bb0 / 0x00926d80; CGameZone's 0x00918b40 is empty):
    /// 0x009191a0(0x3c, all zones), then 0x009159a0 on the underground floor: for each 3x3 block (bx, by from 1 while
    /// below size/3 − 1; bx outer) whose 9x9 surrounding (3b − 3 .. 3b + 5) carries no flag of 0x3e (low byte), the 4x4
    /// level vertices from 3b get 0x20 and the height 2 x 18 (0x00f466b8),
    /// 0x009131d0 smooths the 3-cells from 3b − 3 to 3b + 6, and the 9x9 tiles get flag 0x40; at the end 0x40 becomes 2.
    /// </summary>
    void RockFill(RmgZone z)
    {
        DistanceField(z, 0x3c, allZones: true);
        if (Floors.Count < 2) return;
        var f = Floors[1];
        for (int bx = 1; bx < W / 3 - 1; bx++)
            for (int by = 1; by < H / 3 - 1; by++)
            {
                bool busy = false;
                for (int y = 3 * by - 3; y <= 3 * by + 5 && !busy; y++)
                    for (int x = 3 * bx - 3; x <= 3 * bx + 5; x++)
                        if ((f.Flags[f.At(x, y)] & 0x3e) != 0) { busy = true; break; }
                if (busy) continue;
                for (int k = 0; k < 4; k++)
                    for (int m = 0; m < 4; m++)
                    {
                        f.Level[f.Lv(3 * by + k, 3 * bx + m)] = 0x20;
                        f.Heights[f.Lv(3 * by + k, 3 * bx + m)] = 18f + 18f;
                    }
                for (int r0 = 3 * by - 3; r0 <= 3 * by + 6; r0 += 3)
                    for (int c0 = 3 * bx - 3; c0 <= 3 * bx + 6; c0 += 3)
                        SmoothLevel(f, r0, c0);
                for (int x = 3 * bx - 3; x <= 3 * bx + 5; x++)
                    for (int y = 3 * by - 3; y <= 3 * by + 5; y++)
                        f.Flags[f.At(x, y)] = 0x40;
            }
        for (int i = 0; i < f.Flags.Length; i++)
            if (f.Flags[i] == 0x40) f.Flags[i] = TileFlags.Blocked;
    }

    /// <summary>
    /// exe 0x009131d0 (level part): the 3x3 vertices from (r0, c0) take the bilinear mix of the corners (r0, c0),
    /// (r0 + 3, c0), (r0, c0 + 3), (r0 + 3, c0 + 3) (0x20 off the grid, 0x00f46670), integer / 9; vertices off the grid
    /// are skipped; the vertex's height moves by (new − old) x 1.125 (0x00e0d3e4).
    /// </summary>
    static void SmoothLevel(RmgFloor f, int r0, int c0)
    {
        int rows = f.H + 1, cols = f.W + 1;
        int Get(int r, int c) => r < 0 || r >= rows || c < 0 || c >= cols ? 0x20 : f.Level[f.Lv(r, c)];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                int a = Get(r0, c0), b = Get(r0 + 3, c0), c = Get(r0, c0 + 3), d = Get(r0 + 3, c0 + 3);
                int v = ((b * i + a * (3 - i)) * (3 - j) + (c * (3 - i) + d * i) * j) / 9;
                int rr = r0 + i, cc = c0 + j;
                if (rr < 0 || rr >= rows || cc < 0 || cc >= cols) continue;
                f.Heights[f.Lv(rr, cc)] += (v - f.Level[f.Lv(rr, cc)]) * 1.125f;
                f.Level[f.Lv(rr, cc)] = (byte)v;
            }
    }

    /// <summary>
    /// exe 0x00926e80 (CGameDwarvenZone vf 0x30). Over the whole floor (y outer): tiles whose level vertices (y, x),
    /// (y + 1, x), (y, x + 1) all differ from 0x10, tiles with x or y below 2 and tiles beyond ((size − 1) / 3)·3 get
    /// flags |= 0x440. 0x009191a0(0x400, all zones). Zone tiles at distance 1 where x % 10 == 5 or y % 10 == 5 and no
    /// static of +0x148 lies closer than 4 (0x00e11c80): OneTileBigObjects[Next(n − 1) + 1]; at distance 2 where
    /// x % 10 == 0 or y % 10 == 0 (same test): OneTileSmallBlockers[Next(n)]. 0x009191a0(0x3c); zone tiles with flags 0 at
    /// distance ≥ 3 get OneTileBigObjects[0] (no draw, flags 2 | 0x400), at distance 2 (same test)
    /// OneTileSmallBlockers[Next(n − 1)]. All at angle 0 with vf 0x3c after each, flag 2; the tested ones join +0x148.
    /// </summary>
    void DwarvenOneTileStatics(RmgResult r, RmgZone z)
    {
        var race = _db.Race(z.Preset);
        var f = Floors[z.Floor];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = f.At(x, y);
                if (y < H - 1 && f.Level[f.Lv(y, x)] != 0x10 && f.Level[f.Lv(y + 1, x)] != 0x10 && f.Level[f.Lv(y, x + 1)] != 0x10)
                    f.Flags[i] |= 0x440;
                if (y < 2 || x < 2) f.Flags[i] |= 0x440;
                if ((H - 1) / 3 * 3 < y) f.Flags[i] |= 0x440;
                if ((W - 1) / 3 * 3 < x) f.Flags[i] |= 0x440;
            }
        DistanceField(z, 0x400, allZones: true);
        var one = new List<(int X, int Y)>();
        var two = new List<(int X, int Y)>();
        foreach (var (x, y) in z.Tiles)
        {
            int d = f.Dist[f.At(x, y)];
            if (d == 1) one.Add((x, y));
            if (d == 2) two.Add((x, y));
        }
        var blockers = race.OneTileSmallBlockers;
        var bigs = race.OneTileBigObjects;
        bool Crowded(int x, int y) => z.DwarvenPoints.Any(p => MathF.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)) < 4f);
        void Put(string s, int x, int y, bool remember)
        {
            CreateObject(r, z, s, x, y, 0f, "static");
            StaticLight(z, s);
            f.Flags[f.At(x, y)] = TileFlags.Blocked;
            if (remember) z.DwarvenPoints.Add((x, y));
        }
        foreach (var (x, y) in one)
        {
            if (x % 10 != 5 && y % 10 != 5) continue;
            if (bigs.Count == 0 || Crowded(x, y)) continue;
            int k = Rng.Next(bigs.Count - 1) + 1;
            if (k < bigs.Count) Put(bigs[k], x, y, true);
        }
        foreach (var (x, y) in two)
        {
            if (x % 10 != 0 && y % 10 != 0) continue;
            if (blockers.Count == 0 || Crowded(x, y)) continue;
            Put(blockers[Rng.Next(blockers.Count)], x, y, true);
        }
        DistanceField(z, 0x3c);
        var deep = new List<(int X, int Y)>();
        var edge = new List<(int X, int Y)>();
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Flags[i] != 0) continue;
            if (f.Dist[i] >= 3) deep.Add((x, y));
            else if (f.Dist[i] == 2) edge.Add((x, y));
        }
        if (bigs.Count > 0)
            foreach (var (x, y) in deep)
            {
                Put(bigs[0], x, y, false);
                f.Flags[f.At(x, y)] |= 0x400;
            }
        foreach (var (x, y) in edge)
        {
            if (blockers.Count == 0 || Crowded(x, y)) continue;
            Put(blockers[Rng.Next(blockers.Count - 1)], x, y, true);
        }
    }
}
