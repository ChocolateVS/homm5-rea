using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Treasure visit 00a82c40 with the reward roller FUN_00a82720 and Fortunate Adventurer (vcall 0x178 = 00b4b540).</summary>
[Collection("adv")]
public class TreasureRulesTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h, MapObject o, ObjState os) Setup(Fixture f, string type)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.MovePoints = h.MaxMovePoints = 2000;
        var o = g.Map.Objects.First(x => x.Kind == ObjectKind.Treasure && x.ActiveTiles.Any() && g.S.Obj(x.Index).Guards.Count == 0);
        var old = o.Shared;
        o.Shared = new SharedDef { Type = type, Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
        var os = g.S.Obj(o.Index);
        os.ArtifactId = null; os.SpellId = null;
        return (g, h, o, os);
    }

    static List<AdvEvent> Visit(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        return r.Events.ToList();
    }

    [Theory]
    [InlineData(0, 500, 1000)]
    [InlineData(1, 1000, 1500)]
    [InlineData(2, 1500, 2000)]
    public void ChestOffersAmountTimesFiveHundredExperienceAndOneMoreForGold(int variant, int exp, int gold)
    {
        var (g, h, o, os) = Setup(f, "TREASURE_CHEST");
        os.Variant = variant;
        h.Perks.Remove("HERO_SKILL_FORTUNATE_ADVENTURER");
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_LEARNING");
        h.Artifacts.Clear();
        Assert.Contains(Visit(g, h, o), e => e is ChoiceOffered { Kind: "chest", Gold: var gg, Experience: var ee } && gg == gold && ee == exp);
    }

    [Fact]
    public void ChestExperienceShownWithModifiersIsAddedUnchanged()
    {
        var (g, h, o, os) = Setup(f, "TREASURE_CHEST");
        os.Variant = 1;
        h.Artifacts.Clear();
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_LEARNING");
        h.Skills.Add(new SkillEntry("HERO_SKILL_LEARNING", Mastery.Expert));   // +15 % (vcall 0x19c)
        var offer = Visit(g, h, o).OfType<ChoiceOffered>().Single();
        Assert.Equal(1150, offer.Experience);
        long before = h.Experience;
        Assert.True(g.Apply(new AnswerChoiceCmd(Accept: true, Option: 1) { Player = h.Owner }).Ok);
        Assert.Equal(before + 1150, h.Experience);
    }

    [Fact]
    public void FortunateAdventurerAddsAtLeastOneToPilesAndCampfires()
    {
        var (g, h, o, os) = Setup(f, "TREASURE_CAMPFIRE");
        os.ResourceKind = Purse.Sulfur; os.Amount = 4;
        h.Perks.Add("HERO_SKILL_FORTUNATE_ADVENTURER");
        var p = g.Current;
        int sulfur = p.Purse[Purse.Sulfur], gold = p.Purse[Purse.Gold];
        Visit(g, h, o);
        // max(1, rand(5..25) · 4 / 100) = 1.
        Assert.Equal(sulfur + 5, p.Purse[Purse.Sulfur]);
        Assert.Equal(gold + 500, p.Purse[Purse.Gold]);
    }

    [Fact]
    public void FortunateAdventurerTurnsTheBestFlotsamIntoNothing()
    {
        var (g, h, o, os) = Setup(f, "TREASURE_FLOATSAM");
        os.Variant = 3;   // amount 4 → 5 with the skill: no case in 00a82b9e
        h.Perks.Add("HERO_SKILL_FORTUNATE_ADVENTURER");
        var p = g.Current;
        int wood = p.Purse[Purse.Wood];
        Visit(g, h, o);
        Assert.Equal(wood, p.Purse[Purse.Wood]);
        Assert.True(g.S.Obj(o.Index).Removed);
    }
}
