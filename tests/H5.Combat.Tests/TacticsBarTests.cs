using H5.Core.Data;
using H5.Combat;

namespace H5.Combat.Tests;

/// <summary>Tactics army bar: leaving stacks out, splitting and merging (exe FUN_005779d0 / FUN_004dbd70).</summary>
[Collection("game")]
public sealed class TacticsBarTests
{
    readonly GameDataFixture f;
    public TacticsBarTests(GameDataFixture f) => this.f = f;

    Battle Tactics(params (string, int)[] army)
    {
        var a = new ArmySetup();
        foreach (var (c, n) in army) a.Army.Add(new ArmySlot(c, n));
        var s = BattleSetup.Create(f.Db, a, new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 10) } }, 3, obstacles: Array.Empty<Cell>());
        var b = new Battle(f.Db, s);
        b.Begin();
        s.TacticsDone[1] = true;
        return b;
    }

    [Fact]
    public void ReservedStackSitsOutAndReturnsWithTheArmy()
    {
        var b = Tactics(("CREATURE_FOOTMAN", 10), ("CREATURE_ARCHER", 20));
        var archer = b.State.Units.First(u => u.CreatureId == "CREATURE_ARCHER");
        Assert.True(b.Apply(new ReserveCmd(archer.Id) { Side = 0 }).Ok);
        Assert.Null(b.State.At(archer.Pos));
        Assert.True(b.Apply(new EndTacticsCmd { Side = 0 }).Ok);
        Assert.Equal(BattlePhase.Combat, b.State.Phase);
        Assert.False(archer.Alive);
        Assert.True(archer.Removed && archer.Reserve && archer.Count == 20);
    }

    [Fact]
    public void StartNeedsAPlacedStack()
    {
        var b = Tactics(("CREATURE_FOOTMAN", 10));
        var u = b.State.Units.First(x => x.Side == 0);
        b.Apply(new ReserveCmd(u.Id) { Side = 0 });
        Assert.False(b.Apply(new EndTacticsCmd { Side = 0 }).Ok);
        Assert.True(b.Apply(new DeployCmd(u.Id, new Cell(0, 2)) { Side = 0 }).Ok);
        Assert.False(u.Reserve);
        Assert.True(b.Apply(new EndTacticsCmd { Side = 0 }).Ok);
    }

    [Fact]
    public void DeployingOverAStackSendsItToTheBar()
    {
        var b = Tactics(("CREATURE_FOOTMAN", 10), ("CREATURE_ARCHER", 20));
        var fm = b.State.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
        var ar = b.State.Units.First(u => u.CreatureId == "CREATURE_ARCHER");
        var r = b.Apply(new DeployCmd(fm.Id, ar.Pos) { Side = 0 });
        Assert.True(r.Ok);
        Assert.True(ar.Reserve);
        Assert.Contains(r.Events, e => e is Reserved rv && rv.UnitId == ar.Id);
    }

    [Fact]
    public void SplitMergeFollowTheArmyRules()
    {
        var b = Tactics(("CREATURE_FOOTMAN", 10));
        var fm = b.State.Units.First(u => u.Side == 0);
        var r = b.Apply(new SplitStackCmd(fm.Id, 4) { Side = 0 });
        Assert.True(r.Ok);
        var n = b.State.Units.Single(u => u.Side == 0 && u.Id != fm.Id && u.CreatureId == "CREATURE_FOOTMAN");
        Assert.True(n.Reserve);
        Assert.Equal((6, 4), (fm.Count, n.Count));
        Assert.False(b.Apply(new SplitStackCmd(fm.Id, 7) { Side = 0 }).Ok);
        Assert.True(b.Apply(new SplitStackCmd(n.Id, 4, fm.Id) { Side = 0 }).Ok);   // whole stack: merge
        Assert.Equal(10, fm.Count);
        Assert.True(n.Removed && !n.Reserve);
        Assert.Single(b.TacticsStacks(0));
    }

    [Fact]
    public void SplitNeedsAFreeArmySlot()
    {
        var b = Tactics(("CREATURE_FOOTMAN", 10), ("CREATURE_ARCHER", 2), ("CREATURE_PEASANT", 2), ("CREATURE_GRIFFIN", 2), ("CREATURE_PRIEST", 2), ("CREATURE_CAVALIER", 2), ("CREATURE_ANGEL", 2));
        var fm = b.State.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
        Assert.False(b.Apply(new SplitStackCmd(fm.Id, 5) { Side = 0 }).Ok);
        var ar = b.State.Units.First(u => u.CreatureId == "CREATURE_ARCHER");
        Assert.False(b.Apply(new SplitStackCmd(fm.Id, 5, ar.Id) { Side = 0 }).Ok);   // different creatures
    }

    [Fact]
    public void AutoplacePlacesTheBarToo()
    {
        var b = Tactics(("CREATURE_FOOTMAN", 10), ("CREATURE_ARCHER", 20));
        var ar = b.State.Units.First(u => u.CreatureId == "CREATURE_ARCHER");
        b.Apply(new ReserveCmd(ar.Id) { Side = 0 });
        Assert.True(b.Apply(new AutoplaceCmd { Side = 0 }).Ok);
        Assert.True(ar.Alive);
    }
}
