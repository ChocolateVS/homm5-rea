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
}
