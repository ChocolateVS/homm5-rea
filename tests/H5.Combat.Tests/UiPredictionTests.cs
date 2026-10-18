using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>The hover prediction's retaliation verdict follows the engine's rules (exe 00a5fee0).</summary>
[Collection("game")]
public class UiPredictionTests(GameDataFixture f)
{
    Battle Adjacent() => f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 30, posA: new Cell(4, 4), posB: new Cell(5, 4));

    [Fact]
    public void OnlyExpertBlindStopsRetaliationInPrediction()
    {
        foreach (var (mastery, expect) in new[] { (Mastery.Basic, true), (Mastery.Advanced, true), (Mastery.Expert, false) })
        {
            var b = Adjacent();
            var (a, t) = (b.State.Units[0], b.State.Units[1]);
            b.AddEffect(t, new Effect { Id = "SPELL_BLIND", Mastery = mastery, Remaining = 10 });
            var p = b.PredictAttack(a.Id, t.Id, null, ranged: false)!;
            Assert.Equal(expect, p.Retaliates);
            if (!expect) Assert.Equal("COMBAT_NO_BLIND_RETALIATE", p.RetaliateKey);
        }
    }

    [Fact]
    public void HypnotizedTargetNeverRetaliatesInPrediction()
    {
        var b = Adjacent();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        // exe 00a5fee0 @ 00a5ffbb: only a strike back at the hypnotized stack's controller is suppressed.
        b.AddEffect(t, new Effect { Id = "SPELL_HYPNOTIZE", SourceSide = a.Side, Remaining = 10 });
        Assert.Equal("COMBAT_NO_HYPNOTIZE_RETALIATE", b.PredictAttack(a.Id, t.Id, null, ranged: false)!.RetaliateKey);
    }

    /// <summary>exe 00a63020 sums the prediction over every target of the attack pattern (Cerberus three heads).</summary>
    [Fact]
    public void PatternAttackPredictionSumsAllTargets()
    {
        var b = f.Duel("CREATURE_CERBERI", 20, "CREATURE_PEASANT", 30, posA: new Cell(4, 4), posB: new Cell(5, 4));
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        // Two more peasant stacks on the three-headed fan beside the target cell.
        var up = BattleSetup.AddCreature(f.Db, b.State, 1, "CREATURE_PEASANT", 30, new Cell(5, 3), false);
        var down = BattleSetup.AddCreature(f.Db, b.State, 1, "CREATURE_PEASANT", 30, new Cell(5, 5), false);
        var targets = b.PredictionTargets(a, t, null, AttackKind.Melee, 0, false).Select(x => x.Unit.Id).ToHashSet();
        Assert.Contains(up.Id, targets);
        Assert.Contains(down.Id, targets);

        var all = b.PredictAttack(a.Id, t.Id, null, ranged: false)!;
        var (min, _) = b.DamageRangeOf(a);
        int single = b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Melee, FixedRoll = min }, false);
        Assert.Equal(3 * single, all.MinDamage);   // three identical stacks, full share each
        Assert.Equal(3 * Battle.KillsFor(single, t), all.MinKills);
    }

    /// <summary>Hovering must not change the battle: no RNG draw (Prismatic Breath's radius) and the attacker stays put.</summary>
    [Fact]
    public void PredictionDoesNotChangeState()
    {
        var b = f.Duel("CREATURE_RAINBOW_DRAGON", 2, "CREATURE_PEASANT", 30, posA: new Cell(3, 4), posB: new Cell(6, 4));
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        string before = b.State.ComputeHash();
        for (int i = 0; i < 5; i++) b.PredictAttack(a.Id, t.Id, new Cell(4, 4), ranged: false);
        Assert.Equal(before, b.State.ComputeHash());
        Assert.Equal(new Cell(3, 4), a.Pos);
    }

    [Fact]
    public void RuneOfBerserkingAddsOneStrikeInPrediction()
    {
        var b = Adjacent();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        b.AddEffect(a, new Effect { Id = H5.Combat.Fx.RuneBerserking, Remaining = 10 });
        Assert.Equal(2, b.PredictAttack(a.Id, t.Id, null, ranged: false)!.Strikes);
    }
}
