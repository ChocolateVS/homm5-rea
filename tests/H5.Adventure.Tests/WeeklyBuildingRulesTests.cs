using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Weekly resource buildings: 00b02740 cases 9 (windmill), 8 (water wheel), 0x44 (mystical garden).</summary>
[Collection("adv")]
public class WeeklyBuildingRulesTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h, MapObject o) Setup(Fixture f, string type)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.MovePoints = h.MaxMovePoints = 2000;
        var o = g.Map.Objects.First(x => x.Kind == ObjectKind.Building && x.ActiveTiles.Any() && g.S.Obj(x.Index).Guards.Count == 0
                                         && AdventureDb.BankFor(x.Shared.Type) == null);
        var old = o.Shared;
        o.Shared = new SharedDef { Type = type, Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
        return (g, h, o);
    }

    static void Visit(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public void WindmillGivesThreeToSixOfOreToGemsOnly()
    {
        for (int i = 0; i < 30; i++)
        {
            var (g, h, o) = Setup(f, "BUILDING_WINDMILL");
            g.S.Rng = new H5.Combat.Rng((ulong)(500 + i));
            h.Perks.Remove("HERO_SKILL_FORTUNATE_ADVENTURER");
            var before = g.Current.Purse.R.ToArray();
            Visit(g, h, o);
            var d = g.Current.Purse.R.Select((v, k) => v - before[k]).ToArray();
            Assert.Equal(0, d[Purse.Wood]);
            Assert.Equal(0, d[Purse.Gold]);
            Assert.InRange(d.Sum(), 3, 6);
        }
    }

    [Fact]
    public void WaterWheelGoldGoesThroughFortunateAdventurer()
    {
        var (g, h, o) = Setup(f, "BUILDING_WATER_WHEEL");
        g.S.Day = 8;
        h.Perks.Add("HERO_SKILL_FORTUNATE_ADVENTURER");
        int gold = g.Current.Purse[Purse.Gold];
        Visit(g, h, o);
        Assert.InRange(g.Current.Purse[Purse.Gold] - gold, 1050, 1250);
    }
}
