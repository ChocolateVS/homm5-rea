using H5.Adventure.Map;
using H5.Assets.Terrain;

namespace H5.Adventure.Tests;

/// <summary>Tile passability from the terrain as H5_Game.exe 00990800 builds it (see <see cref="TerrainPassability"/>).</summary>
[Collection("adv")]
public class TerrainPassabilityTests(Fixture f)
{
    /// <summary>A flat 6×6 floor at level 0x10: one grass base layer, chunk 15 all open.</summary>
    static MapFloor Flat(int index = 0, byte level = 0x10)
    {
        const int n = 6;
        var t = new TerrainData
        {
            TilesX = n, TilesY = n,
            Heights = new Grid<float>(n + 1, n + 1, new float[(n + 1) * (n + 1)]),
            VertexFlags = new Grid<byte>(n + 1, n + 1, Enumerable.Repeat(level, (n + 1) * (n + 1)).ToArray()),
            VertexMap15 = new Grid<byte>(n + 1, n + 1, Enumerable.Repeat((byte)1, (n + 1) * (n + 1)).ToArray()),
        };
        t.Layers.Add(new TerrainLayer { TilePath = "grass", Weights = new Grid<byte>(n + 1, n + 1, new byte[(n + 1) * (n + 1)]) });
        return new MapFloor
        {
            Index = index, Width = n, Height = n, Data = t, LayerTypes = new[] { 1 },
            Terrain = new TerrainClass[n * n], TileType = new string[n * n], Blocked = new bool[n * n],
        };
    }

    static void SetLevel(MapFloor m, int x, int y, byte v) => m.Data!.VertexFlags!.Data[y * (m.Width + 1) + x] = v;

    static TileKind Build(MapFloor m, int x, int y, int border = 1)
    {
        TerrainPassability.Build(m, border, m.LayerTypes, new HashSet<(int, int)>());
        return m.Kind[y * m.Width + x];
    }

    [Fact]
    public void TheBorderSizeFrameIsRefused()
    {
        // 00990800: x or y < border, or ≥ size − border → kind 5; border = clamp(BorderSize, 1, 20) (00a0ed80), ≤ the size (00999a60).
        var m = Flat();
        TerrainPassability.Build(m, 2, m.LayerTypes, new HashSet<(int, int)>());
        Assert.Equal(TileKind.Edge, m.Kind[1 * 6 + 3]);
        Assert.Equal(TileKind.Edge, m.Kind[3 * 6 + 4]);
        Assert.Equal(TileKind.Land, m.Kind[2 * 6 + 3]);
        Assert.True(m.TerrainBlocked(3, 1));
        Build(m, 2, 2, border: 0);                       // clamped up to 1
        Assert.Equal(TileKind.Land, m.Kind[1 * 6 + 1]);
        Assert.Equal(TileKind.Edge, m.Kind[0 * 6 + 1]);
    }

    [Fact]
    public void SurfaceCliffsBlockAndOneAxisRampsUnderSixteenPass()
    {
        // 00c0e440, floor 0 (STerrainInfo +0x64 clear).
        var m = Flat();
        SetLevel(m, 3, 2, 0x20); SetLevel(m, 3, 3, 0x20);           // right column of tile (2,2) one level up
        Assert.Equal(TileKind.Impassable, Build(m, 2, 2));           // a 0x10 step is a cliff
        m = Flat();
        SetLevel(m, 3, 2, 0x18); SetLevel(m, 3, 3, 0x18);           // half a level: a ramp along x
        Assert.Equal(TileKind.Land, Build(m, 2, 2));
        m = Flat();
        SetLevel(m, 2, 3, 0x18); SetLevel(m, 3, 3, 0x18);           // a ramp along y
        Assert.Equal(TileKind.Land, Build(m, 2, 2));
        m = Flat();
        SetLevel(m, 3, 3, 0x18);                                     // one corner raised: not a one-axis ramp
        Assert.Equal(TileKind.Impassable, Build(m, 2, 2));
    }

    [Fact]
    public void UndergroundNeedsFlatTilesAndRefusesTheRock()
    {
        // 00c0faa0 sets +0x64 = (floor != 0); then 00c0e440 wants four equal levels and 00c0e3f0 refuses levels > 0x1f.
        var m = Flat(index: 1);
        SetLevel(m, 3, 3, 0x18);
        Assert.Equal(TileKind.Impassable, Build(m, 2, 2));
        m = Flat(index: 1, level: 0x20);
        Assert.Equal(TileKind.Edge, Build(m, 2, 2));
        Assert.True(m.TerrainBlocked(2, 2));
        m = Flat(index: 0, level: 0x20);                             // the same rock on the surface is a plateau
        Assert.Equal(TileKind.Land, Build(m, 2, 2));
    }

