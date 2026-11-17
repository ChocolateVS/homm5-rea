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

    [Fact]
    public void TavernHeroInfoShowsTheArmyTheHireGives()
    {
        // 00636c30 case 4 → 006c44f0: the hero screen of the offered hero object, army included. Ours builds the offer on
        // a copy of the game (TavernHeroPreview), leaving the game untouched; the hire then gives that same army.
        var (g, t) = Town(1);
        t.Built["TB_TAVERN"] = 1;
        foreach (var h in g.S.Heroes.Where(h => h.Pos.Equals(g.EntranceOf(t)))) h.Alive = false;
        Assert.Equal(0, g.TownHireStatus(g.Current, t));
        var offers = g.TownTavernOffers(g.Current, t);
        Assert.NotNull(offers[0]);
        int heroes = g.S.Heroes.Count;
        var sim = g.Simulation(H5.Adventure.AI.StateCopy.Of(g.S));
        var preview = sim.TavernHeroPreview(offers[0]!, g.Current.Index, g.EntranceOf(t));
        Assert.Equal(heroes, g.S.Heroes.Count);
        Assert.Contains(preview.Army, s => s is { Count: > 0 });
        Assert.True(g.Apply(new HireHeroCmd(t.Object, 0) { Player = g.Current.Index }).Ok);
        var hired = g.S.Heroes.Last(h => h.DefId == offers[0]);
        Assert.Equal(preview.Army.Select(s => (s?.Creature, s?.Count)), hired.Army.Select(s => (s?.Creature, s?.Count)));
    }
    (Engine.Adventure G, TownState T, HeroState Visitor) VisitedTown()
    {
        var (g, t) = Town(1);
        t.Built["TB_TAVERN"] = 1;
        var gate = g.EntranceOf(t);
        var v = g.S.Heroes.First(h => h.Alive && h.Owner == g.Current.Index);
        foreach (var h in g.S.Heroes.Where(h => h != v && (h.Pos.Equals(gate) || h.InTown == t.Object))) h.Alive = false;
        v.Pos = gate;
        v.InTown = t.Object;
        v.Garrisoned = false;
        return (g, t, v);
    }

    [Fact]
    public void HireTakesTheGarrisonSlotWhenAHeroVisits()
    {
        // 004f5310 → 004f41b0: the visiting slot (town vf 0x18(0)) is taken → the garrison slot (vf 0x18(1)); 004f4200
        // (slot 1, force) merges the garrison army into the hired hero. Both taken → status 4 and the command fails.
        var (g, t, v) = VisitedTown();
        t.Garrison[0] = new Stack(g.Core.Creatures.Values.First(c => c.Power > 0).Id, 5);
        string garrisonCreature = t.Garrison[0]!.Creature;
        Assert.Equal(1, g.HireTownSlot(g.Current, t));
        Assert.Equal(0, g.TownHireStatus(g.Current, t));
        var offer = g.TownTavernOffers(g.Current, t)[0]!;
        Assert.True(g.Apply(new HireHeroCmd(t.Object, 0) { Player = g.Current.Index }).Ok);
        var hired = g.S.Heroes.Last(h => h.DefId == offer);
        Assert.True(hired.Garrisoned);
        Assert.Equal(t.Object, hired.InTown);
        Assert.False(v.Garrisoned);
        Assert.All(t.Garrison, s => Assert.Null(s));
        Assert.Contains(hired.Army.Live(), s => s.Creature == garrisonCreature);
        Assert.Equal(-1, g.HireTownSlot(g.Current, t));
        Assert.Equal(4, g.TownHireStatus(g.Current, t));
        Assert.False(g.Apply(new HireHeroCmd(t.Object, 1) { Player = g.Current.Index }).Ok);
    }

    [Fact]
    public void ComputerPlayerSkipsTheGarrisonSlotOfABlockedTown()
    {
        // 004f41b0: with player vf 0x18c (computer) and town vf 0x114 (BlockTownGarrisonForAI) the garrison slot is not used.
        var (g, t, _) = VisitedTown();
        t.AiGarrisonBlocked = true;
        g.Current.Human = true;
        Assert.Equal(1, g.HireTownSlot(g.Current, t));
        g.Current.Human = false;
        Assert.Equal(-1, g.HireTownSlot(g.Current, t));
        Assert.Equal(4, g.TownHireStatus(g.Current, t));
        t.AiGarrisonBlocked = false;
        Assert.Equal(1, g.HireTownSlot(g.Current, t));
    }

    [Fact]
    public void ForcedGarrisonMergeKeepsTheStrongestStacksThatFit()
    {
        // 004f4200 force → 0045af10: garrison stacks by stack value (004e9b90) highest first; the hero's own creatures
        // join for free, the first (free slots) of the rest are kept, the others dropped; then all are moved (0045a2c0).
        var (g, t, _) = VisitedTown();
        var offer = g.TownTavernOffers(g.Current, t)[0]!;
        var preview = g.Simulation(H5.Adventure.AI.StateCopy.Of(g.S)).TavernHeroPreview(offer, g.Current.Index, g.EntranceOf(t));
        var own = preview.Army.Live().Select(s => s.Creature).ToHashSet();
        var others = g.Core.Creatures.Values.Where(c => c.Power > 0 && !own.Contains(c.Id)).Select(c => c.Id).Take(7).ToArray();
        Assert.Equal(7, others.Length);
        for (int i = 0; i < 7; i++) t.Garrison[i] = new Stack(others[i], i + 1);
        var st = H5.Adventure.AI.AdventureAiStats.Load(g.Core);
        // The hero's duplicate stacks are combined first (0045a900), so his free slots are 7 − his creature kinds.
        int free = 7 - own.Count;
        var kept = Enumerable.Range(0, 7)
            .OrderByDescending(i => (double)H5.Adventure.AI.OriginalAdventureAI.StackValue(g, st, t.Garrison[i]!, preview))
            .Take(free).Select(i => others[i]).ToHashSet();
        Assert.True(free < 7);
        Assert.True(g.Apply(new HireHeroCmd(t.Object, 0) { Player = g.Current.Index }).Ok);
        var hired = g.S.Heroes.Last(h => h.DefId == offer);
        var got = hired.Army.Live().Select(s => s.Creature).Where(c => !own.Contains(c)).ToHashSet();
        Assert.Equal(kept.OrderBy(x => x), got.OrderBy(x => x));
        Assert.All(t.Garrison, s => Assert.Null(s));
    }
}
