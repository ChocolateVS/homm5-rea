using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>Fog-of-war grid per H5_Game.exe FUN_005b0f80 / FUN_005b0ea0.</summary>
public class WarFogGridTests
{
    [Fact]
    public void GridIsPowerOfTwoPlusOneCornersAndZeroOutsideTheMap()
    {
        var all = Enumerable.Repeat(true, 72 * 40).ToArray();
        var g = WarFogGrid.Build(all, 72, 40, out int gw, out int gh);
        Assert.Equal(129, gw);
        Assert.Equal(65, gh);
        Assert.Equal(255, g[20 * gw + 36]);          // inside
        Assert.Equal(0, g[0]);                       // corner row/column 0 stays black
        Assert.Equal(0, g[20 * gw + 100]);           // beyond the map width
        Assert.True(g[20 * gw + 71] < 255);          // the map border fades to black
    }

    [Fact]
    public void StraightEdgeProfileAndIsolatedTile()
    {
        const int w = 32, h = 32;
        var e = new bool[w * h];
        for (int y = 0; y < h; y++) for (int x = 10; x < w; x++) e[y * w + x] = true;
        var g = WarFogGrid.Build(e, w, h, out int gw, out _);
        // Corner 10 lies between unexplored tile 9 and explored tile 10.
        var row = Enumerable.Range(9, 5).Select(x => (int)g[16 * gw + x]).ToArray();
        Assert.Equal(new[] { 10, 71, 183, 244, 255 }, row);

        var one = new bool[w * h];
        one[16 * w + 16] = true;
        Assert.All(WarFogGrid.Build(one, w, h, out _, out _), v => Assert.Equal(0, v));
    }
}
