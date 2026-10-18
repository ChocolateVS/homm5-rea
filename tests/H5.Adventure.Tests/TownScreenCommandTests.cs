using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

[Collection("adv")]
public class TownScreenCommandTests(Fixture f)
{
    static (Engine.Adventure G, HeroState H) HeroOfCurrent(Fixture f)
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index && x.Army.StackCount() >= 1);
        return (g, h);
    }

    [Fact]
    public void SwapReordersAHerosSlots()
    {
        var (g, h) = HeroOfCurrent(f);
        int from = Array.FindIndex(h.Army, s => s is { Count: > 0 });
        int to = Array.FindIndex(h.Army, s => s == null);
        var stack = h.Army[from]!;
        var r = g.Apply(new SwapStacksCmd(h.Id, -1, from, h.Id, -1, to) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Null(h.Army[from]);
        Assert.Same(stack, h.Army[to]);
    }

    [Fact]
    public void SwapNeverStripsAHeroOfItsLastStack()
    {
        var (g, h) = HeroOfCurrent(f);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        h.Pos = g.EntranceOf(t);
        while (h.Army.StackCount() > 1) h.Army[Array.FindLastIndex(h.Army, s => s is { Count: > 0 })] = null;
        Array.Fill(t.Garrison, null);
        int slot = Array.FindIndex(h.Army, s => s is { Count: > 0 });
        var r = g.Apply(new SwapStacksCmd(h.Id, -1, slot, -1, t.Object, 0) { Player = g.Current.Index });
        Assert.False(r.Ok);
    }

    [Fact]
    public void GarrisoningMergesTheTownGarrisonIntoTheHerosArmy()
    {
        var (g, h) = HeroOfCurrent(f);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        h.Pos = g.EntranceOf(t);
        h.InTown = t.Object;
        Array.Fill(t.Garrison, null);
        t.Garrison[0] = new Stack("CREATURE_PEASANT", 7);
        int before = h.Army.Live().Where(s => s.Creature == "CREATURE_PEASANT").Sum(s => s.Count);
        var r = g.Apply(new GarrisonHeroCmd(t.Object, h.Id, true) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.True(h.Garrisoned);
        Assert.Equal(0, t.Garrison.StackCount());
        Assert.Equal(before + 7, h.Army.Live().Where(s => s.Creature == "CREATURE_PEASANT").Sum(s => s.Count));
        Assert.True(g.Apply(new GarrisonHeroCmd(t.Object, h.Id, false) { Player = g.Current.Index }).Ok);
        Assert.False(h.Garrisoned);
    }

    [Fact]
    public void ArtifactsMoveBetweenWornSlotsAndBackpackRespectingSlotCapacity()
    {
        var (g, h) = HeroOfCurrent(f);
        var rings = g.Core.Artifacts.Values.Where(a => a.Slot == "FINGER").Select(a => a.Id).Take(3).ToList();
        h.Artifacts.Clear();
        h.Backpack.Clear();
        h.Backpack.AddRange(rings);
        int p = g.Current.Index;
        Assert.True(g.Apply(new MoveArtifactCmd(h.Id, rings[0], true, h.Id, false) { Player = p }).Ok);
        Assert.True(g.Apply(new MoveArtifactCmd(h.Id, rings[1], true, h.Id, false) { Player = p }).Ok);
        Assert.False(g.Apply(new MoveArtifactCmd(h.Id, rings[2], true, h.Id, false) { Player = p }).Ok);   // two rings max
        Assert.True(g.Apply(new MoveArtifactCmd(h.Id, rings[0], false, h.Id, true) { Player = p }).Ok);
        Assert.Equal(new[] { rings[1] }, h.Artifacts);
        Assert.Equal(2, h.Backpack.Count);
    }
}

[Collection("adv")]
public class StartRevealTests(Fixture f)
{
    /// <summary>Regression: the adventure client showed only fog because nothing was explored at the start.</summary>
    [Fact]
    public void PlayersStartWithTilesAroundTheirHeroesAndTownsExplored()
    {
        var g = Engine.Adventure.NewGame(f.Db, f.Map("A2M1"), GameSetup.AllPlayers(f.Map("A2M1"), 1, 1)).Game;
        foreach (var p in g.S.Players.Where(p => g.S.Heroes.Any(h => h.Alive && h.Owner == p.Index)))
            Assert.True(p.Explored.Sum(bits => bits.Count(b => b)) > 0, $"player {p.Index} has nothing explored");
    }
}

[Collection("adv")]
public class TavernPoolTests(Fixture f)
{
    /// <summary>Campaign-only heroes (ScenarioHero=true, e.g. GhostFSLord) are never offered in taverns on ordinary maps.</summary>
    [Fact]
    public void TavernPoolExcludesScenarioHeroes()
    {
        var g = f.NewGame();
        Assert.NotEmpty(g.S.HeroPool);
        Assert.DoesNotContain(g.S.HeroPool, id => f.Db.IsScenarioHero(f.Core.Heroes[id]));
        Assert.DoesNotContain("GhostFSLord", g.S.HeroPool);
    }
}