    [Fact]
    public void ChunkFifteenHolesAndTextureLakesAreImpassable()
    {
        // 00c0e420: chunk 15 at vertex (x, y) == 0 → kind 3; 00c0f340: a TT_BIG_WATER layer with weight at a corner → kind 3.
        var m = Flat();
        m.Data!.VertexMap15!.Data[2 * 7 + 2] = 0;
        Assert.Equal(TileKind.Impassable, Build(m, 2, 2));
        Assert.Equal(TileKind.Land, m.Kind[2 * 6 + 1]);              // only the tile whose top-left vertex it is
        m = Flat();
        var w = new byte[49]; w[3 * 7 + 3] = 1;                      // the lake layer touches vertex (3, 3) only
        m.Data!.Layers.Add(new TerrainLayer { TilePath = "lake", Weights = new Grid<byte>(7, 7, w) });
        m.LayerTypes = new[] { 1, TerrainPassability.TypeBigWater };
        TerrainPassability.Build(m, 1, m.LayerTypes, new HashSet<(int, int)>());
        Assert.Equal(TileKind.Impassable, m.Kind[2 * 6 + 2]);
        Assert.Equal(TileKind.Impassable, m.Kind[3 * 6 + 3]);
        Assert.Equal(TileKind.Land, m.Kind[1 * 6 + 1]);
    }

    [Fact]
    public void SeaIsLevelZeroOrTheHalfTileMaskAbove140()
    {
        // 00c0f400: four levels 0 → water; else the half-tile mask (chunk 10) at the centre > 0x8c.
        var m = Flat(level: 0);
        Assert.Equal(TileKind.Water, Build(m, 2, 2));
        m = Flat();
        var h = new byte[13 * 13];
        h[5 * 13 + 5] = 0x8d; h[7 * 13 + 7] = 0x8c;
        m.Data!.HalfTileMap = new Grid<byte>(13, 13, h);
        Assert.Equal(TileKind.Water, Build(m, 2, 2));
        Assert.Equal(TileKind.Land, m.Kind[3 * 6 + 3]);
    }

    [Fact]
    public void ObjectPassableTilesOpenTheirTile()
    {
        // 00990800: a tile listed by an object's passable tiles (vf 0xbc) becomes kind 4 before the terrain tests.
        var m = Flat();
        m.Data!.VertexMap15!.Data[2 * 7 + 2] = 0;
        TerrainPassability.Build(m, 1, m.LayerTypes, new HashSet<(int, int)> { (2, 2) });
        Assert.Equal(TileKind.ObjectPassable, m.Kind[2 * 6 + 2]);
        Assert.False(m.TerrainBlocked(2, 2));
    }

    [Fact]
    public void CentreWeightDoublesAndTruncatesLikeTheExe()
    {
        // 00c0ff70: corners 2v (v < 128) or 255, two truncated row means, then their truncated mean.
        var g = new Grid<byte>(2, 2, new byte[] { 1, 0, 0, 0 });
        Assert.Equal(0, TerrainPassability.CentreWeight(g, 0, 0));   // (2 + 0) / 2 = 1, (1 + 0) / 2 = 0
        g = new Grid<byte>(2, 2, new byte[] { 200, 200, 200, 200 });
        Assert.Equal(255, TerrainPassability.CentreWeight(g, 0, 0));
    }

    [Fact]
    public void StockMapsUseTheTerrainKinds()
    {
        // A1L10 (64,37): vertices 0 / 8 / 0 / 8, a ramp from the sea bed to the shore band, so walkable land beside the sea.
        var a = f.Map("A1L10").Floor(0)!;
        Assert.Equal(TileKind.Land, a.Kind[37 * a.Width + 64]);
        Assert.True(a.IsWater(63, 37));
        // A1M2: the map frame (BorderSize 2) and the underground floor's rock are refused.
        var m = f.Map("A1M2");
        Assert.Equal(2, m.BorderSize);
        Assert.True(m.Floor(0)!.TerrainBlocked(1, 40));
        var u = m.Floor(1)!;
        int rock = 0, refused = 0;
        for (int y = 0; y < u.Height; y++)
            for (int x = 0; x < u.Width; x++)
                if (u.Data!.VertexFlags![x, y] > 0x1f) { rock++; if (u.TerrainBlocked(x, y)) refused++; }
        Assert.True(rock > 0);
        Assert.Equal(rock, refused);
    }

    [Fact]
    public void ObjectPositionsRoundToTheNearestTile()
    {
        // 0041d360 / 0041cfe0 take the SAdvMapObject Pos (+0x44, +0x48) with FISTP (round to nearest): A2C1M3's
        // Necropolis town stands at x = 9.00006 (an integer parse put it at 0).
        var town = f.Map("A2C1M3").Objects.First(o => o.Kind == ObjectKind.Town && o.Shared.TownType == "TOWN_NECROMANCY");
        Assert.Equal(new Tile(9, 18), town.Pos);
    }
}
