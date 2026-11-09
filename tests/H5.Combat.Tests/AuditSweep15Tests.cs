namespace H5.Combat.Tests;

/// <summary>Sweep 15 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_15.md).</summary>
[Collection("game")]
public class AuditSweep15Tests(GameDataFixture f)
{
    static ApplyResult Use(Battle b, Unit u, string ability, Cell cell)
    {
        b.State.Active = u.Id;
        return b.Apply(new UseAbilityCmd(u.Id, ability) { Side = u.Side, Cell = cell });
    }

    // exe FUN_00a6e440 (00a6e66b..00a6e691): chop((1 − ATB) · ATBBaseTime / initiative); FUN_00a6a060: ATBBaseTime /
    // initiative; ATB ≥ 1 → 0.
    [Fact]
    public void TurnTimesAreExeIntegerGameTime()
    {
        var b = f.Duel("CREATURE_ROYAL_GRIFFIN", 10, "CREATURE_PEASANT", 50, 1, new Cell(1, 1), new Cell(10, 5));
        b.Begin();
        var t = b.State.Units.First(u => u.CreatureId == "CREATURE_PEASANT");
        t.Effects.Clear();
        t.Atb = 0.5;
        double init = b.InitiativeOf(t);
        Assert.Equal((uint)Math.Floor((1 - 0.5f) * 100000.0 / init), b.TimeToNextTurn(t));
        Assert.Equal(100000.0 / (float)init, b.FullTurnTime(t), 6);
        t.Atb = 1.1;
        Assert.Equal(0u, b.TimeToNextTurn(t));
    }

    // exe FUN_00984a00 0x9a branch (00985474..00985527): the finish 0x9b is cast at once when chop(the griffins' full
    // turn, vf 0x174) < the target's time to its next turn (vf 0x180), or when the target shoots (vf 0x1e0).
    [Fact]
    public void BattleDiveStrikesAtOnceWhenTheGriffinsActBeforeTheTarget()
    {
        var b = f.Duel("CREATURE_ROYAL_GRIFFIN", 10, "CREATURE_PEASANT", 50, 1, new Cell(1, 1), new Cell(10, 5));
        b.Begin();
        var g = b.State.Units.First(u => u.CreatureId == "CREATURE_ROYAL_GRIFFIN");
        var t = b.State.Units.First(u => u.CreatureId == "CREATURE_PEASANT");
        t.Atb = 0;
        Assert.True((uint)b.FullTurnTime(g) < b.TimeToNextTurn(t));
        int before = t.Count * 1000 + t.TopHealth;
        var r = Use(b, g, Ab.BattleDive, t.Pos);
        Assert.True(r.Ok, r.Error);
        Assert.True(t.Count * 1000 + t.TopHealth < before || !t.Alive);
        Assert.DoesNotContain(b.State.Fields, x => x.Kind == "BATTLE_DIVE");
        Assert.False(g.HasEffect(Fx.Invisible));
    }

    // Otherwise the griffins stay up (0x9a until removed); the turn start FUN_00bbc8e0 → FUN_00bbc510 issues 0x9b.
    [Fact]
    public void BattleDiveWaitsForTheGriffinsTurnWhenTheTargetActsFirst()
    {
        var b = f.Duel("CREATURE_ROYAL_GRIFFIN", 10, "CREATURE_PEASANT", 50, 1, new Cell(1, 1), new Cell(10, 5));
        b.Begin();
        var g = b.State.Units.First(u => u.CreatureId == "CREATURE_ROYAL_GRIFFIN");
        var t = b.State.Units.First(u => u.CreatureId == "CREATURE_PEASANT");
        t.Atb = 0.95;
        Assert.False(b.BattleDiveFinishesAtOnce(g, t));
        int before = t.Count * 1000 + t.TopHealth;
        var r = Use(b, g, Ab.BattleDive, t.Pos);
        Assert.True(r.Ok, r.Error);
        var dive = b.State.Fields.Single(x => x.Kind == "BATTLE_DIVE");
        Assert.True(double.IsPositiveInfinity(dive.Remaining));
        Assert.True(b.BattleDiveTurnStart(g));
        Assert.True(t.Count * 1000 + t.TopHealth < before || !t.Alive);
        Assert.DoesNotContain(b.State.Fields, x => x.Kind == "BATTLE_DIVE");
    }

    [Fact]
    public void BattleDiveOnAShooterStrikesAtOnce()
    {
        var b = f.Duel("CREATURE_ROYAL_GRIFFIN", 10, "CREATURE_ARCHER", 50, 1, new Cell(1, 1), new Cell(10, 5));
        b.Begin();
        var g = b.State.Units.First(u => u.CreatureId == "CREATURE_ROYAL_GRIFFIN");
        var t = b.State.Units.First(u => u.CreatureId == "CREATURE_ARCHER");
        t.Atb = 0.99;
        Assert.True(b.BattleDiveFinishesAtOnce(g, t));
    }

    // exe FUN_00c50a30 → FUN_00c506f0: N(g) = Σ max(1, count / growth), N(t) = 1 + max(1, count / growth); |diff| shifts of
    // 5 points ({10, 40, 40, 10}) toward outcome 1 (N(t) < N(g)) or 4, then Random(0, 100) inclusive, first sum ≥ roll.
    [Fact]
    public void DefileRollShiftsByTheWeeklyGrowthUnits()
    {
        var b = f.Duel("CREATURE_GOBLIN_DEFILER", 2000, "CREATURE_SHAMAN", 1, 3, new Cell(1, 1), new Cell(10, 5));
        b.Begin();
        var gob = b.State.Units.First(u => u.CreatureId == "CREATURE_GOBLIN_DEFILER");
        var sh = b.State.Units.First(u => u.CreatureId == "CREATURE_SHAMAN");
        // N(g) ≫ N(t): {85, 5, 5, 5} — outcome 1 for rolls 0..85 of 0..100.
        var n = new int[5];
        for (int i = 0; i < 3000; i++) n[b.DefileRoll(sh, new[] { gob })]++;
        Assert.Equal(0, n[0]);
        Assert.InRange(n[1] / 3000.0, 0.80, 0.90);
        for (int k = 2; k <= 4; k++) Assert.InRange(n[k] / 3000.0, 0.02, 0.09);
        // No defilers left: no roll.
        gob.Count = 0;
        Assert.Equal(0, b.DefileRoll(sh, new[] { gob }));
    }
}
