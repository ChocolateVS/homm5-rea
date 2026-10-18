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
    public void GrailIsBuriedOnOpenLandAndFoundByDigging()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        Assert.NotNull(g.S.Grail);
        var grail = g.S.Grail!.Value;
        var floor = g.Map.Floor(grail.Floor)!;
        Assert.False(floor.IsBlocked(grail.X, grail.Y));
        Assert.False(floor.IsWater(grail.X, grail.Y));
        // Next to it: nothing (no Tear of Asha Vision); a moved hero cannot dig.
        h.Pos = grail with { X = grail.X + 1 };
        h.MovePoints = h.MaxMovePoints - 1;
        Assert.False(g.Apply(new DigCmd(h.Id) { Player = p.Index }).Ok);
        h.MovePoints = h.MaxMovePoints;
        Assert.True(g.Apply(new DigCmd(h.Id) { Player = p.Index }).Ok);
        Assert.Equal(0, h.MovePoints);
        Assert.False(g.S.GrailFound);
        h.Pos = grail;
        h.MovePoints = h.MaxMovePoints;
        var r = g.Apply(new DigCmd(h.Id) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.True(g.S.GrailFound);
        Assert.Contains(Engine.Adventure.GrailArtifact, h.Artifacts);
    }

    [Fact]
    public void ObelisksUncoverPuzzlePieces()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var obelisks = g.Map.Objects.Where(o => o.Shared.Type == "BUILDING_OBELISK").ToList();
        Assert.Equal(0, g.PuzzlePiecesOpen(p));
        if (obelisks.Count == 0) return;
        g.InteractWithObject(h, obelisks[0].Index);
        Assert.Equal(Engine.Adventure.PuzzlePieces / obelisks.Count, g.PuzzlePiecesOpen(p));
        foreach (var o in obelisks) g.InteractWithObject(h, o.Index);
        Assert.Equal(Engine.Adventure.PuzzlePieces, g.PuzzlePiecesOpen(p));
    }
}
