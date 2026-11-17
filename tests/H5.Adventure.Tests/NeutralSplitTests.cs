using H5.Adventure.State;
using Rng = H5.Combat.Rng;
using HeroState = H5.Adventure.State.HeroState;
using MoonWeek = H5.Combat.MoonWeek;

namespace H5.Adventure.Tests;

/// <summary>
/// Neutral stack splitting at the battle start (exe FUN_00855240 from 00856460) and the hero trace level (008e28c0 with the
/// one-creature ratio 004eab10 and the thresholds of 00c030e0). Stock data: Peasant Power 41 (4100 per creature without
/// bonuses), Skeleton Power 54. The rolls are drawn from the adventure RNG; each test picks a seed whose rolls fall where
/// the case needs them.
/// </summary>
[Collection("adv")]
public class NeutralSplitTests(Fixture f)
{
    static HeroState HeroWith(string creature, int count)
    {
        var h = new HeroState { Level = 1, InTown = -1, Army = Army.Empty() };
        h.Army[0] = new Stack(creature, count);
        return h;
    }

    /// <summary>The first seed whose first rolls (Range(1, 100), Range(1, 100), Range(1, size)) satisfy the test.</summary>
    static ulong Seed(Func<Rng, bool> want)
    {
        for (ulong s = 1; ; s++) if (want(new Rng(s))) return s;
    }

    static List<(string, int)> Split(H5.Adventure.Engine.Adventure g, HeroState h, string creature, int count, ulong seed)
    {
        g.S.Rng = new Rng(seed);
        return g.SplitNeutrals(h, new List<Stack> { new(creature, count) }).Select(s => (s.Creature, s.Count)).ToList();
    }

    static (string, int) P(int n) => ("CREATURE_PEASANT", n);

