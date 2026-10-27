using H5.Adventure.AI;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Adventure AI part 4 (docs/rules/AUDIT_AI.md): money values, market, supplier, garrison, battle estimate.</summary>
[Collection("adv")]
public class AiPart4Tests(Fixture f)
{
    static AdventureAiStats Stats(Engine.Adventure g) => AdventureAiStats.Load(g.Core);

    [Fact]
    public void Money_values_load_their_tunables()
    {
        var g = f.NewGame("A2M1", 5);
        var st = Stats(g);
        Assert.Equal(2, st.SupplyDayIncomes);
        Assert.Equal(0.1, st.WeakMoneyForDeniedBuildings, 6);
    }

    [Fact]
    public void A_rich_player_has_every_money_value_at_one_and_a_low_average()
    {
        // FUN_00a31c20: supply far above demand scales every value by demand / supply; FUN_00a31bb0 takes the average
        // (v0..v5 / 7) before FUN_00a31d10 clamps each value to at least 1.0.
        var g = f.NewGame("A2M1", 5);
        var me = g.Current;
        me.Purse.R = Enumerable.Repeat(100_000_000, 7).ToArray();
        var m = OriginalAdventureAI.MoneyValues(g, Stats(g), me.Index);
        Assert.All(m.Values, v => Assert.Equal(1.0, v));
        Assert.True(m.Average < 0.01, $"average {m.Average}");
        for (int i = 0; i < 7; i++) Assert.Equal(unchecked(g.ExpectedIncome(me)[i] * 2 + me.Purse[i]), m.Supply[i]);
    }

    [Fact]
    public void A_poor_player_values_gold_at_least_by_its_best_creature_offer()
    {
        // FUN_00a31a10: v[gold] = max(v, value / gold cost) per offer (value = ftol(max(Power, 1) · 100) of the best
        // creature of the dwelling); with no money the supply is below the demand and nothing is scaled down.
        var g = f.NewGame("A2M1", 5);
        var me = g.Current;
        me.Purse.R = new int[7];
        var t = g.S.Towns.First(x => x.Owner == me.Index);
        t.Built["TB_DWELLING_1"] = Math.Max(1, t.Level("TB_DWELLING_1"));
        t.Available[0] = 50;
        var m = OriginalAdventureAI.MoneyValues(g, Stats(g), me.Index);
        var c = g.Core.Creatures[g.DwellingCreature(t, 1, 0)!];
        double v = (long)(Math.Max((double)c.Power, 1.0) * 100.0) / (double)c.Cost.Gold;
        Assert.True(m.Demand[Purse.Gold] >= c.Cost.Gold * 50);
        Assert.True(m.Values[Purse.Gold] >= v - 1e-9, $"gold {m.Values[Purse.Gold]} vs {v}");
        Assert.True(m.Values[Purse.Gold] > 1.0);
    }

    [Fact]
    public void Money_values_are_set_at_the_ai_day_start_and_weigh_resources()
    {
        var g = f.NewGame("A2M1", 5);
        var me = g.Current.Index;
        var st = Stats(g);
        var expect = OriginalAdventureAI.MoneyValues(g, st, me);
        new OriginalAdventureAI().PlayTurn(g, 77);
        var p = g.Player(me)!;
        Assert.NotNull(p.AiMoneyValues);
        Assert.Equal(expect.Values, p.AiMoneyValues!);
        Assert.Equal(expect.Average, p.AiMoneyAverage);
        var purse = new Purse(); purse[Purse.Gold] = 1000; purse[Purse.Wood] = 3;
        long want = (long)(p.AiMoneyValues![Purse.Gold] * 1000.0);
        want = (long)(p.AiMoneyValues[Purse.Wood] * 3.0 + want);
        Assert.Equal(want, OriginalAdventureAI.ResourceValue(g, me, purse));
    }

    static (Engine.Adventure G, PlayerState P, int Markets) MarketGame(Fixture f)
    {
        var g = f.NewGame("A2M1", 5);
        var p = g.Current;
        foreach (var t in g.S.Towns.Where(x => x.Owner == p.Index)) t.Built["TB_MARKETPLACE"] = 1;
        return (g, p, g.MarketplaceCount(p));
    }

    [Fact]
    public void Market_trade_sells_the_fewest_units_for_the_missing_gold()
    {
        // FUN_008e70d0: gold deficit d, source rate q → give (d − 1) / q + 1 units for give · q gold.
        var (g, p, m) = MarketGame(f);
        Assert.True(m >= 1);
        p.Purse.R = new[] { 100, 0, 0, 0, 0, 0, 0 };
        var cost = new[] { 0, 0, 0, 0, 0, 0, 500 };
        Assert.True(OriginalAdventureAI.MarketTrade(g, p.Index, cost));
        int q = Engine.Adventure.MarketRates(Purse.Wood, m)[Purse.Gold];
        int give = (500 - 1) / q + 1;
        Assert.Equal(100 - give, p.Purse[Purse.Wood]);
        Assert.Equal(give * q, p.Purse[Purse.Gold]);
    }

