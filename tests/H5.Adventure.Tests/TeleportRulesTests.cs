using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Teleports: CAdvMapTeleport visit 00c24dc0 → 00c252f0, exits FUN_00afabe0 / 00afadd0.</summary>
[Collection("adv")]
public class TeleportRulesTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h, List<MapObject> os) Setup(Fixture f, params string[] types)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.MovePoints = h.MaxMovePoints = 2000;
        // Placed buildings stand in for the teleports (one group, GroupID 77).
        var list = g.Map.Objects.Where(x => x.Kind == ObjectKind.Building && x.ActiveTiles.Any() && g.S.Obj(x.Index).Guards.Count == 0
                                            && AdventureDb.BankFor(x.Shared.Type) == null).Take(types.Length).ToList();
        for (int i = 0; i < types.Length; i++)
        {
            var o = list[i];
            var old = o.Shared;
            o.Shared = new SharedDef { Type = types[i], Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
            o.Props["GroupID"] = "77";
        }
        return (g, h, list);
    }

    static List<AdvEvent> Visit(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        return r.Events.ToList();
    }

    [Fact]
    public void TwoWayMonolithSendsTheHeroToTheOtherOfItsGroup()
    {
        var (g, h, os) = Setup(f, "BUILDING_MONOLITH_TWO_WAY", "BUILDING_MONOLITH_TWO_WAY");
        Visit(g, h, os[0]);
        Assert.Equal(os[1].ActiveTiles.First(), h.Pos);
    }

    [Fact]
    public void MonolithWithoutExitShowsItsSecondText()
    {
        var (g, h, os) = Setup(f, "BUILDING_MONOLITH_ONE_WAY_ENTRANCE");
        Assert.Contains(Visit(g, h, os[0]), e => e is ObjectVisited { What: "msg:2" });
        Assert.Equal(os[0].ActiveTiles.First(), h.Pos);
    }

    [Fact]
    public void HeroOnTheExitBlocksIt()
    {
        var (g, h, os) = Setup(f, "BUILDING_MONOLITH_ONE_WAY_ENTRANCE", "BUILDING_MONOLITH_ONE_WAY_EXIT");
        var other = g.S.Heroes.First(x => x.Alive && x.Id != h.Id);
        other.Pos = os[1].ActiveTiles.First();
        Assert.Contains(Visit(g, h, os[0]), e => e is ObjectVisited { What: "msg:2" });
        Assert.Equal(os[0].ActiveTiles.First(), h.Pos);
    }

    [Fact]
    public void OneWayExitDoesNotTeleport()
    {
        var (g, h, os) = Setup(f, "BUILDING_MONOLITH_ONE_WAY_EXIT", "BUILDING_MONOLITH_ONE_WAY_ENTRANCE");
        Assert.Contains(Visit(g, h, os[0]), e => e is ObjectVisited { What: "msg:2" });
        Assert.Equal(os[0].ActiveTiles.First(), h.Pos);
    }
}
