using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Creature upgrades (town / Hill Fort / regrade) priced as in H5_Game.exe (FUN_004bb570, 008b4360, 004bbce0).</summary>
[Collection("adv")]
public class MapBuildingTests(Fixture f)
{
    static Resources Diff(Resources a, Resources b) => new(a.Wood - b.Wood, a.Ore - b.Ore, a.Mercury - b.Mercury,
        a.Crystal - b.Crystal, a.Sulfur - b.Sulfur, a.Gem - b.Gem, a.Gold - b.Gold);

    [Fact]
    public void UpgradeQuotesMatchTheExe()
    {
        var g = f.NewGame();
        var c = f.Db.Core.Creatures;
        // Town: (upgraded - base) × count.
        var (t, cost) = g.UpgradeQuote("CREATURE_FOOTMAN", 1, 10, hillFort: false);
        Assert.Equal("CREATURE_SWORDSMAN", t);
        Assert.Equal(Purse.Scale(Diff(c["CREATURE_SWORDSMAN"].Cost, c["CREATURE_FOOTMAN"].Cost), 10), cost);
        // Hill Fort, tier 3: factor 1.
        Assert.Equal(cost, g.UpgradeQuote("CREATURE_FOOTMAN", 1, 10, hillFort: true).Cost);
        // Tier 1 upgrades for free at a Hill Fort; tier 2 for half; tier 7 for double.
        var peasant = c.Values.First(x => x.Tier == 1 && !x.Upgrade && x.Upgrades.Count > 0);
        Assert.Equal(0, g.UpgradeQuote(peasant.Id, 1, 50, true).Cost.Gold);
        var t2 = c.Values.First(x => x.Tier == 2 && !x.Upgrade && x.Upgrades.Count > 0);
        int d2 = c[t2.Upgrades[0]].Cost.Gold - t2.Cost.Gold;
        Assert.Equal((int)MathF.Round(d2 * 3 * 0.5f, MidpointRounding.ToEven), g.UpgradeQuote(t2.Id, 1, 3, true).Cost.Gold);
        var t7 = c.Values.First(x => x.Tier == 7 && !x.Upgrade && x.Upgrades.Count > 0);
        Assert.Equal(2 * (c[t7.Upgrades[0]].Cost.Gold - t7.Cost.Gold), g.UpgradeQuote(t7.Id, 1, 1, true).Cost.Gold);
        // Regrade Squire -> Vindicator: ROUND(target cost × RegradeCostCoef 0.1) × count.
        var (rt, rc) = g.UpgradeQuote("CREATURE_SWORDSMAN", 2, 4, false);
        Assert.Equal("CREATURE_VINDICATOR", rt);
        Assert.Equal(4 * (int)MathF.Round(c["CREATURE_VINDICATOR"].Cost.Gold * 0.1f, MidpointRounding.ToEven), rc.Gold);
        // Upgrading to itself is not an upgrade.
        Assert.Null(g.UpgradeQuote("CREATURE_SWORDSMAN", 1, 4, false).Target);
    }

