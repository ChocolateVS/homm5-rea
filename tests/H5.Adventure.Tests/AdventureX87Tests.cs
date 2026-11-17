using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Adventure float sites under H5_Game.exe's x87 control word (_PC_24 | _RC_CHOP): every FMUL / FADD chops to a float and
/// FISTP truncates. Each case gives a different integer under double arithmetic. See docs/rules/audit/adventure_x87.md.
/// </summary>
[Collection("adv")]
public class AdventureX87Tests(Fixture f)
{
    /// <summary>
    /// 00b4de99..00b4dec1: HERO_SPEC_RASH: B = trunc(chop(chop(chop(level · per) + 1.0001f) · B)). With the MMH 5.5 values
    /// (BaseHeroMovement 2800, Rash 0.0033) level 3 gives 2827.9998 → 2827; the exact product of the floats gave 2828.
    /// </summary>
    [Fact]
    public void RashMovementChopsEachStep()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Owner == g.Current.Index);
        h.Skills.Clear(); h.Perks.Clear(); h.Artifacts.Clear();
        h.InBoat = false; h.StablesThisWeek = false;
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        h.Specialization = "HERO_SPEC_RASH"; h.Level = 3;
        var bm = g.Db.Stats.Root.Element("adventure")!.Element("BaseHeroMovement")!;
        var rash = g.Core.Stats.Node("combat/HeroSpecializations/Barbarian/Rash_MovePointsIncreasePerPevel")!;
        string oldB = bm.Value, oldR = rash.Value;
        try
        {
            bm.Value = "2800"; rash.Value = "0.0033";
            Assert.Equal(2827, g.ComputeMaxMovePoints(h));
        }
        finally { bm.Value = oldB; rash.Value = oldR; }
    }

    /// <summary>
    /// 00a3490b..00a34973 (HERO_SPEC_POWER_BARGAIN): trunc(chop(clamp(chop(chop(1 − r) − chop(level · per)), 0.5, 1) · cost)).
    /// r 0.1, per 0.05, level 1, cost 20: chop(0.9) − 0.05 = 0.84999996, · 20 = 16.999999 → 16 (the double 1 − r − level · per
    /// rounded to a float gave 0.85 and 17).
    /// </summary>
    [Fact]
    public void PowerBargainCostChopsEachStep()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Owner == g.Current.Index);
        h.Skills.Clear(); h.Perks.Clear(); h.Artifacts.Clear();
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        h.Specialization = ""; h.Level = 1;
        var spell = g.Core.Spells.Keys.Where(k => k.StartsWith("SPELL_EMPOWERED_", StringComparison.Ordinal))
            .FirstOrDefault(k => g.AdventureSpellCost(h, k) == 20);
        Assert.NotNull(spell);
        var r = g.Core.Stats.Node("combat/HeroSpecializations/Warlock/PowerBargain_CostReduce")!;
        var per = g.Core.Stats.Node("combat/HeroSpecializations/Warlock/PowerBargain_CostReducePerLevel")!;
        string oldR = r.Value, oldP = per.Value;
        try
        {
            r.Value = "0.1"; per.Value = "0.05";
            h.Specialization = "HERO_SPEC_POWER_BARGAIN";
            Assert.Equal(16, g.AdventureSpellCost(h, spell!));
        }
        finally { r.Value = oldR; per.Value = oldP; }
    }

    /// <summary>FUN_00623160: the marketplace pays trunc(chop(cost · coefficient)) (a plain FISTP): an odd cost at 0.5 loses the half.</summary>
    [Fact]
    public void MarketplaceArtifactPriceTruncates()
    {
        var g = f.NewGame();
        var coef = g.Core.Stats.Node("adventure/Marketplace_ArtifactSellCostOfGoldCoef")!;
        string old = coef.Value;
        try
        {
            coef.Value = "0.5";
            var art = g.Core.Artifacts.First(a => a.Value.CostOfGold % 4 == 3);
            Assert.Equal(art.Value.CostOfGold / 2, g.ArtifactSellPrice(art.Key));   // 4m + 3 → 2m + 1 (half to even gave 2m + 2)
        }
        finally { coef.Value = old; }
    }

    /// <summary>
    /// 004ea676..004ea684: the AI army sum adds FILD qword values through the x87, so it is chopped to 24 bits:
    /// 2^24 + 1 + 2 = 16777219 chops to 16777218.
    /// </summary>
    [Fact]
    public void AiArmySumChopsAbove2To24()
    {
        Assert.Equal(16777218L, (long)H5.Core.Numerics.X87.AddExact(H5.Core.Numerics.X87.Mul(2, 1f), 16777217));
        Assert.Equal(300L, (long)H5.Core.Numerics.X87.AddExact(100, 200));
    }
}
