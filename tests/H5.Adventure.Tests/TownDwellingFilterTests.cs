using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>The town dwelling filter FUN_00ccf130 (CreaturesUpgradesFilter lists at CAdvMapTown +0x2e8 / +0x2f4 / +0x30c).</summary>
[Collection("adv")]
public class TownDwellingFilterTests(Fixture f)
{
    static TownState Haven(Engine.Adventure g)
    {
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Type = "TOWN_HEAVEN";
        for (int tier = 1; tier <= 7; tier++) t.Built[$"TB_DWELLING_{tier}"] = 2;
        return t;
    }

    [Fact]
    public void WithoutAFilterEveryDataVariantIsOffered()
    {
        var g = f.NewGame();
        var t = Haven(g);
        for (int tier = 1; tier <= 7; tier++)
        {
            Assert.Equal(g.Db.Building(t.Type, $"TB_DWELLING_{tier}", 1)!.Creature, g.DwellingCreature(t, tier, 0));
            Assert.Equal(g.Db.Building(t.Type, $"TB_DWELLING_{tier}", 2)!.Creature, g.DwellingCreature(t, tier, 1));
            Assert.Equal(g.Db.Building(t.Type, $"TB_DWELLING_{tier}", 2)!.Creature2, g.DwellingCreature(t, tier, 2));
        }
    }

    [Fact]
    public void FilterForbidsUpgradesByTierAndByCreature()
    {
        var g = f.NewGame();
        var t = Haven(g);
        var basic = g.DwellingCreature(t, 2, 1)!;   // first upgrade of the tier-2 base
        var alter = g.DwellingCreature(t, 3, 2)!;   // alternative upgrade of the tier-3 base
        var named = g.DwellingCreature(t, 4, 1)!;
        Assert.Equal(1, g.Core.Creatures[g.Core.Creatures[basic].BaseCreature].Upgrades.IndexOf(basic) + 1);
        Assert.Equal(2, g.Core.Creatures[g.Core.Creatures[alter].BaseCreature].Upgrades.IndexOf(alter) + 1);
        g.Map.Objects[t.Object].TownFilter = new UpgradesFilter(new HashSet<int> { 2 }, new HashSet<int> { 3 },
            new HashSet<string>(), new HashSet<string> { named });
        try
        {
        Assert.Null(g.DwellingCreature(t, 2, 1));
        Assert.NotNull(g.DwellingCreature(t, 2, 2));   // tier 2 forbids only the basic upgrade
        Assert.Null(g.DwellingCreature(t, 3, 2));
        Assert.NotNull(g.DwellingCreature(t, 3, 1));
        Assert.Null(g.DwellingCreature(t, 4, 1));
        Assert.NotNull(g.DwellingCreature(t, 4, 0));   // base creatures are always offered
        }
        finally { g.Map.Objects[t.Object].TownFilter = null; }
    }
}
