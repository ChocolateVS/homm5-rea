using H5.Adventure.Map;

namespace H5.Rmg.Tests;

/// <summary>The adventure passability (H5_Game.exe 00990800) of a generated underground map.</summary>
[Collection("rmg")]
public class RmgPassabilityTests(RmgFixture f)
{
    [StockFact]
    public void UndergroundRockChunk15AndZoneBordersAreRefused()
    {
        var g = new RmgGenerator(f.Db, new RmgOptions { Template = "S1-2P2-4Z4K1S", Size = 2, Underground = true, Seed = 77, Players = 2 });
        var r = g.Generate();
        var map = r.ToAdvMap(new SharedCatalog(f.Xdb));
        // The RMG saves BorderSize 1 (the exe's stock RMG maps carry 1): the outer ring is kind 5.
        Assert.Equal(1, map.BorderSize);
        for (int fi = 0; fi < 2; fi++)
        {
            var fl = map.Floors[fi]!;
            var d = fl.Data!;
            for (int y = 1; y < fl.Height - 1; y++)
                for (int x = 1; x < fl.Width - 1; x++)
                {
                    // 00c0e420: chunk 15 at the tile's top-left vertex 0 (cleared by 0x009198f0 on zone borders and
                    // far from roads and objects) refuses the tile; 00c0e3f0: underground levels above 0x1f (the rock,
                    // 0x00912940 / 0x009159a0) too.
                    if (d.VertexMap15![x, y] == 0) Assert.True(fl.TerrainBlocked(x, y), $"chunk 15 hole ({x},{y},{fi}) walkable");
                    if (fi == 1 && d.VertexFlags![x, y] > 0x1f) Assert.Equal(TileKind.Edge, fl.Kind[y * fl.Width + x]);
                }
            Assert.Equal(TileKind.Edge, fl.Kind[0]);
        }
        // Every start town can be entered: its visit tile is open terrain.
        foreach (var town in r.StartTowns.Values)
        {
            var (ax, ay) = r.Access[town];
            Assert.False(map.Floors[town.Floor]!.TerrainBlocked(ax, ay), $"town {town.Shared} visit tile ({ax},{ay}) refused");
        }
        Assert.Contains(map.Floors[1]!.Kind, k => k == TileKind.Land);
    }
}