    [Fact]
    public void TownUpgradeNeedsTheUpgradedDwelling()
    {
        var g = f.NewGame();
        var p = g.Current;
        var town = g.S.Towns.First(x => x.Owner == p.Index);
        var basic = f.Db.Core.Creatures.Values.First(x => x.Town == town.Type && x.Tier == 1 && !x.Upgrade && x.Upgrades.Count > 0);
        foreach (var h in g.S.Heroes.Where(h => h.InTown == town.Object)) h.InTown = -1;
        town.Garrison[0] = new Stack(basic.Id, 7);
        p.Purse = new Purse(new Resources(99, 99, 99, 99, 99, 99, 99999));
        town.Built["TB_DWELLING_1"] = 1;
        Assert.False(g.Apply(new UpgradeCreatureCmd(-1, town.Object, 0, 1) { Player = p.Index }).Ok);
        town.Built["TB_DWELLING_1"] = 2;
        var target = g.DwellingCreature(town, 1, 1)!;
        int up = basic.Upgrades.IndexOf(target) + 1;
        var (_, cost) = g.UpgradeQuote(basic.Id, up, 7, false);
        var r = g.Apply(new UpgradeCreatureCmd(-1, town.Object, 0, up) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(target, town.Garrison[0]!.Creature);
        Assert.Equal(7, town.Garrison[0]!.Count);
        Assert.Equal(99999 - cost.Gold, p.Purse[Purse.Gold]);
    }
}

/// <summary>Witch Hut and Tree of Knowledge on A2M1 (both stock objects).</summary>
[Collection("adv")]
public class WitchHutAndTreeTests(Fixture f)
{
    [Fact]
    public void WitchHutTeachesItsSkillOnce()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var hut = g.Map.Objects.First(o => o.Shared.Type == "BUILDING_WITCH_HUT");
        Assert.Equal(12, g.WitchHutSkills.Count);
        Assert.DoesNotContain("HERO_SKILL_NECROMANCY", g.WitchHutSkills);
        h.Skills.Clear();
        g.InteractWithObject(h, hut.Index);
        var skill = g.WitchHutSkill(hut.Index);
        Assert.NotNull(skill);
        Assert.Contains(g.WitchHutSkills, s => s == skill);
        Assert.Equal(Mastery.Basic, h.MasteryOf(skill!));
        Assert.Contains(hut.Index, p.VisitedObjects);
        // Once per hero.
        g.InteractWithObject(h, hut.Index);
        Assert.Equal(Mastery.Basic, h.MasteryOf(skill!));
        // Another hero who knows the skill improves it by one level.
        var other = new HeroState { Id = 999, Owner = p.Index, Alive = true, Pos = h.Pos };
        other.Skills.Add(new SkillEntry(skill!, Mastery.Advanced));
        g.S.Heroes.Add(other);
        g.InteractWithObject(other, hut.Index);
        Assert.Equal(Mastery.Expert, other.MasteryOf(skill!));
        Assert.Equal(skill, g.WitchHutSkill(hut.Index));
    }

    [Fact]
    public void TreeOfKnowledgeGrantsOneLevelOncePerHero()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var tree = g.Map.Objects.First(o => o.Shared.Type == "BUILDING_TREE_OF_KNOWLEDGE");
        h.Pos = tree.ActiveTiles.FirstOrDefault(tree.Pos);
        Assert.False(g.Apply(new UseTreeOfKnowledgeCmd(h.Id, tree.Index) { Player = p.Index }).Ok);   // not visited yet
        g.InteractWithObject(h, tree.Index);
        p.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 20, 5000));
        var price = g.TreeOfKnowledgePrice(tree.Index);
        int level = h.Level;
        var r = g.Apply(new UseTreeOfKnowledgeCmd(h.Id, tree.Index) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(level + 1, h.Level);
        Assert.Equal(5000 - price.Gold, p.Purse[Purse.Gold]);
        Assert.Equal(20 - price.Gem, p.Purse[Purse.Gem]);
        Assert.False(g.Apply(new UseTreeOfKnowledgeCmd(h.Id, tree.Index) { Player = p.Index }).Ok);
    }
}

