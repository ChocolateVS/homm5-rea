using H5.Assets.Terrain;

namespace H5.Adventure.Map;

/// <summary>
/// The kind the exe stores at +8 of each tile of the world's passability map (CWorld +0xd0, tiles of 0x24 bytes,
/// default 1 from H5_Game.exe 00bcc5d0). The pathfinder's passability test 009d1410 refuses 3 and 5.
/// </summary>
public enum TileKind : byte
{
    None = 0,
    /// <summary>Walkable ground.</summary>
    Land = 1,
    /// <summary>Sea (the ground type becomes TT_BIG_WATER).</summary>
    Water = 2,
    /// <summary>Terrain that nobody may enter: cliffs, texture lakes, chunk 15 holes.</summary>
    Impassable = 3,
    /// <summary>A passable tile of an object's passableTiles list (bridges …): walkable, road type gravel.</summary>
    ObjectPassable = 4,
    /// <summary>The map's BorderSize frame and, underground, the raised level (&gt; 0x1f).</summary>
    Edge = 5,
}

/// <summary>
/// Tile passability from the terrain, as H5_Game.exe builds it when the world is set up: CWorld vf (00997530) creates
/// the passability map (00bcc6e0, one grid per floor) and fills every floor with 00990800. Per tile (x, y):
/// <list type="number">
/// <item>x or y closer than the floor's border (CWorld vf 9 = 00999a60: map.xdb BorderSize, read by 00a0ed80 and
/// clamped to 1..20, then to 1..min(TilesX, TilesY)) to the map edge → <see cref="TileKind.Edge"/>;</item>
/// <item>a tile listed by an object's passable tiles (floor list +0x6c, object vf 0xbc, offsets from the object
/// position vf 0xa4) → <see cref="TileKind.ObjectPassable"/>, ground type 00c0f040, road type 7;</item>
/// <item>otherwise ground type 00c0f070, then: 00c0e3f0 (underground and the vertex level (x, y) &gt; 0x1f) →
/// Edge; 00c0f340 (a TT_BIG_WATER layer with weight at a corner) or 00c0e420 (chunk 15 at vertex (x, y) is 0) →
/// Impassable; 00c0f400 (the four levels 0, or the half-tile water mask at the centre &gt; 0x8c) → Water;
/// 00c0e440 (cliff) → Impassable; else Land.</item>
/// </list>
/// The level grid is chunk 7, chunk 15 the per-vertex "open" mask and chunk 10 the half-tile water mask (serializer
/// 004faaf0). STerrainInfo +0x64, which switches the cliff rule to "all four levels equal", is saved as chunk 14 but
/// the map load overwrites it with "this is the underground floor" (00c0faa0, from 0041e600 with the floor index), so
/// the byte in the file (9, 0xf5 … on stock maps) is ignored. The load also supplies missing grids (levels 0x10, or
/// 0x20 underground; chunk 15 all 1) and clears a half-tile mask of the wrong size (00c0ea30).
/// </summary>
public static class TerrainPassability
{
    /// <summary>The ESpecialTerrainType ids of the AdvMapTile Type field (+0x60; enum strings 00aab350).</summary>
    public static readonly string[] TypeNames =
    {
        "TT_DIRT", "TT_GRASS", "TT_SAND", "TT_SNOW", "TT_LAVA", "TT_SUBTERRANEAN", "TT_DIRT_ROAD", "TT_GRAVEL_ROAD",
        "TT_COBBLESTONE_ROAD", "TT_NONE", "TT_SMALL_WATER", "TT_BIG_WATER", "TT_DWARVENMINES", "TT_TAIGA", "TT_WASTELAND",
    };

    public const int TypeGravelRoad = 7, TypeNone = 9, TypeSmallWater = 10, TypeBigWater = 11;

    /// <summary>Type id of a tile type name; −1 for an unresolved descriptor (00c10900 skips a layer without one).</summary>
    public static int TypeId(string? name) => name == null ? -1 : Array.IndexOf(TypeNames, name);

