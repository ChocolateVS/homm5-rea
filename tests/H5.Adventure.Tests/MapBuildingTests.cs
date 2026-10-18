using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Creature upgrades (town / Hill Fort / regrade) priced as in H5_Game.exe (FUN_004bb570, 008b4360, 004bbce0).</summary>
[Collection("adv")]
public class MapBuildingTests(Fixture f)
{
    static Resources Diff(Resources a, Resources b) => new(a.Wood - b.Wood, a.Ore - b.Ore, a.Mercury - b.Mercury,
        a.Crystal - b.Crystal, a.Sulfur - b.Sulfur, a.Gem - b.Gem, a.Gold - b.Gold);

    [Fact]
    public void UpgradeQuotesMatchTheExe()
    {
        var g = f.NewGame();
        var c = f.Db.Core.Creatures;
        // Town: (upgraded - base) × count.
        var (t, cost) = g.UpgradeQuote("CREATURE_FOOTMAN", 1, 10, hillFort: false);
        Assert.Equal("CREATURE_SWORDSMAN", t);
        Assert.Equal(Purse.Scale(Diff(c["CREATURE_SWORDSMAN"].Cost, c["CREATURE_FOOTMAN"].Cost), 10), cost);
        // Hill Fort, tier 3: factor 1.
        Assert.Equal(cost, g.UpgradeQuote("CREATURE_FOOTMAN", 1, 10, hillFort: true).Cost);
        // Tier 1 upgrades for free at a Hill Fort; tier 2 for half; tier 7 for double.
        var peasant = c.Values.First(x => x.Tier == 1 && !x.Upgrade && x.Upgrades.Count > 0);
        Assert.Equal(0, g.UpgradeQuote(peasant.Id, 1, 50, true).Cost.Gold);
        var t2 = c.Values.First(x => x.Tier == 2 && !x.Upgrade && x.Upgrades.Count > 0);
        int d2 = c[t2.Upgrades[0]].Cost.Gold - t2.Cost.Gold;
        Assert.Equal((int)MathF.Round(d2 * 3 * 0.5f, MidpointRounding.ToEven), g.UpgradeQuote(t2.Id, 1, 3, true).Cost.Gold);
        var t7 = c.Values.First(x => x.Tier == 7 && !x.Upgrade && x.Upgrades.Count > 0);
        Assert.Equal(2 * (c[t7.Upgrades[0]].Cost.Gold - t7.Cost.Gold), g.UpgradeQuote(t7.Id, 1, 1, true).Cost.Gold);
        // Regrade Squire -> Vindicator: ROUND(target cost × RegradeCostCoef 0.1) × count.
        var (rt, rc) = g.UpgradeQuote("CREATURE_SWORDSMAN", 2, 4, false);
        Assert.Equal("CREATURE_VINDICATOR", rt);
        Assert.Equal(4 * (int)MathF.Round(c["CREATURE_VINDICATOR"].Cost.Gold * 0.1f, MidpointRounding.ToEven), rc.Gold);
        // Upgrading to itself is not an upgrade.
        Assert.Null(g.UpgradeQuote("CREATURE_SWORDSMAN", 1, 4, false).Target);
    }

    [Fact]
    public void TownUpgradeNeedsTheUpgradedDwelling()
    {
        var g = f.NewGame();
        var p = g.Current;
        var town = g.S.Towns.First(x => x.Owner == p.Index);
        var basic = f.Db.Core.Creatures.Values.First(x => x.Town == town.Type && x.Tier == 1 && !x.Upgrade && x.Upgrades.Count > 0);
        foreach (var h in g.S.Heroes.Where(h => h.InTown == town.Object)) h.InTown = -1;
        town.Garrison[0] = new Stack(basic.Id, 7);
        p.Purse = new Purse(new Resources(99, 99, 99, 99, 99, 99, 99999));
        town.Built["TB_DWELLING_1"] = 1;
        Assert.False(g.Apply(new UpgradeCreatureCmd(-1, town.Object, 0, 1) { Player = p.Index }).Ok);
        town.Built["TB_DWELLING_1"] = 2;
        var target = g.DwellingCreature(town, 1, 1)!;
        int up = basic.Upgrades.IndexOf(target) + 1;
        var (_, cost) = g.UpgradeQuote(basic.Id, up, 7, false);
        var r = g.Apply(new UpgradeCreatureCmd(-1, town.Object, 0, up) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(target, town.Garrison[0]!.Creature);
        Assert.Equal(7, town.Garrison[0]!.Count);
        Assert.Equal(99999 - cost.Gold, p.Purse[Purse.Gold]);
    }
}
