using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 9 of the combat audit: GAP rows closed against H5_Game.exe (docs/rules/audit/combat_sweep_9.md).</summary>
[Collection("game")]
public class AuditSweep9Tests(GameDataFixture f)
{
    (Battle b, Unit a, Unit t) Make(string mine, int countA, string theirs, int countB, Cell posA, Cell posB)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot(mine, countA) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot(theirs, countB) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        foreach (var u in s.Units.Where(u => u.IsWarMachine).ToList()) s.Units.Remove(u);
        var a = s.Units.First(u => u.Side == 0 && u.CreatureId == mine);
        var t = s.Units.First(u => u.Side == 1 && u.CreatureId == theirs);
        a.Pos = a.StartPos = posA;
        t.Pos = t.StartPos = posB;
        return (new Battle(f.Db, s), a, t);
    }

    static List<BattleEvent> UntilNextTurn(ApplyResult r)
    {
        int next = r.Events.FindIndex(e => e is TurnStarted);
        return next < 0 ? r.Events : r.Events.Take(next).ToList();
    }

    // exe FUN_00984a00 @ 00988051 → FUN_00bbc1d0: the cast adds 0xf5 with count 2, then FUN_00bbc0e0 / FUN_00bbbc00 make
    // the berserk attack at once (enemy army first), unanswered because FUN_00a5fee0 @ 00a6005c refuses retaliation to an
    // attacker whose 0xf5 count is ≥ 2; vf 0x268 then drops the count to 1. The engine made no attack in the cast and
    // forced one at the stack's next turn instead.
    [Fact]
    public void BerserkerRageCastAttacksAtOnceWithoutRetaliation()
    {
        var (b, u, t) = Make("CREATURE_BERSERKER", 20, "CREATURE_PEASANT", 100, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        b.State.Active = u.Id;
        var r = b.Apply(new UseAbilityCmd(u.Id, Ab.BerserkerRage) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var cast = UntilNextTurn(r);
        Assert.Contains(cast, e => e is AttackStarted a && a.Attacker == u.Id && a.Target == t.Id);
        Assert.DoesNotContain(cast, e => e is AttackStarted { Kind: AttackKind.Retaliation });
        Assert.Equal(1, u.GetEffect(Fx.BerserkerRage)!.Value2);
        Assert.False(Battle.BerserkerRageBlocksRetaliation(u));   // count 1: later attacks are answered again
    }

    // exe FUN_00bbc0e0: no stack to attack → a Defend event (vtable 00e17db8 → FUN_004e0280 → the Defend handler
    // FUN_008a6be0); the effect stays until the stack's next turn start, where FUN_00bbc8e0 @ 00bbc951 removes it
    // (vf 0x270(0xf5, time, force)).
    [Fact]
    public void BerserkerRageWithoutTargetDefendsAndEndsAtTheNextTurnStart()
    {
        var (b, u, t) = Make("CREATURE_BERSERKER", 20, "CREATURE_PEASANT", 100, new Cell(1, 1), new Cell(14, 10));
        b.Begin();
        b.State.Active = u.Id;
        var start = u.Pos;
        var r = b.Apply(new UseAbilityCmd(u.Id, Ab.BerserkerRage) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var cast = UntilNextTurn(r);
        Assert.Contains(cast, e => e is Defended d && d.Actor == u.Id);
        Assert.DoesNotContain(cast, e => e is AttackStarted);
        Assert.Equal(start, u.Pos);
        Assert.True(u.HasEffect(Fx.BerserkerRage));
        b.Sweep7TurnStart(u);
        Assert.False(u.HasEffect(Fx.BerserkerRage));
    }

    // exe FUN_00a2d800 (the CArenaATB comparator): turns sorted by time ascending, then by the LARGER current ATB
    // (+0x1c). Two stacks reaching ATB 1 at the same moment: the one with more ATB (so the lower initiative) acts first.
    // The engine took the higher initiative first.
    [Fact]
    public void TurnTimeTiesGoToTheLargerCurrentAtb()
    {
        var (b, fast, slow) = Make("CREATURE_PEASANT", 10, "CREATURE_ZOMBIE", 10, new Cell(1, 1), new Cell(14, 10));
        b.Begin();
        double rf = b.InitiativeOf(fast) / 10, rs = b.InitiativeOf(slow) / 10;
        if (rf < rs) (fast, slow, rf, rs) = (slow, fast, rs, rf);
        Assert.True(rf > rs);
        foreach (var h in b.State.Heroes) h.Atb = -100;
        fast.Atb = 1 - 0.5 * rf;
        slow.Atb = 1 - 0.5 * rs;
        Assert.Equal(slow.Id, b.AdvanceTimeToNextActor());
    }
}