/// <summary>Artifact merchants (exe CBlackMarket): 3 minor, 2 major, 1 relic; CostOfGold, halved in the Week of Trade.</summary>
[Collection("adv")]
public class ArtifactMerchantTests(Fixture f)
{
    [Fact]
    public void MapMerchantIsStockedAndSells()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var wagon = g.Map.Objects.First(o => o.Shared.Type == "BUILDING_BLACK_MARKET");
        var stock = g.MerchantStock(wagon.Index);
        Assert.Equal(6, stock.Count);
        Assert.Equal(stock.Count(a => a != null), stock.Where(a => a != null).Distinct().Count());
        string[] classes = { "ARTF_CLASS_MINOR", "ARTF_CLASS_MINOR", "ARTF_CLASS_MINOR", "ARTF_CLASS_MAJOR", "ARTF_CLASS_MAJOR", "ARTF_CLASS_RELIC" };
        for (int i = 0; i < 6; i++)
            if (stock[i] is { } a) { Assert.Equal(classes[i], f.Db.ArtifactClass[a]); Assert.DoesNotContain(a, f.Db.ArtifactsNotForSale); }
        Assert.Contains("GOLDEN_SEXTANT", f.Db.ArtifactsNotForSale);
        int slot = Enumerable.Range(0, 6).First(i => stock[i] != null);
        var art = stock[slot]!;
        p.Purse[Purse.Gold] = 100000;
        Assert.False(g.Apply(new BuyMerchantArtifactCmd(h.Id, wagon.Index, slot) { Player = p.Index }).Ok);   // not there
        h.Pos = wagon.ActiveTiles.FirstOrDefault(wagon.Pos);
        g.S.MoonWeek = "WEEK_OF_TRADE";
        int price = g.MerchantPrice(art);
        Assert.Equal(f.Core.Artifacts[art].CostOfGold / 2, price);
        var r = g.Apply(new BuyMerchantArtifactCmd(h.Id, wagon.Index, slot) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(art, h.Artifacts);
        Assert.Equal(100000 - price, p.Purse[Purse.Gold]);
        Assert.Null(g.MerchantStock(wagon.Index)[slot]);
    }

    [Fact]
    public void AcademyMerchantStocksWhenBuilt()
    {
        var g = f.NewGame("A2M1");
        var town = g.S.Towns.First();
        town.Type = "TOWN_ACADEMY";
        Assert.Empty(g.MerchantStock(town.Object));
        town.Built[Engine.Adventure.ArtifactMerchantBuilding] = 1;
        g.StockArtifactMerchants();
        Assert.Equal(6, g.MerchantStock(town.Object).Count);
    }
}

