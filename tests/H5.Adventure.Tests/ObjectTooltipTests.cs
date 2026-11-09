using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// What an adventure object's hover tooltip carries (ITooltiped vf0 per class: default 004b5190, building 00b01390 /
/// 00b01470, sphinx 009cb850, tent 00bc94b0, dwelling 009a6660 / 009a6210, mine 00b091d0, abandoned mine 008b5f20,
/// garrison 00ca4ee0, artifact 004ed5c0, flagged 004527e0).
/// </summary>
[Collection("adv")]
public class ObjectTooltipTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h) Setup(Fixture f)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        return (g, h);
    }

    static MapObject Obj(ObjectKind kind, string type, int index = 900001, params string[] messages) => new()
    {
        Index = index,
        Kind = kind,
        Shared = new SharedDef { Type = type, Messages = messages.Length > 0 ? messages.ToList() : new List<string> { "n.txt", "d.txt" } },
    };

    [Theory]
    [InlineData("BUILDING_WINDMILL", "player_week")]
    [InlineData("BUILDING_MAGIC_SPRING", "player_week")]
    [InlineData("BUILDING_LEAN_TO", "player")]
    [InlineData("BUILDING_OBELISK", "player")]
    [InlineData("BUILDING_TOMB_OF_THE_WARRIOR", "player")]
    [InlineData("BUILDING_STABLES", "stables")]
    [InlineData("BUILDING_RALLY_FLAG", "hero_battle")]
    [InlineData("BUILDING_FORTUITOUS_SANCTUARY", "hero_battle")]
    [InlineData("BUILDING_MAGIC_WELL", "well")]
    [InlineData("BUILDING_STAR_AXIS", "hero")]
    [InlineData("BUILDING_LIBRARY_OF_ENLIGHTENMENT", "hero")]
    [InlineData("BUILDING_WITCH_HUT", "witch_hut")]
    [InlineData("BUILDING_TRADING_POST", "")]
    [InlineData("BUILDING_DEN_OF_THIEVES", "")]
    public void BuildingMarkerGroupsFollowTheTypeSwitch(string type, string group) =>
        Assert.Equal(group, Engine.Adventure.BuildingMarkGroup(type));

    [Fact]
    public void DefaultTooltipIsNameAndSecondMessageAsDescription()
    {
        var (g, h) = Setup(f);
        var t = g.ObjectTooltip(h.Owner, h, Obj(ObjectKind.Treasure, "TREASURE", messages: new[] { "name.txt", "desc.txt", "x.txt" }))!;
        Assert.Equal(ObjTooltipKind.Common, t.Kind);
        Assert.Equal("name.txt", t.NameRef);
        Assert.Equal("desc.txt", t.DescRef);
        Assert.Null(t.Mark);
        // 00ce1820: fewer than two messages → no description.
        Assert.Null(g.ObjectTooltip(h.Owner, h, Obj(ObjectKind.Treasure, "TREASURE", messages: new[] { "name.txt" }))!.DescRef);
    }

    [Fact]
    public void HeroVisitedBuildingsShowVisitedOnlyWithAHero()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.Building, "BUILDING_STAR_AXIS");
        Assert.Equal("TT_NOT_VISITED", g.ObjectTooltip(h.Owner, h, o)!.Mark);
        h.VisitedOnce.Add(o.Index);
        Assert.Equal("TT_VISITED", g.ObjectTooltip(h.Owner, h, o)!.Mark);
        // 00b014b3: no hero argument → no marker.
        Assert.Null(g.ObjectTooltip(h.Owner, null, o)!.Mark);
        var flag = Obj(ObjectKind.Building, "BUILDING_RALLY_FLAG", 900002);
        Assert.Equal("TT_NOT_VISITED", g.ObjectTooltip(h.Owner, h, flag)!.Mark);
        h.VisitedSinceBattle.Add(flag.Index);
        Assert.Equal("TT_VISITED", g.ObjectTooltip(h.Owner, h, flag)!.Mark);
        var stables = Obj(ObjectKind.Building, "BUILDING_STABLES", 900003);
        h.StablesThisWeek = true;
        Assert.Equal("TT_VISITED", g.ObjectTooltip(h.Owner, h, stables)!.Mark);
    }

    [Fact]
    public void PlayerVisitedBuildingsAndSphinxUseThePlayersVisits()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.Building, "BUILDING_OBELISK");
        Assert.Equal("TT_NOT_VISITED", g.ObjectTooltip(h.Owner, h, o)!.Mark);
        g.S.Players.First(p => p.Index == h.Owner).VisitedObjects.Add(o.Index);
        Assert.Equal("TT_VISITED", g.ObjectTooltip(h.Owner, h, o)!.Mark);
        var sphinx = Obj(ObjectKind.Sphinx, "SPHINX", 900002);
        Assert.Equal("TT_NOT_VISITED", g.ObjectTooltip(h.Owner, null, sphinx)!.Mark);
    }

    [Fact]
    public void WitchHutNamesItsSkillOnceThePlayerKnowsIt()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.Building, "BUILDING_WITCH_HUT");
        g.S.Obj(o.Index).SkillId = "HERO_SKILL_LOGISTICS";
        var t = g.ObjectTooltip(h.Owner, h, o)!;
        Assert.Null(t.NameSkill);
        Assert.Null(t.Mark);
        g.S.Players.First(p => p.Index == h.Owner).VisitedObjects.Add(o.Index);
        t = g.ObjectTooltip(h.Owner, h, o)!;
        Assert.Equal("HERO_SKILL_LOGISTICS", t.NameSkill);
        Assert.Equal("TT_NOT_LEARNED", t.Mark);
        h.VisitedOnce.Add(o.Index);
        Assert.Equal("TT_LEARNED", g.ObjectTooltip(h.Owner, h, o)!.Mark);
    }

    [Fact]
    public void KeymasterTentReportsTheKeyByColour()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.Tent, "BUILDING_KEYMASTER_TENT");
        o.Shared.Color = 3;
        Assert.Equal("ADVENTURE_KEYNOTOBTAINED", g.ObjectTooltip(h.Owner, h, o)!.Mark);
        g.S.Players.First(p => p.Index == h.Owner).Keys.Add(3);
        Assert.Equal("ADVENTURE_KEYOBTAINED", g.ObjectTooltip(h.Owner, h, o)!.Mark);
    }

    [Fact]
    public void MineArmyIsExactForTheTeamAndRoughWithThreatLevelOtherwise()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.Mine, "BUILDING_SAWMILL");
        var os = g.S.Obj(o.Index);
        os.Owner = h.Owner;
        os.Army[0] = new Stack("CREATURE_PEASANT", 30);
        var own = g.ObjectTooltip(h.Owner, h, o)!;
        Assert.Equal(ObjTooltipKind.Mine, own.Kind);
        Assert.Equal(h.Owner, own.Owner);
        Assert.Equal(ArmyDetail.Exact, own.Detail);
        Assert.Null(own.Danger);
        var enemy = g.S.Players.First(p => p.Index != h.Owner && p.Team != g.S.Players.First(q => q.Index == h.Owner).Team);
        var foe = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == enemy.Index) ?? h;
        var theirs = g.ObjectTooltip(enemy.Index, foe.Owner == enemy.Index ? foe : null, o)!;
        Assert.Equal(ArmyDetail.Rough, theirs.Detail);
        Assert.Single(theirs.Army!);
        Assert.Equal(foe.Owner == enemy.Index, theirs.Danger != null);
    }

    [Fact]
    public void AbandonedMineDescriptionComesFromItsResource()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.AbandonedMine, "BUILDING_ABANDONED_MINE");
        g.S.Obj(o.Index).ResourceKind = Purse.Gold;
        var t = g.ObjectTooltip(h.Owner, h, o)!;
        Assert.Null(t.DescRef);
        Assert.Equal("ADVENTURE_ABANDONED_MINE_DESC_GOLD", t.DescRpg);
        g.S.Obj(o.Index).ResourceKind = Purse.Wood;
        Assert.Null(g.ObjectTooltip(h.Owner, h, o)!.DescRpg);
    }

    [Fact]
    public void DwellingOwnerSeesTheCreaturesLine()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.Dwelling, "BUILDING_PEASANT_HUT");
        o.Shared.DwellingCreatures.Add("CREATURE_PEASANT");
        var os = g.S.Obj(o.Index);
        os.Available = new[] { 0 };
        Assert.False(g.ObjectTooltip(h.Owner, h, o)!.DwellingLine);
        os.Owner = h.Owner;
        var t = g.ObjectTooltip(h.Owner, h, o)!;
        Assert.Equal(ObjTooltipKind.Flagged, t.Kind);
        Assert.Equal("ADVENTURE_DWELLING_TOOLTIP_CREATURES_NOT_AVAILABLE", t.Mark);
        Assert.True(t.DwellingLine);
        os.Available = new[] { 7 };
        t = g.ObjectTooltip(h.Owner, h, o)!;
        Assert.Equal("ADVENTURE_DWELLING_TOOLTIP_CREATURES_AVAILABLE", t.Mark);
        Assert.Equal(7, t.DwellingCount);
        // Refugee camp (type 0x5e) uses the default tooltip.
        Assert.Equal(ObjTooltipKind.Common, g.ObjectTooltip(h.Owner, h, Obj(ObjectKind.Dwelling, "BUILDING_REFUGEE_CAMP", 900002))!.Kind);
    }

    [Fact]
    public void ArtifactTooltipNeedsAnArtifact()
    {
        var (g, h) = Setup(f);
        var o = Obj(ObjectKind.Artifact, "ARTIFACT");
        Assert.Null(g.ObjectTooltip(h.Owner, h, o));
        o.ArtifactId = "SWORD_OF_RUINS";
        Assert.Equal("SWORD_OF_RUINS", g.ObjectTooltip(h.Owner, h, o)!.ArtifactId);
    }
}
