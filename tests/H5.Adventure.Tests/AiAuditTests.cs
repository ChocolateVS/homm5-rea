using H5.Adventure.AI;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Adventure AI audit (docs/rules/AUDIT_AI.md): formulas and gates traced in H5_Game.exe.</summary>
[Collection("adv")]
public class AiAuditTests(Fixture f)
{
    [Fact]
    public void Old_target_bonus_multiplies_and_adds_twenty()
    {
        // FUN_00a78940 at 0x00a78a67: v >= 0 → ftol(v · mult) + 20; v < 0 → ftol((float)v / mult) + 20.
        Assert.Equal(1520, OriginalAdventureAI.OldTargetValue(1000, 1.5f));
        Assert.Equal(20, OriginalAdventureAI.OldTargetValue(0, 1.5f));
        Assert.Equal(-646, OriginalAdventureAI.OldTargetValue(-1000, 1.5f));
    }

    [Fact]
    public void Random_factor_only_lowers_a_positive_value()
    {
        // FUN_00a78940 at 0x00a78ab7: r = Range(1, R) + 100 − R ∈ [101 − R, 100]; v > 0 → max(1, ftol(r·v·0.01f)).
        // 0.01f is 0.0099999998, and _ftol truncates: 76·1000·0.01f = 759.99998 → 759.
        Assert.Equal(759, OriginalAdventureAI.RandomizedValue(1000, 1, 25));
        Assert.Equal(999, OriginalAdventureAI.RandomizedValue(1000, 25, 25));
        Assert.Equal(1, OriginalAdventureAI.RandomizedValue(1, 1, 25));
        // v <= 0 → ftol(v / r · 100): a negative value gets worse.
        Assert.Equal(-1315, OriginalAdventureAI.RandomizedValue(-1000, 1, 25));
        Assert.Equal(0, OriginalAdventureAI.RandomizedValue(0, 1, 25));
    }

    [Fact]
    public void Distance_discount_divides_by_cost_over_average_terrain_cost()
    {
        // FUN_00a7b630 at 0x00a7b9b0: positive candidates beyond SearchAverageTerrainCost are worth v·T/cost, ≥ 5.
        Assert.Equal(200, OriginalAdventureAI.DiscountedValue(0, 1000, 500, 100));
        Assert.Equal(1000, OriginalAdventureAI.DiscountedValue(0, 1000, 100, 100));
        Assert.Equal(5, OriginalAdventureAI.DiscountedValue(0, 10, 1000, 100));
        Assert.Equal(-100, OriginalAdventureAI.DiscountedValue(0, -100, 500, 100));
        // A positive state (attractor priority 2 → state 3) is discounted whatever its value: at least 5.
        Assert.Equal(5, OriginalAdventureAI.DiscountedValue(3, -100, 500, 100));
    }

    [Fact]
    public void Higher_state_outranks_any_value_and_ties_keep_the_first()
    {
        Assert.True(OriginalAdventureAI.Better(3, 5, 0, 1_000_000_000));
        Assert.False(OriginalAdventureAI.Better(0, 1_000_000_000, 3, 5));
        Assert.False(OriginalAdventureAI.Better(0, 100, 0, 100));
        Assert.True(OriginalAdventureAI.Better(0, 101, 0, 100));
    }

    [Fact]
    public void Weakest_hero_by_primary_stats_acts_first()
    {
        // FUN_00869770: lowest attack + defence + spell power + knowledge among unfinished heroes; ties → earlier.
        var g = f.NewGame("A2M1", 5);
        int me = g.Current.Index;
        g.S.Heroes.RemoveAll(h => h.Owner == me);
        var strong = new HeroState { Id = 9001, Owner = me, Attack = 5, Defence = 5, SpellPower = 5, Knowledge = 5 };
        var weak = new HeroState { Id = 9002, Owner = me, Attack = 1, Defence = 1, SpellPower = 1, Knowledge = 1 };
        var weakToo = new HeroState { Id = 9003, Owner = me, Attack = 0, Defence = 2, SpellPower = 1, Knowledge = 1 };
        var off = new HeroState { Id = 9004, Owner = me, AiEnabled = false };
        g.S.Heroes.AddRange(new[] { strong, weak, weakToo, off });
        var done = new HashSet<int>();
        Assert.Same(weak, OriginalAdventureAI.NextHero(g, me, done));
        done.Add(weak.Id);
        Assert.Same(weakToo, OriginalAdventureAI.NextHero(g, me, done));
        done.Add(weakToo.Id);
        Assert.Same(strong, OriginalAdventureAI.NextHero(g, me, done));
        done.Add(strong.Id);
        Assert.Null(OriginalAdventureAI.NextHero(g, me, done));
        Assert.Contains(off.Id, done);   // an AI-disabled hero is finished without acting
    }

