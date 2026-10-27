using System.Diagnostics;
using H5.Adventure.AI;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Adventure AI part 3 (docs/rules/AUDIT_AI.md): simulated visits, values, towns, threats.</summary>
[Collection("adv")]
public class AiSimulationTests(Fixture f)
{
    [Fact]
    public void State_copy_equals_the_json_clone_and_shares_nothing_mutable()
    {
        var g = f.NewGame("A2M1", 5);
        var ai = new OriginalAdventureAI();
        for (int turn = 0; turn < 10 && !g.S.Finished; turn++) ai.PlayTurn(g, 500UL + (ulong)turn);
        string before = g.S.ComputeHash();
        var copy = StateCopy.Of(g.S);
        Assert.Equal(before, copy.ComputeHash());
        Assert.Equal(g.S.Serialize(), copy.Serialize());

        // Mutate everything reachable in the copy: the original must not change.
        foreach (var p in copy.Players) { p.Purse.R[6] += 1000; foreach (var a in p.Explored) Array.Fill(a, true); p.VisitedObjects.Add(-5); }
        foreach (var h in copy.Heroes) { h.Experience += 10; foreach (var s in h.Army) if (s != null) s.Count++; h.Army[0] = null; h.Spells.Add("X"); h.Skills.Clear(); h.VisitedOnce.Add(-3); }
        foreach (var t in copy.Towns) { t.Available[0] += 3; t.Built["X"] = 1; t.Garrison[0] = null; }
        foreach (var o in copy.Objects.Values) { o.Removed = !o.Removed; o.Guards.Clear(); o.VisitedBy.Add(-1); }
        copy.Rng.NextU64();
        Assert.Equal(before, g.S.ComputeHash());
    }

