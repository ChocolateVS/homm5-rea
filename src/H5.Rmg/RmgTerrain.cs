namespace H5.Rmg;

/// <summary>
/// Map finish (exe 0x00904ad0, the last stage before the save 0x0090a980): every zone's vf 0x38, the road painting
/// 0x00916570 and the surface height pass 0x00918180. The save writes each floor's terrain object as it stands
/// (0x00a57ea0 with ECX = floor 0 / floor 1 → GroundTerrain.bin / UndergroundTerrain.bin), so the vertex grids
/// <see cref="RmgFloor.Heights"/> (chunk 5), <see cref="RmgFloor.Level"/> (chunk 7) and <see cref="RmgFloor.Chunk15"/>
/// (chunk 15) are what the map carries.
/// </summary>
public sealed partial class RmgGenerator
{
    void FinishMap(RmgResult r)
    {
        foreach (var f in Floors)
            foreach (var z in f.Zones.Values)
                if (z.Class == RmgZoneClass.WaterBordered) WaterZoneFinish(z); else ZoneFinish(z);
        PaintRoads(r);
        SurfaceHeights(r);
    }

    /// <summary>
    /// exe 0x009198f0 (vf 0x38 of CGameZone and the underground classes; the water class has its own 0x00bac6b0,
    /// <see cref="WaterZoneFinish"/>): 0x009191a0(0x3c), then every zone tile with distance &gt; 2 or border distance 0 clears its vertex (x, y)
    /// of chunk 15 (0x00915d20).
    /// </summary>
    void ZoneFinish(RmgZone z)
    {
        DistanceField(z, 0x3c);
        var f = Floors[z.Floor];
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Dist[i] > 2 || f.BorderDist[i] == 0) f.Chunk15[f.Lv(y, x)] = 0;
        }
    }

    /// <summary>
    /// exe 0x00918180 on the surface (floor 0): 0x00916290 relief; a tile mask of 1 (builder +0x10); 0x00915860 lakes;
    /// 0x00917a30; 0x00917e10; 0x00912d10 (1); mask reset to 1; 0x00912d10 (1); 0x00917e10; 0x00915f20 lake beds;
    /// 0x00912d10 (0) (disassembly 0x00918180..0x0091826b; the mask is +0x10, rows +0x14, [x][y]).
    /// </summary>
    void SurfaceHeights(RmgResult r)
    {
        var f = Floors[0];
        int n = W;                       // RMG maps are square; the exe mixes the two sizes freely
        Relief(f, n);
        var mask = new byte[W * H];      // [x][y], tile-sized
        Array.Fill(mask, (byte)1);
        for (int x = 0; x < W; x++)      // 0x00915860
            for (int y = 0; y < H; y++)
                if ((f.Flags[f.At(x, y)] & TileFlags.Lake) != 0)
                {
                    AddHeight(f, y, x, -0.5f); AddHeight(f, y, x + 1, -0.5f);
                    AddHeight(f, y + 1, x, -0.5f); AddHeight(f, y + 1, x + 1, -0.5f);
                    mask[x * H + y] = 0;
                }
        TownAndDwellingHollows(r, f);
        LevelUnderObjects(r, f, mask);
        Smooth(f, mask, true);
        Array.Fill(mask, (byte)1);
        Smooth(f, mask, true);
        LevelUnderObjects(r, f, mask);
        LakeBeds(f);
        Smooth(f, mask, false);
    }

    /// <summary>
    /// exe 0x00917a30: per object record (builder +0x40, 0x20 bytes, creation order) on the surface (+0x54 == 0): an
    /// Inferno town (town shared +0xfc == 8) takes the vertices (x, y), x &lt; W, y &lt; H, with √(z² + (ox − (x + 1))² +
    /// (oy − (y + 1))²) &lt; 8 (0x00e2f79c), an Inferno dwelling (Type +0xec 0x48..0x4b: Demon Gate, Imp Crucible,
    /// Kennels, Inferno Military Post) those with √(z² + (ox − x)² + (oy − y)²) &lt; 2.5 (0x00e57d48); z = Pos.z, which
    /// the generator never writes (0x009155f0 sets x and y). 0x00912e70 then sets each to the list's mean height plus −1
    /// (0xbf800000) or −2.5 (0xc0200000): the sum from 0.0 by FADD, FDIVR by the count, FADD of the delta, each chopped
    /// to 24 bits (row "x87 control word"); the distance tests compare square roots of integers with 8 / 2.5, which the
    /// rounding mode cannot move.
    /// </summary>
    void TownAndDwellingHollows(RmgResult r, RmgFloor f)
    {
        foreach (var o in r.Objects)
        {
            if (o.Floor != 0) continue;
            float radius, delta;
            bool shift;
            if (o.Def.Tag == "AdvMapTownShared" && o.Def.TownType == "TOWN_INFERNO") { radius = 8f; delta = -1f; shift = true; }
            else if (o.Def.Tag == "AdvMapDwellingShared" && o.Def.Type is "BUILDING_DEMON_GATE" or "BUILDING_IMP_CRUCIBLE" or "BUILDING_KENNELS" or "BUILDING_INFERNO_MILITARY_POST")
            { radius = 2.5f; delta = -2.5f; shift = false; }
            else continue;
            var list = new List<(int X, int Y)>();
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    float dx = o.X - (float)(shift ? x + 1 : x), dy = o.Y - (float)(shift ? y + 1 : y);
                    if (MathF.Sqrt(0f * 0f + dx * dx + dy * dy) < radius) list.Add((x, y));
                }
            if (list.Count == 0) continue;
            float sum = 0f;                                                     // 0x00e0be10; FADD, FDIVR, FADD chopped
            foreach (var (x, y) in list) sum = X87.Add(sum, f.Heights[f.Lv(y, x)]);
            float v = X87.Add(X87.Div(sum, list.Count), delta);
            foreach (var (x, y) in list) f.Heights[f.Lv(y, x)] = v;
        }
    }

    /// <summary>
    /// exe 0x00917e10: per object record on the surface that is not a static, not an Academy town (town shared +0xfc
    /// == 5) and not an Academy dwelling (Type 0x51, 0x55..0x57): its shared blockedTiles (+0x54) followed by the first
    /// of its activeTiles (+0x6c; 0x00918007..0x00918013 pushes &amp;active[0] once, 0x00a68e00 = push_back of one
    /// 8-byte point), turned by the object's angle (0x00b0be60); each turned tile clears the smoothing mask at
    /// [x + dx][y + dy]; then 0x00913850 (pos, tiles, floor 0) levels them.
    /// </summary>
    void LevelUnderObjects(RmgResult r, RmgFloor f, byte[] mask)
    {
        foreach (var o in r.Objects)
        {
            if (o.Floor != 0 || o.Def.Tag == "AdvMapStaticShared") continue;
            if (o.Def.Tag == "AdvMapTownShared" && o.Def.TownType == "TOWN_ACADEMY") continue;
            if (o.Def.Tag == "AdvMapDwellingShared" && o.Def.Type is "BUILDING_WORKSHOP" or "BUILDING_STONE_PARAPET" or "BUILDING_GOLEM_FORGE" or "BUILDING_ACADEMY_MILITARY_POST") continue;
            int q = ExeQuarter(o.Rot);
            var tiles = LevelTiles(o.Def).Select(t => Rotate(t, q)).ToList();
            foreach (var (dx, dy) in tiles)
            {
                int x = o.X + dx, y = o.Y + dy;
                if (f.In(x, y)) mask[x * H + y] = 0;                            // the exe writes without a test
            }
            LevelFootprint(f, o.X, o.Y, tiles);
        }
    }

    /// <summary>
    /// exe 0x00913850 (pos, turned tiles, floor): the points pos + tile; per column x (hash map keyed by trunc x, 10 → 13
    /// buckets) the largest trunc y, adding (x, max + 1) per entry in map order; then per row y over all points the
    /// largest trunc x, adding (max + 1, y). The mean of the heights [y][x] of the points with 0 ≤ x ≤ W and
    /// 0 ≤ y ≤ H, divided by the count of all points (x87, from 0.0 at 0x00e0be18; every FADD and the FDIVP chopped to
    /// 24 bits, 0x00913f4c..0x00913fa7), is written to those points.
    /// </summary>
    static void LevelFootprint(RmgFloor f, int px, int py, List<(int X, int Y)> tiles)
    {
        if (tiles.Count == 0) return;
        var pts = LevelPoints(px, py, tiles);
        float sum = 0f;                                                     // FLD double 0.0, FADD float per point
        foreach (var (x, y) in pts)
            if (y >= 0 && y <= f.H && x >= 0 && x <= f.W) sum = X87.Add(sum, f.Heights[f.Lv(y, x)]);
        float v = X87.Div(sum, pts.Count);                                  // FILD count, FDIVP
        foreach (var (x, y) in pts)
            if (y >= 0 && y <= f.H && x >= 0 && x <= f.W) f.Heights[f.Lv(y, x)] = v;
    }

    /// <summary>The footprint 0x00917e10 levels: blockedTiles and the first activeTiles entry (see <see cref="LevelUnderObjects"/>).</summary>
    public static IEnumerable<(int X, int Y)> LevelTiles(H5.Adventure.Map.SharedDef d) => d.Blocked.Concat(d.Active.Take(1));

    /// <summary>The vertex points of 0x00913850 for an object at (px, py) with its turned tiles (see <see cref="LevelFootprint"/>).</summary>
    public static List<(int X, int Y)> LevelPoints(int px, int py, IEnumerable<(int X, int Y)> tiles)
    {
        var pts = tiles.Select(t => (X: px + t.X, Y: py + t.Y)).ToList();
        var byX = new NivalHashMap<int>(10);
        foreach (var (x, y) in pts)
        {
            if (byX.TryGetValue(x, out var m)) { if (m < y) byX[x] = y; }
            else byX[x] = y;
        }
        foreach (var (x, m) in byX.Items().ToList()) pts.Add((x, m + 1));
        var byY = new NivalHashMap<int>(10);
        foreach (var (x, y) in pts)
        {
            if (byY.TryGetValue(y, out var m)) { if (m < x) byY[y] = x; }
            else byY[y] = x;
        }
        foreach (var (y, m) in byY.Items().ToList()) pts.Add((m + 1, y));
        return pts;
    }

    static void AddHeight(RmgFloor f, int row, int col, float d)
    {
        if (row <= f.H && col <= f.W) f.Heights[f.Lv(row, col)] = X87.Add(d, f.Heights[f.Lv(row, col)]);   // 0x009127b0
    }

    /// <summary>
    /// exe 0x00916290: for every vertex (row o = y, column i = x, 0..size): b = border distance of tile (min(i, size − 1),
    /// min(o, size − 1)) x 1/3, negated in Necropolis / Inferno zones (zone race 7 or 8); v = cos(i·1/13)·sin(i·1/29)·
    /// sin(o·0.1)·sin(o·1/42)·6.6666665 + b + 12, the vertex rises by min(v, 3); the four vertices of a tile with road
    /// flags (8 | 0x10) sink by 1. Every FMUL / FADD chopped to 24 bits, FSIN / FCOS at full precision (row "x87 control
    /// word"; disassembly 0x009162e7..0x00916412).
    /// </summary>
    void Relief(RmgFloor f, int n)
    {
        for (int o = 0; o <= n; o++)
        {
            float s1 = X87.Chop(X87.Sin(X87.Mul(o, 0.023809524f)));           // 0x00e57d44, FSTP float
            float s2 = X87.Chop(X87.Sin(X87.Mul(o, 0.1f)));                   // 0x00e12f10
            for (int i = 0; i <= n; i++)
            {
                int tx = Math.Min(i, n - 1), ty = Math.Min(o, n - 1);
                int t = f.At(tx, ty);
                float b = X87.Mul(f.BorderDist[t], 0.33333334f);               // 0x00e19950
                if (f.Zones.TryGetValue(f.Zone[t], out var z) && (z.Race == 8 || z.Race == 7)) b *= -1f;   // 0x00e12f14
                // FMUL ST1 / FSIN, FMUL / FCOS, FMULP, FMUL s2, FMUL s1, FMUL 0x00e57d38, FADD b, FADD 0x00e57d34
                double sa = X87.Sin(X87.Mul(0.03448276f, i)), cb = X87.Cos(X87.Mul(i, 0.07692308f));
                float v = X87.Mul(sa, cb);
                v = X87.Mul(v, s2);
                v = X87.Mul(v, s1);
                v = X87.Mul(v, 6.6666665f);
                v = X87.Add(v, b);
                v = X87.Add(v, 12f);
                float add = 3f <= v ? 3f : v;                                  // 0x00e13280
                AddHeight(f, o, i, add);
                if (o < n && i < n && (f.Flags[f.At(o, i)] & (TileFlags.MainRoad | TileFlags.MineRoad)) != 0)
                {
                    AddHeight(f, i, o, -1f); AddHeight(f, i, o + 1, -1f);
                    AddHeight(f, i + 1, o, -1f); AddHeight(f, i + 1, o + 1, -1f);
                }
            }
        }
    }

    /// <summary>
    /// exe 0x00912d10 (floor 0, mask): every inner vertex (row and column 1 .. size − 1) whose mask entry [row][column]
    /// (the tile mask read with the vertex indices) is set becomes centre·0.8 + Σ8·0.025 (flag 1) or centre·0.2 +
    /// Σ8·0.1 (flag 0), computed from the old grid: the eight FADDs in the exe's order, both FMULs and the FADDP chopped
    /// to 24 bits (0x00912d95..0x00912ddc).
    /// </summary>
    void Smooth(RmgFloor f, byte[] mask, bool soft)
    {
        var h = f.Heights;
        var old = (float[])h.Clone();
        int rows = H + 1, cols = W + 1;
        float kc = soft ? 0.8f : 0.2f, kn = soft ? 0.025f : 0.1f;            // 0x00e1857c / 0x00e57b04, 0x00e12ff8 / 0x00e12f10
        for (int r = 1; r < rows - 1; r++)
            for (int c = 1; c < cols - 1; c++)
            {
                if (mask[c * H + r] == 0) continue;
                float sum = X87.Add(old[f.Lv(r, c - 1)], old[f.Lv(r, c + 1)]);
                sum = X87.Add(sum, old[f.Lv(r + 1, c - 1)]);
                sum = X87.Add(sum, old[f.Lv(r + 1, c + 1)]);
                sum = X87.Add(sum, old[f.Lv(r - 1, c - 1)]);
                sum = X87.Add(sum, old[f.Lv(r - 1, c + 1)]);
                sum = X87.Add(sum, old[f.Lv(r + 1, c)]);
                sum = X87.Add(sum, old[f.Lv(r - 1, c)]);
                h[f.Lv(r, c)] = X87.Add(X87.Mul(sum, kn), X87.Mul(old[f.Lv(r, c)], kc));
            }
    }

    /// <summary>
    /// exe 0x00915f20: each 8-connected group of lake tiles (flag 0x80, scanned x outer from (0, 0), grown over 1 .. size − 2)
    /// gets 0x00912f90(−0.1): the lowest height of the group's tiles' vertex (x, y), minus 0.1, on all four vertices of
    /// every tile of the group.
    /// </summary>
    void LakeBeds(RmgFloor f)
    {
        var seen = new bool[W * H];
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
            {
                int i = f.At(x, y);
                if ((f.Flags[i] & TileFlags.Lake) == 0 || seen[i] || (x == 0 && y == 0)) continue;
                var group = new List<(int X, int Y)> { (x, y) };
                seen[i] = true;
                for (int k = 0; k < group.Count; k++)
                {
                    var (gx, gy) = group[k];
                    if (gx < 1 || gy < 1 || gx > W - 2 || gy > H - 2) continue;
                    foreach (var (dx, dy) in Around8)                              // 0x00f466d8
                    {
                        int nx = gx + dx, ny = gy + dy;
                        int j = f.At(nx, ny);
                        if ((f.Flags[j] & TileFlags.Lake) == 0 || seen[j]) continue;
                        seen[j] = true;
                        group.Add((nx, ny));
                    }
                }
                float min = 1000f;                                                 // 0x00e156d8
                foreach (var (gx, gy) in group) min = Math.Min(min, f.Heights[f.Lv(gy, gx)]);
                min = X87.Add(min, -0.1f);                                         // 0x00912f90: FADD of the argument
                foreach (var (gx, gy) in group)
                {
                    f.Heights[f.Lv(gy, gx)] = min; f.Heights[f.Lv(gy + 1, gx)] = min;
                    f.Heights[f.Lv(gy, gx + 1)] = min; f.Heights[f.Lv(gy + 1, gx + 1)] = min;
                }
            }
    }
}
