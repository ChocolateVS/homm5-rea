using H5.Core.Data;

namespace H5.Combat;

/// <summary>An obstacle placed on the battlefield, for the client to render the original model.</summary>
public sealed class ObstaclePlacement
{
    /// <summary>Pak path of the ArenaObstacle xdb.</summary>
    public string Obstacle = "";
    public string? Model;
    /// <summary>Anchor cell in playable coordinates (tile (0,0) of the rotated footprint).</summary>
    public Cell Anchor;
    /// <summary>Quarter turns counter-clockwise (0..3) applied to the footprint and model.</summary>
    public int Rotation;
    /// <summary>Playable cells this obstacle blocks (cells outside the 12x10 field are omitted).</summary>
    public List<Cell> Cells = new();
}

/// <summary>
/// Port of the original combat obstacle generator (exe 004b3860 and helpers 004b33a0, 004b3020,
/// 004b3650, 004b34a0, 004b27b0, 004b2850). It works on the engine's internal grid, which is the
/// playable 12x10 field with a 2-cell margin left/right and 1 row top/bottom (internal = playable + (2,1)).
///
/// * Free lines: n = min(FreeLines, H−4) consecutive internal rows starting at rand(1, H−n−1) stay clear.
/// * Mode = rand mod 3: small (≤ 2 tiles), big (> 2 tiles) or mixed obstacles; count = rand(Min, Max) for
///   that mode, clamped to [0,100]. ObstaclesMaxArea is never read: the area limit is (W−4)(H−2)/4.
/// * Each obstacle: a random candidate whose area still fits (others are dropped); a random rotation
///   rand(0,4) quarter turns (4 = full turn, so "no rotation" is twice as likely); every anchor in
///   x ∈ [4, W−5], y ∈ [1, H−2] where all tiles are inside that zone, off free rows and unoccupied;
///   one of them at random. The area counts even if no anchor fits.
/// * Validation: a 2x2 creature starting at internal (3,2) must be able to cover every free zone cell;
///   up to 10 attempts, after which the field stays empty.
/// * Fixed groups (boat/abyss arenas) block their listed internal cells directly.
/// The RNG's integer range is inclusive, as in the original (exe 008c04c0).
/// </summary>
public static class ObstacleGenerator
{
    const int Margin = 2, MarginY = 1;

