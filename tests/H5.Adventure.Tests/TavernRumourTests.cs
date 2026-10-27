using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Town tavern rumours (CRerollRumourCmd 004f4750, price RPGStats +0x54 = RumorCost) and hire status (004f4570).</summary>
[Collection("adv")]
public class TavernRumourTests(Fixture f)
{
    (Engine.Adventure G, TownState T) Town(int gold)
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Built["TB_TAVERN"] = 1;
        g.Current.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, gold));
        return (g, t);
    }

    [Fact]
    public void RumourCostIsDefaultStatsRumorCost()
    {
        var (g, _) = Town(0);
        Assert.Equal(100, g.RumourCost);
        Assert.NotEmpty(f.Db.CommonRumours);
    }

    [Fact]
    public void BuyingPaysAndAddsOneRumour()
    {
        var (g, t) = Town(250);
        Assert.True(g.Apply(new BuyRumourCmd(t.Object) { Player = g.Current.Index }).Ok);
        Assert.Equal(150, g.Current.Purse[Purse.Gold]);
        Assert.Single(t.Rumours);
        Assert.Contains(f.Db.CommonRumours, r => r.Text == t.Rumours[0]);
        Assert.True(g.Apply(new BuyRumourCmd(t.Object) { Player = g.Current.Index }).Ok);
        Assert.Equal(2, t.Rumours.Count);
    }

    [Fact]
    public void NoGoldNoRumour()
    {
        var (g, t) = Town(99);
        Assert.False(g.Apply(new BuyRumourCmd(t.Object) { Player = g.Current.Index }).Ok);
        Assert.Equal(99, g.Current.Purse[Purse.Gold]);
        Assert.Empty(t.Rumours);
    }

    [Fact]
    public void TownHireStatusOrder()
    {
        var (g, t) = Town(0);
        t.Built["TB_TAVERN"] = 0;
        Assert.Equal(2, g.TownHireStatus(g.Current, t));
        t.Built["TB_TAVERN"] = 1;
        Assert.Equal(3, g.TownHireStatus(g.Current, t));
        g.Current.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 100000));
        foreach (var h in g.S.Heroes.Where(h => h.Pos == g.EntranceOf(t)).ToList()) h.Pos = new H5.Adventure.Map.Tile(0, 0, 0);
        Assert.Equal(0, g.TownHireStatus(g.Current, t));
    }
}
