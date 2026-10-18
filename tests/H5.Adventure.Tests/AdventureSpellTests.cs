using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

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
        var g = f.NewGame();
        Assert.Equal(15, g.AdventureSpellCost(Engine.Adventure.SpellDimensionDoor));
        Assert.Equal(20, g.AdventureSpellCost(Engine.Adventure.SpellTownPortal));
        Assert.Equal(0, g.AdventureSpellCost(Engine.Adventure.SpellSummonCreatures));
        Assert.Equal(4, g.AdventureSpellCost(Engine.Adventure.SpellSummonBoat));
    }

    [Fact]
    public void FlatCostMatchesSideAndDiagonalSteps()
    {
        Assert.Equal(800, Engine.Adventure.FlatMoveCost(new Tile(0, 0, 0), new Tile(8, 0, 0)));
        Assert.Equal(141 * 5 + 100, Engine.Adventure.FlatMoveCost(new Tile(0, 0, 0), new Tile(5, 6, 0)));
    }

    [Fact]
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
        var town = g.NearestOwnTown(h)!;
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