    [Fact]
    public void Hiring_gates_follow_the_hire_phase()
    {
        // FUN_0086b0a0: heroes < max − not-for-hire; heroes < base + perTown·towns; computer heroes < global limit
        // (only checked when the player already has a hero).
        var g = f.NewGame("A2M1", 5);
        var me = g.Current;
        var st = AdventureAiStats.Load(f.Core);
        var diff = st.Difficulty[1] with { HireHeroesBase = 1, HireHeroesPerTown = 1, HireHeroesGlobalLimit = 100 };
        Assert.True(OriginalAdventureAI.HiringAllowed(g, me, 0, 0, diff));
        Assert.False(OriginalAdventureAI.HiringAllowed(g, me, 1, 0, diff));
        Assert.True(OriginalAdventureAI.HiringAllowed(g, me, 1, 1, diff));
        int computers = OriginalAdventureAI.ComputerHeroes(g);
        var capped = diff with { HireHeroesGlobalLimit = computers };
        Assert.False(OriginalAdventureAI.HiringAllowed(g, me, 1, 5, capped));
        Assert.True(OriginalAdventureAI.HiringAllowed(g, me, 0, 5, capped));
        me.HeroesNotForHire = Engine.Adventure.MaxHeroesPerPlayer - 1;
        Assert.False(OriginalAdventureAI.HiringAllowed(g, me, 1, 5, diff));
    }

    [Fact]
    public void Computer_town_growth_scales_with_the_difficulty_coefficient()
    {
        // FUN_00cd27b0: round(n·c + (week·c − round(0.01 + week·c))), x87 half-to-even, c clamped to [0, 10].
        Assert.Equal(7, Engine.Adventure.ComputerTownGrowth(7, 1.0, 3));
        Assert.Equal(1, Engine.Adventure.ComputerTownGrowth(3, 0.5, 1));   // 1.5 − 0.5 → 1
        Assert.Equal(2, Engine.Adventure.ComputerTownGrowth(3, 0.5, 2));   // 1.5 + 0 → 2 (half to even)
        Assert.Equal(2, Engine.Adventure.ComputerTownGrowth(5, 0.5, 2));   // 2.5 → 2
        Assert.Equal(2, Engine.Adventure.ComputerTownGrowth(5, 0.5, 1));   // 2.5 − 0.5 → 2
        Assert.Equal(0, Engine.Adventure.ComputerTownGrowth(5, -3, 1));
        Assert.Equal(50, Engine.Adventure.ComputerTownGrowth(5, 20, 1));
    }

    [Fact]
    public void Neutral_town_growth_is_halved_and_human_towns_are_untouched()
    {
        var g = f.NewGame("A2M1", 5, humans: 1);
        var t = g.S.Towns[0];
        int owner = t.Owner;
        t.Owner = -1;
        Assert.Equal(3, g.OwnerAdjustedGrowth(t, 7));
        var human = g.S.Players.First(p => p.Human);
        t.Owner = human.Index;
        Assert.Equal(7, g.OwnerAdjustedGrowth(t, 7));
        t.Owner = owner;
    }

    [StockFact]
    public void Easy_computer_towns_grow_at_half_rate_on_stock_data()
    {
        var g = f.NewGame("A2M1", 5);
        g.S.Difficulty = 0;
        var t = g.S.Towns.First(x => x.Owner >= 0);
        g.S.Players[t.Owner].Human = false;
        Assert.Equal(Engine.Adventure.ComputerTownGrowth(8, 0.5, g.S.Week), g.OwnerAdjustedGrowth(t, 8));
        Assert.Equal(4, g.OwnerAdjustedGrowth(t, 8));
    }
}
