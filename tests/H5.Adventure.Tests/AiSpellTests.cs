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

    [Fact]
    public void Town_portal_starts_a_second_flood_root_at_the_portal_town()
    {
        // 00c08be0 (pf +0x54): the pf +0x98 entry (00868560: 00865170 → 00ccf6e0) is a start node with flag 0x20,
        // w2 = the hero's movement points, w3 = land day move + 200 + (1 − 1) · 500 + w2; its steps inherit 0x20 and
        // a destination reached that way casts the spell (00a7af80 command +0x31 → 0086b6d0 spell 0x33).
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index && !x.Garrisoned);
        var town = g.PortalTown(h);
        Assert.NotNull(town);
        var entrance = g.EntranceOf(town!);
        var fl = g.Map.Floor(entrance.Floor)!;
        Tile? far = null;
        for (int y = 0; y < fl.Height && far == null; y++)
            for (int x = 0; x < fl.Width; x++)
            {
                var t = new Tile(x, y, entrance.Floor);
                if (Math.Max(Math.Abs(x - entrance.X), Math.Abs(y - entrance.Y)) < 31 || fl.IsBlocked(x, y) || fl.IsWater(x, y)) continue;
                if (g.ObjectsAtTile(t).Any() || g.HeroAtTile(t) != null || g.BoatAt(t) != null) continue;
                far = t;
                break;
            }
        Assert.NotNull(far);
        h.Pos = far!.Value;
        h.MovePoints = 600;
        var off = g.SpellFlood(h, 3000, new DimensionDoorSettings(false, 0, 0, 0, false, 1500));
        Assert.False(off.Nodes.ContainsKey(entrance));
        Assert.Equal(new[] { entrance }, g.PortalStartTiles(h));
        var flood = g.SpellFlood(h, 3000, new DimensionDoorSettings(false, 0, 0, 0, true, 1500));
        var root = flood.Nodes[entrance];
        Assert.True(root.Portal);
        Assert.Equal(600, root.Cost);
        Assert.Equal(1500 + 200 + 600, root.Priority);
        Assert.Equal(h.Pos, flood.Parent[entrance]);
        var step = flood.Nodes.Values.Where(n => n.Portal && n.Tile != entrance).OrderBy(n => n.Cost).First();
        var route = flood.Route(step.Tile)!;
        Assert.Equal(h.Pos, route[0].Tile);
        Assert.Equal(entrance, route[1].Tile);
        Assert.True(OriginalAdventureAI.CastsTownPortal(flood, step.Tile));
        Assert.False(OriginalAdventureAI.CastsTownPortal(flood, h.Pos));
        // 008648b0: another hero in the town's slot empties the list.
        var other = g.S.Heroes.First(x => x.Id != h.Id && x.Alive);
        var keep = other.Pos;
        other.Pos = entrance;
        Assert.Empty(g.PortalStartTiles(h));
        other.Pos = keep;
    }

    [Fact]
    public void Town_portal_setting_needs_the_spell_mana_and_land()
    {
        var g = f.NewGame();
        var st = AdventureAiStats.Load(g.Core);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        int cost = g.AdventureSpellCost(Engine.Adventure.SpellTownPortal);
        h.Spells.Remove(Engine.Adventure.SpellTownPortal);
        h.Mana = cost;
        Assert.False(OriginalAdventureAI.DimensionDoorSettingsFor(g, h, st).TownPortal);
        h.Spells.Add(Engine.Adventure.SpellTownPortal);
        var s = OriginalAdventureAI.DimensionDoorSettingsFor(g, h, st);
        Assert.True(s.TownPortal);
        Assert.Equal(h.MaxMovePoints, s.LandDayMove);
        h.Mana = cost - 1;
        Assert.False(OriginalAdventureAI.DimensionDoorSettingsFor(g, h, st).TownPortal);
        h.Mana = cost;
        h.InBoat = true;
        Assert.False(OriginalAdventureAI.DimensionDoorSettingsFor(g, h, st).TownPortal);
        h.InBoat = false;
    }

    static readonly (int dx, int dy)[] Around = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    /// <summary>A1L10: a ship built on a shipyard's slipway and the current hero on a free shore tile next to it.</summary>
    (Engine.Adventure G, HeroState H, BoatState B) HeroBesideShip()
    {
        var g = f.NewGame("A1L10");
        var h = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == g.Current.Index) ?? g.S.Heroes.First(x => x.Alive);
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == h.Owner);
        foreach (var yard in g.Map.Objects.Where(o => o.Kind == ObjectKind.Shipyard && g.ShipyardStatus(o.Index) == "ok"))
        {
            var slip = g.SlipwayOf(yard.Index)!.Value;
            var fl = g.Map.Floor(slip.Floor)!;
            foreach (var (dx, dy) in Around)
            {
                var t = slip.Offset(dx, dy);
                if (!fl.InBounds(t.X, t.Y) || fl.IsWater(t.X, t.Y) || fl.IsBlocked(t.X, t.Y) || g.GuardingMonster(t) != null) continue;
                if (g.S.Heroes.Any(x => x.Pos == t) || g.ObjectsAtTile(t).Any()) continue;
                g.S.Obj(yard.Index).Owner = g.Current.Index;
                g.Current.Purse[Purse.Gold] = 5000;
                g.Current.Purse[Purse.Wood] = 50;
                Assert.True(g.Apply(new BuildShipCmd(yard.Index) { Player = g.Current.Index }).Ok);
                h.Pos = t;
                h.InTown = -1;
                h.InBoat = false;
                h.Perks.Remove("HERO_SKILL_SNATCH");
                h.MovePoints = 2000;
                return (g, h, g.BoatAt(slip)!);
            }
        }
        throw new InvalidOperationException("no shipyard with a free shore");
    }

    [Fact]
    public void A_popped_ship_node_boards_and_floods_on_at_sea()
    {
        // 009d22f0: a land node on a free ship: w2 + w4 (the rest of the day), w4 = sea day, w1 | 2, then it expands.
        var (g, h, b) = HeroBesideShip();
        var flood = g.SpellFlood(h, 0, default);
        var ship = flood.Nodes[b.Pos];
        Assert.True(ship.Boat);
        int rest = 2000 - ship.Cost;
        var fl = g.Map.Floor(b.Pos.Floor)!;
        var sea = Around.Select(d => b.Pos.Offset(d.dx, d.dy))
            .Where(t => fl.InBounds(t.X, t.Y) && flood.Nodes.TryGetValue(t, out var n) && n.Boat && flood.Parent[t] == b.Pos).ToList();
        Assert.NotEmpty(sea);
        foreach (var t in sea)
            Assert.Equal(ship.Cost + rest + (t.X != b.Pos.X && t.Y != b.Pos.Y ? 141 : 100), flood.Nodes[t].Cost);
        // Ours: the move goes to the ship first (the route's first node with another boat state).
        var far = flood.Nodes.Values.Where(n => n.Boat && n.Tile != b.Pos).OrderByDescending(n => n.Cost).First();
        var route = flood.Route(far.Tile)!;
        Assert.Equal(b.Pos, route[OriginalAdventureAI.ShoreIndex(route)].Tile);
    }

    [Fact]
    public void Vessel_of_shalassa_edge_needs_a_free_ship_and_is_cast_from_the_shore()
    {
        // 00c07f80 with pf +0x55: land node (P) → free water, provider vf 0xc (a free ship on the floor) → 0x2000002,
        // not P, cost w4 (2000 ≥ the step), priority + 500, w4 = sea day; 008675f0 → command +0x32 when it is node 1.
        var (g, h, b) = HeroBesideShip();
        var slip = b.Pos;
        var fl = g.Map.Floor(slip.Floor)!;
        Tile? away = null;
        for (int y = 0; y < fl.Height && away == null; y++)
            for (int x = 0; x < fl.Width; x++)
            {
                var t = new Tile(x, y, slip.Floor);
                if (fl.IsWater(x, y) && !fl.IsBlocked(x, y) && Math.Max(Math.Abs(x - slip.X), Math.Abs(y - slip.Y)) > 6 && g.BoatAt(t) == null
                    && !g.ObjectsAtTile(t).Any()) { away = t; break; }
            }
        b.Pos = away!.Value;
        var sb = new DimensionDoorSettings(false, 0, 0, 0, false, h.MaxMovePoints, SummonBoat: true);
        Assert.False(g.SpellFlood(h, 0, sb with { SummonBoat = false }).Nodes.ContainsKey(slip));
        var flood = g.SpellFlood(h, 0, sb);
        var n = flood.Nodes[slip];
        Assert.True(n.Summon && n.Boat && !n.CanStop);
        Assert.Equal(2000, n.Cost);
        Assert.Equal(2500, n.Priority);
        var beyond = flood.Nodes.Values.First(x => x.Boat && flood.Parent.TryGetValue(x.Tile, out var p) && p == slip);
        var route = flood.Route(beyond.Tile)!;
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.SummonBoat, 0), OriginalAdventureAI.BoatStep(route));
        // Two steps from the shore: walk to the node before the summon node.
        var longer = new List<FloodNode> { route[0], route[0] with { Tile = route[0].Tile.Offset(0, 1) }, route[1], route[2] };
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Walk, 1), OriginalAdventureAI.BoatStep(longer));
        // No free ship on the floor: no edge.
        g.S.Boats.Clear();
        Assert.False(g.SpellFlood(h, 0, sb).Nodes.ContainsKey(slip));
    }

    [Fact]
    public void No_instant_travel_cast_to_a_node_beyond_today()
    {
        // 008675f0 LAB_00867e34: the candidate's w1 0x800000 (w2 > hero move points, 00c09d90) refuses the cast.
        var cast = new List<FloodNode> { N(0, 0), N(5, 0, landing: true), N(6, 100), N(20, 1500) };
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Cast, 2), Step(cast, 750, 0));
        cast[2] = cast[2] with { Late = true };
        Assert.Equal((OriginalAdventureAI.DimDoorStepKind.Stop, 0), Step(cast, 750, 0));
    }

    [Fact]
    public void Instant_travel_landing_needs_no_exploration()
    {
        // 008649f0 reads the cell, objects and units only (was: our extra explored-by-the-caster test).
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        foreach (var e in g.S.Players.First(x => x.Index == h.Owner).Explored) Array.Fill(e, false);
        var fl = g.Map.Floor(h.Pos.Floor)!;
        bool any = false;
        for (int dy = -8; dy <= 8 && !any; dy++)
            for (int dx = -8; dx <= 8 && !any; dx++)
            {
                var t = h.Pos.Offset(dx, dy);
                if (fl.InBounds(t.X, t.Y) && !fl.IsBlocked(t.X, t.Y) && !fl.IsWater(t.X, t.Y) && g.HeroAtTile(t) == null && !g.ObjectsAtTile(t).Any()
                    && g.BoatAt(t) == null)
                    any = g.DimensionDoorLandingOk(h, t, false);
            }
        Assert.True(any);
    }
}
