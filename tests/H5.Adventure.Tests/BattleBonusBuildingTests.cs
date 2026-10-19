using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Mother Earth Shrine (BUILDING_NOMADS_SHAMAN) and Fortuitous Sanctuary: exe 00b02740 cases 0x76 / 0x77.</summary>
[Collection("adv")]
public class BattleBonusBuildingTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h, MapObject o) Setup(Fixture f, string type)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.MovePoints = h.MaxMovePoints = 2000;
        // Any placed building stands in for the object type (no stock campaign map carries these).
        var o = g.Map.Objects.First(x => x.Kind == ObjectKind.Building && x.ActiveTiles.Any() && g.S.Obj(x.Index).Guards.Count == 0
                                         && AdventureDb.BankFor(x.Shared.Type) == null);
        var old = o.Shared;
        o.Shared = new SharedDef { Type = type, Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
        return (g, h, o);
    }

    static List<AdvEvent> Visit(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        return r.Events.ToList();
    }

    [Fact]
    public void ShamanGivesTenPercentHitPointsUntilTheNextBattle()
    {
        var (g, h, o) = Setup(f, "BUILDING_NOMADS_SHAMAN");
        Assert.Contains(Visit(g, h, o), e => e is ObjectVisited { What: "battlebonus:4=10" });
        Assert.Equal(10, h.NextBattleHitPoints);
        Assert.Contains(Visit(g, h, o), e => e is ObjectVisited { What: "battlebonus:again" });
        Assert.Equal(10, h.NextBattleHitPoints);
    }

    [Theory]
    [InlineData(1, "battlebonus:2=3")]
    [InlineData(2, "battlebonus:1=1")]
    [InlineData(6, "battlebonus:2=2,3=2")]
    [InlineData(7, "battlebonus:6=1")]
    public void SanctuaryBonusFollowsTheDayOfTheWeek(int day, string what)
    {
        var (g, h, o) = Setup(f, "BUILDING_FORTUITOUS_SANCTUARY");
        g.S.Day = day;
        Assert.Contains(Visit(g, h, o), e => e is ObjectVisited v && v.What == what);
    }
}
