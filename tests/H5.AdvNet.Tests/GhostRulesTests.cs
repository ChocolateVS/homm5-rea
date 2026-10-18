using H5.AdvNet;
using H5.Combat;

namespace H5.AdvNet.Tests;

/// <summary>Ghost Mode formulas ported from H5_Game.exe (addresses in src/H5.AdvNet/GhostMode.cs).</summary>
public class GhostRulesTests
{
    static readonly GhostData D = new(); // stock DefaultStats values: EnergyPerLevel 1000, BaseMoveCost 200…

    [Fact]
    public void ExperienceThresholdsFollowTheTriangularFormula() // 00ca1320
    {
        Assert.Equal(0, GhostRules.ExperienceFor(D, 1));
        Assert.Equal(2000, GhostRules.ExperienceFor(D, 2));
        Assert.Equal(5000, GhostRules.ExperienceFor(D, 3));
        Assert.Equal(1000 * (30 * 31 / 2 - 1), GhostRules.ExperienceFor(D, 30));
    }

    [Fact]
    public void StepCostDividesBySpeedAndDiagonalsCostRoot2() // 00bc7b30, 00ca9a20
    {
        Assert.Equal(200, GhostRules.MoveCost(D, 1, false));
        Assert.Equal(66, GhostRules.MoveCost(D, 3, false));
        Assert.Equal(283, GhostRules.MoveCost(D, 1, true));
        Assert.Equal(93, GhostRules.MoveCost(D, 3, true));
    }

    [Fact]
    public void RevivedEnergyGrowsPerWholeMinuteOfTheEnemyTurn() // 00ca1790
    {
        Assert.Equal(2000, GhostRules.Revive(D, 59_999));
        Assert.Equal(2200, GhostRules.Revive(D, 60_000));
        Assert.Equal(2600, GhostRules.Revive(D, 3 * 60_000 + 5));
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(7)] [InlineData(13)] [InlineData(30)]
    public void HauntTakesExactlyItsLevelPerWeek(int level) // 0044dac0 + table 00f2c850
    {
        int week = Enumerable.Range(0, 7).Sum(day => GhostRules.HauntLoss(level, day));
        Assert.Equal(level, week);
    }

    [Fact]
    public void CursePenaltyIsHalfTheLevelAtLeastOne() // 00ca1580
    {
        Assert.Equal(1, GhostRules.CursePenalty(1));
        Assert.Equal(1, GhostRules.CursePenalty(3));
        Assert.Equal(5, GhostRules.CursePenalty(10));
    }

    [Fact]
    public void RevengeRoundsStochasticallyAndPossessDiceGrowWithTier() // 00ca1430, 0044db60
    {
        var rng = new Rng(7);
        // Level 10, one curse: power 2.0 → exactly 2.
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal(2, GhostRules.RevengeLevels(D, rng, 10, 1)));
        // Level 2: power 0.4 → 0 or 1.
        var r = Enumerable.Range(0, 400).Select(_ => GhostRules.RevengeLevels(D, rng, 2, 1)).ToList();
        Assert.All(r, v => Assert.InRange(v, 0, 1));
        Assert.InRange(r.Count(v => v == 1), 100, 220);
        Assert.Equal(1, GhostRules.PossessResistDice(null));
        Assert.Equal(1 + 2 + 7, GhostRules.PossessResistDice(new H5.Core.Data.CreatureDef { Tier = 7, Upgrade = true }));
    }
}
