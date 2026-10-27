using H5.Adventure.AI;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Instant Travel in the AI path search and step decision (docs/rules/AUDIT_AI.md "Adventure spells").</summary>
[Collection("adv")]
public class AiSpellTests(Fixture f)
{
    static FloodNode N(int x, int cost, bool landing = false) => new(new Tile(x, 0, 0), cost, cost, true, false, false, landing, landing ? 1 : 0);

    static (OriginalAdventureAI.DimDoorStepKind, int) Step(List<FloodNode> route, int moveCost, int count, int daily = 1500) =>
        OriginalAdventureAI.DimensionDoorStep(route, moveCost, count, daily, 0, _ => true);

    [Fact]
    public void Route_with_a_landing_walks_to_the_origin_then_casts()
    {
        // 008675f0: no candidate from the hero's node but a landing (w1 0x4000000) on the route → walk to the last
        // node before it; at the origin the landing is a candidate (the route reached it) → cast.
        var walk = new List<FloodNode> { N(0, 0), N(1, 100), N(10, 100, landing: true), N(11, 200) };
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Walk, 1), Step(walk, 750, 0));
        var cast = new List<FloodNode> { N(0, 0), N(5, 0, landing: true), N(6, 100), N(20, 1500) };
        // The farthest node within dx² + dy² < 65 after the landing wins.
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Cast, 2), Step(cast, 750, 0));
    }

    [Fact]
    public void Shortcut_needs_a_saving_over_the_count_threshold()
    {
        // Table 00f44124 = {−1, 200, 100, 50} % of the day's movement by min(count, 3); saving = cost(j) − move cost −
        // (cost(start) + 200). 300 per step: from the hero, node 8 saves 2400 − 750 − 200 = 1450.
        var route = Enumerable.Range(0, 11).Select(i => N(i, 300 * i)).ToList();
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Cast, 8), Step(route, 750, 3));   // 1450 > 750
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Normal, 0), Step(route, 750, 2)); // 1450 ≤ 1500
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Normal, 0), Step(route, 750, 0)); // −1 %: never a shortcut
    }

    [Fact]
    public void A_later_start_with_a_better_saving_is_walked_to_first()
    {
        // First step 100, then 300 each: from node 0 the best saving is node 8 (2200 − 950 = 1250); from node 1,
        // node 9 saves 2500 − 750 − 300 = 1450 > 1250 → walk to node 1.
        var route = Enumerable.Range(0, 12).Select(i => N(i, i == 0 ? 0 : 100 + 300 * (i - 1))).ToList();
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Walk, 1), Step(route, 750, 3));
    }

    [Fact]
    public void Settings_follow_ceb190()
    {
        var g = f.NewGame();
        var st = AdventureAiStats.Load(g.Core);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        int cost = g.AdventureSpellCost(Engine.Adventure.SpellDimensionDoor);
        h.Spells.Remove(Engine.Adventure.SpellDimensionDoor);
        h.Mana = 100;
        Assert.False(OriginalAdventureAI.DimensionDoorSettingsFor(g, h, st).Enabled);
        h.Spells.Add(Engine.Adventure.SpellDimensionDoor);
        var s = OriginalAdventureAI.DimensionDoorSettingsFor(g, h, st);
        Assert.True(s.Enabled);
        Assert.Equal(h.MaxMovePoints / 2, s.MoveCost);
        Assert.Equal((100 - 20) / cost, s.Count);
        Assert.Equal(st.DimDoorStartCostLimit, s.StartCostLimit);
        h.Mana = cost;                     // castable, but (mana − 20) / cost < 1 → count 0
        Assert.Equal(0, OriginalAdventureAI.DimensionDoorSettingsFor(g, h, st).Count);
    }

    [Fact]
    public void Flood_without_the_spell_matches_the_plain_flood()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var plain = g.ReachableCosts(h, 3000);
        var flood = g.SpellFlood(h, 3000, default).Costs();
        Assert.Equal(plain.OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X), flood.OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X));
    }

    [Fact]
    public void Landings_keep_the_origin_cost_and_stay_in_the_radius()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var dd = new DimensionDoorSettings(true, h.MaxMovePoints / 2, 3, 0);
        var flood = g.SpellFlood(h, 3000, dd);
        var landings = flood.Nodes.Values.Where(n => n.Landing).ToList();
        Assert.NotEmpty(landings);
        foreach (var n in landings)
        {
            var o = flood.Parent[n.Tile];
            int dx = n.Tile.X - o.X, dy = n.Tile.Y - o.Y;
            Assert.InRange(dx * dx + dy * dy, 1, 64);
            Assert.Equal(flood.Nodes[o].Cost, n.Cost);
            Assert.True(n.Priority >= flood.Nodes[o].Priority + dd.MoveCost + 500);
            Assert.True(n.Casts >= 1);
            Assert.True(g.DimensionDoorLandingOk(h, n.Tile, h.InBoat));
        }
        // Every route back from a landing reaches the hero.
        Assert.All(landings, n => Assert.NotNull(flood.Route(n.Tile)));
    }

    [Fact]
    public void Instant_travel_range_is_euclidean()
    {
        // 008652f0 case 0x32: dx² + dy² < 0x41 on the hero's floor (was 800 movement points: (5, 6) = 805 was refused).
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var p = g.S.Players.First(x => x.Index == h.Owner);
        foreach (var e in p.Explored) Array.Fill(e, true);
        int inRange = 0;
        for (int dy = -9; dy <= 9; dy++)
            for (int dx = -9; dx <= 9; dx++)
            {
                var t = new Tile(h.Pos.X + dx, h.Pos.Y + dy, h.Pos.Floor);
                if (!g.DimensionDoorLandingOk(h, t, h.InBoat)) continue;
                Assert.Equal(dx * dx + dy * dy <= 64, g.CanDimensionDoorTo(h, t));
                if (dx * dx + dy * dy <= 64 && Engine.Adventure.FlatMoveCost(h.Pos, t) > 800) inRange++;
            }
        Assert.True(inRange > 0);
    }

    [Fact]
    public void A_route_through_a_landing_from_the_hero_is_cast_with_the_adventure_command()
    {
        // Flood landing whose origin is the hero's tile → 008675f0 casts from there (008656e0 case 0x32 executes it).
        var g = f.NewGame("A2M1", 5);
        foreach (var h in g.S.Heroes.Where(x => x.Alive && !x.Garrisoned && x.Owner >= 0))
        {
            h.Spells.Add(Engine.Adventure.SpellDimensionDoor);
            h.Mana = 100;
            h.MovePoints = h.MaxMovePoints;
            var dd = new DimensionDoorSettings(true, h.MaxMovePoints / 2, 0, 0);
            var flood = g.SpellFlood(h, 3000, dd);
            var landing = flood.Nodes.Values.FirstOrDefault(n => n.Landing && flood.Parent[n.Tile] == h.Pos);
            if (!landing.Landing) continue;
            var route = flood.Route(landing.Tile)!;
            var (kind, i) = OriginalAdventureAI.DimensionDoorStep(route, dd.MoveCost, dd.Count, h.MaxMovePoints, 0,
                                                                  n => g.DimensionDoorLandingOk(h, n.Tile, n.Boat));
            Assert.Equal(OriginalAdventureAI.DimDoorStepKind.Cast, kind);
            while (g.Current.Index != h.Owner) g.Apply(new EndTurnCmd { Player = g.Current.Index });
            h.MovePoints = h.MaxMovePoints;
            var r = g.Apply(new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellDimensionDoor, route[i].Tile) { Player = h.Owner });
            Assert.True(r.Ok, r.Error);
            Assert.Equal(route[i].Tile, h.Pos);
            return;
        }
        Assert.Fail("no hero next to a blocked tile");
    }
}
