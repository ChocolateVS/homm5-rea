namespace H5.Combat.Tests;

/// <summary>
/// The attacker / defender stages of the damage chain under the x87 control word (_PC_24 | _RC_CHOP): the exe's
/// factor order (00a58de0, 00a5a900) and the inner products it chops before the 1 ± x step. Each case gives a different
/// integer under double arithmetic or the old factor order. See docs/rules/audit/combat_x87.md.
/// </summary>
[Collection("game")]
public class DamageX87StageTests(GameDataFixture f)
{
    /// <summary>
    /// 00a5ae11..00a5ae15: the luck multiplier (00a5a900's 4th argument) is multiplied in after the attack / defence
    /// factor: 240 · 1.0999999 = 263.99997 (chopped), · 2.25 = 593.99994 → 593. Luck first gave 540 · 1.0999999 → 594.
    /// </summary>
    [StockFact]
    public void LuckMultipliesAfterTheDefenderStage()
    {
        var b = f.Duel("CREATURE_PEASANT", 240, "CREATURE_PEASANT", 100);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 2; t.BaseDefence = 0;
        Assert.Equal(593, b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 1 }, false, luckMult: 2.25));
    }

    /// <summary>
    /// 00a590ac..00a590ff: Jousting is FIMUL cells · 0.05f, FADD 1, both chopped: 2 cells → 1.0999999, so 1000 cavaliers
    /// with roll 1 deal 1099 (double 1 + 0.05 · 2 rounded to the float 1.1 gave 1100).
    /// </summary>
    [StockFact]
    public void JoustingFactorChops()
    {
        var b = f.Duel("CREATURE_CAVALIER", 1000, "CREATURE_PEASANT", 100);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 0; t.BaseDefence = 0;
        Assert.Equal(1099, b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Melee, CellsWalked = 2, FixedRoll = 1 }, false));
    }

    /// <summary>
    /// 00a5ad38..00a5ad66: Evasive is 1 − chop(level · per + base), per = 0.02f (RPGStats): level 7 → 1 − 0.13999999 =
    /// 0.86000001; 10000 shots with roll 1 at close range deal 8600 (the double 0.14 rounded to a float gave 0.85999995 → 8599).
    /// </summary>
    [StockFact]
    public void EvasiveReductionChopsItsLevelProduct()
    {
        var b = f.Duel("CREATURE_ARCHER", 10000, "CREATURE_PEASANT", 100, 1, new Cell(5, 5), new Cell(7, 5));
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 0; t.BaseDefence = 0;
        var th = b.State.Heroes[t.Side];
        th.Present = true; th.Specialization = "HERO_SPEC_EVASIVE"; th.Level = 7;
        Assert.Equal(8600, b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Ranged, FixedRoll = 1 }, true));
    }

    /// <summary>00a5929f..00a592dd: Leap's factor FILD cells · per + base, FADD 1, chopped: 1 cell at 0.1 → 1.0999999 (not 1.1f).</summary>
    [Fact]
    public void LeapFactorChops()
    {
        Assert.Equal(MathF.BitDecrement(1.1f), Battle.LeapFactor(1, 0.1, 0));
        Assert.Equal(1.5f, Battle.LeapFactor(5, 0.1, 0));
    }
}
