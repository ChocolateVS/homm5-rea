namespace H5.Adventure.Map;

/// <summary>
/// Navigable sea tiles. The texture layers under the sea are ordinary ground textures (grass,
/// sand, TT_SMALL_WATER…), so water comes from the terrain's level data instead:
/// <list type="bullet">
/// <item>chunk 7 holds each vertex's plateau level (0 = sea level, 16/32/64 = levels 1–3);
/// a tile whose four vertices are all at level 0 is sea;</item>
/// <item>maps whose sea sits on a raised level mark it in the half-tile water mask (chunk 10,
/// graded 0–255); the tile centre at 200 or more is sea.</item>
/// </list>
/// Scored against every object entrance on all 75 maps (<c>h5tool adv-water-rule</c>): 686/689
/// water-based objects classify as sea and 33524/33546 land objects as land (13 of the misses are
/// objects parked at (0,0)). TODO(exe): the passability builder itself was not traced.
/// </summary>
public static class WaterMask
{
    public const int HalfTileThreshold = 200;

    public static void Apply(MapFloor f)
    {
        var d = f.Data;
        if (d?.VertexFlags is not { } levels) return;
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
                if (IsSea(d, levels, x, y)) f.Terrain[y * f.Width + x] = TerrainClass.Water;
    }

    static bool IsSea(H5.Assets.Terrain.TerrainData d, H5.Assets.Terrain.Grid<byte> levels, int x, int y)
    {
        bool allZero = true;
        for (int dy = 0; dy <= 1 && allZero; dy++)
            for (int dx = 0; dx <= 1 && allZero; dx++)
                if (levels[Math.Min(x + dx, levels.Width - 1), Math.Min(y + dy, levels.Height - 1)] != 0) allZero = false;
        if (allZero) return true;
        return d.HalfTileMap is { } h && 2 * x + 1 < h.Width && 2 * y + 1 < h.Height && h[2 * x + 1, 2 * y + 1] >= HalfTileThreshold;
    }
}
