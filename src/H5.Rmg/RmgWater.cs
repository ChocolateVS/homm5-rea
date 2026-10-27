using H5.Adventure.Map;

namespace H5.Rmg;

/// <summary>
/// The water option's zone class NRMG::CGameWaterBorderedZone (constructor 0x00bae920, vtable 0x00e8280c). 0x0090bd80
/// creates it for every surface zone when the generator's water flag (+0xa6) is set; it keeps CGameZone's preset
/// (0x00918a50), town (0x00923e50), connections (0x0091beb0) and big statics (0x009253b0) and replaces vf 0x24 (the
/// water border, 0x00bae0f0), vf 0x2c (0x00bae0d0: teleports, then the shipyard), vf 0x30 (one-tile statics
/// 0x00bad6c0), vf 0x38 (finish 0x00bac6b0) and vf 0x44 (the statics' test 0x00bac810).
/// </summary>
public sealed partial class RmgGenerator
{
    /// <summary>
    /// exe 0x00904770 (0x0090e7e0 after "towns placed", water flag set): the width by map size (generator +0x20: 2, 3,
    /// 4, 5, 7, 8, 10; jump table 0x00904824, default 3), then vf 0x24 of every zone of floor 0 in the floor's zone map
    /// order (+0xa8). CGameZone's vf 0x24 (0x00918b20) does nothing; with the flag every surface zone is water-bordered.
    /// </summary>
    void WaterBorders(RmgResult r)
    {
        int w = SizeIndex switch { 0 => 2, 2 => 4, 3 => 5, 4 => 7, 5 => 8, 6 => 10, _ => 3 };
        foreach (var z in Floors[0].Zones.Values)
            if (z.Class == RmgZoneClass.WaterBordered) WaterBorder(r, z, w);
    }

