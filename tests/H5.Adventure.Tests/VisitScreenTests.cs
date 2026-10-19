using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Visits that open a visit screen (exe dispatcher 005a5d90): the engine reports them and the screen's commands work.</summary>
[Collection("adv")]
public class VisitScreenTests(Fixture f)
{
    static HeroState MyHero(Engine.Adventure g)
    {
        var h = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == g.Current.Index) ?? g.S.Heroes.First(x => x.Alive);
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == h.Owner);
        h.MovePoints = h.MaxMovePoints = 2000;
        return h;
    }

    static List<AdvEvent> VisitFromEntrance(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        return r.Events.ToList();
    }

    [Fact]
    public void DwellingVisitOpensTheHireScreenEveryTime()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var d = g.Map.Objects.First(o => o.Kind == ObjectKind.Dwelling && o.ActiveTiles.Any() && g.S.Obj(o.Index).Guards.Count == 0);
        Assert.Contains(VisitFromEntrance(g, h, d), e => e is ObjectVisited { What: "dwelling" } v && v.Object == d.Index);
        Assert.Equal(h.Owner, g.S.Obj(d.Index).Owner);
        Assert.Contains(VisitFromEntrance(g, h, d), e => e is ObjectVisited { What: "dwelling" });
        // The screen's Buy: RecruitCmd on the dwelling puts the creatures into the visiting hero.
        var os = g.S.Obj(d.Index);
        int row = Array.FindIndex(os.Available, n => n > 0);
        Assert.True(row >= 0);
        g.Current.Purse = new Purse(new H5.Core.Data.Resources(99, 99, 99, 99, 99, 99, 999999));
        for (int i = 0; i < Army.Slots; i++) h.Army[i] = null;
        int before = os.Available[row];
        var r = g.Apply(new RecruitCmd(d.Index, row + 1, 1) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(before - 1, os.Available[row]);
        Assert.Contains(h.Army, s => s is { Count: 1 } && s.Creature == d.Shared.DwellingCreatures[row]);
    }
}
