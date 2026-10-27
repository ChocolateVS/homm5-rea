using System.Xml.Linq;
using H5.Adventure.AI;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Follow-ups of the Walker's Hut and the town dwelling filter: the map loaders' TalismanLevel (00a08b50, combined by
/// 009a9cf0, applied by 009aacb0 through vf 0x304 / 0x384), the town's CreaturesUpgradesFilter (00a0a490 → 00a0f620)
/// and the AI's talisman purchase in its own town (009c4710, 009c4aae..009c4bfe).
/// </summary>
[Collection("adv")]
public class TalismanFollowUpTests(Fixture f)
{
    [Fact]
    public void TalismanLevelIsTheMapHerosWithMaskBit0x40ElseTheLarger()
    {
        // 009a9cf0: (mask & 0x40) ? override : max(base, override).
        Assert.Equal(1, MapLoader.CombineTalismanLevel(0x40, 3, 1));
        Assert.Equal(3, MapLoader.CombineTalismanLevel(0, 3, 1));
        Assert.Equal(2, MapLoader.CombineTalismanLevel(0x3f, 0, 2));
        Assert.Equal(0, MapLoader.CombineTalismanLevel(0x41, 2, 0));
    }

    [Fact]
    public void TownFilterUsesTheHillFortReader()
    {
        var x = XElement.Parse("""
            <CreaturesUpgradesFilter>
              <ForbiddenBasicUpgradeTiers><Item>2</Item><Item>7</Item></ForbiddenBasicUpgradeTiers>
              <ForbiddenAlterUpgradeTiers><Item>3</Item></ForbiddenAlterUpgradeTiers>
              <NotUpgradeable/>
              <ForbiddenUpgrades><Item>CREATURE_ARCHANGEL</Item></ForbiddenUpgrades>
            </CreaturesUpgradesFilter>
            """);
        var flt = MapLoader.ReadUpgradesFilter(x);
        Assert.Equal(new[] { 2, 7 }, flt.ForbiddenBasicUpgradeTiers.OrderBy(i => i));
        Assert.Equal(new[] { 3 }, flt.ForbiddenAlterUpgradeTiers);
        Assert.Empty(flt.NotUpgradeable);
        Assert.Equal(new[] { "CREATURE_ARCHANGEL" }, flt.ForbiddenUpgrades);
    }

    (Engine.Adventure G, TownState T, HeroState H) Setup()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        t.Type = "TOWN_STRONGHOLD";
        t.Built[Engine.Adventure.WalkersHut] = 1;
        h.Class = "HERO_CLASS_BARBARIAN";
        h.Level = 40;
        h.TalismanLevel = 0;
        h.Spells.Clear();
        h.Pos = g.EntranceOf(t);
        h.InTown = t.Object;
        h.Garrisoned = false;
        for (int i = 0; i < 7; i++) g.Current.Purse[i] = 100000;
        g.Current.AiSupply = Enumerable.Repeat(100000, 7).ToArray();
        return (g, t, h);
    }

    [Fact]
    public void AiBuysOneLevelPerVisit()
    {
        var (g, t, h) = Setup();
        var cost = g.TalismanLevels[0].Cost;
        OriginalAdventureAI.BuyTalismanLevel(g, h, t);
        Assert.Equal(1, h.TalismanLevel);
        Assert.Equal(100000 - cost.Gold, g.Current.Purse[Purse.Gold]);
        Assert.Contains(g.TalismanLevels[0].Spell, h.Spells);
    }

    [Fact]
    public void AiNeverBuysTheLastLevel()
    {
        // 009c4b09: next < town maximum (vf 0x11c), strictly.
        var (g, t, h) = Setup();
        h.TalismanLevel = g.TalismanMaxLevel - 1;
        OriginalAdventureAI.BuyTalismanLevel(g, h, t);
        Assert.Equal(g.TalismanMaxLevel - 1, h.TalismanLevel);
    }

    [Fact]
    public void AiNeedsTheSupplyAsWellAsThePurse()
    {
        // CPlayerAIInformation vf 4 (00a367b0) → the supply set, checked by 009d0720 after the purse.
        var (g, t, h) = Setup();
        g.Current.AiSupply = null;
        OriginalAdventureAI.BuyTalismanLevel(g, h, t);
        Assert.Equal(0, h.TalismanLevel);
        g.Current.AiSupply = Enumerable.Repeat(100000, 7).ToArray();
        g.Current.Purse[Purse.Gold] = g.TalismanLevels[0].Cost.Gold - 1;
        OriginalAdventureAI.BuyTalismanLevel(g, h, t);
        Assert.Equal(0, h.TalismanLevel);
    }

    [Fact]
    public void AiSkipsNonBarbariansAndTownsWithoutTheHut()
    {
        var (g, t, h) = Setup();
        h.Class = "HERO_CLASS_KNIGHT";
        OriginalAdventureAI.BuyTalismanLevel(g, h, t);
        Assert.Equal(0, h.TalismanLevel);
        h.Class = "HERO_CLASS_BARBARIAN";
        t.Built[Engine.Adventure.WalkersHut] = 0;
        OriginalAdventureAI.BuyTalismanLevel(g, h, t);
        Assert.Equal(0, h.TalismanLevel);
    }
}
