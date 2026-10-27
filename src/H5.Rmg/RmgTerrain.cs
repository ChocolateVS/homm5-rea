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
        SurfaceHeights();
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
    /// 0x00912d10 (0).
    /// TODO(exe): 0x00917a30 (vertices within 8 of a town whose type +0xfc is 8 set to their mean − 1, within 2.5 of
    /// dwellings 0x48..0x4b to their mean − 2.5) and 0x00917e10 (0x00913850 levels the vertices under every
    /// non-static object, both floors) are not modelled.
    /// </summary>
    void SurfaceHeights()
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
        Smooth(f, mask, true);
        Array.Fill(mask, (byte)1);
        Smooth(f, mask, true);
        LakeBeds(f);
        Smooth(f, mask, false);
    }

    static void AddHeight(RmgFloor f, int row, int col, float d)
    {
        if (row <= f.H && col <= f.W) f.Heights[f.Lv(row, col)] += d;
    }

    /// <summary>
    /// exe 0x00916290: for every vertex (row o = y, column i = x, 0..size): b = border distance of tile (min(i, size − 1),
    /// min(o, size − 1)) x 1/3, negated in Necropolis / Inferno zones (zone race 7 or 8); v = cos(i·1/13)·sin(i·1/29)·
    /// sin(o·0.1)·sin(o·1/42)·6.6666665 + b + 12, the vertex rises by min(v, 3); the four vertices of a tile with road
    /// flags (8 | 0x10) sink by 1.
    /// </summary>
    void Relief(RmgFloor f, int n)
    {
        for (int o = 0; o <= n; o++)
        {
            float s1 = (float)Math.Sin(o * (double)0.023809524f);              // 0x00e57d44
            float s2 = (float)Math.Sin(o * (double)0.1f);                      // 0x00e12f10
            for (int i = 0; i <= n; i++)
            {
                int tx = Math.Min(i, n - 1), ty = Math.Min(o, n - 1);
                int t = f.At(tx, ty);
                float b = f.BorderDist[t] * 0.33333334f;                       // 0x00e19950
                if (f.Zones.TryGetValue(f.Zone[t], out var z) && (z.Race == 8 || z.Race == 7)) b *= -1f;   // 0x00e12f14
                double v = Math.Cos(i * (double)0.07692308f) * Math.Sin(0.03448276f * (double)i) * s2 * s1 * 6.6666665f + b + 12.0;
                float add = 3.0 <= v ? 3f : (float)v;                          // 0x00e13280
                f.Heights[f.Lv(o, i)] += add;
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
    /// Σ8·0.1 (flag 0), computed from the old grid.
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
                if (mask[r * H + c] == 0) continue;
                float sum = old[f.Lv(r, c - 1)] + old[f.Lv(r, c + 1)] + old[f.Lv(r + 1, c - 1)] + old[f.Lv(r + 1, c + 1)]
                          + old[f.Lv(r - 1, c - 1)] + old[f.Lv(r - 1, c + 1)] + old[f.Lv(r + 1, c)] + old[f.Lv(r - 1, c)];
                h[f.Lv(r, c)] = old[f.Lv(r, c)] * kc + sum * kn;
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
                min += -0.1f;
                foreach (var (gx, gy) in group)
                {
                    f.Heights[f.Lv(gy, gx)] = min; f.Heights[f.Lv(gy + 1, gx)] = min;
                    f.Heights[f.Lv(gy, gx + 1)] = min; f.Heights[f.Lv(gy + 1, gx + 1)] = min;
                }
            }
    }
}
