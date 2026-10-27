using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Stronghold Walker's Hut: town quote FUN_00ccf7b0 (CAdvMapTown slot 0x118), command 00429730
/// (CUpgradeHeroTalismanOfAdventureAtTownCmd), spell grant FUN_00b56b50 (hero vf 0x384).
/// </summary>
[Collection("adv")]
public class WalkersHutTests(Fixture f)
{
    (Engine.Adventure G, TownState T, HeroState H) Setup()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        t.Type = "TOWN_STRONGHOLD";
        t.Built[Engine.Adventure.WalkersHut] = 1;
        h.Class = "HERO_CLASS_BARBARIAN";
        h.Level = 1;
        h.TalismanLevel = 0;
        h.Spells.Clear();
        h.Pos = g.EntranceOf(t);
        h.InTown = t.Object;
        for (int i = 0; i < 7; i++) g.Current.Purse[i] = 100;
        g.Current.Purse[Purse.Gold] = 100000;
        return (g, t, h);
    }

    static Resources Sum(Engine.Adventure g, int first, int last)
    {
        var p = new Purse();
        for (int i = first; i <= last; i++) p.Add(g.TalismanLevels[i - 1].Cost);
        return new Resources(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
    }

    [Fact]
    public void LevelsComeFromDefaultStats()
    {
        var (g, _, _) = Setup();
        // FUN_00ccec40: the town maximum is the number of TalismanOfAdventure levels (four in the data, ascending HeroLevel).
        Assert.Equal(4, g.TalismanMaxLevel);
        Assert.Equal("SPELL_SUMMON_BOAT", g.TalismanLevels[0].Spell);
        Assert.Equal(0, g.TalismanLevels[0].HeroLevel);
        Assert.True(g.TalismanLevels.Zip(g.TalismanLevels.Skip(1)).All(p => p.First.HeroLevel < p.Second.HeroLevel));
    }

    [Fact]
    public void QuoteStatusesFollowTheExeOrder()
    {
        var (g, t, h) = Setup();
        var L = g.TalismanLevels;
        Assert.Equal(TalismanStatus.Invalid, g.TalismanQuoteFor(t, null, 1, 1).Status);
        Assert.Equal(TalismanStatus.Ok, g.TalismanQuoteFor(t, h, 1, 1).Status);
        // Level 2 needs a higher hero level (status 6), whatever the range.
        h.Level = L[1].HeroLevel - 1;
        Assert.Equal(TalismanStatus.HeroLevelTooLow, g.TalismanQuoteFor(t, h, 1, 2).Status);
        Assert.Equal(TalismanStatus.Invalid, g.TalismanQuoteFor(t, h, 0, 1).Status);
        Assert.Equal(TalismanStatus.Invalid, g.TalismanQuoteFor(t, h, 1, 5).Status);
        Assert.Equal(TalismanStatus.Invalid, g.TalismanQuoteFor(t, h, 2, 1).Status);
        h.TalismanLevel = 2;
        h.Level = 40;
        Assert.Equal(TalismanStatus.AlreadyUpgraded, g.TalismanQuoteFor(t, h, 2, 2).Status);
        Assert.Equal(TalismanStatus.FirstLevelOwned, g.TalismanQuoteFor(t, h, 2, 3).Status);
        Assert.Equal(TalismanStatus.Ok, g.TalismanQuoteFor(t, h, 3, 4).Status);
        // The costs of the range are summed.
        Assert.Equal(Sum(g, 3, 4), g.TalismanQuoteFor(t, h, 3, 4).Cost);
        Assert.Equal(new[] { L[2].Spell, L[3].Spell }, g.TalismanQuoteFor(t, h, 3, 4).Allowed);
        h.Class = "HERO_CLASS_KNIGHT";
        Assert.Equal(TalismanStatus.NotBarbarian, g.TalismanQuoteFor(t, h, 3, 4).Status);
        t.Built[Engine.Adventure.WalkersHut] = 0;
        Assert.Equal(TalismanStatus.NoHut, g.TalismanQuoteFor(t, h, 3, 4).Status);
    }

    [Fact]
    public void UpgradePaysSetsTheLevelAndGrantsSpells()
    {
        var (g, t, h) = Setup();
        var L = g.TalismanLevels;
        h.Level = L[2].HeroLevel;
        var cost = Sum(g, 1, 3);
        var r = g.Apply(new UpgradeTalismanCmd(t.Object, h.Id, 3) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, h.TalismanLevel);
        Assert.Equal(100000 - cost.Gold, g.Current.Purse[Purse.Gold]);
        Assert.Equal(100 - cost.Wood, g.Current.Purse[Purse.Wood]);
        Assert.Equal(100 - cost.Mercury, g.Current.Purse[Purse.Mercury]);
        Assert.Equal(new[] { L[0].Spell, L[1].Spell, L[2].Spell }, h.Spells);
        // Level 4 needs a higher hero level; level 3 is owned.
        Assert.False(g.Apply(new UpgradeTalismanCmd(t.Object, h.Id, 4) { Player = g.Current.Index }).Ok);
        Assert.False(g.Apply(new UpgradeTalismanCmd(t.Object, h.Id, 3) { Player = g.Current.Index }).Ok);
    }

    [Fact]
    public void UpgradeNeedsTheWholeCost()
    {
        var (g, t, h) = Setup();
        g.Current.Purse[Purse.Gold] = g.TalismanLevels[0].Cost.Gold - 1;
        Assert.False(g.Apply(new UpgradeTalismanCmd(t.Object, h.Id, 1) { Player = g.Current.Index }).Ok);
        Assert.Equal(0, h.TalismanLevel);
    }

    [Fact]
    public void GrantStopsAtTheFirstLevelAboveTheHero()
    {
        // FUN_00b56b50 returns at the first level whose HeroLevel exceeds the hero's level.
        var (g, _, h) = Setup();
        var L = g.TalismanLevels;
        h.TalismanLevel = 4;
        h.Level = L[2].HeroLevel - 1;
        g.GrantTalismanSpells(h);
        Assert.Equal(new[] { L[0].Spell, L[1].Spell }, h.Spells);
        // FUN_00b4ce30 clamps the level to 0..levels.
        g.SetTalismanLevel(h, 9);
        Assert.Equal(4, h.TalismanLevel);
        g.SetTalismanLevel(h, -1);
        Assert.Equal(0, h.TalismanLevel);
    }
}
