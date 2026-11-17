using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>The army screens' drop / split rules (ArmySplit: 00615670, 006c5e60, 00652b70, controller 00786800 / 007869a0).</summary>
[Collection("adv")]
public class ArmySplitTests(Fixture f)
{
    static (Engine.Adventure G, HeroState H, TownState T) HeroAtTown(Fixture f)
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        h.Pos = g.EntranceOf(t);
        Array.Fill(h.Army, null);
        Array.Fill(t.Garrison, null);
        return (g, h, t);
    }

    static ArmySplit.Side HeroSide(HeroState h, int slot) => new(h.Id, -1, -1, slot, h.Army);
    static ArmySplit.Side TownSide(TownState t, int slot) => new(-1, t.Object, -1, slot, t.Garrison);

    static void Send(Engine.Adventure g, AdvCommand? cmd)
    {
        Assert.NotNull(cmd);
        var r = g.Apply(cmd! with { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public void DroppingOnTheSameCreatureInsideOneArmyMerges()
    {
        var (g, h, _) = HeroAtTown(f);
        h.Army[0] = new Stack("CREATURE_PEASANT", 10);
        h.Army[3] = new Stack("CREATURE_PEASANT", 5);
        var cmd = ArmySplit.Drop(HeroSide(h, 0), HeroSide(h, 3));
        Assert.IsType<TransferStackCmd>(cmd);
        Send(g, cmd);
        Assert.Null(h.Army[0]);
        Assert.Equal(15, h.Army[3]!.Count);
    }

    [Fact]
    public void DroppingOnAnEmptySlotOrOtherCreatureMovesOrSwaps()
    {
        var (g, h, t) = HeroAtTown(f);
        h.Army[0] = new Stack("CREATURE_PEASANT", 10);
        h.Army[1] = new Stack("CREATURE_ARCHER", 4);
        t.Garrison[0] = new Stack("CREATURE_FOOTMAN", 3);
        Send(g, ArmySplit.Drop(HeroSide(h, 1), TownSide(t, 5)));          // hero → empty garrison slot
        Assert.Null(h.Army[1]);
        Assert.Equal("CREATURE_ARCHER", t.Garrison[5]!.Creature);
        Send(g, ArmySplit.Drop(TownSide(t, 0), HeroSide(h, 0)));          // different creatures exchange
        Assert.Equal("CREATURE_FOOTMAN", h.Army[0]!.Creature);
        Assert.Equal("CREATURE_PEASANT", t.Garrison[0]!.Creature);
        Assert.Null(ArmySplit.Drop(HeroSide(h, 4), HeroSide(h, 5)));      // two empty slots
        Assert.Null(ArmySplit.Drop(HeroSide(h, 0), HeroSide(h, 0)));      // same slot
    }

    [Fact]
    public void SplitIntoAnEmptySlotStartsAtHalfRoundedUpForTheTarget()
    {
        var (g, h, _) = HeroAtTown(f);
        h.Army[0] = new Stack("CREATURE_PEASANT", 9);
        var a = HeroSide(h, 0);
        var b = HeroSide(h, 2);
        Assert.True(ArmySplit.CanSplit(a, b));
        Assert.Equal(5, ArmySplit.StartValue(a, b, out int lo, out int hi));   // (9 + 1) / 2 to the target
        Assert.Equal(0, lo);
        Assert.Equal(9, hi);                                                    // same army: no minimum
        foreach (var c in ArmySplit.SplitCommands(a, b, 5)) Send(g, c);
        Assert.Equal(4, h.Army[0]!.Count);
        Assert.Equal(5, h.Army[2]!.Count);
    }

    [Fact]
    public void SplitBetweenSameCreaturesSetsTheTargetsSizeBothWays()
    {
        var (g, h, t) = HeroAtTown(f);
        h.Army[0] = new Stack("CREATURE_PEASANT", 6);
        h.Army[1] = new Stack("CREATURE_ARCHER", 1);
        t.Garrison[2] = new Stack("CREATURE_PEASANT", 4);
        var a = HeroSide(h, 0);
        var b = TownSide(t, 2);
        Assert.Equal(4, ArmySplit.StartValue(a, b, out _, out _));             // the target's own size
        foreach (var c in ArmySplit.SplitCommands(a, b, 9)) Send(g, c);       // target 9 → 5 move over
        Assert.Equal(1, h.Army[0]!.Count);
        Assert.Equal(9, t.Garrison[2]!.Count);
        a = HeroSide(h, 0);
        b = TownSide(t, 2);
        foreach (var c in ArmySplit.SplitCommands(a, b, 2)) Send(g, c);       // target 2 → 7 come back
        Assert.Equal(8, h.Army[0]!.Count);
        Assert.Equal(2, t.Garrison[2]!.Count);
    }

    [Fact]
    public void SplitKeepsAHerosLastCreatureAndRefusesOtherCreatures()
    {
        var (_, h, t) = HeroAtTown(f);
        h.Army[0] = new Stack("CREATURE_PEASANT", 1);
        var a = HeroSide(h, 0);
        Assert.Equal(1, ArmySplit.Min(a, TownSide(t, 0)));
        Assert.False(ArmySplit.CanSplit(a, TownSide(t, 0)));                   // 1 creature, minimum 1
        Assert.True(ArmySplit.CanSplit(a, HeroSide(h, 4)));                    // inside one army there is no minimum
        h.Army[0] = new Stack("CREATURE_PEASANT", 5);
        a = HeroSide(h, 0);
        Assert.True(ArmySplit.CanSplit(a, TownSide(t, 0)));
        Assert.Equal(3, ArmySplit.StartValue(a, TownSide(t, 0), out int lo, out int hi));
        Assert.Equal(0, lo);
        Assert.Equal(4, hi);                                                    // one stays with the hero
        Assert.Empty(ArmySplit.SplitCommands(a, TownSide(t, 0), 5));
        t.Garrison[1] = new Stack("CREATURE_ARCHER", 3);
        Assert.False(ArmySplit.CanSplit(a, TownSide(t, 1)));
        Assert.Empty(ArmySplit.SplitCommands(a, TownSide(t, 1), 2));
    }
}
