using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// Route markers (route display 005b0c10 / 005b08e0) and the hover action of script-disabled objects (005ff8f0).
/// </summary>
[Collection("adv")]
public class RouteDisplayTests(Fixture f)
{
    static HeroState MyHero(Engine.Adventure g)
    {
        var h = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == g.Current.Index) ?? g.S.Heroes.First(x => x.Alive);
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == h.Owner);
        h.MovePoints = h.MaxMovePoints = 100000;
        return h;
    }

    /// <summary>A neutral stack with five free cells in a row east of it, only the first next to it.</summary>
    static MapObject LoneMonster(Engine.Adventure g)
    {
        var fl = g.Map.Floor(0)!;
        foreach (var m in g.Map.Objects.Where(o => o.Kind == ObjectKind.Monster && o.Pos.Floor == 0))
        {
            bool ok = true;
            for (int d = 1; d <= 5 && ok; d++)
            {
                var t = m.Pos.Offset(d, 0);
                ok = fl.InBounds(t.X, t.Y) && (d == 1 ? g.GuardingMonster(t)?.Index == m.Index : g.GuardingMonster(t) == null);
            }
            if (ok) return m;
        }
        throw new InvalidOperationException("no lone monster on A2M1");
    }

    [Fact]
    public void GuardZoneStepsAreContestedAndLaterDaysGray()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var m = LoneMonster(g);
        h.Pos = m.Pos.Offset(5, 0);
        var path = new List<Tile> { h.Pos, m.Pos.Offset(4, 0), m.Pos.Offset(3, 0), m.Pos.Offset(2, 0), m.Pos.Offset(1, 0), m.Pos };
        // 005b0aac..: index ≥ guard (+0x20, first cell in a guard zone via 00ceab70) → Contested; the hero's tile is left out.
        Assert.Equal(new[] { RouteMarker.Available, RouteMarker.Available, RouteMarker.Available, RouteMarker.Contested, RouteMarker.Contested },
            g.RouteMarkers(h, path));
        // index ≥ today (+0x18, IAdvMapMovable vfunc 0x3c) → Gray, before the Contested test.
        h.MovePoints = g.StepCost(h, path[0], path[1]) + g.StepCost(h, path[1], path[2]);
        Assert.Equal(new[] { RouteMarker.Available, RouteMarker.Available, RouteMarker.Gray, RouteMarker.Gray, RouteMarker.Gray },
            g.RouteMarkers(h, path));
        h.MovePoints = 0;
        Assert.All(g.RouteMarkers(h, path), k => Assert.Equal(RouteMarker.Gray, k));
    }

    sealed class Hooks(int index, int? mode) : IAdventureScriptHooks
    {
        public bool IsTileBlocked(int player, Tile tile) => false;
        public bool InterceptVisit(HeroState hero, MapObject obj) => false;
        public bool StopMovementAt(HeroState hero, Tile from, Tile to) => false;
        public int? DisabledObjectMode(MapObject obj) => obj.Index == index ? mode : null;
    }

    [Fact]
    public void DisabledObjectModeDecidesTheAction()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var m = LoneMonster(g);
        h.Pos = m.Pos.Offset(3, 0);
        Assert.Equal(AdvAction.Attack, g.Target(h.Owner, h, m.Pos, m).Action);
        // 005ff8f0: disabled object → mode 1 → 7, 2 → 5, 3 → 4; mode 0 and enabled objects keep the normal action.
        (int? mode, AdvAction act)[] cases = { (2, AdvAction.Interact), (3, AdvAction.NoPath), (1, AdvAction.Attack), (0, AdvAction.Attack), (null, AdvAction.Attack) };
        foreach (var (mode, act) in cases)
        {
            g.ScriptHooks = new Hooks(m.Index, mode);
            Assert.Equal(act, g.Target(h.Owner, h, m.Pos, m).Action);
        }
    }
}
