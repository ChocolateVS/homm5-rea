using H5.Adventure.AI;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Adventure AI part 5 (docs/rules/AUDIT_AI.md): the adventure facts of the combat flee decision, hero meeting
/// slots / arrangement, the garrison swap.</summary>
[Collection("adv")]
public class AiPart5Tests(Fixture f)
{
    [Fact]
    public void Flee_facts_sum_the_artifacts_and_read_experience_and_hire_price()
    {
        // FUN_00c96ea0: Σ max(004ea2f0, 00ca6410) over backpack and worn slots; hero vf 0x1a4; 00b4ced0 without a town.
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var arts = g.Core.Artifacts.Keys.OrderBy(k => k).Take(3).ToList();
        h.Artifacts.Clear(); h.Backpack.Clear();
        h.Artifacts.Add(arts[0]); h.Backpack.Add(arts[1]); h.Backpack.Add(arts[2]);
        h.Experience = 12345;
        var st = AdventureAiStats.Load(g.Core);
        long expect = arts.Sum(a => Math.Max(OriginalAdventureAI.ArtifactItemValue(g, st, a), g.Core.Artifacts[a].CostOfGold));
        var info = g.AiRetreatInfoFor(h);
        Assert.Equal(expect, info.ArtifactValue);
        Assert.Equal(12345, info.Experience);
        Assert.Equal(g.HireCost(g.Current), info.HireCost);
    }

    static AdventureAiStats Stats(Engine.Adventure g) => AdventureAiStats.Load(g.Core);

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

    static List<string> Creatures(Engine.Adventure g, bool shooter) =>
        g.Core.Creatures.Values.Where(c => c.Power > 0 && (c.Shots > 0) == shooter).Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void A_taker_with_six_stacks_swaps_its_weakest_stack_instead_of_filling_the_seventh_slot()
    {
        // FUN_00cdacd0: same creature → that slot; fewer than 6 stacks → first free slot; else FUN_00cdab70 → the
        // occupied slot with the lowest stack value (FUN_004e9b90), and the displaced stack goes back to the giver.
        var (g, com, sup) = TwoHeroes(f);
        var st = Stats(g);
        var walkers = Creatures(g, false);
        for (int i = 0; i < 7; i++) { com.Army[i] = null; sup.Army[i] = null; }
        for (int i = 0; i < 5; i++) com.Army[i] = new Stack(walkers[i], 10 + i);
        sup.Army[0] = new Stack(walkers[6], 5);
        sup.Army[1] = new Stack(walkers[7], 5);
        var p = OriginalAdventureAI.PlanMove(g, st, com, sup, 0);
        Assert.Equal(new OriginalAdventureAI.MovePlan(5, 5, false), p);
        com.Army[5] = new Stack(walkers[5], 1);
        int weakest = Enumerable.Range(0, 6).OrderBy(i => OriginalAdventureAI.StackValue(g, st, com.Army[i]!, com)).First();
        p = OriginalAdventureAI.PlanMove(g, st, com, sup, 0);
        Assert.Equal(new OriginalAdventureAI.MovePlan(weakest, 5, true), p);
        // the giver hero's last stack: a same-creature / empty slot leaves one creature, a swap moves it whole.
        sup.Army[1] = null;
        com.Army[3] = new Stack(walkers[6], 1);
        Assert.Equal(new OriginalAdventureAI.MovePlan(3, 4, false), OriginalAdventureAI.PlanMove(g, st, com, sup, 0));
    }

    [Fact]
    public void Shooter_rules_keep_a_lone_shooter_and_let_a_shooter_replace_only_shooters_when_there_are_four()
    {
        var (g, _, h) = TwoHeroes(f);
        var st = Stats(g);
        var shooters = Creatures(g, true);
        var walkers = Creatures(g, false);
        for (int i = 0; i < 7; i++) h.Army[i] = null;
        h.Army[0] = new Stack(shooters[0], 1);   // the only shooter, and the weakest stack
        for (int i = 1; i < 6; i++) h.Army[i] = new Stack(walkers[i], 1000);
        Assert.Equal(0, OriginalAdventureAI.WeakestSlot(g, st, h, true));
        Assert.NotEqual(0, OriginalAdventureAI.WeakestSlot(g, st, h, false));
        // four shooters: an incoming shooter only replaces the weakest shooter, a walker the weakest stack.
        for (int i = 0; i < 4; i++) h.Army[i] = new Stack(shooters[i], i == 3 ? 900 : 1000);
        h.Army[4] = new Stack(walkers[4], 1);
        int weakestShooter = Enumerable.Range(0, 4).OrderBy(i => OriginalAdventureAI.StackValue(g, st, h.Army[i]!, h)).First();
        Assert.Equal(4, OriginalAdventureAI.WeakestSlot(g, st, h, false));
        Assert.Equal(weakestShooter, OriginalAdventureAI.WeakestSlot(g, st, h, true));
    }

