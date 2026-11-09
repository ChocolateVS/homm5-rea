namespace H5.Combat.Tests;

/// <summary>
/// The physical damage chain under H5_Game.exe's x87 control word (0x009dc690: _PC_24 | _RC_CHOP): every FMUL / FADD /
/// FSUB / FDIV chops to a float and _ftol (00d11a60) / FISTP truncate (the final + 1e-4f, 00a5ae1b, is chopped too). Each case distinguishes it from double
/// arithmetic with round-to-nearest (the old engine: double products, Math.Round). See docs/rules/audit/combat_x87.md.
/// </summary>
[Collection("game")]
public class DamageX87Tests(GameDataFixture f)
{
    /// <summary>00a58d30: d = 2 → 0.05f · 2 = 0.1f, 1 + 0.1f = 1.1000000015 chops to the float below 1.1f.</summary>
    [Fact]
    public void AttackBonusFactorChops()
        => Assert.Equal(MathF.BitDecrement(1.1f), Battle.AttackDefenceFactor(2, 0, 0.05f, 0.05f));

    /// <summary>00a58d30: d = −5 → p = chop(−0.25000000373) = −0.25, 1 / 1.25 = 0.8 chops to the float below 0.8f.</summary>
    [Fact]
    public void DefenceFactorChops()
        => Assert.Equal(MathF.BitDecrement(0.8f), Battle.AttackDefenceFactor(0, 5, 0.05f, 0.05f));

    /// <summary>Exact cases stay exact: d = 100 → 6; d = 0 → 1; a non-positive coefficient counts as 0.</summary>
    [Fact]
    public void ExactFactorsStayExact()
    {
        Assert.Equal(6f, Battle.AttackDefenceFactor(100, 0, 0.05f, 0.05f));
        Assert.Equal(1f, Battle.AttackDefenceFactor(7, 7, 0.05f, 0.05f));
        Assert.Equal(1f, Battle.AttackDefenceFactor(9, 3, -0.05f, 0.05f));
    }

    /// <summary>
    /// 1000 peasants, attack 2 over defence 0: 1000 · 1.0999999 = 1099.9999 (chopped), + 1e-4f = 1099.9999 → 1099
    /// (double arithmetic: 1100 + 1e-4 → 1100). With 10 peasants the epsilon still lifts 10.999999 to 11.
    /// </summary>
    [StockFact]
    public void AttackBonusDamageTruncatesTheChoppedProduct()
    {
        var b = f.Duel("CREATURE_PEASANT", 1000, "CREATURE_PEASANT", 100);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 2; t.BaseDefence = 0;
        Assert.Equal(1099, b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 1 }, false));
        var b2 = f.Duel("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 100);
        b2.State.Units[0].BaseAttack = 2; b2.State.Units[1].BaseDefence = 0;
        Assert.Equal(11, b2.ComputeDamage(b2.State.Units[0], b2.State.Units[1], new Battle.Hit { FixedRoll = 1 }, false));
    }

    /// <summary>2500 peasants, attack 0 under defence 5: 2500 · 0.79999995 = 1999.9999, + 1e-4f → 1999 (double: 2000).</summary>
    [StockFact]
    public void DefenceDamageTruncatesTheChoppedProduct()
    {
        var b = f.Duel("CREATURE_PEASANT", 2500, "CREATURE_PEASANT", 100);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 0; t.BaseDefence = 5;
        Assert.Equal(1999, b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 1 }, false));
    }

    /// <summary>00a59809..00a59827: Forgetfulness keeps FISTP-chop((1 − 0.5) · 3) = 1 creature (Math.Round gave 2).</summary>
    [StockFact]
    public void ForgetfulnessTruncatesTheKeptCount()
    {
        var b = f.Duel("CREATURE_PEASANT", 3, "CREATURE_PEASANT", 100);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 0; t.BaseDefence = 0;
        a.Effects.Add(new Effect { Id = "SPELL_FORGETFULNESS", Value = 0.5, Remaining = 5 });
        Assert.Equal(2, b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Retaliation, FixedRoll = 2 }, false));
    }
}