    [StockFact]
    public void A_weak_hero_meets_four_stacks_and_the_middle_ones_may_be_upgraded()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        var h = HeroWith("CREATURE_PEASANT", 10);   // R = 41000 / 410000 = 0.1 < 0.5 (00f43e80) → 4
        var ups = f.Core.Creatures["CREATURE_PEASANT"].Upgrades;
        Assert.NotEmpty(ups);
        // Roll 31..69 keeps 4; the upgrade roll ≥ 50 keeps the base creature: 100 / 4 = 25 each.
        ulong s = Seed(r => r.Range(1, 100) is > 30 and < 70 && r.Range(1, 100) >= 50);
        Assert.Equal(new[] { P(25), P(25), P(25), P(25) }, Split(g, h, "CREATURE_PEASANT", 100, s));
        // The upgrade roll < 50: Upgrades[Range(1, size) − 1] for stacks 1 and 2 (0 < i < n − 1, 00855240).
        s = Seed(r => r.Range(1, 100) is > 30 and < 70 && r.Range(1, 100) < 50);
        var rr = new Rng(s); rr.Range(1, 100); rr.Range(1, 100);
        string u = ups[rr.Range(1, ups.Count) - 1];
        // 102 = 4 · 25 + 2: the first two stacks get one more.
        Assert.Equal(new[] { P(26), (u, 26), (u, 25), P(25) }, Split(g, h, "CREATURE_PEASANT", 102, s));
    }

    [StockFact]
    public void The_count_roll_moves_by_one_but_not_past_the_maximum()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        var weak = HeroWith("CREATURE_PEASANT", 10);       // base 4
        var mid = HeroWith("CREATURE_PEASANT", 70);        // R = 0.7: 0.5 ≤ R < 1.0 (00f43e78) → 3
        var strong = HeroWith("CREATURE_PEASANT", 200);    // R = 2 → 2
        ulong low = Seed(r => r.Range(1, 100) <= 30 && r.Range(1, 100) >= 50);
        ulong high = Seed(r => r.Range(1, 100) >= 70 && r.Range(1, 100) >= 50);
        ulong keep = Seed(r => r.Range(1, 100) is > 30 and < 70 && r.Range(1, 100) >= 50);
        Assert.Equal(new[] { P(34), P(33), P(33) }, Split(g, weak, "CREATURE_PEASANT", 100, low));      // 4 − 1
        Assert.Equal(4, Split(g, weak, "CREATURE_PEASANT", 100, high).Count);                          // already the maximum
        Assert.Equal(3, Split(g, mid, "CREATURE_PEASANT", 100, keep).Count);
        Assert.Equal(4, Split(g, mid, "CREATURE_PEASANT", 100, high).Count);
        Assert.Equal(2, Split(g, mid, "CREATURE_PEASANT", 100, low).Count);
        Assert.Equal(new[] { P(50), P(50) }, Split(g, strong, "CREATURE_PEASANT", 100, keep));
        Assert.Equal(new[] { P(100) }, Split(g, strong, "CREATURE_PEASANT", 100, low));                // 2 − 1
        Assert.Equal(3, Split(g, strong, "CREATURE_PEASANT", 100, high).Count);
    }

    [StockFact]
    public void A_ratio_of_exactly_one_half_is_not_below_the_first_threshold()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        var h = HeroWith("CREATURE_PEASANT", 50);   // 205000 / 410000 = 0.5 exactly: FCOM not below → 3
        ulong keep = Seed(r => r.Range(1, 100) is > 30 and < 70 && r.Range(1, 100) >= 50);
        Assert.Equal(3, Split(g, h, "CREATURE_PEASANT", 100, keep).Count);
    }

    [StockFact]
    public void Two_stacks_or_fewer_creatures_than_stacks()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        var h = HeroWith("CREATURE_PEASANT", 10);
        ulong keep = Seed(r => r.Range(1, 100) is > 30 and < 70);
        // Count clamp: 2 creatures → 2 stacks of one; n = 2 draws no upgrade roll.
        g.S.Rng = new Rng(keep);
        Assert.Equal(new[] { P(1), P(1) }, g.SplitNeutrals(h, new List<Stack> { new("CREATURE_PEASANT", 2) }).Select(s => (s.Creature, s.Count)));
        var after = new Rng(keep); after.Range(1, 100);
        Assert.Equal(after.NextU64(), g.S.Rng.NextU64());
        // Two live stacks are kept as they are, nothing drawn.
        g.S.Rng = new Rng(keep);
        var two = new List<Stack> { new("CREATURE_PEASANT", 7), new("CREATURE_SKELETON", 5) };
        Assert.Equal(new[] { P(7), ("CREATURE_SKELETON", 5) }, g.SplitNeutrals(h, two).Select(s => (s.Creature, s.Count)));
        Assert.Equal(new Rng(keep).NextU64(), g.S.Rng.NextU64());
    }

    [StockFact]
    public void Skeletons_against_different_heroes()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        ulong keep = Seed(r => r.Range(1, 100) is > 30 and < 70 && r.Range(1, 100) >= 50);
        // 60 skeletons = 60 · 5400 = 324000.
        Assert.Equal(4, Split(g, HeroWith("CREATURE_PEASANT", 39), "CREATURE_SKELETON", 60, keep).Count);   // 159900 / 324000 < 0.5
        Assert.Equal(3, Split(g, HeroWith("CREATURE_PEASANT", 40), "CREATURE_SKELETON", 60, keep).Count);   // 164000 / 324000 ≥ 0.5
        Assert.Equal(3, Split(g, HeroWith("CREATURE_PEASANT", 79), "CREATURE_SKELETON", 60, keep).Count);   // 323900 / 324000 < 1
        Assert.Equal(2, Split(g, HeroWith("CREATURE_SKELETON", 60), "CREATURE_SKELETON", 60, keep).Count);  // R = 1
    }

    [StockFact]
    public void The_hero_trace_level_counts_the_army_in_peasants()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        // 004eab10: Σ / ftol(max(41, 1) · 100.0) = Σ / 4100; 00c030e0: > 1600 → 2, > 1100 → 1, > 0 → 0.
        Assert.Equal(1100f, g.ArmyPowerPerCreature(HeroWith("CREATURE_PEASANT", 1100), "CREATURE_PEASANT"));
        Assert.Equal(0, g.HeroTrace(HeroWith("CREATURE_PEASANT", 1100)));
        Assert.Equal(1, g.HeroTrace(HeroWith("CREATURE_PEASANT", 1101)));
        Assert.Equal(1, g.HeroTrace(HeroWith("CREATURE_PEASANT", 1600)));
        Assert.Equal(2, g.HeroTrace(HeroWith("CREATURE_PEASANT", 1601)));
        Assert.Equal(0, g.HeroTrace(HeroWith("CREATURE_SKELETON", 1)));   // 5400 / 4100
        Assert.Equal(-1, g.HeroTrace(new HeroState { Level = 1, InTown = -1, Army = Army.Empty() }));
        // No creature record: Σ / 1.
        Assert.Equal(4100f, g.ArmyPowerPerCreature(HeroWith("CREATURE_PEASANT", 1), "CREATURE_NO_SUCH"));
    }
}
