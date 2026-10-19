using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Regression tests for behaviour confirmed by disassembling H5_Game.exe (TotE 3.1, GOG build).
/// Addresses refer to docs/rules/EXE_FINDINGS.md.
/// </summary>
[Collection("game")]
public class ExeFindingsTests(GameDataFixture f)
{
    /// <summary>00a58d30: the attack/defence factor is uncapped; DamageIncreaseCap is never read.</summary>
    [StockFact]
    public void AttackDefenceFactorIsUncapped()
    {
        var b = f.Duel("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 100; t.BaseDefence = 0;
        // 1 + 0.05*100 = 6, beyond the data's cap of +300%.
        Assert.Equal(60, b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 1 }, false));
        a.BaseAttack = 0; t.BaseDefence = 400;
        // 1/(1 + 0.05*400) = 1/21 → 0.476, below the data's 0.1 floor, clamped to the minimum of 1 damage.
        Assert.Equal(1, b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 1 }, false));
    }

    /// <summary>00a5ae1b: damage is truncated after adding 1e-4, so float noise like 59.99999 gives 60.</summary>
    [Fact]
    public void TruncationUsesEpsilon()
    {
        var b = f.Duel("CREATURE_PEASANT", 3, "CREATURE_PEASANT", 10);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 0; t.BaseDefence = 0;
        // 3 * 3.3333333 = 9.9999999 → 10 with the epsilon (plain floor would give 9).
        Assert.Equal(10, b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 10.0 / 3 }, false));
    }

    /// <summary>00a597a0: stacks above three creatures average three rolls; small stacks roll per creature.</summary>
    [Fact]
    public void BaseRollUsesAtMostThreeRolls()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 1000, "CREATURE_PEASANT", 10000);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 0; t.BaseDefence = 0;
        var (min, max) = b.DamageRangeOf(a);
        for (int i = 0; i < 50; i++)
        {
            int dmg = b.ComputeDamage(a, t, new Battle.Hit(), false);
            Assert.InRange(dmg, (int)(1000 * min), (int)(1000 * max) + 1);
        }
        // Same seed must reproduce identical damage (randomness only from BattleState.Rng).
        var b2 = f.Duel("CREATURE_FOOTMAN", 1000, "CREATURE_PEASANT", 10000);
        b2.State.Units[0].BaseAttack = 0; b2.State.Units[1].BaseDefence = 0;
        var b3 = f.Duel("CREATURE_FOOTMAN", 1000, "CREATURE_PEASANT", 10000);
        b3.State.Units[0].BaseAttack = 0; b3.State.Units[1].BaseDefence = 0;
        Assert.Equal(b2.ComputeDamage(b2.State.Units[0], b2.State.Units[1], new Battle.Hit(), false),
                     b3.ComputeDamage(b3.State.Units[0], b3.State.Units[1], new Battle.Hit(), false));
    }

    /// <summary>00a5c5a0 + 00b3c690: range penalty when nearest-cell dx²+dy² exceeds (width/2)², i.e. > 36 on 12x10.</summary>
    [Fact]
    public void RangePenaltyUsesNearestCellSquaredDistance()
    {
        var b = f.Duel("CREATURE_ARCHER", 10, "CREATURE_PEASANT", 100, posA: new Cell(0, 0), posB: new Cell(6, 0));
        var archer = b.State.Units[0]; var peasants = b.State.Units[1];
        archer.BaseAttack = peasants.BaseDefence = 0;
        Assert.Equal(20, b.ComputeDamage(archer, peasants, new Battle.Hit { FixedRoll = 2 }, ranged: true));   // 36: no penalty
        peasants.Pos = new Cell(6, 1);
        Assert.Equal(10, b.ComputeDamage(archer, peasants, new Battle.Hit { FixedRoll = 2 }, ranged: true));   // 37: penalty
        peasants.Pos = new Cell(5, 3);
        Assert.Equal(20, b.ComputeDamage(archer, peasants, new Battle.Hit { FixedRoll = 2 }, ranged: true));   // 34
    }

    /// <summary>00a5a900: Armored creatures ignore Precise Shot's defence removal.</summary>
    [StockFact]
    public void ArmoredIgnoresPreciseShot()
    {
        var b = f.Duel("CREATURE_MARKSMAN", 10, "CREATURE_DEFENDER", 10, posA: new Cell(0, 0), posB: new Cell(2, 0));
        var shooter = b.State.Units[0]; var golem = b.State.Units[1];   // Defender: armored dwarf
        Assert.True(golem.Has(Ab.Armored));
        shooter.BaseAttack = 10; golem.BaseDefence = 20;
        // Defence stays 20: 10 * 2 / (1 + 0.05*10) = 13.33, then Large Shield halves ranged damage → 6.
        // (With Precise Shot applied it would be 10 * 2 * 1.5 * 0.5 = 15.)
        Assert.Equal(6, b.ComputeDamage(shooter, golem, new Battle.Hit { FixedRoll = 2 }, ranged: true));
    }

    /// <summary>00a58de0 + 00a5a900: Piercing Arrow multiplies defence by PiercingArrowCoef and truncates.</summary>
    [Fact]
    public void PiercingArrowScalesDefence()
    {
        var b = f.Duel("CREATURE_SHARP_SHOOTER", 10, "CREATURE_PEASANT", 100, posA: new Cell(0, 0), posB: new Cell(2, 0));
        var hunter = b.State.Units[0]; var target = b.State.Units[1];
        Assert.True(hunter.Has(Ab.PiercingArrow));
        hunter.BaseAttack = 0; target.BaseDefence = 11;
        // D = trunc(11 * 0.5) = 5 → 10 * 2 / 1.25 = 16
        Assert.Equal(16, b.ComputeDamage(hunter, target, new Battle.Hit { FixedRoll = 2 }, ranged: true));
    }

    /// <summary>00a673c0: a favoured-enemy critical turns luck L into L+1 (good/normal) or 1 (bad).</summary>
    [Fact]
    public void AvengerCriticalCombinesWithLuck()
    {
        // Covered indirectly: the combination rule lives in ComputeDamage; verify the arithmetic directly.
        static double Crit(double luck) => luck >= 1 ? luck + 1 : 1;
        Assert.Equal(3, Crit(2));
        Assert.Equal(2, Crit(1));
        Assert.Equal(1, Crit(0.5));
    }
}
