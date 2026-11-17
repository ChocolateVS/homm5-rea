using H5.Core.Data;
using H5.Core.Numerics;

namespace H5.Combat.Tests;

/// <summary>
/// Non-damage float → integer sites of the combat code under the x87 control word (_PC_24 | _RC_CHOP): Ghidra's ROUND on a
/// plain FISTP truncates there. Each case gives a different integer under round-half-even or double arithmetic.
/// See docs/rules/audit/combat_x87.md ("Other float → integer sites").
/// </summary>
[Collection("game")]
public class X87SitesTests(GameDataFixture f)
{
    /// <summary>00a34c40: FILD power · PerPower + Base, chopped: 3 · 0.1f = 0.300000004 exactly, chopped to 0.29999998 (nearest: 0.30000001).</summary>
    [Fact]
    public void PowerFormulaChopsEachStep()
    {
        Assert.Equal(MathF.BitDecrement(0.3f), new PowerFormula(0, 0.1).EvalX87(3));
        Assert.Equal(25f, new PowerFormula(10, 3).EvalX87(5));
    }

    /// <summary>008a2c20 (008a2d1e..008a2d9b): Power of Blood's rage factor truncates: 25 · 1.5 = 37.5 → 37 (round half even gave 38).</summary>
    [Fact]
    public void PowerOfBloodRageTruncates()
    {
        var b = f.Duel("CREATURE_GOBLIN", 20, "CREATURE_IMP", 20);
        var gob = b.State.Units[0];
        if (!gob.Abilities.Contains("ABILITY_DEMON_RAGED")) gob.Abilities.Add("ABILITY_DEMON_RAGED");
        var h = b.State.Heroes[0];
        h.Present = true; h.Specialization = "";
        h.Perks.Add(Sk.Bloodfire);
        float coef = (float)f.Db.Stats.Get("combat/Creatures/Stronghold/DemonRage/RPCoefPowerOfBlood", 1.5);
        int before = gob.RagePoints;
        b.AddRage(gob, 25);
        Assert.Equal(before + (int)X87.Mul(25, coef), gob.RagePoints);
        if (coef == 1.5f) Assert.Equal(before + 37, gob.RagePoints);
    }

    /// <summary>
    /// 0097b690: Consume Corpse mana = trunc(max(1, chop(HP · chop(1 / HPForOneMana)))): with 3 HP per mana, chop(1/3) =
    /// 0.33333331 and 30 HP give 9.9999990 → 9 mana (HP / 3 rounded gave 10).
    /// </summary>
    [Fact]
    public void ConsumeCorpseManaChopsTheReciprocal()
    {
        float r = X87.Div(1.0, 3f);
        Assert.Equal(MathF.BitDecrement(1f / 3f), r);
        Assert.Equal(9, (int)X87.Mul(30, r));
    }

    /// <summary>
    /// 00983150 (Spell Twister): trunc(chop(chop(chop(level · PerLevel) + Prob) · 100)): level 10, 0.02f, 0.4f →
    /// chop(0.2) + 0.4 = 0.59999996, · 100 = 59.999996 → 59 (the double (0.4 + 0.02 · 10) · 100 rounded gave 60).
    /// </summary>
    [Fact]
    public void SpellTwisterChanceChops()
    {
        Assert.Equal(59, (int)X87.Mul(X87.Add(X87.Mul(10, 0.02f), 0.4f), 100f));
        Assert.Equal(60, (int)Math.Round((0.4 + 0.02 * 10) * 100));
    }

    /// <summary>009d0d10: Strong Rune cost trunc(chop(resource · k)): 5 · 1.5f = 7.5 → 7 (round half even gave 8).</summary>
    [Fact]
    public void RuneCostScalingTruncates()
    {
        Assert.Equal(7, (int)X87.Mul(5, 1.5f));
    }

    /// <summary>
    /// 00a5ab87..00a5aba4: a ranged hit on a Blade Barrier (ABILITY_BLADE_BARRIER 0x68) is × chop(1 − RangedResistance)
    /// (RPGStats +0xa48, 0.75 in the shipped data): 10000 shots with roll 1 at close range deal 2500. The engine had no
    /// ranged reduction for the barrier.
    /// </summary>
    [Fact]
    public void BladeBarrierResistsRangedDamage()
    {
        var b = f.Duel("CREATURE_ARCHER", 10000, "CREATURE_PEASANT", 100, 1, new Cell(5, 5), new Cell(7, 5));
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 0; t.BaseDefence = 0;
        foreach (var h in b.State.Heroes) h.Present = false;
        int plain = b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Ranged, FixedRoll = 1 }, true);
        t.CreatureId = "BLADE_BARRIER";
        float keep = X87.Sub(1f, (float)f.Db.Stats.Get("combat/Spells/BladeBarrier/RangedResistance", 0.75));
        Assert.Equal((int)X87.Add(X87.Mul(plain, keep), 1e-4f), b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Ranged, FixedRoll = 1 }, true));
        if (keep == 0.25f) Assert.Equal(2500, b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Ranged, FixedRoll = 1 }, true));
    }
}
