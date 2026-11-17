using H5.Adventure.AI;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// The AI's own-town visit and the walk to a remembered target (docs/rules/AUDIT_AI.md): the town provider sends a town
/// to the visit simulation, whose enter-town command runs the AI's town stay with its purchase; the purchase buys into
/// the hero; a hero's tavern morale is part of its army's base and of the stack value, and they cancel; a remembered
/// target is walked to and forgotten when the walk reaches it today.
/// </summary>
[Collection("adv")]
public class AiTownVisitTests(Fixture f)
{
    static (Engine.Adventure G, HeroState H, TownState T) HeroOutsideOwnTown(Fixture f)
    {
        var g = f.NewGame("A2M13", 1);
        int me = g.Current.Index;
        var t = g.S.Towns.First(x => x.Owner == me);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == me && !x.Garrisoned);
        if (h.InTown >= 0)
        {
            // Step off the town onto a free neighbour of the entrance.
            var e = g.EntranceOf(t);
            var off = g.ReachableCosts(h, 400).Keys.First(x => x != e && x != h.Pos && g.TownAt(x) == null && Math.Max(Math.Abs(x.X - e.X), Math.Abs(x.Y - e.Y)) <= 2);
            Assert.True(g.Apply(new MoveHeroCmd(h.Id, off) { Player = me }).Ok);
        }
        Assert.True(h.InTown < 0);
        t.Built["TB_DWELLING_1"] = Math.Max(1, t.Level("TB_DWELLING_1"));
        t.Built["TB_TAVERN"] = Math.Max(1, t.Level("TB_TAVERN"));
        t.Available[0] = 30;
        g.Current.Purse[Purse.Gold] = 50000;
        return (g, h, t);
    }

    [Fact]
    public void The_ai_purchase_hires_into_the_buyer_hero()
    {
        // 00cde4c0 → 00cdd630 → 00cdd180: the bought stacks go to the buyer's army (the hero), not the town screen's
        // garrison slot.
        var (g, h, t) = HeroOutsideOwnTown(f);
        int me = g.Current.Index;
        Assert.True(g.Apply(new MoveHeroCmd(h.Id, g.EntranceOf(t)) { Player = me }).Ok);
        Assert.Equal(t.Object, h.InTown);
        string creature = g.DwellingCreature(t, 1, 0)!;
        int before = h.Army.Live().Where(s => s.Creature == creature).Sum(s => s.Count);
        int garrison = t.Garrison.Live().Sum(s => s.Count);
        Assert.True(g.Apply(new RecruitCmd(t.Object, 1, 5, 0, h.Id) { Player = me }).Ok);
        Assert.Equal(before + 5, h.Army.Live().Where(s => s.Creature == creature).Sum(s => s.Count));
        Assert.Equal(garrison, t.Garrison.Live().Sum(s => s.Count));
        // A hero outside the town cannot be the buyer.
        var other = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == me && x.Id != h.Id && x.InTown != t.Object);
        if (other != null) Assert.False(g.Apply(new RecruitCmd(t.Object, 1, 1, 0, other.Id) { Player = me }).Ok);
    }

    [Fact]
    public void An_own_town_is_valued_by_the_purchase_of_the_simulated_visit()
    {
        // Town provider (vf 0x6c → +0xac: vf 4 = 00a332c0 false) → visit simulation 00bc5480 → town visit 00cd1010 →
        // enter-town command 0042ce30 → 009c4710 (computer owner, player vf 0x18c) → the purchase 00cde4c0. Before, the
        // visit bought nothing and an own town was worth ≤ 0, so heroes never came back to recruit.
        var (g, h, t) = HeroOutsideOwnTown(f);
        var ai = new OriginalAdventureAI();
        var townObj = g.Map.Objects[t.Object];
        string hash = g.S.ComputeHash();
        var (state, value) = ai.SimulatedVisitForTest(g, h, townObj);
        Assert.Equal(hash, g.S.ComputeHash());
        Assert.Equal(0, state);
        Assert.True(value > 0, $"value {value}");
    }

    [Fact]
    public void The_tavern_morale_of_a_hero_in_town_cancels_in_the_stack_value()
    {
        // 0045b7b0: a hero's army morale base (+0x28) gets + 1 when the hero's town (vf 0x54 = +0x17c) has a tavern
        // (vf 0x34(4)); the stack value 004e9b90 subtracts 1 for the same tavern (004e9dcb). Ours applied only the − 1
        // (base taken as 0), so a hero entering an own town lost value.
        var (g, h, t) = HeroOutsideOwnTown(f);
        Assert.True(t.Level("TB_TAVERN") > 0);
        var s = h.Army.Live().First(x => g.Core.Creatures.TryGetValue(x.Creature, out var c) && !c.Has("ABILITY_UNDEAD") && !c.Has("ABILITY_MECHANICAL"));
        long outside = g.StackPower(s, h);
        h.InTown = t.Object;
        Assert.Equal(outside, g.StackPower(s, h));
    }

    [Fact]
    public void A_remembered_target_is_walked_to_and_forgotten_when_reached_today()
    {
        // 0086b400 → 00868f70: with a valid remembered target not yet reached the hero walks its route without a search;
        // when the route ends on the target and the move points cover every step (hero vf 0x118 = 00b4d030 ≥ nodes − 1)
        // the target is forgotten with the move (0086e810 → 0042bc00). A teammate's tile (never stepped on) was kept as the
        // target and searched again with the old-target bonus: the hired hero "met" its teammate eight times a turn.
        var (g, h, _) = HeroOutsideOwnTown(f);
        var ai = new OriginalAdventureAI();
        var near = g.ReachableCosts(h, 400).First(kv => kv.Key != h.Pos).Key;
        var walk = ai.RememberedRouteForTest(g, h, near);
        Assert.NotNull(walk);
        Assert.True(walk!.Value.Today);
        h.MovePoints = 0;
        Assert.False(ai.RememberedRouteForTest(g, h, near)!.Value.Today);
        Assert.Null(ai.RememberedRouteForTest(g, h, h.Pos));
    }
    [Fact]
    public void The_ai_build_waits_only_for_the_town_build_counter()
    {
        // 009c5650: a town blocked by vf 0x9c = 00cce4e0 (counter +0x80 below the period 00ccedf0) only counts; the
        // counter is the engine's (00cd6460 / 00cce2b0 / 00cd4a40). Ours also required (day − 1) % period = 0.
        var g = f.NewGame("A2M13", 1);
        g.S.Difficulty = 0;
        int me = g.Current.Index;
        var t = g.S.Towns.First(x => x.Owner == me);
        int period = g.TownBuildPeriod(t);
        Assert.True(period > 1);
        g.S.Day = 2;
        t.BuildCounter = period;
        t.BuiltToday = false;
        var p = g.Current.Purse;
        for (int i = 0; i < 7; i++) p.R[i] = i == Purse.Gold ? 100000 : 100;
        int dev = t.DevLevel;
        new OriginalAdventureAI().PlayTurn(g, 5);
        Assert.True(t.DevLevel > dev);
    }
}
