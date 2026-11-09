using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

[Collection("adv")]
public class AdventureSpellTests(Fixture f)
{
    static (Engine.Adventure G, HeroState H) Setup(Fixture f, string spell)
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index && x.Army.StackCount() >= 1);
        h.Spells.Add(spell);
        h.Mana = 100;
        h.MovePoints = h.MaxMovePoints;
        return (g, h);
    }

    [Fact]
    public void SpellCostsComeFromTheSpellData()
    {
        // TrainedCost: stock 15/20/0/4; MMH55-Index.pak DimensionDoor.xdb 25, Town_Portal.xdb 20, Summon_Boat.xdb 5.
        var g = f.NewGame();
        Assert.Equal(DataFixture.Pick(15, 25), g.AdventureSpellCost(Engine.Adventure.SpellDimensionDoor));
        Assert.Equal(20, g.AdventureSpellCost(Engine.Adventure.SpellTownPortal));
        Assert.Equal(0, g.AdventureSpellCost(Engine.Adventure.SpellSummonCreatures));
        Assert.Equal(DataFixture.Pick(4, 5), g.AdventureSpellCost(Engine.Adventure.SpellSummonBoat));
    }

    /// <summary>
    /// The adventure cast pays 00b4c430 → 008647d0 → 00a34740: the week (00bcddf0), a worn Ring of Magi / 2, then
    /// Arcane Training chop(min(cost · 0.8f, cost − 1)).
    /// </summary>
    [Fact]
    public void AdventureCastCostFollowsTheExeChain()
    {
        var g = f.NewGame();
        var h = new HeroState { Town = "TOWN_ACADEMY" };
        const string dd = Engine.Adventure.SpellDimensionDoor;
        int b = DataFixture.Pick(15, 25);
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        Assert.Equal(b, g.AdventureSpellCost(h, dd));
        h.Perks.Add("HERO_SKILL_ARCANE_TRAINING");
        Assert.Equal((int)Math.Min(b * (double)0.8f, b - 1), g.AdventureSpellCost(h, dd));    // 12 / 20
        h.Artifacts.Add("RING_OF_MAGI");
        Assert.Equal((int)Math.Min(b / 2 * (double)0.8f, b / 2 - 1), g.AdventureSpellCost(h, dd));   // 7 → 5 / 12 → 9
        h.Perks.Clear();
        g.S.MoonWeek = "WEEK_OF_MAGIC";
        Assert.Equal(b / 2 / 2, g.AdventureSpellCost(h, dd));
        Assert.Equal(b, g.AdventureSpellCost(dd));   // the record's cost
    }

    /// <summary>The cast takes that cost from the hero's mana (008656e0 via 008647d0).</summary>
    [Fact]
    public void InstantTravelChargesTheModifiedCost()
    {
        var (g, h) = Setup(f, Engine.Adventure.SpellDimensionDoor);
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        h.Artifacts.Add("RING_OF_MAGI");
        var floor = g.Map.Floor(h.Pos.Floor)!;
        Tile? target = null;
        for (int dy = -8; dy <= 8 && target == null; dy++)
            for (int dx = -8; dx <= 8 && target == null; dx++)
            {
                var t = new Tile(h.Pos.X + dx, h.Pos.Y + dy, h.Pos.Floor);
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 2 && g.CanDimensionDoorTo(h, t)) target = t;
            }
        Assert.NotNull(target);
        int mana = h.Mana, cost = g.AdventureSpellCost(h, Engine.Adventure.SpellDimensionDoor);
        Assert.True(cost < g.AdventureSpellCost(Engine.Adventure.SpellDimensionDoor));
        var r = g.Apply(new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellDimensionDoor, target) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(mana - cost, h.Mana);
    }

    [Fact]
    public void FlatCostMatchesSideAndDiagonalSteps()
    {
        Assert.Equal(800, Engine.Adventure.FlatMoveCost(new Tile(0, 0, 0), new Tile(8, 0, 0)));
        Assert.Equal(141 * 5 + 100, Engine.Adventure.FlatMoveCost(new Tile(0, 0, 0), new Tile(5, 6, 0)));
    }

    [StockFact]
    public void InstantTravelMovesWithinRangeAndCostsHalfTheDay()
    {
        var (g, h) = Setup(f, Engine.Adventure.SpellDimensionDoor);
        var p = g.S.Players.First(x => x.Index == h.Owner);
        var floor = g.Map.Floor(h.Pos.Floor)!;
        // Any explored free tile 3..8 steps away.
        Tile? target = null;
        for (int dy = -8; dy <= 8 && target == null; dy++)
            for (int dx = -8; dx <= 8 && target == null; dx++)
            {
                var t = new Tile(h.Pos.X + dx, h.Pos.Y + dy, h.Pos.Floor);
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 3 && g.CanDimensionDoorTo(h, t)) target = t;
            }
        Assert.NotNull(target);
        int mana = h.Mana, move = h.MovePoints;
        var r = g.Apply(new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellDimensionDoor, target) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(target, h.Pos);
        Assert.Equal(mana - 15, h.Mana);
        Assert.Equal(move - h.MaxMovePoints / 2, h.MovePoints);
        Assert.Contains(r.Events, e => e is AdventureSpellCast);
        Assert.True(p.Explored[target!.Value.Floor][target.Value.Y * floor.Width + target.Value.X]);
    }

    [Fact]
    public void InstantTravelRejectsOutOfRangeAndLeavesStateUnchanged()
    {
        var (g, h) = Setup(f, Engine.Adventure.SpellDimensionDoor);
        var far = new Tile(h.Pos.X + 9, h.Pos.Y, h.Pos.Floor);
        var before = g.S.ComputeHash();
        var r = g.Apply(new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellDimensionDoor, far) { Player = g.Current.Index });
        Assert.False(r.Ok);
        Assert.Equal(before, g.S.ComputeHash());
    }

    [Fact]
    public void TownPortalGoesToTheNearestOwnTownAndSpendsAllMovement()
    {
        var (g, h) = Setup(f, Engine.Adventure.SpellTownPortal);
        var town = g.PortalTown(h)!;
        // Step away from the entrance so the portal has somewhere to go.
        var entrance = g.EntranceOf(town);
        if (h.Pos == entrance)
        {
            var f0 = g.Map.Floor(h.Pos.Floor)!;
            for (int d = 1; d < 6 && h.Pos == entrance; d++)
                foreach (var t in new[] { new Tile(entrance.X + d, entrance.Y, entrance.Floor), new Tile(entrance.X, entrance.Y - d, entrance.Floor) })
                    if (f0.InBounds(t.X, t.Y) && !f0.IsBlocked(t.X, t.Y) && !f0.IsWater(t.X, t.Y)) { h.Pos = t; break; }
        }
        Assert.NotEqual(entrance, h.Pos);
        var r = g.Apply(new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellTownPortal) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(entrance, h.Pos);
        Assert.Equal(0, h.MovePoints);
        Assert.Equal(80, h.Mana);
    }

    [Fact]
    public void TownPortalPicksTheSmallestSquaredDistanceWithFloorDepth()
    {
        var (g, h) = Setup(f, Engine.Adventure.SpellTownPortal);
        foreach (var t in g.S.Towns) t.Owner = h.Owner;
        int depth = g.Db.Stats.Root.Element("adventure")!.Int("DepthBetweenFloors", 10);
        long D(TownState t)
        {
            long dx = t.Pos.X - h.Pos.X, dy = t.Pos.Y - h.Pos.Y, dz = (long)depth * (t.Pos.Floor - h.Pos.Floor);
            return dx * dx + dy * dy + dz * dz;
        }
        long min = g.S.Towns.Min(D);
        Assert.Same(g.S.Towns.First(t => D(t) == min), g.PortalTown(h));
    }

    [Fact]
    public void SummonCreaturesTakesChosenGarrisonStacksForOneManaEach()
    {
        var (g, h) = Setup(f, Engine.Adventure.SpellSummonCreatures);
        var town = g.S.Towns.First(t => t.Owner == h.Owner);
        Array.Fill(town.Garrison, null);
        var creature = h.Army.Live().First().Creature;
        town.Garrison[2] = new Stack(creature, 7);
        int before = h.Army.Live().Where(s => s.Creature == creature).Sum(s => s.Count);
        int move = h.MovePoints;
        var r = g.Apply(new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellSummonCreatures, TownObject: town.Object, Stacks: new() { 2 }) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Null(town.Garrison[2]);
        Assert.Equal(before + 7, h.Army.Live().Where(s => s.Creature == creature).Sum(s => s.Count));
        Assert.Equal(100 - 7, h.Mana);
        Assert.Equal(move - h.MaxMovePoints * 3 / 4, h.MovePoints);
    }

    [Fact]
    public void UnknownSpellIsRejected()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.Spells.Remove(Engine.Adventure.SpellTownPortal);
        var r = g.Apply(new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellTownPortal) { Player = g.Current.Index });
        Assert.False(r.Ok);
    }
}
