using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// The pathfinder's object map (H5_Game.exe 0045d910, built per search by 00ceb670, read by 009d1410): an object's
/// action tile is enterable whatever the terrain kind there, unless a multi-tile object blocks it.
/// </summary>
[Collection("adv")]
public class ActionTilePassabilityTests(Fixture f)
{
    static readonly (int dx, int dy)[] Around = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    /// <summary>Puts a hero on an open neighbour of <paramref name="target"/> and returns the path onto it.</summary>
    static List<Tile>? PathFromNeighbour(Engine.Adventure g, Tile target)
    {
        var fl = g.Map.Floor(target.Floor)!;
        var h = g.S.Heroes.First(x => x.Alive);
        var from = Around.Select(d => target.Offset(d.dx, d.dy)).First(n =>
            fl.InBounds(n.X, n.Y) && !fl.IsBlocked(n.X, n.Y) && !fl.IsWater(n.X, n.Y) && g.S.Heroes.All(x => x.Pos != n));
        h.Pos = from;
        h.InTown = -1;
        h.InBoat = false;
        h.MovePoints = 5000;
        return g.FindPath(h, target);
    }

    [Theory]
    [InlineData("A2C1M4", "BUILDING_MONOLITH_TWO_WAY", 110, 119, 1)] // a monolith on a chunk 15 hole (00c0e420) underground
    [InlineData("A2C2M2", "MONSTER_SPECIFIC", 26, 79, 0)]            // a guard on a chunk 15 hole
    public void ActionTilesOnRefusedTerrainAreEnterable(string map, string type, int x, int y, int floor)
    {
        var g = f.NewGame(map);
        var o = g.Map.Objects.First(o => o.Shared.Type == type && o.Pos == new Tile(x, y, floor));
        var at = o.ActionTiles.First();
        var fl = g.Map.Floor(floor)!;
        Assert.True(fl.TerrainBlocked(at.X, at.Y));                     // 00990800 gives the tile kind 3
        Assert.False(fl.ObjectBlocked[at.Y * fl.Width + at.X]);
        var path = PathFromNeighbour(g, at);                             // 009d1410 → 0045d690 lets it in
        Assert.NotNull(path);
        Assert.Equal(at, path![^1]);
    }

    [Fact]
    public void SingleTileObjectsRegisterTheirPositionMultiTileObjectsTheirEntrances()
    {
        // 0045d910: at most one blocked and one active tile → the position (0045d8a0); else the active tiles (0045d740),
        // and the blocked tiles go to the blocked set (0045d6d0), refused before the action tiles.
        var m = f.Map("A2C1M4");
        var town = m.Objects.First(o => o.Kind == ObjectKind.Town);
        Assert.True(town.MultiTile);
        Assert.Equal(town.ActiveTiles, town.ActionTiles);
        var fl = m.Floor(town.Pos.Floor)!;
        var entrance = town.ActiveTiles.First();
        Assert.False(fl.ObjectBlocked[entrance.Y * fl.Width + entrance.X]);
        Assert.Contains(town.BlockedTiles, b => fl.ObjectBlocked[b.Y * fl.Width + b.X]);
        var monster = m.Objects.First(o => o.Kind == ObjectKind.Monster && !o.MultiTile);
        Assert.Equal(new[] { monster.Pos }, monster.ActionTiles);
    }

    [Theory]
    [InlineData("A2C3M3", "Mutazz", 44, 84)]   // MapScript.lua: ZehirTownInit("Mutazz"); A2_Zehir.lua moves it (SetObjectPosition)
    [InlineData("A2C3M4", "z_town", 26, 70)]   // script.lua: ZehirTownInit("z_town")
    public void ZehirsMovingTownIsParkedInTheRockAtLoad(string map, string name, int x, int y)
    {
        // The A2C3 campaign keeps Zehir's flying town underground in solid rock (levels 0x20, 00c0e3f0 → kind 5) until
        // the script moves it; nothing around it is enterable, so it is out of reach at load as in the original.
        var m = f.Map(map);
        var town = m.Objects.First(o => o.Kind == ObjectKind.Town && o.Name == name);
        Assert.Equal(new Tile(x, y, 1), town.Pos);
        var fl = m.Floor(1)!;
        foreach (var t in town.ActionTiles.SelectMany(a => Around.Select(d => a.Offset(d.dx, d.dy))))
            Assert.True(fl.TerrainBlocked(t.X, t.Y));
        Assert.Equal(TileKind.Edge, fl.Kind[y * fl.Width + x]);
    }

    [Fact]
    public void ACampaignTownOnOpenGroundIsReachable()
    {
        // A2C3M2: Zehir's town lands on the surface (129, 48); its entrance is enterable from the land around it.
        var g = f.NewGame("A2C3M2");
        var town = g.Map.Objects.First(o => o.Kind == ObjectKind.Town && o.Name == "ZehirsTown");
        var entrance = town.ActiveTiles.First();
        Assert.NotNull(PathFromNeighbour(g, entrance));
    }
}
