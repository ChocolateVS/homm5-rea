using H5.Adventure.Engine;
using H5.Adventure.State;
using Rng = H5.Combat.Rng;
using HeroState = H5.Adventure.State.HeroState;
using H5.Core.Data;
using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>
/// The hero start army per H5_Game.exe: the generator FUN_00b57de0 (CAdvMapHero vfunc 0x1f0), its stack roll
/// FUN_00b513e0, the specialisation table FUN_00b4bf10 / 00f75648, and its callers: map heroes without an army
/// (009a96d0), the weekly tavern refill (00990550 → 004fd600 with true), the daily refill of an emptied slot
/// (009949d0 → 004fd600 with false) and a fled hero (004fe770 → vfunc 0x1d8 = 00b52e00 → 0x1f0(false)).
/// </summary>
[Collection("adv")]
public class StartArmyTests(Fixture f)
{
    // FUN_00b57de0: per tier the LAST creature id 1..179 of the hero's town with BaseCreature = CREATURE_UNKNOWN
    // (record +0x100). The Tribes of the East alternative upgrades (Upgrade=false, BaseCreature set) never qualify.
    [Theory]
    [InlineData("TOWN_HEAVEN", "CREATURE_PEASANT", "CREATURE_ARCHER", "CREATURE_FOOTMAN")]
    [InlineData("TOWN_INFERNO", "CREATURE_FAMILIAR", "CREATURE_DEMON", "CREATURE_HELL_HOUND")]
    [InlineData("TOWN_NECROMANCY", "CREATURE_SKELETON", "CREATURE_WALKING_DEAD", "CREATURE_MANES")]
    [InlineData("TOWN_PRESERVE", "CREATURE_PIXIE", "CREATURE_BLADE_JUGGLER", "CREATURE_WOOD_ELF")]
    [InlineData("TOWN_DUNGEON", "CREATURE_SCOUT", "CREATURE_WITCH", "CREATURE_MINOTAUR")]
    [InlineData("TOWN_ACADEMY", "CREATURE_GREMLIN", "CREATURE_STONE_GARGOYLE", "CREATURE_IRON_GOLEM")]
    [InlineData("TOWN_FORTRESS", "CREATURE_DEFENDER", "CREATURE_AXE_FIGHTER", "CREATURE_BEAR_RIDER")]
    [InlineData("TOWN_STRONGHOLD", "CREATURE_GOBLIN", "CREATURE_CENTAUR", "CREATURE_ORC_WARRIOR")]
    public void BaseCreaturePerTier(string town, string t1, string t2, string t3)
    {
        var g = f.NewGame();
        Assert.Equal(new string?[] { null, t1, t2, t3 }, g.StartArmyBases(town));
    }

    // FUN_00b513e0: lo/hi stored as floats (FSTP float), count chopped (control word | 0xc00), rand32 · 2^-32.
    [Fact]
    public void StackRollIsChoppedFromFloatBounds()
    {
        Assert.Equal(22, Engine.Adventure.StartArmyCount(22, 1f, 1.5f, 0));
        Assert.Equal(27, Engine.Adventure.StartArmyCount(22, 1f, 1.5f, 0x80000000));
        Assert.Equal(32, Engine.Adventure.StartArmyCount(22, 1f, 1.5f, uint.MaxValue));
        Assert.Equal(-1, Engine.Adventure.StartArmyCount(3, -0.6f, 0.4f, 0));         // ≤ 0: no stack
        Assert.Equal(0, Engine.Adventure.StartArmyCount(3, -0.6f, 0.4f, 0x80000000)); // −0.3 chops to 0
        Assert.Equal(4, Engine.Adventure.StartArmyCount(7, 0.7f, 0.9f, 0));            // (float)4.9 → 4
    }

    static Rng Copy(Rng r) => new() { S0 = r.S0, S1 = r.S1, S2 = r.S2, S3 = r.S3 };

