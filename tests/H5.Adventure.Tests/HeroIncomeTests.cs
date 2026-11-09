using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Hero daily income CHero 00b4e100 and the income rebuild 0098ef10 (H5_Game.exe).</summary>
[Collection("adv")]
public class HeroIncomeTests(Fixture f)
{
    (Engine.Adventure G, HeroState H) Hero()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.Specialization = "HERO_SPEC_NONE";
        h.Perks.Clear();
        h.Artifacts.Clear();
        for (int i = 0; i < h.Army.Length; i++) h.Army[i] = null;
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        return (g, h);
    }

    [Fact]
    public void SuzerainEstatesAndGoldArtifactsPayGold()
    {
        var (g, h) = Hero();
        Assert.Equal(0, g.HeroIncome(h)[Purse.Gold]);
        h.Specialization = "HERO_SPEC_SUZERAIN";                 // vf 0x190(1): 250
        Assert.Equal(250, g.HeroIncome(h)[Purse.Gold]);
        h.Specialization = "HERO_SPEC_NONE";
        h.Perks.Add("HERO_SKILL_ESTATES");                       // skill 0x1d: 250, ×2 with the crown (vf 0x310)
        Assert.Equal(250, g.HeroIncome(h)[Purse.Gold]);
        h.Artifacts.Add("ARTIFACT_CROWN_OF_LEADER");
        Assert.Equal(500, g.HeroIncome(h)[Purse.Gold]);
        h.Perks.Clear(); h.Artifacts.Clear();
        h.Artifacts.Add("ENDLESS_SACK_OF_GOLD");                 // 00ca6680(0x1c) × 1000
        h.Artifacts.Add("ENDLESS_BAG_OF_GOLD");                  // 00ca6680(0x1d) × 250
        Assert.Equal(1250, g.HeroIncome(h)[Purse.Gold]);
        g.S.MoonWeek = "WEEK_OF_GOLD";                           // 00bcda40 per part
        Assert.Equal(2500, g.HeroIncome(h)[Purse.Gold]);
        g.S.MoonWeek = "WEEK_OF_CONJUNCTION";
        Assert.Equal(3750, g.HeroIncome(h)[Purse.Gold]);
        g.S.MoonWeek = "WEEK_OF_FESTIVALS";                      // heroes are not halved (0098ef10)
        Assert.Equal(1250, g.HeroIncome(h)[Purse.Gold]);
    }

    [Fact]
    public void TaxpayersInTheArmyPayAGoldEachDoubledForThePeasantSpecialist()
    {
        var (g, h) = Hero();
        if (!f.Core.Creatures["CREATURE_PEASANT"].Has("ABILITY_TAXPAYER")) return;
        h.Army[0] = new Stack("CREATURE_PEASANT", 40);           // ability 0x46 via 00b61ba0
        Assert.Equal(40, g.HeroIncome(h)[Purse.Gold]);
        h.Specialization = "HERO_SPEC_PEASANTS";                 // vf 0x190(4) → ×2
        Assert.Equal(80, g.HeroIncome(h)[Purse.Gold]);
    }

    [Fact]
    public void HornOfPlentyGivesTheDaysResource()
    {
        // Day counter % 7 (jump table 00b4e434): wood 2..4, ore 2..4, sulfur, gems, mercury, crystal 1..2, gold 500..750 by 25.
        var (g, h) = Hero();
        h.Artifacts.Add("ARTIFACT_HORN_OF_PLENTY");
        int[] res = { Purse.Wood, Purse.Ore, Purse.Sulfur, Purse.Gem, Purse.Mercury, Purse.Crystal, Purse.Gold };
        for (int day = 1; day <= 14; day++)
        {
            g.S.Day = day;
            Assert.All(g.HeroIncome(h).R, v => Assert.Equal(0, v));   // the display leaves the roll out
            var inc = g.HeroIncome(h, display: false);
            int r = res[(day - 1) % 7];
            for (int i = 0; i < 7; i++)
                if (i != r) Assert.Equal(0, inc[i]);
            var (lo, hi) = r switch { Purse.Wood or Purse.Ore => (2, 4), Purse.Gold => (500, 750), _ => (1, 2) };
            Assert.InRange(inc[r], lo, hi);
            if (r == Purse.Gold) Assert.Equal(0, inc[r] % 25);
        }
    }

    [Fact]
    public void FestivalsHalveEachObjectAndMoonFactorsFollowTheResource()
    {
        Assert.Equal(2, Engine.Adventure.MoonFactor(Purse.Wood, 1, "WEEK_OF_HARVEST"));
        Assert.Equal(1, Engine.Adventure.MoonFactor(Purse.Wood, 1, "WEEK_OF_JEWELS"));
        Assert.Equal(2, Engine.Adventure.MoonFactor(Purse.Gem, 1, "WEEK_OF_JEWELS"));
        Assert.Equal(2, Engine.Adventure.MoonFactor(Purse.Sulfur, 1, "WEEK_OF_ALCHEMY"));
        Assert.Equal(3, Engine.Adventure.MoonFactor(Purse.Mercury, 1, "WEEK_OF_CONJUNCTION"));
        Assert.Equal(1000, Engine.Adventure.MoonFactor(Purse.Gold, 1000, "WEEK_OF_FESTIVALS"));
    }
}
