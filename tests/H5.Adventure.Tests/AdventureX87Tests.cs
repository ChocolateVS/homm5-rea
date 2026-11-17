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
}
