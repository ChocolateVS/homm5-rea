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

    [Fact]
    public void DwellingHireAllTakesEveryRowLastFirstWithMoneyAndFreeSlots()
    {
        // CHireAllCreaturesCmd 0042b870 → FUN_009a52a0 over the dwelling's rows.
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var d = g.Map.Objects.First(o => o.Kind == ObjectKind.Dwelling && o.ActiveTiles.Any() && g.S.Obj(o.Index).Guards.Count == 0
            && o.Shared.DwellingCreatures.Distinct().Count() >= 2);
        VisitFromEntrance(g, h, d);
        var os = g.S.Obj(d.Index);
        var list = d.Shared.DwellingCreatures;
        for (int i = 0; i < list.Count; i++) os.Available[i] = 5;
        for (int i = 0; i < Army.Slots; i++) h.Army[i] = null;
        g.Current.Purse = new Purse(new H5.Core.Data.Resources(999, 999, 999, 999, 999, 999, 9999999));
        var plan = g.DwellingHireAllPlan(d.Index, g.Current.Purse, h.Army);
        Assert.Equal(Enumerable.Range(0, list.Count).Reverse(), plan.Select(x => x.Row));
        Assert.True(g.Apply(new HireAllCmd(d.Index) { Player = h.Owner }).Ok);
        Assert.All(Enumerable.Range(0, list.Count), i => Assert.Equal(0, os.Available[i]));
        Assert.False(g.Apply(new HireAllCmd(d.Index) { Player = h.Owner }).Ok);   // nothing left
        // Money: count = min(pool, purse / cost); full army: a creature not held yet is skipped.
        for (int i = 0; i < list.Count; i++) os.Available[i] = 5;
        var last = list[^1];
        var cost = g.Core.Creatures[last].Cost;
        g.Current.Purse = new Purse(H5.Adventure.State.Purse.Scale(cost, 2));
        for (int i = 0; i < Army.Slots; i++) h.Army[i] = null;
        Assert.Equal((list.Count - 1, last, 2), g.DwellingHireAllPlan(d.Index, g.Current.Purse, h.Army)[0]);
        for (int i = 0; i < Army.Slots; i++) h.Army[i] = new Stack("CREATURE_PEASANT", 1);
        Assert.Empty(g.DwellingHireAllPlan(d.Index, new Purse(new H5.Core.Data.Resources(999, 999, 999, 999, 999, 999, 9999999)), h.Army)
            .Where(x => x.Creature != "CREATURE_PEASANT"));
    }

    [Fact]
    public void OwnGarrisonTakesTroopsAndDefendsWithThem()
    {
        var g = f.NewGame("A2M11");
        var h = MyHero(g);
        var gar = g.Map.Objects.First(o => o.Kind == ObjectKind.Garrison && o.ActiveTiles.Any());
        var os = g.S.Obj(gar.Index);
        os.Guards.Clear();
        Array.Fill(os.Army, null);
        os.Owner = h.Owner;
        Assert.Contains(VisitFromEntrance(g, h, gar), e => e is ObjectVisited { What: "garrison" } v && v.Object == gar.Index);
        int from = Array.FindIndex(h.Army, s => s is { Count: > 0 });
        h.Army[(from + 1) % Army.Slots] ??= new Stack(h.Army[from]!.Creature, 1);   // keep one stack with the hero
        var moved = h.Army[from]!;
        var r = g.Apply(new SwapStacksCmd(h.Id, -1, from, -1, gar.Index, 0) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Same(moved, os.Army[0]);
        // An enemy hero meets those troops in battle.
        int team = g.S.Players.First(p => p.Index == h.Owner).Team;
        var enemy = g.S.Heroes.First(x => x.Alive && g.S.Players.First(p => p.Index == x.Owner).Team != team);
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == enemy.Owner);
        enemy.MovePoints = enemy.MaxMovePoints = 2000;
        enemy.Pos = gar.ActiveTiles.First();
        h.Pos = new Tile(0, 0, 0);
        var er = g.Apply(new MoveHeroCmd(enemy.Id, enemy.Pos) { Player = enemy.Owner });
        Assert.True(er.Ok, er.Error);
        Assert.NotNull(g.S.Battle);
        Assert.Equal(gar.Index, g.S.Battle!.DefenderObject);
    }

    [Fact]
    public void OwnMineRevisitOpensTheGarrisonScreen()
    {
        var g = f.NewGame("A2M1");
        var h = MyHero(g);
        var mine = g.Map.Objects.First(o => o.Kind == ObjectKind.Mine && o.ActiveTiles.Any() && g.S.Obj(o.Index).Guards.Count == 0 && g.S.Obj(o.Index).Owner < 0);
        Assert.DoesNotContain(VisitFromEntrance(g, h, mine), e => e is ObjectVisited { What: "garrison" });
        Assert.Contains(VisitFromEntrance(g, h, mine), e => e is ObjectVisited { What: "garrison" });
    }

    [Fact]
    public void SignCarriesItsOwnText()
    {
        var g = f.NewGame("A2S3");
        var sign = g.Map.Objects.First(o => o.Kind == ObjectKind.Sign);
        Assert.False(string.IsNullOrWhiteSpace(sign.MessageText));
        var h = MyHero(g);
        Assert.Contains(VisitFromEntrance(g, h, sign), e => e is ObjectVisited { What: "BUILDING_SIGN" } v && v.Object == sign.Index);
    }
}
