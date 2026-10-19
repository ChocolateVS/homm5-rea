using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Resource exchange and artifact selling, against the tables in H5_Game.exe (00dc64a0..).</summary>
[Collection("adv")]
public class MarketTests(Fixture f)
{
    [Fact]
    public void RatesMatchTheExeTables()
    {
        Assert.Equal(new[] { 10, 10, 20, 20, 20, 20, 25 }, Engine.Adventure.MarketRates(Purse.Wood, 1));
        Assert.Equal(new[] { 2, 2, 4, 4, 4, 4, 120 }, Engine.Adventure.MarketRates(Purse.Ore, 9));
        Assert.Equal(new[] { 3, 3, 5, 5, 5, 5, 100 }, Engine.Adventure.MarketRates(Purse.Crystal, 3));
        Assert.Equal(new[] { 1250, 1250, 2500, 2500, 2500, 2500, 0 }, Engine.Adventure.MarketRates(Purse.Gold, 3));
        // More than nine marketplaces trade like nine; none means no trading.
        Assert.Equal(Engine.Adventure.MarketRates(Purse.Gem, 9), Engine.Adventure.MarketRates(Purse.Gem, 14));
        Assert.All(Engine.Adventure.MarketRates(Purse.Wood, 0), r => Assert.Equal(0, r));
    }

    [Fact]
    public void QuotesFollowTheExchangeCommand()
    {
        // 1 marketplace: 25 wood buy 2 ore at 10:1 (5 wood kept back), 3 wood sell for 75 gold.
        Assert.Equal((2, 20), Engine.Adventure.MarketQuote(Purse.Wood, Purse.Ore, 25, 1));
        Assert.Equal((75, 3), Engine.Adventure.MarketQuote(Purse.Wood, Purse.Gold, 3, 1));
        // Gold buys rare resources at 5000 each with one marketplace.
        Assert.Equal((2, 10000), Engine.Adventure.MarketQuote(Purse.Gold, Purse.Sulfur, 12000, 1));
        Assert.Equal((0, 0), Engine.Adventure.MarketQuote(Purse.Gold, Purse.Gold, 100, 1));
        Assert.Equal((0, 0), Engine.Adventure.MarketQuote(Purse.Mercury, Purse.Gem, 9, 1));
    }

    [Fact]
    public void ExchangeNeedsAMarketplaceAndMovesResources()
    {
        var g = f.NewGame();
        var p = g.Current;
        foreach (var t in g.S.Towns.Where(t => t.Owner == p.Index)) t.Built.Remove("TB_MARKETPLACE");
        p.Purse = new Purse(new Resources(30, 30, 10, 10, 10, 10, 20000));
        Assert.False(g.Apply(new ExchangeResourcesCmd(Purse.Wood, Purse.Gold, 10) { Player = p.Index }).Ok);

        var town = g.S.Towns.First(t => t.Owner == p.Index);
        town.Built["TB_MARKETPLACE"] = 1;
        int n = g.MarketplaceCount(p);
        Assert.InRange(n, 1, 2);
        var (received, cost) = Engine.Adventure.MarketQuote(Purse.Wood, Purse.Gold, 10, n);
        var r = g.Apply(new ExchangeResourcesCmd(Purse.Wood, Purse.Gold, 10) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(30 - cost, p.Purse[Purse.Wood]);
        Assert.Equal(20000 + received, p.Purse[Purse.Gold]);
        Assert.False(g.Apply(new ExchangeResourcesCmd(Purse.Wood, Purse.Wood, 1) { Player = p.Index }).Ok);
        Assert.False(g.Apply(new ExchangeResourcesCmd(Purse.Gem, Purse.Gold, 999) { Player = p.Index }).Ok);
    }

    [StockFact]
    public void ArtifactsSellForHalfTheirGoldCost()
    {
        var g = f.NewGame();
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var town = g.S.Towns.First(t => t.Owner == p.Index);
        var art = f.Db.Core.Artifacts.Values.First(a => a.CostOfGold > 0);
        h.Artifacts.Add(art.Id);
        h.InTown = -1;
        Assert.False(g.Apply(new SellArtifactCmd(h.Id, art.Id) { Player = p.Index }).Ok);
        h.InTown = town.Object;
        town.Built["TB_MARKETPLACE"] = 1;
        int gold = p.Purse[Purse.Gold];
        var r = g.Apply(new SellArtifactCmd(h.Id, art.Id) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(gold + (int)Math.Round(art.CostOfGold * 0.5, MidpointRounding.ToEven), p.Purse[Purse.Gold]);
        Assert.DoesNotContain(art.Id, h.Artifacts);
    }
}
