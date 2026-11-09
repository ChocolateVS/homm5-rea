using H5.Core.Data;
namespace H5.Combat.Tests;

/// <summary>Sweep 16 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_16.md).</summary>
[Collection("game")]
public class AuditSweep16Tests(GameDataFixture f)
{
    (Battle b, Unit a, Unit d) Two(string a, string d)
    {
        var b = f.Duel(a, 10, d, 10, 1, new Cell(1, 1), new Cell(14, 10));
        b.Begin();
        return (b, b.State.Units[0], b.State.Units[1]);
    }

    // exe FUN_00a6a0b0: ATB' = max(0, min(1.25, (float)dt / vf 0x174 + ATB)) in single precision.
    [Fact]
    public void TheTickAddsDtOverOneFullTurnInSinglePrecision()
    {
        var (b, a, d) = Two("CREATURE_PEASANT", "CREATURE_ZOMBIE");
        a.Atb = 0.25f; d.Atb = 1.2f;
        double t0 = b.State.Time;
        b.ClockTick(1234);
        Assert.Equal((double)(float)(1234f / b.FullTurnTime(a) + 0.25f), a.Atb);
        Assert.Equal(1.25, d.Atb);
        Assert.Equal(1234 * 10.0 / b.AtbBaseTime(), b.State.Time - t0, 9);
    }

    // exe FUN_00a6a260: a stack under FUN_004e4920 ≠ 0 (Blind, Frozen, Sabotage, Sleeping Strike) is not advanced, and
    // FUN_00a6e440 fills its gauge at 0 while the effect lasts.
    [Fact]
    public void ADisabledStackKeepsItsAtb()
    {
        var (b, a, d) = Two("CREATURE_PEASANT", "CREATURE_ZOMBIE");
        a.Atb = 0.5;
        a.Effects.Add(new Effect { Id = "SPELL_BLIND", Remaining = 1 });
        uint frozenFor = (uint)(1 * b.AtbBaseTime() / 10);
        Assert.InRange(b.TimeToNextTurn(a), (uint)Math.Floor(frozenFor + 0.5 * b.FullTurnTime(a)) - 1, (uint)Math.Floor(frozenFor + 0.5 * b.FullTurnTime(a)) + 1);
        b.ClockTick(100);
        Assert.Equal(0.5, a.Atb);
    }

    // exe FUN_00af5930 (00af5a75..00af5aed) with FUN_00af2690 / FUN_004d9310: dt = max(1, the least vf 0x180). A time of
    // 5000.5 is chopped to 5000; that tick leaves the stack just under 1.0, and the next tick of 1 brings it over.
    [Fact]
    public void AChoppedTurnTimeTakesASecondTickOfOne()
    {
        var (b, a, d) = Two("CREATURE_PEASANT", "CREATURE_ZOMBIE");
        d.Atb = 0;
        a.Atb = (float)(1 - 5000.5 / b.FullTurnTime(a));
        Assert.Equal(5000u, b.TimeToNextTurn(a));
        double t0 = b.State.Time;
        Assert.Equal(a.Id, b.AdvanceTimeToNextActor());
        Assert.Equal(5001 * 10.0 / b.AtbBaseTime(), b.State.Time - t0, 9);
        Assert.True(a.Atb >= 1);
    }

    // exe FUN_00a2e0c0 / FUN_00a2d8f0 / FUN_00af4550: the sequence lists the attacker's stacks (vf 0x84(1) = +0x8c),
    // then the defender's, then the heroes; FUN_00a2e8b0 is a stable merge sort under FUN_00a2d800 (time, ATB desc).
    // Equal times and ATBs: the attacker's stack first; a hero after the stacks.
    [Fact]
    public void FullTiesGoToTheAttackerThenTheHeroes()
    {
        var (b, a, d) = Two("CREATURE_PEASANT", "CREATURE_PEASANT");
        a.Atb = 1.1f; d.Atb = 1.1f;
        Assert.Equal(new[] { a.Id, d.Id }, b.TurnSequence().Take(2));
        d.Atb = 1.15f;
        Assert.Equal(d.Id, b.TurnSequence()[0]);

        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_PEASANT", 10) } },
            new ArmySetup { Army = new() { new ArmySlot("CREATURE_PEASANT", 10) } }, 1, obstacles: Array.Empty<Cell>(), skipTactics: true);
        var hb = new Battle(f.Db, s);
        hb.Begin();
        foreach (var u in s.Units) u.Atb = 1.1f;
        s.Heroes[0].Atb = 1.1f;
        var seq = hb.TurnSequence();
        int hero = seq.IndexOf(Actor.Hero(0));
        Assert.True(hero > 0);
        Assert.All(seq.Where(x => !Actor.IsHero(x)), x => Assert.True(seq.IndexOf(x) < hero));
    }
}