    [Fact]
    public void Arrangement_puts_shooters_on_even_slots_first_and_the_rest_in_the_free_slots()
    {
        // FUN_00cddfd0: shooters in slot order to 0, 2, 4, 6, 1, 3, 5; the others in slot order into the first free.
        var (g, _, h) = TwoHeroes(f);
        var shooters = Creatures(g, true);
        var walkers = Creatures(g, false);
        var army = new Stack?[7];
        army[0] = new Stack(walkers[0], 1);
        army[1] = new Stack(shooters[0], 1);
        army[3] = new Stack(walkers[1], 1);
        army[4] = new Stack(shooters[1], 1);
        army[6] = new Stack(shooters[2], 1);
        Assert.Equal(new[] { 1, 0, 4, 3, 6, -1, -1 }, OriginalAdventureAI.Arrangement(g, army));
        for (int i = 0; i < 5; i++) army[i] = new Stack(shooters[i], 1);
        army[5] = new Stack(walkers[0], 1);
        army[6] = new Stack(shooters[5], 1);
        Assert.Equal(new[] { 0, 4, 1, 6, 2, 5, 3 }, OriginalAdventureAI.Arrangement(g, army));
    }

    [Fact]
    public void Meeting_heroes_swap_into_the_weakest_slot_and_arrange_the_taker()
    {
        var (g, com, sup) = TwoHeroes(f);
        var st = Stats(g);
        var ai = new OriginalAdventureAI();
        ai.SetRole(com.Id, HeroRole.Commander, sup.Id);
        ai.SetRole(sup.Id, HeroRole.Supplier, com.Id);
        var walkers = Creatures(g, false);
        for (int i = 0; i < 7; i++) { com.Army[i] = null; sup.Army[i] = null; }
        for (int i = 0; i < 6; i++) com.Army[i] = new Stack(walkers[i], i == 2 ? 1 : 50);
        string strong = walkers.OrderByDescending(c => g.Core.Creatures[c].Power).First(c => !walkers.Take(6).Contains(c));
        sup.Army[0] = new Stack(strong, 50);
        sup.Army[1] = new Stack(walkers[0], 1);
        string weak = com.Army[Enumerable.Range(0, 6).OrderBy(i => OriginalAdventureAI.StackValue(g, st, com.Army[i]!, com)).First()]!.Creature;
        ai.HeroesMeet(g, st, com, sup);
        Assert.Contains(com.Army, s => s is { } x && x.Creature == strong && x.Count == 50);
        Assert.DoesNotContain(com.Army, s => s is { } x && x.Creature == weak);
        Assert.Contains(sup.Army, s => s is { } x && x.Creature == weak);
        Assert.Equal(6, com.Army.StackCount());
        Assert.Null(com.Army[6]);
    }

    [Fact]
    public void Garrisoning_a_visiting_hero_brings_the_garrison_hero_out()
    {
        // 004f58e0: the visiting hero goes into the garrison, the old garrison hero becomes the visitor.
        var (g, h, other) = TwoHeroes(f);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        foreach (var x in new[] { h, other }) { x.Pos = g.EntranceOf(t); x.InTown = t.Object; x.Garrisoned = false; }
        Array.Fill(t.Garrison, null);
        other.Garrisoned = true;
        Assert.True(g.Apply(new Engine.GarrisonHeroCmd(t.Object, h.Id, true) { Player = h.Owner }).Ok);
        Assert.True(h.Garrisoned);
        Assert.False(other.Garrisoned);
    }
}
