namespace H5.Rmg;

/// <summary>Shipyards of the water option's zones (exe 0x00bac930) and the terrain water tests they use.</summary>
public sealed partial class RmgGenerator
{
    const string ShipyardShared = "MapObjects/Shipyard.(AdvMapShipyardShared).xdb";   // 0x010413fc (init 0x00dd78d0)

    /// <summary>exe 0x00dddc50 (table 0x010d9340): the 46 (ship dx, dy, shore dx, dy) offsets 0x00c59e20 tries in order.</summary>
    static readonly (int Sx, int Sy, int Lx, int Ly)[] ShipOffsets =
    {
        (0, -4, 0, -3), (-1, -4, -1, -3), (1, -4, 1, -3), (-2, -4, -2, -3), (2, -4, 2, -3), (0, 4, 0, 3), (-1, 4, -1, 3), (1, 4, 1, 3),
        (-2, 4, -2, 3), (2, 4, 2, 3), (-3, -3, -2, -3), (-3, -3, -3, -2), (3, -3, 2, -3), (3, -3, 3, -2), (-3, 3, -2, 3), (-3, 3, -3, 2),
        (3, 3, 2, 3), (3, 3, 3, 2), (-4, -2, -3, -2), (4, -2, 3, -2), (-4, -1, -3, -1), (4, -1, 3, -1), (-4, 0, -3, 0), (4, 0, 3, 0),
        (-4, 1, -3, 1), (4, 1, 3, 1), (-4, 2, -3, 2), (4, 2, 3, 2), (-3, 2, -2, 2), (-3, 2, -3, 1), (3, 2, 2, 2), (3, 2, 3, 1),
        (-3, -2, -2, -2), (-3, -2, -3, -1), (3, -2, 2, -2), (3, -2, 3, -1), (0, 3, 0, 2), (-1, 3, -1, 2), (1, 3, 1, 2), (-2, 3, -2, 2),
        (2, 3, 2, 2), (0, -3, 0, -2), (-1, -3, -1, -2), (1, -3, 1, -2), (-2, -3, -2, -2), (2, -3, 2, -2),
    };

    static readonly (int Dx, int Dy)[] ShipyardGuardSteps = { (0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1) };   // 0x00f76d00

    /// <summary>
    /// exe 0x00bac930 (CGameWaterBorderedZone vf 0x2c 0x00bae0d0 after the teleports, when +0x164 Shipyard is set): one
    /// round (size ≤ 100) or two. Every round appends to one candidate list that is never cleared (round 2 holds each
    /// tile twice): the zone's land tiles with border distance 2 or 3 and w + 3 &lt; x, y &lt; size − w − 3 (w = +0x160, the
    /// water width); then 0x009191a0(4), M = 0x00919710 over the list and the tiles with distance &gt; 4M/5 — none ends
    /// the routine. Until one fits: Next(n) tile, angle 0x00bac500, a ship tile (0x00c59e20) and 0x00919be0 (pos, angle,
    /// Shipyard shared); a failure erases the tile, an exhausted list ends the routine. 0x00915120 creates the shipyard
    /// (0x009145b0, then "ShipTile" = the ship offset), 0x0091a420 stamps it with the connection list +0xc0, the tiles
    /// with flags 0 in the 5 x 5 square around it get flags 1. Its guard: from k = chop(angle·4/π + 0.5), eight steps of
    /// 0x00f76d00[k &amp; 7] from the last connection point; the first tile whose flags are 0 or carry 0x39 gets 0x00ba7690
    /// (BasicLevelGuardPower x ConnectionGuardLevel x 20, angle − π/2) and joins +0x98; then the round ends.
    /// </summary>
    void Shipyards(RmgResult r, RmgZone z)
    {
        var f = Floors[z.Floor];
        int rounds = W > 100 ? 2 : 1;                                                   // 0x00bac958 (its "> 250 → 3" arm is unreachable)
        int w = z.WaterWidth;
        if (!_db.Xdb.FileSystem.Exists(ShipyardShared)) return;                         // the cast to SAdvMapShipyardShared fails
        var d = Catalog.Get(ShipyardShared);
        var cands = new List<(int X, int Y)>();
        for (int round = 0; round < rounds; round++)
        {
            foreach (var (x, y) in z.Tiles)
            {
                int b = f.BorderDist[f.At(x, y)];
                if (b > 1 && b < 4 && w + 3 < x && w + 3 < y && x < W - w - 3 && y < H - w - 3) cands.Add((x, y));
            }
            DistanceField(z, 4);
            int threshold = MaxDist(z, cands) * 4 / 5;
            var far = cands.Where(t => f.Dist[f.At(t.X, t.Y)] > threshold).ToList();
            if (far.Count == 0) return;
            RmgObject? o = null;
            float angle = 0;
            while (o == null)
            {
                if (far.Count == 0) return;
                int i = Rng.Next(far.Count);
                var (x, y) = far[i];
                angle = ShipyardFacing(z, x, y);
                if (ShipTileAt(f, x, y) is { } ship && FootprintFits(z, x, y, angle, d))
                {
                    o = CreateObject(r, z, ShipyardShared, x, y, angle, "shipyard");
                    o.ShipTile = ship;
                }
                else far.RemoveAt(i);
            }
            Stamp(z, o.X, o.Y, angle, d, z.ConnectionPoints);
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                {
                    int x = o.X + dx, y = o.Y + dy;                                      // no bounds test in the exe
                    if (f.In(x, y) && f.Flags[f.At(x, y)] == 0) f.Flags[f.At(x, y)] = 1;
                }
            if (z.ConnectionPoints.Count == 0) continue;
            var (vx, vy) = z.ConnectionPoints[^1];
            int k = (int)(angle * 1.2732395f + 0.5f);                                       // 0x00e57dd0, 0x00e0ab70
            for (int u = k; u < k + 8; u++)
            {
                var (sx, sy) = ShipyardGuardSteps[((u % 8) + 8) % 8];
                int gx = (int)(vx + sx), gy = (int)(vy + sy);
                if (!f.In(gx, gy)) continue;
                int fl = f.Flags[f.At(gx, gy)];
                if ((fl & 0x39) == 0 && fl != 0) continue;
                PlaceGuard(r, z, gx, gy, _db.Params.BasicLevelGuardPower * _db.Params.ConnectionGuardLevel * 20, "shipyard guard", angle - 1.5707964f);
                z.GuardPoints.Add((gx, gy));
                break;
            }
        }
    }