    static int Roll(Engine.Adventure g, Rng r, string creature, string key)
    {
        var gen = g.Db.Stats.Node("HeroArmyGeneration");
        float min = key.Length == 0 ? 0 : (float)gen.Dbl("MinWeekGrowthCoef_" + key);
        float max = key.Length == 0 ? 0 : (float)gen.Dbl("MaxWeekGrowthCoef_" + key);
        return Engine.Adventure.StartArmyCount(g.Core.Creatures[creature].WeeklyGrowth, min, max, (uint)(r.NextU64() >> 32));
    }

    static List<(string, int)> Army(HeroState h) => h.Army.Where(s => s != null).Select(s => (s!.Creature, s.Count)).ToList();

    // No specialisation creature: one roll each of the tier 1, 2, 3 bases (Min/MaxWeekGrowthCoef_TierN); a roll ≤ 0
    // adds nothing (MMH55's tier 3 is 0..0, stock −0.6..0.4). The old army is cleared first.
    [Fact]
    public void PlainHeroRollsTierOneToThree()
    {
        var g = f.NewGame();
        var h = new HeroState { Town = "TOWN_ACADEMY", Specialization = "HERO_SPEC_NONE" };
        h.Army[5] = new Stack("CREATURE_TITAN", 9);
        var r = Copy(g.S.Rng);
        var want = new List<(string, int)>();
        foreach (var (c, t) in new[] { ("CREATURE_GREMLIN", 1), ("CREATURE_STONE_GARGOYLE", 2), ("CREATURE_IRON_GOLEM", 3) })
            if (Roll(g, r, c, "Tier" + t) is > 0 and var n) want.Add((c, n));
        g.GiveStartingArmy(h);
        Assert.Equal(want, Army(h));
        Assert.Equal(r.S0, g.S.Rng.S0);
    }

    // Specialisation creature of tier 1..3: only NumSuchStacks_SpecTierN separate stacks of it (never merged).
    [Fact]
    public void TierTwoSpecialistGetsOnlyStacksOfHisCreature()
    {
        var g = f.NewGame();
        var h = new HeroState { Town = "TOWN_HEAVEN", Specialization = "HERO_SPEC_ARCHERS" };
        var r = Copy(g.S.Rng);
        int stacks = g.Db.Stats.Node("HeroArmyGeneration").Int("NumSuchStacks_SpecTier2");
        var want = new List<(string, int)>();
        for (int i = 0; i < stacks; i++) want.Add(("CREATURE_ARCHER", Math.Max(1, Roll(g, r, "CREATURE_ARCHER", "SpecTier2"))));
        g.GiveStartingArmy(h);
        Assert.True(stacks > 0);
        Assert.Equal(want, Army(h));
    }

    // Specialisation creature above tier 3 (HERO_SPEC_UNICORNS → tier 5): tier 1 and 2 bases, then one forced
    // FUN_00b513e0(0, 0, 1, 1) stack of it: count 0, forced to 1 (it still draws a random number).
    [Fact]
    public void HighTierSpecialistGetsTierOneTwoAndOneOfHisCreature()
    {
        var g = f.NewGame();
        var h = new HeroState { Town = "TOWN_PRESERVE", Specialization = "HERO_SPEC_UNICORNS" };
        var r = Copy(g.S.Rng);
        var want = new List<(string, int)>();
        if (Roll(g, r, "CREATURE_PIXIE", "Tier1") is > 0 and var a) want.Add(("CREATURE_PIXIE", a));
        if (Roll(g, r, "CREATURE_BLADE_JUGGLER", "Tier2") is > 0 and var b) want.Add(("CREATURE_BLADE_JUGGLER", b));
        Roll(g, r, "CREATURE_UNICORN", "");
        want.Add(("CREATURE_UNICORN", 1));
        g.GiveStartingArmy(h);
        Assert.Equal(want, Army(h));
        Assert.Equal(r.S0, g.S.Rng.S0);
    }