    [Fact]
    public void Market_trade_buys_whole_lots_and_fails_without_enough_to_sell()
    {
        // Non-gold target: q · d of the source, at most the surplus rounded down to whole lots; nothing left → false.
        var (g, p, m) = MarketGame(f);
        int q = Engine.Adventure.MarketRates(Purse.Gold, m)[Purse.Ore];
        p.Purse.R = new[] { 0, 0, 0, 0, 0, 0, q * 10 };
        Assert.True(OriginalAdventureAI.MarketTrade(g, p.Index, new[] { 0, 10, 0, 0, 0, 0, 0 }));
        Assert.Equal(new[] { 0, 10, 0, 0, 0, 0, 0 }, p.Purse.R);
        p.Purse.R = new[] { 0, 0, 0, 0, 0, 0, q * 3 + 1 };
        Assert.False(OriginalAdventureAI.MarketTrade(g, p.Index, new[] { 0, 10, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Market_trade_sells_the_resource_with_the_most_weighted_surplus()
    {
        // Score = weight · max(0, supply − demand) · 0.5; strict >, so the first source wins ties.
        var (g, p, m) = MarketGame(f);
        p.Purse.R = new[] { 50, 50, 0, 0, 0, 0, 0 };
        var cost = new[] { 0, 0, 0, 0, 0, 0, 100 };
        p.AiSupply = new[] { 10, 30, 0, 0, 0, 0, 0 };
        p.AiDemand = new int[7];
        Assert.True(OriginalAdventureAI.MarketTrade(g, p.Index, cost));
        Assert.Equal(50, p.Purse[Purse.Wood]);
        Assert.True(p.Purse[Purse.Ore] < 50);
        p.Purse.R = new[] { 50, 50, 0, 0, 0, 0, 0 };
        p.AiSupply = new int[7];
        Assert.True(OriginalAdventureAI.MarketTrade(g, p.Index, cost));
        Assert.True(p.Purse[Purse.Wood] < 50);
        Assert.Equal(50, p.Purse[Purse.Ore]);
    }

    [Fact]
    public void Market_trade_builds_a_marketplace_first_when_there_is_none()
    {
        // FUN_008e6d60: no marketplace → build one where the purse allows, its cost comes off before the trade.
        var g = f.NewGame("A2M1", 5);
        var p = g.Current;
        var t = g.S.Towns.First(x => x.Owner == p.Index);
        foreach (var x in g.S.Towns.Where(x => x.Owner == p.Index)) x.Built.Remove("TB_MARKETPLACE");
        Assert.Equal(Engine.BuildDenial.CanBuild, g.BuildCheck(t, "TB_MARKETPLACE", new Purse { R = Enumerable.Repeat(1_000_000, 7).ToArray() }));
        var mc = new Purse(g.Db.Building(t.Type, "TB_MARKETPLACE", 1)!.Cost).R;
        p.Purse.R = (int[])mc.Clone();
        p.Purse[Purse.Wood] += 200;
        Assert.True(OriginalAdventureAI.MarketTrade(g, p.Index, new[] { 0, 0, 0, 0, 0, 0, 1000 }));
        Assert.Equal(1, t.Level("TB_MARKETPLACE"));
        int q = Engine.Adventure.MarketRates(Purse.Wood, g.MarketplaceCount(p))[Purse.Gold];
        int give = (1000 - 1) / q + 1;
        Assert.Equal(200 - give, p.Purse[Purse.Wood]);
        Assert.Equal(give * q, p.Purse[Purse.Gold]);
    }

    static (Engine.Adventure G, HeroState Com, HeroState Sup) TwoHeroes(Fixture f)
    {
        var g = f.NewGame("A2M1", 5);
        int me = g.Current.Index;
        var com = g.S.Heroes.First(x => x.Alive && x.Owner == me);
        var sup = StateCopy.Of(g.S).Heroes.First(x => x.Id == com.Id);
        sup.Id = g.S.Heroes.Max(x => x.Id) + 1;
        sup.Pos = com.Pos with { X = com.Pos.X + 1 };
        g.S.Heroes.Add(sup);
        return (g, com, sup);
    }

    [Fact]
    public void Transfer_rule_table_decides_who_takes()
    {
        // DAT_00f7a6f0[role(a) · 4 + role(b)]: commander with its own supplier → the commander takes (rule 6);
        // two collectors → nothing (0).
        var (g, com, sup) = TwoHeroes(f);
        var st = Stats(g);
        var ai = new OriginalAdventureAI();
        ai.SetRole(com.Id, HeroRole.Commander, sup.Id);
        ai.SetRole(sup.Id, HeroRole.Supplier, com.Id);
        var sides = ai.TransferSides(g, st, com, sup);
        Assert.NotNull(sides);
        Assert.Equal(com.Id, sides!.Value.Taker.Id);
        Assert.Equal(sup.Id, sides.Value.Giver.Id);
        ai.SetRole(com.Id, HeroRole.Collector, sup.Id);
        ai.SetRole(sup.Id, HeroRole.Collector, com.Id);
        Assert.Null(ai.TransferSides(g, st, com, sup));
    }

    [Fact]
    public void Commander_takes_the_stacks_that_raise_its_estimate_and_the_giver_keeps_one_creature()
    {
        // FUN_00cde140 / FUN_00cdd180: greedy moves while the taker's estimate grows; the giver hero's last stack
        // leaves one creature behind.
        var (g, com, sup) = TwoHeroes(f);
        var st = Stats(g);
        var ai = new OriginalAdventureAI();
        ai.SetRole(com.Id, HeroRole.Commander, sup.Id);
        ai.SetRole(sup.Id, HeroRole.Supplier, com.Id);
        string c = com.Army.Live().First().Creature;
        for (int i = 0; i < 7; i++) sup.Army[i] = null;
        sup.Army[0] = new Stack(c, 40);
        int before = com.Army.Live().Where(x => x.Creature == c).Sum(x => x.Count);
        ai.HeroesMeet(g, st, com, sup);
        Assert.Equal(before + 39, com.Army.Live().Where(x => x.Creature == c).Sum(x => x.Count));
        Assert.Equal(1, sup.Army.Live().Sum(x => x.Count));
        // FUN_00c5ab30: having met its partner, the supplier is a collector again.
        Assert.Equal(HeroRole.Collector, ai.Role(sup.Id));
    }

    [Fact]
    public void Supplier_test_needs_more_than_ten_percent_for_the_commander()
    {
        // FUN_00c5af30: commander's estimate after the simulated transfer / before > 1.1; nothing changes in the game.
        var (g, com, sup) = TwoHeroes(f);
        var st = Stats(g);
        var ai = new OriginalAdventureAI();
        string c = com.Army.Live().First().Creature;
        for (int i = 0; i < 7; i++) sup.Army[i] = null;
        sup.Army[0] = new Stack(c, 500);
        string hash = g.S.ComputeHash();
        Assert.True(ai.SupplierTest(g, st, sup, com, HeroRole.Collector, HeroRole.Commander));
        Assert.Equal(hash, g.S.ComputeHash());
        sup.Army[0] = new Stack(c, 2);
        sup.Army[1] = new Stack(c, 1);
        Assert.False(ai.SupplierTest(g, st, sup, com, HeroRole.Collector, HeroRole.Commander));
        Assert.False(ai.SupplierTest(g, st, sup, com, HeroRole.Freelancer, HeroRole.Commander));
    }

    [Fact]
    public void A_hero_in_an_own_town_goes_into_the_garrison_only_while_the_flag_is_off()
    {
        // FUN_009c3f20: runs when ConsiderGarrisoningHeroToImportantTown == 0 (stock Easy / Normal), needs a second
        // map hero and no garrison hero with a higher estimate.
        var (g, h, other) = TwoHeroes(f);
        var st = Stats(g);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        Assert.True(g.Apply(new Engine.MoveHeroCmd(h.Id, g.EntranceOf(t)) { Player = h.Owner }).Ok || h.InTown == t.Object);
        if (h.InTown != t.Object) { h.Pos = g.EntranceOf(t); h.InTown = t.Object; }
        var ai = new OriginalAdventureAI();
        Assert.True(st.Difficulty[2].ConsiderGarrisoningHeroToImportantTown);
        ai.GarrisonHeroInTown(g, st, st.Difficulty[2], h, t);
        Assert.False(h.Garrisoned);
        ai.SetRole(h.Id, HeroRole.Collector, other.Id);
        ai.GarrisonHeroInTown(g, st, st.Difficulty[1], h, t);
        Assert.False(h.Garrisoned);
        ai.SetRole(h.Id, HeroRole.Commander);
        Assert.False(st.Difficulty[1].ConsiderGarrisoningHeroToImportantTown);
        ai.GarrisonHeroInTown(g, st, st.Difficulty[1], h, t);
        Assert.True(h.Garrisoned);
    }
}