    /// <summary>
    /// exe 0x00bac500: the reference point is the town (+0x0c) once placed (+0xf8), else the mean of the zone's tiles
    /// (+0xcc); |dx| ≤ |dy| → π + π/2 (dy &lt; 0) or π/2, else 3π/2 + π/2 (dx &gt; 0) or π/2 + π/2. The exe's mean starts from
    /// two uninitialised stack floats (0x00bac524 / 0x00bac53e); ours from 0.
    /// </summary>
    float ShipyardFacing(RmgZone z, int x, int y)
    {
        float cx, cy;
        if (z.TownPlaced && z.TownPos is { } t) { cx = t.X; cy = t.Y; }
        else
        {
            float sx = 0, sy = 0;
            foreach (var p in z.Tiles) { sx += p.X; sy += p.Y; }
            float inv = 1f / z.Tiles.Count;                                              // 0x00e0a7e4 / n
            cx = sx * inv; cy = sy * inv;
        }
        float dx = x - cx, dy = y - cy;
        if (MathF.Abs(dx) <= MathF.Abs(dy)) return dy < 0f ? 3.1415927f + 1.5707964f : 1.5707964f;
        return dx > 0f ? 4.712389f + 1.5707964f : 1.5707964f + 1.5707964f;
    }

    /// <summary>
    /// exe 0x00c59e20 → 0x00c59d20 → 0x00c59c00 on the floor's terrain: the first offset whose ship tile is water
    /// (0x00c0f720) while its shore tile is neither water nor a cliff (0x00c0e440); null when none.
    /// </summary>
    static (int X, int Y)? ShipTileAt(RmgFloor f, int x, int y)
    {
        foreach (var (sx, sy, lx, ly) in ShipOffsets)
            if (TerrainWater(f, x + sx, y + sy) && !TerrainWater(f, x + lx, y + ly) && !TerrainCliff(f, x + lx, y + ly)) return (sx, sy);
        return null;
    }

    /// <summary>
    /// exe 0x00c0f720 / 0x00c0f400 on the floor's terrain (indices clamped to 0 .. size − 1): water when the four levels
    /// are 0; else not when a TT_BIG_WATER layer has weight at a corner (0x00c0f340); else when the depth grid at
    /// (2y + 1, 2x + 1) is above 0x8c. 0x00c0f720's further test of the float grid +0x58 never applies: the generator
    /// leaves that grid empty.
    /// </summary>
    static bool TerrainWater(RmgFloor f, int x, int y)
    {
        x = Math.Clamp(x, 0, f.W - 1); y = Math.Clamp(y, 0, f.H - 1);
        int a = f.Lv(y, x), b = f.Lv(y, x + 1), c = f.Lv(y + 1, x), e = f.Lv(y + 1, x + 1);
        if (f.Level[a] == 0 && f.Level[b] == 0 && f.Level[c] == 0 && f.Level[e] == 0) return true;
        foreach (var l in f.Layers)
            if (l.Type == 11 && (l.Weights[a] != 0 || l.Weights[b] != 0 || l.Weights[c] != 0 || l.Weights[e] != 0)) return false;
        return f.Depth[(2 * y + 1) * (2 * f.W + 1) + 2 * x + 1] > 0x8c;
    }

    /// <summary>
    /// exe 0x00c0e440 with terrain +0x64 clear: a cliff unless the top and bottom vertex pairs are each equal and differ
    /// by less than 16, or the left and right pairs likewise.
    /// </summary>
    static bool TerrainCliff(RmgFloor f, int x, int y)
    {
        x = Math.Clamp(x, 0, f.W - 1); y = Math.Clamp(y, 0, f.H - 1);
        int b1 = f.Level[f.Lv(y, x)], b2 = f.Level[f.Lv(y, x + 1)], b4 = f.Level[f.Lv(y + 1, x)], b3 = f.Level[f.Lv(y + 1, x + 1)];
        if (b1 == b2 && b4 == b3 && Math.Abs(b1 - b4) < 16 && Math.Abs(b2 - b3) < 16) return false;
        if (b1 == b4 && b2 == b3 && Math.Abs(b1 - b2) < 16 && Math.Abs(b3 - b4) < 16) return false;
        return true;
    }
}