    [Fact]
    public void State_copy_is_faster_than_the_json_clone()
    {
        var g = f.NewGame("A2M1", 5);
        StateCopy.Of(g.S);
        g.S.Clone();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) StateCopy.Of(g.S);
        long copy = sw.ElapsedTicks;
        sw.Restart();
        for (int i = 0; i < 20; i++) g.S.Clone();
        long json = sw.ElapsedTicks;
        Assert.True(copy * 3 < json, $"copy {copy} vs json {json} ticks");
    }

    [Fact]
    public void Town_build_list_wants_the_hall_first_then_dwellings_and_fort()
    {
        // FUN_009c33d0: hall up to the Capitol, then (nothing built yet) every level-1 dwelling, then the fort.
        var g = f.NewGame("A2M1", 5);
        var t = g.S.Towns.First(x => x.Owner >= 0);
        t.Built.Clear();
        Assert.Equal(0, OriginalAdventureAI.WantedBuilding(g, t, built: false));
        t.Built["TB_TOWN_HALL"] = g.Db.MaxLevel(t.Type, "TB_TOWN_HALL");
        int want = OriginalAdventureAI.WantedBuilding(g, t, built: false);
        Assert.InRange(want, 7, 13);
        Assert.Equal("TB_DWELLING_1", OriginalAdventureAI.BuildingName(want));
        // With something built this turn the dwellings and the fort are skipped: the marketplace comes next.
        Assert.Equal(2, OriginalAdventureAI.WantedBuilding(g, t, built: true));
    }

    [Fact]
    public void Town_build_weight_is_days_owned_times_level_and_main_town_counts_more()
    {
        // FUN_009c3a60 / FUN_00451d70: owned since the start → 336 days; main town × 2.7.
        var g = f.NewGame("A2M1", 5);
        var t = g.S.Towns.First(x => x.Owner >= 0);
        Assert.Equal(336.0 * t.DevLevel, OriginalAdventureAI.BuildWeight(g, t, false));
        Assert.Equal(336.0 * t.DevLevel * 2.7, OriginalAdventureAI.BuildWeight(g, t, true), 6);
        g.S.Day = 10;
        t.OwnedSinceDay = 7;
        Assert.Equal(3.0 * t.DevLevel, OriginalAdventureAI.BuildWeight(g, t, false));
    }

    [Fact]
    public void Resource_value_weighs_every_resource_one()
    {
        // FUN_004ea050 with the CPlayerAIInformation weights (all 1.0 from FUN_00c28ce0).
        var g = f.NewGame("A2M1", 5);
        var p = new Purse(); p[Purse.Gold] = 1000; p[Purse.Wood] = 5; p[Purse.Gem] = 2;
        Assert.Equal(1007, OriginalAdventureAI.ResourceValue(g, g.Current.Index, p));
    }

    [Fact]
    public void Days_and_state_merge_follow_the_exe_helpers()
    {
        // FUN_00ce9b00: 0 with the current move points, else (cost − cur) / max + 1.
        Assert.Equal(0, OriginalAdventureAI.DaysTo(500, 600, 1500));
        Assert.Equal(1, OriginalAdventureAI.DaysTo(700, 600, 1500));
        Assert.Equal(2, OriginalAdventureAI.DaysTo(2200, 600, 1500));
        // merge: a negative state dominates, else the larger.
        Assert.Equal(-1, OriginalAdventureAI.MergeState(3, -1));
        Assert.Equal(3, OriginalAdventureAI.MergeState(0, 3));
    }

    [Fact]
    public void Plain_stack_value_is_power_times_hundred_times_count()
    {
        // FUN_004e9b90 with no hero, no week effect and no creature artifact: every adjustment is zero.
        var g = f.NewGame("A2M1", 5);
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        var st = AdventureAiStats.Load(f.Core);
        var c = f.Core.Creatures["CREATURE_PEASANT"];
        Assert.Equal(Math.Max(c.Power, 1) * 100L * 7, OriginalAdventureAI.StackValue(g, st, new Stack("CREATURE_PEASANT", 7), null));
    }

    [Fact]
    public void Race_coefficients_divide_power_by_heroes_and_favour_races_without_heroes()
    {
        // FUN_008683f0: coef = P / H for a race with heroes, P + 1 without (both normalised by max(1, max |v|)).
        var g = f.NewGame("A2M1", 5);
        int me = g.Current.Index;
        var coef = OriginalAdventureAI.RaceCoefficients(g, me);
        var races = g.S.Heroes.Where(h => h.Alive && h.Owner == me).Select(h => h.Town).ToHashSet();
        for (int i = 0; i < coef.Length; i++)
            if (!races.Contains(HeroFilter.Races[i])) Assert.True(coef[i] >= 1.0);
    }

    [Fact]
    public void Treasure_visit_is_valued_by_simulating_it()
    {
        // 00bc5480: the visit runs on a copy of the game; its value is the position value after − before, and the real
        // game is untouched.
        var g = f.NewGame("A2M1", 5);
        var ai = new OriginalAdventureAI();
        int me = g.Current.Index;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == me && !x.Garrisoned);
        var o = g.Map.Objects.First(x => x.Kind == H5.Adventure.Map.ObjectKind.Treasure && g.S.Objects.TryGetValue(x.Index, out var s) && !s.Removed
                                         && s.ResourceKind == Purse.Gold && s.Amount > 0 && x.Shared.Type is not ("TREASURE_CHEST" or "TREASURE_CAMPFIRE"));
        string hash = g.S.ComputeHash();
        var (state, value) = ai.SimulatedVisitForTest(g, h, o);
        Assert.Equal(hash, g.S.ComputeHash());
        Assert.Equal(0, state);
        Assert.True(value >= g.S.Objects[o.Index].Amount, $"value {value}");
    }

    [Fact]
    public void Enemy_hero_near_an_own_town_is_a_threat_of_value_times_multiplier_over_days()
    {
        // FUN_0086cd10: threat = town value · ThreatsMultiplierOurs / max(1e-4, eff / MP), eff = cost on the first day.
        var g = f.NewGame("A2M1", 5);
        int me = g.Current.Index;
        var town = g.S.Towns.First(t => t.Owner == me);
        var enemy = g.S.Players.First(p => p.Alive && p.Team != g.S.Players.First(x => x.Index == me).Team);
        var eh = g.S.Heroes.First(h => h.Alive && h.Owner == enemy.Index && !h.Garrisoned);
        var gate = g.EntranceOf(town);
        var spot = Enumerable.Range(-3, 7).SelectMany(dx => Enumerable.Range(-3, 7).Select(dy => gate.Offset(dx, dy)))
            .First(t => t != gate && g.FindPath(new HeroState { Id = eh.Id, Owner = eh.Owner, Pos = t, MovePoints = 5000, MaxMovePoints = 5000 }, gate) != null
                        && !g.S.Heroes.Any(h => h.Alive && h.Pos == t));
        eh.Pos = spot;
        var ai = new OriginalAdventureAI();
        var st = AdventureAiStats.Load(f.Core);
        var diff = st.Difficulty[Math.Clamp(g.S.Difficulty, 0, 3)];
        var table = ai.CheckThreats(g, st, diff, me);
        Assert.True(table.T.ContainsKey(town.Object));
        int mp = Math.Max(100, eh.MaxMovePoints);
        int cost = g.ReachableCosts(eh, diff.ThreatsDistanceInDays * mp)[gate];
        double expected = OriginalAdventureAI.TownValue(g, st, diff, town, me) * (double)diff.ThreatsMultiplierOurs / Math.Max(1e-4, (double)cost / mp);
        Assert.Equal(expected, table.H[eh.Id].Find(town.Object)!.Threat, 6);
    }

    [Fact]
    public void Treasure_and_building_providers_give_their_values_directly()
    {
        // 00a832b0: a gold pile is worth 100 · max(1, (Min + 2 · Max) / 3) from the shared descriptor.
        // 00b01af0: Star Axis is worth ValueOfAttributeSpellPower until the hero has visited it.
        var g = f.NewGame("A2M1", 5);
        var ai = new OriginalAdventureAI();
        var st = AdventureAiStats.Load(f.Core);
        int me = g.Current.Index;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == me && !x.Garrisoned);
        var gold = g.Map.Objects.FirstOrDefault(x => x.Kind == H5.Adventure.Map.ObjectKind.Treasure && x.Shared.Type == "TREASURE_GOLD");
        if (gold != null)
            Assert.Equal((0, 100L * Math.Max(1, (gold.Shared.MinResource + 2 * gold.Shared.MaxResource) / 3)), ai.ValueForTest(g, h, gold));
        var axis = new H5.Adventure.Map.MapObject { Index = 999999, Kind = H5.Adventure.Map.ObjectKind.Building,
            Shared = new H5.Adventure.Map.SharedDef { Type = "BUILDING_STAR_AXIS" } };
        Assert.Equal((0, (long)st.ValueOfAttributeSpellPower), ai.ValueForTest(g, h, axis));
        h.VisitedOnce.Add(axis.Index);
        Assert.Equal((0, 0L), ai.ValueForTest(g, h, axis));
    }
}
