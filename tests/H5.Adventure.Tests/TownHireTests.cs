using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Town hiring: the recruit army (0061af30) and "hire all" (FUN_009a52a0 / CHireAllCreaturesCmd 0042b870).</summary>
[Collection("adv")]
public class TownHireTests(Fixture f)
{
    (Engine.Adventure G, TownState T) Town(int dwellingLevel)
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        g.Current.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 999999));
        for (int tier = 1; tier <= 7; tier++)
        {
            var b = $"TB_DWELLING_{tier}";
            t.Built[b] = Math.Min(dwellingLevel, g.Db.MaxLevel(t.Type, b));
            t.Available[tier - 1] = 10;
        }
        for (int i = 0; i < 7; i++) t.Garrison[i] = null;
        foreach (var h in g.S.Heroes.Where(h => h.InTown == t.Object)) h.InTown = -1;
        return (g, t);
    }

    [Fact]
    public void RecruitGoesToTheGarrisonNotTheVisitingHero()
    {
        var (g, t) = Town(1);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.InTown = t.Object;
        h.Garrisoned = false;
        int before = h.Army.Live().Sum(s => s.Count);
        Assert.True(g.Apply(new RecruitCmd(t.Object, 1, 3) { Player = g.Current.Index }).Ok);
        Assert.Equal(before, h.Army.Live().Sum(s => s.Count));
        Assert.Equal(3, t.Garrison.Live().Single().Count);
        // A garrison hero takes the creatures instead of the town army.
        h.Garrisoned = true;
        Assert.True(g.Apply(new RecruitCmd(t.Object, 1, 2) { Player = g.Current.Index }).Ok);
        Assert.Equal(before + 2, h.Army.Live().Sum(s => s.Count));
    }

    [Fact]
    public void HireAllTakesEveryDwellingFromTheTopAndBestVariantFirst()
    {
        var (g, t) = Town(2);
        var plan = g.HireAllPlan(t, g.Current.Purse, t.Garrison, null);
        Assert.Equal(plan.Select(p => p.Tier).OrderByDescending(x => x), plan.Select(p => p.Tier));
        // Rich purse, empty army: the last variant of each tier takes the whole pool (7 tiers, 7 free slots).
        Assert.Equal(7, plan.Count);
        Assert.All(plan, p => Assert.Equal(10, p.Count));
        Assert.All(plan, p => Assert.Equal(Enumerable.Range(0, 3).Last(v => g.DwellingCreature(t, p.Tier, v) != null), p.Variant));
        Assert.True(g.Apply(new HireAllCmd(t.Object) { Player = g.Current.Index }).Ok);
        Assert.All(t.Available, n => Assert.Equal(0, n));
        Assert.Equal(7, t.Garrison.StackCount());
        Assert.False(g.Apply(new HireAllCmd(t.Object) { Player = g.Current.Index }).Ok);
    }

    [Fact]
    public void HireAllRespectsSelectedColumnsMoneyAndFreeSlots()
    {
        var (g, t) = Town(2);
        // Selected base column for every tier.
        var plan = g.HireAllPlan(t, g.Current.Purse, t.Garrison, Enumerable.Repeat(0, 7).ToArray());
        Assert.All(plan, p => Assert.Equal(0, p.Variant));
        // Money: only enough gold for part of the tier 7 pool; the rest of the gold flows down the tiers.
        var top = g.DwellingCreature(t, 7, 0)!;
        var cost = g.Core.Creatures[top].Cost;
        g.Current.Purse = new Purse(Purse.Scale(cost, 3));
        plan = g.HireAllPlan(t, g.Current.Purse, t.Garrison, Enumerable.Repeat(0, 7).ToArray());
        Assert.Equal((7, 3), (plan[0].Tier, plan[0].Count));
        // Full army of other creatures: nothing new fits.
        for (int i = 0; i < 7; i++) t.Garrison[i] = new Stack("CREATURE_PEASANT_DUMMY_" + i, 1);
        g.Current.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 999999));
        Assert.Empty(g.HireAllPlan(t, g.Current.Purse, t.Garrison, null));
    }
}