    // full = false: one creature of the tier-1 base, no random draw.
    [Fact]
    public void OneCreatureArmy()
    {
        var g = f.NewGame();
        var h = new HeroState { Town = "TOWN_DUNGEON", Specialization = "HERO_SPEC_WITCHES" };
        ulong s0 = g.S.Rng.S0;
        g.GiveStartingArmy(h, full: false);
        Assert.Equal(new List<(string, int)> { ("CREATURE_SCOUT", 1) }, Army(h));
        Assert.Equal(s0, g.S.Rng.S0);
    }

    // 009949d0: each new day refills an emptied tavern slot through 004fd600(pool, false); that hero is hired with one
    // tier-1 creature. The weekly refill (00990550, also run at the start) uses true: the full army.
    [Fact]
    public void DailyTavernRefillBringsOneCreature()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        Assert.All(p.TavernArmy, a => Assert.True(a.StackCount() >= 1));
        p.Tavern[0] = null;
        int day = g.S.Day;
        while (g.S.Day == day) Assert.True(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Ok);
        Assert.NotEqual(1, g.S.DayOfWeek);
        Assert.NotNull(p.Tavern[0]);
        Assert.Equal(1, p.TavernArmy[0].StackCount());
        Assert.Equal(1, p.TavernArmy[0].Live().Single().Count);
        while (g.Current != p) Assert.True(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Ok);
        var town = g.S.Towns.First(t => t.Owner == p.Index);
        town.Built["TB_TAVERN"] = 1;
        foreach (var x in g.S.Heroes.Where(x => x.Alive && x.Pos == g.EntranceOf(town))) x.Pos = new Tile(0, 0, x.Pos.Floor);
        foreach (var x in g.S.Heroes.Where(x => x.Owner == p.Index)) x.Alive = false;
        p.Purse[Purse.Gold] = 100000;
        string defId = p.Tavern[0]!;
        var r = g.Apply(new HireHeroCmd(town.Object, Array.IndexOf(g.TownTavernOffers(p, town), defId)) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        var hired = g.S.Heroes.Single(x => x.Alive && x.DefId == defId);
        Assert.Equal(new List<(string, int)> { (g.StartArmyBases(hired.Town)[1]!, 1) }, Army(hired));
    }

    // 004fd600: the army is rolled when the hero enters the slot (vfunc 0x1f0 right after the pool draw), so the hire
    // brings exactly that army; the pool gives a hero to one player at a time (00ce7920: the hero has no player).
    [Fact]
    public void TavernArmyIsRolledAtSlotEntryAndOffersAreExclusive()
    {
        var g = f.NewGame("A2M1");
        var offered = g.S.Players.Where(x => x.Alive).SelectMany(x => x.Tavern).Where(id => id != null).ToList();
        Assert.Equal(offered.Count, offered.Distinct().Count());
        var p = g.Current;
        var town = g.S.Towns.First(t => t.Owner == p.Index);
        town.Built["TB_TAVERN"] = 1;
        foreach (var x in g.S.Heroes.Where(x => x.Alive && x.Pos == g.EntranceOf(town))) x.Pos = new Tile(0, 0, x.Pos.Floor);
        foreach (var x in g.S.Heroes.Where(x => x.Owner == p.Index)) x.Alive = false;
        p.Purse[Purse.Gold] = 100000;
        string defId = g.TownTavernOffers(p, town).First(id => id != null)!;
        var expected = p.TavernArmy[Array.IndexOf(p.Tavern, defId)].Live().Select(st => (st.Creature, st.Count)).ToList();
        var r = g.Apply(new HireHeroCmd(town.Object, Array.IndexOf(g.TownTavernOffers(p, town), defId)) { Player = p.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(expected, Army(g.S.Heroes.Single(x => x.Alive && x.DefId == defId)));
    }
}
