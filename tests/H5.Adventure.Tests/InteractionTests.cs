using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// Adventure-map pointing and clicking (H5_Game.exe 00600e70 / 00600060 / 005ff8f0 / 00cebd50): any cell of
/// an object aims at the object, the route ends at its entrance, the action decides the cursor.
/// </summary>
[Collection("adv")]
public class InteractionTests(Fixture f)
{
    static readonly (int dx, int dy)[] Around = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    static HeroState MyHero(Engine.Adventure g)
    {
        var h = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == g.Current.Index) ?? g.S.Heroes.First(x => x.Alive);
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == h.Owner);
        h.MovePoints = h.MaxMovePoints = 2000;
        return h;
    }

    /// <summary>Puts the hero next to the object's first entrance and returns a body cell of the object that is not an entrance.</summary>
    static (MapObject Mine, Tile Body)? Setup(Engine.Adventure g, HeroState h, ObjectKind kind)
    {
        foreach (var o in g.Map.Objects.Where(o => o.Kind == kind && o.Pos.Floor == 0))
        {
            var active = o.ActiveTiles.ToList();
            var body = o.BlockedTiles.Where(t => !active.Contains(t) && t != o.Pos).ToList();
            if (active.Count == 0 || body.Count == 0) continue;
            var f = g.Map.Floor(0)!;
            foreach (var (dx, dy) in Around)
            {
                var n = active[0].Offset(dx, dy);
                if (!f.InBounds(n.X, n.Y) || f.IsBlocked(n.X, n.Y) || f.IsWater(n.X, n.Y) || active.Contains(n)) continue;
                if (g.S.Heroes.Any(x => x.Alive && x.Pos == n) || g.GuardingMonster(n) != null || g.GuardingMonster(active[0]) != null) continue;
                if (g.ObjectAtTile(n) != null) continue;
                h.Pos = n;
                g.S.Obj(o.Index).Guards.Clear();
                g.S.Obj(o.Index).Owner = -1;
                if (g.FindPath(h, active[0]) is { Count: > 0 }) return (o, body[0]);
            }
        }
        return null;
    }

    [Fact]
    public void PointingAtAMineBodyAimsAtItsEntrance()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var (mine, body) = Setup(g, h, ObjectKind.Mine) ?? throw new InvalidOperationException($"no reachable mine: {g.Map.Objects.Count(o => o.Kind == ObjectKind.Mine)} mines, {g.Map.Objects.Count(o => o.Kind == ObjectKind.Mine && o.ActiveTiles.Any())} with entrances, {g.Map.Objects.Count(o => o.Kind == ObjectKind.Mine && o.BlockedTiles.Any(b => !o.ActiveTiles.Contains(b) && b != o.Pos))} with body");
        Assert.Equal(mine.Index, g.ObjectAtTile(body)?.Index);
        var t = g.Target(h.Owner, h, body, g.ObjectAtTile(body));
        Assert.Equal(AdvAction.Use, t.Action);
        Assert.Contains(t.Dest, mine.ActiveTiles);
        Assert.Equal(t.Dest, t.Path![^1]);
        Assert.Equal(0, t.Days);
        Assert.Equal(mine.Index, t.Object);

        // Owned by an ally → own-building cursor; guarded → fight.
        g.S.Obj(mine.Index).Owner = h.Owner;
        Assert.Equal(AdvAction.OwnBuilding, g.Target(h.Owner, h, body, mine).Action);
        g.S.Obj(mine.Index).Owner = -1;
        g.S.Obj(mine.Index).Guards.Add(new Stack("CREATURE_PEASANT", 10));
        Assert.Equal(AdvAction.Attack, g.Target(h.Owner, h, body, mine).Action);
        g.S.Obj(mine.Index).Guards.Clear();

        // Walking there captures the mine.
        var r = g.Apply(new MoveHeroCmd(h.Id, t.Dest) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(h.Owner, g.S.Obj(mine.Index).Owner);
    }

    [Fact]
    public void EntranceNearestToThePointedCellWins()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        foreach (var o in g.Map.Objects.Where(o => o.Kind != ObjectKind.Static && o.ActiveTiles.Count() >= 2))
        {
            var active = o.ActiveTiles.ToList();
            var far = active.OrderByDescending(a => (a.X - active[0].X) * (a.X - active[0].X) + (a.Y - active[0].Y) * (a.Y - active[0].Y)).First();
            if (far == active[0]) continue;
            h.Pos = far;
            // Standing on one entrance and pointing at it: that entrance is the destination.
            var t = g.Target(h.Owner, h, far, o);
            if (t.Action == AdvAction.NoPath) continue;
            Assert.Equal(far, t.Dest);
            return;
        }
    }

    [Fact]
    public void HeroesAndPlainTiles()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        Assert.Equal(AdvAction.SelfHero, g.Target(h.Owner, h, h.Pos, null).Action);
        Assert.Equal(AdvAction.NoHero, g.Target(h.Owner, null, h.Pos.Offset(1, 0), null).Action);
        var fl = g.Map.Floor(h.Pos.Floor)!;
        foreach (var (dx, dy) in Around)
        {
            var n = h.Pos.Offset(dx, dy);
            if (!fl.InBounds(n.X, n.Y) || fl.IsBlocked(n.X, n.Y) || fl.IsWater(n.X, n.Y) || g.ObjectAtTile(n) != null || g.GuardingMonster(n) != null) continue;
            var t = g.Target(h.Owner, h, n, null);
            Assert.Equal(AdvAction.Move, t.Action);
            Assert.Equal(0, t.Days);
            return;
        }
    }

    [Fact]
    public void DaysCountDayRollovers()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var a = h.Pos;
        var path = new List<Tile> { a.Offset(1, 0), a.Offset(2, 0), a.Offset(3, 0) };
        h.MovePoints = 0;
        h.MaxMovePoints = 100000;
        Assert.Equal(1, g.DaysFor(h, path));
        h.MovePoints = 100000;
        Assert.Equal(0, g.DaysFor(h, path));
    }

    /// <summary>Visits one object directly from its entrance and returns the visit events.</summary>
    static List<AdvEvent> VisitFromEntrance(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        return r.Events.ToList();
    }

    [Fact]
    public void BorderGuardAsksBeforeLettingThrough()
    {
        var g = f.NewGame("A2M11");
        var h = MyHero(g);
        var guard = g.Map.Objects.First(o => o.Shared.Type == "BUILDING_BORDER_GUARD" && o.ActiveTiles.Any());
        var p = g.S.Players.First(x => x.Index == h.Owner);
        p.Keys.Remove(guard.Shared.Color);
        Assert.Contains(VisitFromEntrance(g, h, guard), e => e is ObjectVisited { What: "border_guard_closed" });
        Assert.False(g.S.Obj(guard.Index).Removed);
        p.Keys.Add(guard.Shared.Color);
        VisitFromEntrance(g, h, guard);
        Assert.Equal("border_guard", g.S.Choice?.Kind);
        g.Apply(new AnswerChoiceCmd(false, 0) { Player = h.Owner });
        Assert.False(g.S.Obj(guard.Index).Removed);
        VisitFromEntrance(g, h, guard);
        g.Apply(new AnswerChoiceCmd(true, 0) { Player = h.Owner });
        Assert.True(g.S.Obj(guard.Index).Removed);
    }

    [Fact]
    public void ShrineReportsTheOutcome()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var shrine = g.Map.Objects.First(o => o.Kind == ObjectKind.Shrine && g.S.Obj(o.Index).SpellId != null && o.ActiveTiles.Any());
        var spell = g.S.Obj(shrine.Index).SpellId!;
        h.Spells.Remove(spell);
        var first = VisitFromEntrance(g, h, shrine).OfType<ObjectVisited>().Single(e => e.Object == shrine.Index).What;
        Assert.True(first is "shrine:learned" or "shrine:cant", first);
        if (!h.Spells.Contains(spell)) h.Spells.Add(spell);
        Assert.Contains(VisitFromEntrance(g, h, shrine), e => e is ObjectVisited { What: "shrine:known" });
    }

    [Fact]
    public void CreatureBankAsksBeforeTheFight()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var bank = g.Map.Objects.First(o => o.Kind == ObjectKind.Building && H5.Adventure.Data.AdventureDb.BankFor(o.Shared.Type) != null && o.ActiveTiles.Any());
        VisitFromEntrance(g, h, bank);
        Assert.Equal("bank_search", g.S.Choice?.Kind);
        g.Apply(new AnswerChoiceCmd(false, 0) { Player = h.Owner });
        Assert.Null(g.S.Battle);
        VisitFromEntrance(g, h, bank);
        Assert.Equal("bank_search", g.S.Choice?.Kind);
        g.Apply(new AnswerChoiceCmd(true, 0) { Player = h.Owner });
        Assert.NotNull(g.S.Battle);
    }

    [Fact]
    public void PrisonReportsWithItsOwnTexts()
    {
        foreach (var id in new[] { "A2M1", "A2M11", "A2M7", "A2M13" })
        {
            var g = f.NewGame(id);
            var prison = g.Map.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Prison && o.Hero != null && o.ActiveTiles.Any());
            if (prison == null) continue;
            var h = MyHero(g);
            Assert.Contains(VisitFromEntrance(g, h, prison), e => e is ObjectVisited { What: "prison:freed" });
            Assert.True(g.S.Obj(prison.Index).Removed);
            return;
        }
    }
}
