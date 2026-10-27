namespace H5.Rmg;

/// <summary>
/// Statics (exe 0x00906130, after the roads): every template zone in order, its zone's vf 0x34 ("at %g big statics in
/// zone %d set") then vf 0x30 ("one tile statics"). Surface zones are CGameZone (0x009253b0 / 0x00922f70); the
/// underground classes (CGameSubInfernoZone / CGameSubterraZone / CGameDwarvenZone, 0x0090bd80) are not ported.
/// </summary>
public sealed partial class RmgGenerator
{
    void PlaceStatics(RmgResult r)
    {
        for (int zi = 0; zi < _t.Zones.Count; zi++)
        {
            var z = Zones.FirstOrDefault(zz => zz.Def == _t.Zones[zi]);
            if (z == null) continue;
            if (z.Floor != 0)
            {
                // TODO(exe): underground zone classes' statics (0x00baa4a0/0x00baa900, 0x0092ac10's 0x00928cb0/0x00929110,
                // 0x00926d60/0x00926e80) are not ported; their zones get no statics.
                continue;
            }
            BigStatics(r, z);
            OneTileStatics(r, z);
        }
    }

    static bool NameHas(string shared, string part) => shared.Contains(part, StringComparison.Ordinal);

    /// <summary>
    /// exe 0x009253b0 (CGameZone vf 0x34): off the underground floor the lakes (0x00924940) and mountains (0x00922a10)
    /// first; then 0x009191a0(0x3c) and the zone tiles with distance &gt; 1. For each of the preset's BigStatics (+0xb4)
    /// in order and each such tile: a static with more than 10 blocked tiles skips a tile closer than 15 (0x00e44668)
    /// to an earlier big static (+0xb4 of the zone) when its name holds "Crater" (0x009141b0), and tries the 4 quarter
    /// turns in order, a smaller one one Next(4) turn; the first turn passing 0x00919a50 draws Float(0,1) and places the
    /// static when it is below 1 / (blocked tiles + 1) (0x009145b0, 0x0091a420; a big one's tile joins +0xb4).
    /// TODO(exe): the terrain under "Mountain" statics with more than 15 tiles (0x00915e00) is not painted.
    /// </summary>
    void BigStatics(RmgResult r, RmgZone z)
    {
        var race = _db.Race(z.Race);
        if (z.Floor != 1)
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
                        if (big) z.BigStaticPoints.Add((x, y));
                        Stamp(z, x, y, a, d, null);
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
    /// TODO(exe): the lake terrain (0x00916f80) is not painted.
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
        foreach (var c in centres) LakeCentreObjects(r, z, race, c.X, c.Y);
        LakeTileObjects(r, z, race, lake);
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
    /// 0x009145b0; its rotated blocked tiles get flag 0x100 (2 once all mountains stand). No 0x0091a420.
    /// TODO(exe): the terrain under the mountains (0x00915e00) is not painted.
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
            placed.Add((x, y));
        }
        foreach (var (tx, ty) in tiles)
            if (f.In(tx, ty)) f.Flags[f.At(tx, ty)] = TileFlags.Blocked;
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
    void OneTileStatics(RmgResult r, RmgZone z)
    {
        var race = _db.Race(z.Race);
        var f = Floors[z.Floor];
        DistanceField(z, 0x3c);
        var far = new List<(int X, int Y)>();
        var mid = new List<(int X, int Y)>();
        var near = new List<(int X, int Y)>();
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Flags[i] != 0 || f.BorderDist[i] == 0) continue;
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

        foreach (var (x, y) in z.Tiles)
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
}