    /// <summary>
    /// exe 0x00bae0f0 (vf 0x24, width w; nothing on floor 1). +0x160 = w. Per zone tile in list order: border distance
    /// below w → the tile joins the water list (stored as (y, x)) and leaves the zone map (−1); the border distance
    /// moves by 1 − w; −w &lt; distance &lt; 0 paints the tile's four vertices with Params DeepWaterBottom (+0x158) at 200,
    /// 0 ≤ distance &lt; 2 with the +0x20 preset's WaterCoastTile (+0x88; DeepWaterBottom when empty) at 200 (0x00913510).
    /// Then 0x00916790 (water list, zone id): DeepWaterTile (+0x150) at 200 and the depth grid; the zone tiles with a
    /// negative border distance move to +0x148, the others stay the zone's tiles; 0x00bad280 water treasures.
    /// TODO(exe): the vertex strengths of 0x00913510 are not modelled; the tile's one layer entry takes the last tile
    /// painted on it.
    /// </summary>
    void WaterBorder(RmgResult r, RmgZone z, int w)
    {
        if (z.Floor == 1) return;
        var f = Floors[z.Floor];
        var p = _db.Params;
        var race = _db.Race(z.Preset);
        z.WaterWidth = w;
        var water = new List<(int A, int B)>();          // (y, x), as 0x00bae0f0 pushes them
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.BorderDist[i] < w)
            {
                water.Add((y, x));
                f.Zone[i] = -1;
            }
            f.BorderDist[i] += 1 - w;
            int b = f.BorderDist[i];
            if (-w < b && b < 0) PaintWater(r, f, x, y, p.DeepWaterBottom);
            if (b > -1 && b < 2) PaintWater(r, f, x, y, race.WaterCoastTile.Length > 0 ? race.WaterCoastTile : p.DeepWaterBottom);
        }
        WaterDepth(r, f, water, p.DeepWaterTile);
        var land = new List<(int X, int Y)>();
        foreach (var (x, y) in z.Tiles)
        {
            if (f.BorderDist[f.At(x, y)] < 0) z.WaterTiles.Add((x, y));
            else land.Add((x, y));
        }
        z.Tiles = land;
        WaterTreasures(r, z, water);
    }

    /// <summary>The coarse stand-in for 0x00913510 on a tile's four vertices: the tile's layer entry.</summary>
    static void PaintWater(RmgResult r, RmgFloor f, int x, int y, string tile)
    {
        if (tile.Length > 0) r.TileLayers[f.Index][y * f.W + x] = tile;
    }

    /// <summary>
    /// exe 0x00916790 (builder +0x60; water list of (a, b) = (y, x), zone id): nothing when no zone has the id. Per entry:
    /// DeepWaterTile at 200 on the vertices (a, b), (a + 1, b), (a, b + 1), (a + 1, b + 1); the depth grid (floor
    /// +0x44, (2W + 1) x (2H + 1), rows 2a .. 2a + 3, columns 2b .. 2b + 3, within the grid) gets min(7 − border
    /// distance of tile (b, a), 5) x 0x50, or 0xff when that is not below 0xff. Then twice over the list (entry k % n)
    /// for 0 &lt; a, b ≤ W − 3: each of those 4 x 4 cells in order becomes (2·c + the cells 2 rows / columns away) / 6,
    /// then (2·c + the cells 1 away) / 6, in place.
    /// </summary>
    static void WaterDepth(RmgResult r, RmgFloor f, List<(int A, int B)> water, string tile)
    {
        int n = water.Count;
        int rows = 2 * f.W + 1, cols = 2 * f.H + 1;       // +0x4c, +0x50
        var d = f.Depth;
        foreach (var (a, b) in water)
        {
            PaintWater(r, f, b, a, tile);
            int bd = f.BorderDist[f.At(b, a)];
            int v = 7 - bd;
            byte c = Math.Min(v, 5) * 0x50 < 0xff ? (byte)(Math.Min(v, 5) * 0x50) : (byte)0xff;
            for (int k = 0; k < 4; k++)
                for (int j = 0; j < 4; j++)
                    if (2 * a + k < rows && 2 * b + j < cols) d[(2 * a + k) * cols + 2 * b + j] = c;
        }
        for (int k = 0; k < 2 * n; k++)
        {
            var (a, b) = water[k % n];
            if (a <= 0 || b <= 0 || a > f.W - 3 || b > f.W - 3) continue;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    int row = 2 * a + i, col = 2 * b + j;
                    if (row >= rows || col >= cols) continue;
                    int at = row * cols + col;
                    d[at] = (byte)((d[at - 2 * cols] + d[at + 2 * cols] + d[at] * 2 + d[at - 2] + d[at + 2]) / 6);
                }
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    int row = 2 * a + i, col = 2 * b + j;
                    if (row >= rows || col >= cols) continue;
                    int at = row * cols + col;
                    d[at] = (byte)((d[at - cols] + d[at + cols] + d[at - 1] + d[at + 1] + d[at] * 2) / 6);
                }
        }
    }

    /// <summary>
    /// exe 0x00bad280 (the water list of 0x00bae0f0): (zone tiles after the split) / 200 rounds; each collects the water
    /// tiles (x, y) with 1 ≤ x, y &lt; size − 1 not closer than 5 (0x00e2f798) to a water object of the zone (+0x154);
    /// none → next round; else Next(n) picks the tile, an empty Params WaterTreasures (+0x210) ends the function,
    /// else Next(#WaterTreasures) the treasure and Next(4) the quarter turn; 0x009145b0 creates it, the tile joins
    /// +0x154. No flag is written.
    /// </summary>
    void WaterTreasures(RmgResult r, RmgZone z, List<(int A, int B)> water)
    {
        var list = _db.Params.WaterTreasures;
        int rounds = z.Tiles.Count / 200;
        for (int round = 0; round < rounds; round++)
        {
            var cands = new List<(int X, int Y)>();
            foreach (var (a, b) in water)
            {
                if (b < 1 || b >= W - 1 || a < 1 || a >= H - 1) continue;
                bool near = false;
                foreach (var (px, py) in z.WaterObjects)
                {
                    float dx = px - b, dy = py - a;
                    if (MathF.Sqrt(dy * dy + dx * dx) < 5f) { near = true; break; }
                }
                if (!near) cands.Add((b, a));
            }
            if (cands.Count == 0) continue;
            var (cx, cy) = cands[Rng.Next(cands.Count)];
            if (list.Count == 0) return;
            var t = list[Rng.Next(list.Count)];
            float angle = QuarterAngle(Rng.Next(4));
            CreateObject(r, z, t, cx, cy, angle, "water treasure");
            z.WaterObjects.Add((cx, cy));
        }
    }

    /// <summary>
    /// exe 0x00bac810 (CGameWaterBorderedZone vf 0x44): every rotated blocked tile (0x00b0be60, coordinates rounded,
    /// 0x00d11a60) inside the map, with border distance ≥ 3, (flags &amp; 0x3e) == 0 and a distance field value ≥ 2.
    /// </summary>
    bool WaterStaticFits(RmgZone z, int px, int py, float angle, SharedDef d)
    {
        var f = Floors[z.Floor];
        int q = ExeQuarter(angle);
        foreach (var b in d.Blocked)
        {
            var (ox, oy) = Rotate(b, q);
            int x = px + ox, y = py + oy;
            if (!f.In(x, y)) return false;
            int i = f.At(x, y);
            if (f.BorderDist[i] < 3 || (f.Flags[i] & 0x3e) != 0 || f.Dist[i] < 2) return false;
        }
        return true;
    }

    /// <summary>
    /// exe 0x00bac6b0 (CGameWaterBorderedZone vf 0x38): 0x009191a0(0x3c), then every zone tile with distance &gt; 2 and
    /// border distance &gt; 1 clears its vertex (x, y) of chunk 15 (0x00915d20).
    /// </summary>
    void WaterZoneFinish(RmgZone z)
    {
        DistanceField(z, 0x3c);
        var f = Floors[z.Floor];
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Dist[i] > 2 && f.BorderDist[i] > 1) f.Chunk15[f.Lv(y, x)] = 0;
        }
    }
}