/// <summary>Tear of Asha, digging and obelisk puzzle pieces.</summary>
[Collection("adv")]
public class GrailTests(Fixture f)
{
    [Fact]
    public void GrailIsOneOfTheMapsGraalSpotsAndFoundByDigging()
    {
        // A2M13 places four Graal objects; one is picked and all are taken off the map (009960e0).
        var g = f.NewGame("A2M13");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var spots = g.Map.Objects.Where(o => o.Kind == Map.ObjectKind.Artifact && o.ArtifactId == Engine.Adventure.GrailArtifact).ToList();
        Assert.Equal(4, spots.Count);
        Assert.NotNull(g.S.Grail);
        var grail = g.S.Grail!.Value;
        Assert.Contains(spots, o => o.Pos == grail);
        Assert.All(spots, o => Assert.True(g.S.Obj(o.Index).Removed));
        h.Perks.Remove("HERO_SKILL_GRAIL_VISION");
        // Three tiles east: a moved hero cannot dig; a fresh one gets the "west" hint and leaves a hole.
        h.Pos = grail with { X = grail.X + 3 };
        h.MovePoints = h.MaxMovePoints - 1;
        var refused = g.Apply(new DigCmd(h.Id) { Player = p.Index });
        Assert.False(refused.Ok);
        Assert.Equal("GRAAL_NOT_ENOUGH_MOVEMENT", refused.Error);
        h.MovePoints = h.MaxMovePoints;
        var r = g.Apply(new DigCmd(h.Id) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events, e => e is ObjectVisited { What: "dig:GRAAL_HINT_WEST" });
        Assert.Equal(0, h.MovePoints);
        Assert.False(g.S.GrailFound);
        // Digging the same hole again only says so.
        var again = g.Apply(new DigCmd(h.Id) { Player = p.Index });
        Assert.Contains(again.Events, e => e is ObjectVisited { What: "dig:GRAAL_HAS_HOLE" });
        h.Pos = grail;
        h.MovePoints = h.MaxMovePoints;
        r = g.Apply(new DigCmd(h.Id) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.True(g.S.GrailFound);
        Assert.Contains(Engine.Adventure.GrailArtifact, h.Artifacts);
    }

    [Fact]
    public void MapWithoutGraalHasNoGrail() => Assert.Null(f.NewGame("A2M1").S.Grail);

    [Theory]
    // 00a7f8c0: each visit opens (36 - open) / (obelisks left).
    [InlineData(8, new[] { 4, 8, 12, 16, 21, 26, 31, 36 })]
    [InlineData(5, new[] { 7, 14, 21, 28, 36 })]
    [InlineData(3, new[] { 12, 24, 36 })]
    public void PuzzlePiecesPerObelisk(int obelisks, int[] expected)
    {
        for (int v = 1; v <= obelisks; v++) Assert.Equal(expected[v - 1], Engine.Adventure.PuzzlePiecesFor(v, obelisks));
        Assert.Equal(0, Engine.Adventure.PuzzlePiecesFor(1, obelisks, hasGrail: false));
    }

    [Theory]
    [InlineData(0, 3, "GRAAL_HINT_NORTH")]
    [InlineData(0, -3, "GRAAL_HINT_SOUTH")]
    [InlineData(3, 0, "GRAAL_HINT_EAST")]
    [InlineData(-3, 0, "GRAAL_HINT_WEST")]
    [InlineData(3, 3, "GRAAL_HINT_NORTH_EAST")]
    [InlineData(-3, -3, "GRAAL_HINT_SOUTH_WEST")]
    [InlineData(5, 1, "GRAAL_HINT_EAST")]   // 1*26 <= 5*10
    [InlineData(2, 1, "GRAAL_HINT_NORTH_EAST")]
    public void HintDirections(int dx, int dy, string hint) =>
        Assert.Equal(hint, Engine.Adventure.GrailHint(new Map.Tile(10, 10), new Map.Tile(10 + dx, 10 + dy)));

    [Fact]
    public void ObelisksUncoverPuzzlePieces()
    {
        var g = f.NewGame("A2M13");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var obelisks = g.Map.Objects.Where(o => o.Shared.Type == "BUILDING_OBELISK").ToList();
        Assert.Equal(0, g.PuzzlePiecesOpen(p));
        if (obelisks.Count == 0) return;
        g.InteractWithObject(h, obelisks[0].Index);
        Assert.Equal(Engine.Adventure.PuzzlePiecesFor(1, obelisks.Count), g.PuzzlePiecesOpen(p));
        foreach (var o in obelisks) g.InteractWithObject(h, o.Index);
        Assert.Equal(Engine.Adventure.PuzzlePieces, g.PuzzlePiecesOpen(p));
    }
}

/// <summary>Seer Hut quests (A2S4: bring an artifact; A2S2: assemble an army).</summary>
[Collection("adv")]
public class SeerHutTests(Fixture f)
{
    [Theory]
    [InlineData("A2S4")]
    [InlineData("A2S2")]
    public void QuestIsCompletedWhenMet(string map)
    {
        var g = f.NewGame(map);
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var hut = g.Map.Objects.First(o => o.Kind == Map.ObjectKind.SeerHut && o.Quest?.Objective.Kind is "OBJECTIVE_KIND_OBTAIN_ARTIFACT" or "OBJECTIVE_KIND_ASSEMBLE_ARMY");
        var (kind, id, amount, _) = g.QuestRequirement(hut.Quest!);
        Assert.NotNull(id);
        h.Pos = hut.ActiveTiles.FirstOrDefault(hut.Pos);
        Assert.False(g.SeerQuestMet(h, hut.Index));
        Assert.False(g.Apply(new CompleteSeerQuestCmd(h.Id, hut.Index) { Player = p.Index }).Ok);
        // The quest must be taken first (Accept in mode 1), once.
        Assert.True(g.Apply(new AcceptSeerQuestCmd(h.Id, hut.Index) { Player = p.Index }).Ok);
        Assert.False(g.Apply(new AcceptSeerQuestCmd(h.Id, hut.Index) { Player = p.Index }).Ok);
        Assert.False(g.Apply(new CompleteSeerQuestCmd(h.Id, hut.Index) { Player = p.Index }).Ok);
        if (kind == "artifact") { Assert.True(f.Core.Artifacts.ContainsKey(id!)); h.Artifacts.Add(id!); }
        else { Assert.True(f.Core.Creatures.ContainsKey(id!)); for (int i = 0; i < 7; i++) h.Army[i] = null; h.Army[0] = new Stack(id!, amount); }
        Assert.True(g.SeerQuestMet(h, hut.Index));
        var r = g.Apply(new CompleteSeerQuestCmd(h.Id, hut.Index) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.True(g.S.Obj(hut.Index).QuestDone);
        Assert.False(g.Apply(new CompleteSeerQuestCmd(h.Id, hut.Index) { Player = p.Index }).Ok);
    }

    [Fact]
    public void RewardResourceSkipsSulfur()
    {
        // exe 006eefe0 tests wood, ore, mercury, crystal, gems, gold — not sulfur.
        Assert.Equal(-1, Engine.Adventure.ShownRewardResource(new[] { 0, 0, 0, 0, 5, 0, 0 }));
        Assert.Equal(5, Engine.Adventure.ShownRewardResource(new[] { 0, 0, 0, 0, 5, 3, 100 }));
        Assert.Equal(0, Engine.Adventure.ShownRewardResource(new[] { 1, 0, 0, 0, 0, 0, 100 }));
    }
}

/// <summary>Sphinx riddles (A2M7 has a random-riddle Sphinx).</summary>
[Collection("adv")]
public class SphinxTests(Fixture f)
{
    [Fact]
    public void RiddlePoolHasTheStockRiddles()
    {
        var g = f.NewGame("A2M7");
        Assert.Equal(30, g.Db.RandomRiddles.Count);
        Assert.All(g.Db.RandomRiddles, r => { Assert.Equal(3, r.Answers.Count); Assert.NotEmpty(r.Question); });
        Assert.Equal(1500, g.Db.RandomRiddles[0].Experience);
        Assert.Equal(3000, g.Db.RandomRiddles[0].Gold);
        Assert.Equal("BOOTS_OF_SPEED", g.Db.RandomRiddles[0].Artifact);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void AnswerRewardsOrFailsAndUsesTheSphinx(bool right, int reward)
    {
        var g = f.NewGame("A2M7");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var sx = g.Map.Objects.First(o => o.Kind == Map.ObjectKind.Sphinx);
        Assert.True(sx.SphinxRandomRiddle);
        h.Pos = sx.ActiveTiles.FirstOrDefault(sx.Pos);
        g.InteractWithObject(h, sx.Index);
        var r = g.S.Obj(sx.Index).Riddle;
        Assert.NotNull(r);
        Assert.Equal(29, g.S.RiddlePool.Count);
        var riddle = g.Db.RandomRiddles.First(x => x.Question == r!.Question);
        Assert.Equal(riddle.Answers[0], r!.Answers[r.Correct]);
        Assert.Equal(3, r.Answers.Distinct().Count());
        int gold = p.Purse[Purse.Gold];
        long exp = h.Experience;
        int answer = right ? r.Correct : (r.Correct + 1) % 3;
        var res = g.Apply(new AnswerSphinxCmd(h.Id, sx.Index, answer, reward) { Player = p.Index });
        Assert.True(res.Ok, res.Error);
        Assert.True(g.S.Obj(sx.Index).SphinxUsed);
        if (!right) { Assert.Equal(gold, p.Purse[Purse.Gold]); Assert.Equal(exp, h.Experience); }
        else if (reward == 0) Assert.Equal(gold + riddle.Gold, p.Purse[Purse.Gold]);
        else if (reward == 1) Assert.Contains(riddle.Artifact!, h.Artifacts);
        else Assert.True(h.Experience > exp);
        // A used Sphinx poses no riddle.
        g.InteractWithObject(h, sx.Index);
        Assert.Null(g.S.Obj(sx.Index).Riddle);
        Assert.False(g.Apply(new AnswerSphinxCmd(h.Id, sx.Index, 0, 0) { Player = p.Index }).Ok);
    }
}
