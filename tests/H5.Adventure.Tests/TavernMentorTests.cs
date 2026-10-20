using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Map tavern hiring (CHireHeroInTavernCmd 004f5700, status 004f46a0, price 00b4ced0) and the Memory Mentor
/// (CUnlearnHeroSkillCmd 00429d90, can-unlearn 00b4edc0, price 00b4c300, CRelearnHeroSkillCmd 00429e50).
/// </summary>
[Collection("adv")]
public class TavernMentorTests(Fixture f)
{
    [Fact]
    public void HirePriceIsFiveHundredTimesHeroesPlusFour()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        int n = g.S.Heroes.Count(h => h.Alive && h.Owner == p.Index);
        Assert.Equal(500 * (n + 4), g.HireCost(p));
        var h0 = g.S.Heroes.First(h => h.Alive && h.Owner == p.Index);
        foreach (var h in g.S.Heroes.Where(h => h.Owner == p.Index && h != h0)) h.Alive = false;
        Assert.Equal(2500, g.HireCost(p));
    }

    /// <summary>00b4ced0: a town specialised in TOWN_BONUS_HEROES (vf 0xdc → +0x5c == 0x25) hires at (heroes + 4) × 4500 / 10.</summary>
    [Fact]
    public void TownBonusHeroesHiresAtNinetyPercent()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var t = g.S.Towns.First();
        int n = g.S.Heroes.Count(h => h.Alive && h.Owner == p.Index) + 4;
        Assert.Equal(n * 500, g.HireCost(p, t));
        const string spec = "GameMechanics/TownSpecialization/Fortress/Random/Heroes.xdb";
        if (g.Db.TownSpecializationBonus(spec) != "TOWN_BONUS_HEROES") return;   // MMH55 data only
        t.Specialization = spec;
        Assert.Equal(n * 4500 / 10, g.HireCost(p, t));
        Assert.Equal(n * 500, g.HireCost(p));
    }

    [Fact]
    public void MapTavernHiresOntoTheLastFreeTileAndPays()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var o = g.Map.Objects.First(x => x.Kind == ObjectKind.Building && x.ActiveTiles.Any() && H5.Adventure.Data.AdventureDb.BankFor(x.Shared.Type) == null);
        var old = o.Shared;
        o.Shared = new SharedDef { Type = Engine.Adventure.MapTavern, Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
        var offer = g.S.HeroPool.First();
        p.Tavern[0] = offer;
        int cost = g.HireCost(p);

        // 004f46a0 status 3: gold below the price.
        p.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, cost - 1));
        Assert.Equal(3, g.MapTavernHireStatus(p));
        Assert.False(g.Apply(new HireHeroInTavernCmd(h.Id, o.Index, 0) { Player = p.Index }).Ok);

        p.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, cost + 100));
        var tiles = g.MapTavernFreeTiles(h);
        Assert.NotEmpty(tiles);
        var r = g.Apply(new HireHeroInTavernCmd(h.Id, o.Index, 0) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        var hired = g.S.Heroes.Single(x => x.Alive && x.DefId == offer);
        Assert.Equal(tiles[^1], hired.Pos);
        Assert.Equal(100, p.Purse[Purse.Gold]);
        Assert.Null(p.Tavern[0]);
    }

    [Fact]
    public void UnlearnPriceIsCeilSqrtLevelTimesFiveHundred()
    {
        var h = new HeroState();
        foreach (var (level, cost) in new[] { (1, 500), (2, 1000), (4, 1000), (5, 1500), (10, 2000), (16, 2000), (17, 2500), (30, 3000) })
        {
            h.Level = level;
            Assert.Equal(cost, Engine.Adventure.UnlearnCost(h));
        }
    }

    [Fact]
    public void UnlearnLowersASkillOnlyWhileItsPerksStillFitAndKeepsTheRacialBasic()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        h.PendingLevelUps.Clear();
        h.Level = 9;
        const string skill = "HERO_SKILL_LOGISTICS";
        h.Skills.RemoveAll(s => s.SkillId == skill);
        h.Skills.Add(new SkillEntry(skill, Mastery.Advanced));
        var perks = g.Db.Perks.Values.Where(d => d.IsStandardPerk && d.BasicSkill == skill).Select(d => d.Id).Take(2).ToList();
        h.Perks.RemoveAll(perks.Contains);
        h.Perks.AddRange(perks);
        // Advanced (2) with two perks: the level cannot go (perks must number fewer than the mastery).
        Assert.False(g.CanUnlearn(h, skill));
        Assert.True(g.CanUnlearn(h, perks[1]));

        p.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 1500 * 2 - 1));
        Assert.True(g.Apply(new UnlearnHeroSkillCmd(h.Id, perks[1]) { Player = p.Index }).Ok);
        Assert.DoesNotContain(perks[1], h.Perks);
        Assert.Equal(1499, p.Purse[Purse.Gold]);
        Assert.Equal(1, h.UnlearnedPoints);
        Assert.NotNull(h.RelearnOffer);
        // Now one perk on Advanced: the skill may drop one level, but the purse is short.
        Assert.True(g.CanUnlearn(h, skill));
        Assert.False(g.Apply(new UnlearnHeroSkillCmd(h.Id, skill) { Player = p.Index }).Ok);
        p.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 1500));
        Assert.True(g.Apply(new UnlearnHeroSkillCmd(h.Id, skill) { Player = p.Index }).Ok);
        Assert.Equal(Mastery.Basic, h.MasteryOf(skill));
        Assert.Equal(2, h.UnlearnedPoints);

        // Relearn: one forgotten point per command; the offer is rerolled while points remain.
        Assert.True(g.Apply(new RelearnHeroSkillCmd(h.Id, 0) { Player = p.Index }).Ok);
        Assert.Equal(1, h.UnlearnedPoints);
        Assert.NotNull(h.RelearnOffer);
        Assert.True(g.Apply(new RelearnHeroSkillCmd(h.Id, 0) { Player = p.Index }).Ok);
        Assert.Equal(0, h.UnlearnedPoints);
        Assert.Null(h.RelearnOffer);
        Assert.False(g.Apply(new RelearnHeroSkillCmd(h.Id, 0) { Player = p.Index }).Ok);

        // The racial skill keeps its Basic level (00b4edc0: mastery must exceed 1).
        h.Town = "TOWN_HEAVEN";
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_TRAINING");
        h.Skills.Add(new SkillEntry("HERO_SKILL_TRAINING", Mastery.Basic));
        h.Perks.RemoveAll(x => g.Db.Perks.TryGetValue(x, out var d) && d.BasicSkill == "HERO_SKILL_TRAINING");
        Assert.False(g.CanUnlearn(h, "HERO_SKILL_TRAINING"));
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_TRAINING");
        h.Skills.Add(new SkillEntry("HERO_SKILL_TRAINING", Mastery.Advanced));
        Assert.True(g.CanUnlearn(h, "HERO_SKILL_TRAINING"));
    }

    [Fact]
    public void APerkThatAnotherSpecialPerkNeedsCannotBeForgotten()
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.PendingLevelUps.Clear();
        var special = g.Db.Perks.Values.First(d => d.IsSpecialPerk && d.PrereqByClass.TryGetValue(h.Class, out var deps) && deps.Count > 0);
        var dep = special.PrereqByClass[h.Class][0];
        h.Perks.RemoveAll(x => x == dep || x == special.Id);
        h.Perks.Add(dep);
        Assert.True(g.CanUnlearn(h, dep));
        h.Perks.Add(special.Id);
        Assert.False(g.CanUnlearn(h, dep));
        Assert.True(g.CanUnlearn(h, special.Id));
    }
}
