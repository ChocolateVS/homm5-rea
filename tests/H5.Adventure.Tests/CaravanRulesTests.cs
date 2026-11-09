using H5.Core.Data;
using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Caravan rules (H5_Game.exe 00ce38c0 / 00ce4d40 / 00ce6110 / 0061a390).</summary>
[Collection("adv")]
public class CaravanRulesTests(Fixture f)
{
    [Fact]
    public void SpeedIsBaseMovementTimes130PercentAndDaysRoundUp()
    {
        var g = f.NewGame();
        int b = g.Db.Stats.Root.Element("adventure").Int("BaseHeroMovement", 2500);
        Assert.Equal(b * 130 / 100, g.CaravanSpeed());                // 00ce38c0
        int v = g.CaravanSpeed();
        Assert.Equal(1, g.CaravanDays(1));                             // (cost − 1) / speed + 1
        Assert.Equal(1, g.CaravanDays(v));
        Assert.Equal(2, g.CaravanDays(v + 1));
        Assert.Equal(3, g.CaravanDays(2 * v + 1));
    }

    [Fact]
    public void SourcesAreTheOwnersOtherTownsAndDwellings()
    {
        var g = f.NewGame();
        var dest = g.S.Towns.First(t => t.Owner == g.Current.Index);
        foreach (var s in g.CaravanSources(dest))
        {
            Assert.NotEqual(dest.Object, s.Object);
            Assert.True(s.Days >= 1);
            int owner = s.IsTown ? g.S.Towns.First(t => t.Object == s.Object).Owner : g.S.Objects[s.Object].Owner;
            Assert.Equal(dest.Owner, owner);
        }
    }

    [Fact]
    public void FortOpensWithADwellingBuilt()
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        for (int tier = 1; tier <= 7; tier++) t.Built[$"TB_DWELLING_{tier}"] = 0;
        bool viaCaravan = g.CaravanSources(t).Any(s => (s.IsTown ? g.S.Towns.First(x => x.Object == s.Object).Available : g.S.Objects[s.Object].Available).Any(n => n > 0));
        Assert.Equal(viaCaravan, g.CanEnterFort(t));
        t.Built["TB_DWELLING_1"] = 1;
        Assert.True(g.CanEnterFort(t));
    }
}
