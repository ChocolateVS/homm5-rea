using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>What the monster tooltip reveals (exe 004efc30 / 004fcf90 / 004ea7a0 / 004e9ac0 / 00b4bfd0).</summary>
[Collection("adv")]
public class MonsterTooltipTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h, MapObject m) Setup(Fixture f)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        foreach (var o in g.S.Heroes.Where(x => x.Owner == h.Owner && x != h)) o.Alive = false;
        h.Perks.Remove("HERO_SKILL_SCOUTING");
        h.Perks.Remove("HERO_SKILL_DISGUISE_AND_RECKON");
        h.Artifacts.Remove("CROWN_OF_MANY_EYES");
        var m = g.Map.Objects.First(x => x.Kind == ObjectKind.Monster && !g.S.Obj(x.Index).Removed);
        h.Pos = new Tile(m.Pos.X, m.Pos.Y, m.Pos.Floor);
        return (g, h, m);
    }

    [Fact]
    public void WithoutScoutingOrReckonNothingIsRevealed()
    {
        var (g, h, m) = Setup(f);
        Assert.Equal((false, false), g.MonsterInsight(h.Owner, m.Pos));
    }

    [Fact]
    public void ScoutingRevealsCountsAndReckonRevealsCourageWithinReach()
    {
        var (g, h, m) = Setup(f);
        h.Perks.Add("HERO_SKILL_SCOUTING");
        Assert.Equal((true, false), g.MonsterInsight(h.Owner, m.Pos));
        h.Perks.Remove("HERO_SKILL_SCOUTING");
        h.Perks.Add("HERO_SKILL_DISGUISE_AND_RECKON");
        Assert.Equal((false, true), g.MonsterInsight(h.Owner, m.Pos));
        // Reach 12 (+0 for Disguise and Reckon in the data): dx² + dy² ≤ 144.
        int r = g.ReckonRadius(h);
        Assert.Equal((false, true), g.MonsterInsight(h.Owner, m.Pos with { X = h.Pos.X + r }));
        Assert.Equal((false, false), g.MonsterInsight(h.Owner, m.Pos with { X = h.Pos.X + r, Y = h.Pos.Y + 1 }));
        Assert.Equal((false, false), g.MonsterInsight(h.Owner, m.Pos with { Floor = m.Pos.Floor + 1 }));
        Assert.Equal((false, false), g.MonsterInsight(h.Owner + 1, m.Pos));
    }

    [Fact]
    public void ReckonRadiusIsTwelvePlusScoutingAndCrownOfManyEyes()
    {
        var (g, h, _) = Setup(f);
        Assert.Equal(12, g.ReckonRadius(h));
        h.Artifacts.Add("CROWN_OF_MANY_EYES");
        Assert.Equal(16, g.ReckonRadius(h));
        h.Perks.Add("HERO_SKILL_SCOUTING");
        var inc = H5.Core.Data.XmlHelpers.Int(g.Db.Stats.Root.Element("adventure")?.Element("HeroSkills"), "Scouting_VisibilityIncrease", 0);
        Assert.Equal(12 + inc + 4, g.ReckonRadius(h));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.19, 0)]
    [InlineData(0.2, 1)]
    [InlineData(0.39, 1)]
    [InlineData(0.4, 2)]
    [InlineData(0.6, 3)]
    [InlineData(0.79, 3)]
    [InlineData(0.8, 4)]
    [InlineData(1.0, 4)]
    public void DangerThresholds(double share, int level) => Assert.Equal(level, Engine.Adventure.DangerLevel(share));

    [Fact]
    public void DangerNeedsAHeroAndLiveMonsters()
    {
        var (g, h, m) = Setup(f);
        var guards = g.S.Obj(m.Index).Guards;
        Assert.Null(g.MonsterDanger(null, guards));
        Assert.Null(g.MonsterDanger(h, new List<Stack>()));
        Assert.NotNull(g.MonsterDanger(h, guards));
        Assert.Equal(4, g.MonsterDanger(h, new List<Stack> { new(guards[0].Creature, 100000) }));
    }
}