    /// <summary>
    /// Fills <see cref="MapFloor.Kind"/> and <see cref="MapFloor.Ground"/> (exe 00990800). <paramref name="layerTypes"/>
    /// are the type ids of the terrain's layers; <paramref name="objectPassable"/> the absolute passable tiles of the
    /// floor's objects.
    /// </summary>
    public static void Build(MapFloor f, int borderSize, IReadOnlyList<int> layerTypes, IReadOnlySet<(int X, int Y)> objectPassable)
    {
        var t = f.Data ?? throw new InvalidOperationException("floor without terrain data");
        int w = t.TilesX, h = t.TilesY;
        f.Kind = new TileKind[f.Width * f.Height];
        f.Ground = new byte[f.Width * f.Height];
        int b = Math.Max(1, Math.Min(Math.Clamp(borderSize, 1, 20), Math.Min(w, h)));     // 00a0ed80 / 00999a60
        bool ug = f.Index != 0;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (x >= f.Width || y >= f.Height) continue;
                int i = y * f.Width + x;
                f.Ground[i] = 1;                                                           // 00bcc5d0 default
                if (x < b || y < b || w - b <= x || h - b <= y) { f.Kind[i] = TileKind.Edge; continue; }
                if (objectPassable.Contains((x, y)))
                {
                    f.Ground[i] = (byte)DominantType(t, layerTypes, x, y, excludeWater: true);   // 00c0f040 → 00c107d0
                    f.Kind[i] = TileKind.ObjectPassable;
                    continue;
                }
                f.Ground[i] = (byte)DominantType(t, layerTypes, x, y, excludeWater: false);      // 00c0f070 → 00c10900
                if (RaisedFlagged(t, ug, x, y)) f.Kind[i] = TileKind.Edge;                            // 00c0e3f0
                else if (BigWaterLayer(t, layerTypes, x, y) || Closed(t, x, y)) f.Kind[i] = TileKind.Impassable;
                else if (IsWater(t, ug, layerTypes, x, y)) { f.Kind[i] = TileKind.Water; f.Ground[i] = TypeBigWater; }
                else f.Kind[i] = Cliff(t, ug, x, y) ? TileKind.Impassable : TileKind.Land;
            }
    }

    /// <summary>
    /// The exe's ground type of a tile (00c10900 / 00c107d0): layers from the top, each layer's weight w at the tile
    /// centre (<see cref="CentreWeight"/>; the base layer is 0xff everywhere, the load 00c0e5b0 fills it) counts w·r where
    /// r (from 1) shrinks by (1 − w/255); the layer that counts most
    /// wins. Roads (6..8) and TT_SMALL_WATER never count; with <paramref name="excludeWater"/> TT_BIG_WATER neither.
    /// None found → TT_NONE (9).
    /// </summary>
    public static int DominantType(TerrainData t, IReadOnlyList<int> layerTypes, int x, int y, bool excludeWater)
    {
        if (x < 0 || x >= t.TilesX || y < 0 || y >= t.TilesY) return TypeNone;
        float best = 0f, rem = 1f;
        int type = TypeNone;
        for (int li = t.Layers.Count - 1; li >= 0; li--)
        {
            int ty = li < layerTypes.Count ? layerTypes[li] : -1;
            if (ty < 0 || (ty >= 6 && ty <= 8) || ty == TypeSmallWater || (excludeWater && ty == TypeBigWater)) continue;
            byte wgt = li == 0 ? (byte)0xff : CentreWeight(t.Layers[li].Weights, x, y);
            float vis = wgt * rem;
            rem = (1f - wgt * (1f / 255f)) * rem;
            if (best < vis) { best = vis; type = ty; }
        }
        return type;
    }

    /// <summary>
    /// 00c0ff70 at (x + 0.5, y + 0.5): each corner weight v becomes 2v (v &lt; 128) or 255, the row pairs are averaged
    /// and the two rows averaged, each step truncated (_ftol 00d11a60).
    /// </summary>
    public static byte CentreWeight(Grid<byte> g, int x, int y)
    {
        int x0 = Math.Clamp(x, 0, g.Width - 1), y0 = Math.Clamp(y, 0, g.Height - 1);
        int x1 = Math.Clamp(x + 1, 0, g.Width - 1), y1 = Math.Clamp(y + 1, 0, g.Height - 1);
        static int D(byte v) => v < 0x80 ? 2 * v : 0xff;
        int a = D(g[x0, y0]), bb = D(g[x1, y0]), c = D(g[x0, y1]), d = D(g[x1, y1]);
        int top = (a + bb) / 2, bottom = (c + d) / 2;
        return (byte)((top + bottom) / 2);
    }

    /// <summary>Vertex level (chunk 7); a map without the grid gets 0x10, underground 0x20 (00c0faa0).</summary>
    static int Level(TerrainData t, bool ug, int x, int y) =>
        t.VertexFlags is { } l ? l[Math.Clamp(x, 0, l.Width - 1), Math.Clamp(y, 0, l.Height - 1)] : ug ? 0x20 : 0x10;

    /// <summary>00c0e3f0: underground, the vertex (x, y) level above 0x1f.</summary>
    public static bool RaisedFlagged(TerrainData t, bool ug, int x, int y) => ug && Level(t, ug, x, y) > 0x1f;

    /// <summary>00c0e420: chunk 15 at vertex (x, y) is 0.</summary>
    public static bool Closed(TerrainData t, int x, int y) =>
        t.VertexMap15 is { } g && g[Math.Clamp(x, 0, g.Width - 1), Math.Clamp(y, 0, g.Height - 1)] == 0;

    /// <summary>00c0f340: a TT_BIG_WATER layer with a non-zero weight at one of the tile's corners.</summary>
    public static bool BigWaterLayer(TerrainData t, IReadOnlyList<int> layerTypes, int x, int y)
    {
        for (int li = 0; li < t.Layers.Count; li++)
        {
            if ((li < layerTypes.Count ? layerTypes[li] : -1) != TypeBigWater) continue;
            if (li == 0) return true;                                   // the base layer is all 0xff after the load
            var g = t.Layers[li].Weights;
            int x1 = Math.Min(x + 1, g.Width - 1), y1 = Math.Min(y + 1, g.Height - 1);
            if (g[x, y] != 0 || g[x1, y] != 0 || g[x1, y1] != 0 || g[x, y1] != 0) return true;
        }
        return false;
    }

    /// <summary>00c0f400: the tile's four levels are 0 (x, y clamped to the last tile), else — unless a TT_BIG_WATER
    /// layer touches it — the half-tile water mask (chunk 10) at the centre above 0x8c.</summary>
    public static bool IsWater(TerrainData t, bool ug, IReadOnlyList<int> layerTypes, int x, int y)
    {
        int cx = Math.Max(0, Math.Min(x, t.TilesX - 1)), cy = Math.Max(0, Math.Min(y, t.TilesY - 1));
        if (Level(t, ug, cx, cy) == 0 && Level(t, ug, cx + 1, cy) == 0 && Level(t, ug, cx, cy + 1) == 0 && Level(t, ug, cx + 1, cy + 1) == 0)
            return true;
        if (BigWaterLayer(t, layerTypes, cx, cy)) return false;
        // 00c0ea30 clears a mask that is not (2·TilesX + 1) × (2·TilesY + 1).
        return t.HalfTileMap is { } hm && hm.Width == 2 * t.TilesX + 1 && hm.Height == 2 * t.TilesY + 1
            && hm[2 * cx + 1, 2 * cy + 1] > 0x8c;
    }

    /// <summary>
    /// 00c0e440 (x, y clamped to the last tile): on the surface the tile is open when its levels form a flat or a
    /// one-axis ramp — top pair equal and bottom pair equal with both columns differing by less than 0x10, or left
    /// pair equal and right pair equal with both rows differing by less than 0x10; underground only when all
    /// four levels are equal.
    /// </summary>
    public static bool Cliff(TerrainData t, bool ug, int x, int y)
    {
        int cx = Math.Max(0, Math.Min(x, t.TilesX - 1)), cy = Math.Max(0, Math.Min(y, t.TilesY - 1));
        int b1 = Level(t, ug, cx, cy), b2 = Level(t, ug, cx + 1, cy), b4 = Level(t, ug, cx, cy + 1), b3 = Level(t, ug, cx + 1, cy + 1);
        if (ug) return !(b1 == b2 && b2 == b3 && b3 == b4);
        if (b1 == b2 && b4 == b3 && Math.Abs(b1 - b4) < 0x10 && Math.Abs(b2 - b3) < 0x10) return false;
        if (b1 == b4 && b2 == b3 && Math.Abs(b1 - b2) < 0x10 && Math.Abs(b3 - b4) < 0x10) return false;
        return true;
    }
}
