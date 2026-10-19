using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Attribute buildings: 00b02740 cases 0x32..0x36 and 0x6a, FUN_00b01980 (RaiseAttributesBuildings).</summary>
[Collection("adv")]
public class StatBuildingRulesTests(Fixture f)
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

    static List<AdvEvent> Visit(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        return r.Events.ToList();
    }

    [Fact]
    public void MercenaryCampRaisesWhatTheDataListsOncePerHero()
    {
        var (g, h, o) = Setup(f, "BUILDING_MERCENARY_CAMP");
        int attack = h.Attack;
        Visit(g, h, o);
        Assert.Equal(attack + 1, h.Attack);
        Visit(g, h, o);
        Assert.Equal(attack + 1, h.Attack);
    }

    [Fact]
    public void LibraryRefusesALowHeroWithItsFourthText()
    {
        var (g, h, o) = Setup(f, "BUILDING_LIBRARY_OF_ENLIGHTENMENT");
        h.Level = 5;
        h.Perks.Remove("HERO_SKILL_WISDOM");
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_WISDOM");
        int k = h.Knowledge;
        Assert.Contains(Visit(g, h, o), e => e is ObjectVisited { What: "msg:4" });
        Assert.Equal(k, h.Knowledge);
    }

    [Fact]
    public void LibraryTeachesALowHeroWithWisdom()
    {
        var (g, h, o) = Setup(f, "BUILDING_LIBRARY_OF_ENLIGHTENMENT");
        h.Level = 5;
        h.Perks.Add("HERO_SKILL_WISDOM");
        int k = h.Knowledge, sp = h.SpellPower;
        Visit(g, h, o);
        Assert.Equal(k + 2, h.Knowledge);
        Assert.Equal(sp + 2, h.SpellPower);
    }

    [Fact]
    public void FountainOfFortuneGivesMinusOneToThreeAndFortunateAdventurerAvoidsTheLoss()
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < 40; i++)
        {
            var (g, h, o) = Setup(f, "BUILDING_FONTAIN_OF_FORTUNE");
            g.S.Rng = new H5.Combat.Rng((ulong)(1000 + i));
            h.NextBattleLuck = 0;
            bool fa = i % 2 == 1;
            if (fa) h.Perks.Add("HERO_SKILL_FORTUNATE_ADVENTURER"); else h.Perks.Remove("HERO_SKILL_FORTUNATE_ADVENTURER");
            Visit(g, h, o);
            Assert.Contains(h.NextBattleLuck, fa ? new[] { 1, 2, 3 } : new[] { -1, 1, 2, 3 });
            seen.Add(h.NextBattleLuck);
        }
        Assert.Contains(-1, seen);
        Assert.Contains(3, seen);
    }

    [Fact]
    public void LeanToStaysAndShowsItsThirdTextOnceLooted()
    {
        var (g, h, o) = Setup(f, "BUILDING_LEAN_TO");
        var p = g.Current;
        int before = Enumerable.Range(0, 6).Sum(i => p.Purse[i]);
        Assert.Contains(Visit(g, h, o), e => e is ObjectVisited { What: "msg:2" });
        int got = Enumerable.Range(0, 6).Sum(i => p.Purse[i]) - before;
        Assert.InRange(got, 1, 4);
        Assert.False(g.S.Obj(o.Index).Removed);
        Assert.Contains(Visit(g, h, o), e => e is ObjectVisited { What: "msg:3" });
        Assert.Equal(got, Enumerable.Range(0, 6).Sum(i => p.Purse[i]) - before);
    }
}