    public static List<ObstaclePlacement> Generate(GameDatabase db, Rng rng, ArenaObstacleGroupDef? group, int width, int height)
    {
        var result = new List<ObstaclePlacement>();
        if (group == null || group.Obstacles.Count == 0) return result;
        int W = width + 2 * Margin, H = height + 2 * MarginY;

        if (group.FixedObstacles)
        {
            foreach (var o in group.Obstacles)
                result.Add(MakePlacement(o, 0, 0, 0, o.Tiles, width, height));
            return result;
        }

        // ---- parameters (004b33a0 / 004b3020)
        int maxArea = (W - 4) * (H - 2) / 4;
        var freeRows = new bool[H];
        int freeLines = Math.Min((int)db.Stats.Get("combat/Obstacles/FreeLines", 2), H - 4);
        if (freeLines > 0)
        {
            int start = RandInclusive(rng, 1, H - freeLines - 1);
            for (int i = 0; i < freeLines; i++) freeRows[start + i] = true;
        }
        int mode = rng.Next(3);
        var (minKey, maxKey) = mode switch
        {
            0 => ("MinSmallObstacles", "MaxSmallObstacles"),
            1 => ("MinBigObstacles", "MaxBigObstacles"),
            _ => ("MinMixedObstacles", "MaxMixedObstacles"),
        };
        int count = RollCount(db, rng, minKey, maxKey);
        var candidates = group.Obstacles.Where(o => mode == 2 || (mode == 0) == (o.Tiles.Count <= 2)).ToList();
        if (candidates.Count == 0)
        {
            candidates = group.Obstacles.ToList();
            RollCount(db, rng, "MinSmallObstacles", "MaxSmallObstacles");   // re-rolled but unused by the original
        }

        // ---- placement attempts (004b3860)
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var occupied = new bool[W, H];
            var placed = new List<ObstaclePlacement>();
            int area = 0;
            for (int n = 0; n < count; n++)
            {
                var pool = candidates.ToList();
                ArenaObstacleDef? pick = null;
                while (pool.Count > 0)
                {
                    int idx = pool.Count > 1 ? rng.Next(pool.Count) : 0;
                    if (area + pool[idx].Tiles.Count <= maxArea) { pick = pool[idx]; break; }
                    pool.RemoveAt(idx);
                }
                if (pick == null) break;
                area += pick.Tiles.Count;
                int rot = RandInclusive(rng, 0, 4) & 3;
                var tiles = pick.Tiles.Select(t => Rotate(t, rot)).ToList();
                var anchors = new List<(int X, int Y)>();
                for (int x = 4; x <= W - 5; x++)
                    for (int y = 1; y <= H - 2; y++)
                        if (Fits(tiles, x, y, W, H, freeRows, occupied)) anchors.Add((x, y));
                if (anchors.Count == 0) continue;
                var (ax, ay) = anchors[RandInclusive(rng, 0, anchors.Count - 1)];
                foreach (var (tx, ty) in tiles) occupied[ax + tx, ay + ty] = true;
                placed.Add(MakePlacement(pick, ax, ay, rot, tiles, width, height));
            }
            if (Valid(occupied, W, H)) return placed;
        }
        return result;
    }

    static int RollCount(GameDatabase db, Rng rng, string minKey, string maxKey)
    {
        int min = Math.Clamp((int)db.Stats.Get("combat/Obstacles/" + minKey, 0), 0, 100);
        int max = Math.Min((int)db.Stats.Get("combat/Obstacles/" + maxKey, 0), 100);
        if (max < min) max = min;
        return RandInclusive(rng, min, max);
    }

    /// <summary>The original's inclusive integer range: min + r mod (max − min + 1).</summary>
    public static int RandInclusive(Rng rng, int min, int max) => min + rng.Next(max - min + 1);

    /// <summary>Counter-clockwise quarter turns, rounding like the original's float rotation.</summary>
    static (int X, int Y) Rotate((int X, int Y) t, int k) => k switch
    {
        1 => (-t.Y, t.X),
        2 => (-t.X, -t.Y),
        3 => (t.Y, -t.X),
        _ => t,
    };

    static bool Fits(List<(int X, int Y)> tiles, int ax, int ay, int W, int H, bool[] freeRows, bool[,] occupied)
    {
        foreach (var (tx, ty) in tiles)
        {
            int x = ax + tx, y = ay + ty;
            if (y < 1 || y > H - 2 || freeRows[y]) return false;
            if (x < 4 || x > W - 5 || occupied[x, y]) return false;
        }
        return true;
    }

    /// <summary>004b2850: every free cell of the obstacle zone must be coverable by a reachable 2x2 creature.</summary>
    static bool Valid(bool[,] occupied, int W, int H)
    {
        bool AnchorFree(int x, int y) =>
            x >= 0 && y >= 1 && x + 1 < W && y < H &&
            !occupied[x, y] && !occupied[x + 1, y] && !occupied[x, y - 1] && !occupied[x + 1, y - 1];

        var covered = new bool[W, H];
        var seen = new bool[W, H];
        var queue = new Queue<(int, int)>();
        if (AnchorFree(3, 2)) { queue.Enqueue((3, 2)); seen[3, 2] = true; }
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            covered[x, y] = covered[x + 1, y] = covered[x, y - 1] = covered[x + 1, y - 1] = true;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= W || ny >= H || seen[nx, ny] || !AnchorFree(nx, ny)) continue;
                    seen[nx, ny] = true;
                    queue.Enqueue((nx, ny));
                }
        }
        for (int x = 4; x <= W - 5; x++)
            for (int y = 1; y <= H - 2; y++)
                if (!occupied[x, y] && !covered[x, y]) return false;
        return true;
    }

    static ObstaclePlacement MakePlacement(ArenaObstacleDef o, int ax, int ay, int rot, List<(int X, int Y)> tiles, int width, int height)
    {
        var p = new ObstaclePlacement
        {
            Obstacle = o.Path, Model = o.ModelPath, Rotation = rot,
            Anchor = new Cell(ax - Margin, ay - MarginY),
        };
        foreach (var (tx, ty) in tiles)
        {
            var c = new Cell(ax + tx - Margin, ay + ty - MarginY);
            if (c.X >= 0 && c.Y >= 0 && c.X < width && c.Y < height) p.Cells.Add(c);
        }
        return p;
    }
}
