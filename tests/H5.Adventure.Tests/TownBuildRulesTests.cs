using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Town build check (exe 00cd6a90 / 00cd0e20) and town income (00cd43f0 / 00cd31b0).</summary>
[Collection("adv")]
public class TownBuildRulesTests(Fixture f)
{
    static Purse Rich() => new(new Resources(999, 999, 999, 999, 999, 999, 999999));

    (Engine.Adventure G, TownState T) Town()
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        g.Current.Purse = Rich();
        return (g, t);
    }

    static void Max(Engine.Adventure g, TownState t, string type)
    {
        t.MaxLevel[type] = g.Db.MaxLevel(t.Type, type);
        t.Built.TryAdd(type, 0);
    }

    [Fact]
    public void TownPointsComeBeforePrerequisites()
    {
        var (g, t) = Town();
        // A building whose town level requirement is unmet: town_points even if its prerequisites are missing too.
        foreach (var k in t.Built.Keys.ToList()) t.Built[k] = 0;
        t.Built["TB_TOWN_HALL"] = 1;
        var def = f.Db.Buildings[t.Type].Where(d => d.DevLevelNeeded > t.DevLevel && d.Level == 1 && d.Dependencies.Count > 0).OrderBy(d => d.DevLevelNeeded).FirstOrDefault();
        Assert.NotNull(def);
        Max(g, t, def!.Type);
        t.Built[def.Type] = 0;
        Assert.Equal(BuildDenial.TownPoints, g.BuildCheck(t, def.Type, g.Current.Purse));
    }

    [Fact]
    public void MoneyIsCheckedBeforeBuiltToday()
    {
        var (g, t) = Town();
        var type = t.MaxLevel.Keys.First(k => g.BuildCheck(t, k, g.Current.Purse) == BuildDenial.CanBuild);
        t.BuiltToday = true;
        Assert.Equal(BuildDenial.BuiltThisTurn, g.BuildCheck(t, type, g.Current.Purse));
        var cost = f.Db.Building(t.Type, type, t.Level(type) + 1)!.Cost;
        if (cost.Gold > 0)
        {
            g.Current.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 0));
            Assert.Equal(BuildDenial.NoMoney, g.BuildCheck(t, type, g.Current.Purse));
        }
    }

    [Fact]
    public void LastLevelIsAlreadyBuiltAndTheMapLimitIsLimitUpgrade()
    {
        var (g, t) = Town();
        Max(g, t, "TB_TOWN_HALL");
        int max = f.Db.MaxLevel(t.Type, "TB_TOWN_HALL");
        t.Built["TB_TOWN_HALL"] = 1;
        t.MaxLevel["TB_TOWN_HALL"] = 1;
        Assert.Equal(BuildDenial.LimitUpgrade, g.BuildCheck(t, "TB_TOWN_HALL", g.Current.Purse));
        t.MaxLevel["TB_TOWN_HALL"] = max;
        t.Built["TB_TOWN_HALL"] = max;
        Assert.Equal(BuildDenial.AlreadyBuilt, g.BuildCheck(t, "TB_TOWN_HALL", g.Current.Purse));
        Assert.Equal(BuildDenial.CantBuild, g.BuildCheck(t, "TB_NO_SUCH_BUILDING", g.Current.Purse));
    }

    [Fact]
    public void OnlyOneCapitolPerPlayer()
    {
        var (g, t) = Town();
        var other = new TownState { Object = -77, Type = t.Type, Owner = t.Owner };
        other.Built["TB_TOWN_HALL"] = 4;
        g.S.Towns.Add(other);
        Max(g, t, "TB_TOWN_HALL");
        t.Built["TB_TOWN_HALL"] = 3;
        Assert.Equal(BuildDenial.OnlyOneCapitol, g.BuildCheck(t, "TB_TOWN_HALL", g.Current.Purse));
        g.S.Towns.Remove(other);
        Assert.NotEqual(BuildDenial.OnlyOneCapitol, g.BuildCheck(t, "TB_TOWN_HALL", g.Current.Purse));
    }

    [Fact]
    public void GrailNeedsATearOfAshaInTheTownAndUsesItUp()
    {
        var (g, t) = Town();
        if (f.Db.MaxLevel(t.Type, "TB_GRAIL") <= 0) return;
        Max(g, t, "TB_GRAIL");
        t.BuiltToday = false;
        Assert.Equal(BuildDenial.Grail, g.BuildCheck(t, "TB_GRAIL", g.Current.Purse));
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == t.Owner);
        h.InTown = t.Object;
        h.Garrisoned = false;
        h.Backpack.Add(Engine.Adventure.GrailArtifact);
        var why = g.BuildCheck(t, "TB_GRAIL", g.Current.Purse);
        Assert.NotEqual(BuildDenial.Grail, why);
        if (why != BuildDenial.CanBuild) return;   // other requirements (town level) of this faction's grail
        var r = g.Apply(new BuildCmd(t.Object, "TB_GRAIL") { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.DoesNotContain(Engine.Adventure.GrailArtifact, h.Backpack);
    }

    [Fact]
    public void ShipyardNeedsWater()
    {
        var (g, t) = Town();
        if (f.Db.MaxLevel(t.Type, "TB_SHIPYARD") <= 0) return;
        Max(g, t, "TB_SHIPYARD");
        bool water = g.SlipwayOf(t.Object) is { } s && g.IsWaterTile(s);
        var why = g.BuildCheck(t, "TB_SHIPYARD", g.Current.Purse);
        if (!water) Assert.Equal(BuildDenial.NoWater, why);
        else Assert.NotEqual(BuildDenial.NoWater, why);
    }

    [Fact]
    public void TownIncomePerHallLevelGrailAndTaxpayers()
    {
        var (g, t) = Town();
        Array.Fill(t.Garrison, null);
        t.Specialization = null;
        t.Built["TB_MARKETPLACE"] = 0;
        t.Built["TB_GRAIL"] = 0;
        foreach (var (lvl, gold) in new[] { (0, 0), (1, 500), (2, 1000), (3, 2000), (4, 4000) })
        {
            t.Built["TB_TOWN_HALL"] = lvl;
            Assert.Equal(gold, g.TownIncome(t)[Purse.Gold]);
        }
        t.Built["TB_GRAIL"] = 1;
        Assert.Equal(9000, g.TownIncome(t)[Purse.Gold]);
        t.Built["TB_GRAIL"] = 0;
        t.Garrison[0] = new Stack("CREATURE_PEASANT", 30);
        bool tax = f.Core.Creatures["CREATURE_PEASANT"].Has("ABILITY_TAXPAYER");
        Assert.Equal(4000 + (tax ? 30 : 0), g.TownIncome(t)[Purse.Gold]);
        // No town hall: nothing at all, not even the silo or the grail.
        t.Built["TB_TOWN_HALL"] = 0;
        t.Built["TB_GRAIL"] = 1;
        t.Built["TB_MARKETPLACE"] = 2;
        Assert.All(g.TownIncome(t).R, v => Assert.Equal(0, v));
    }

    [Fact]
    public void SiloIncomeNeedsTheSilo()
    {
        var (g, t) = Town();
        t.Built["TB_TOWN_HALL"] = 1;
        t.Built["TB_MARKETPLACE"] = 1;
        var one = g.TownIncome(t);
        t.Built["TB_MARKETPLACE"] = 2;
        var two = g.TownIncome(t);
        var silo = new Purse(f.Db.SiloIncome[t.Type]);
        for (int i = 0; i < 6; i++) Assert.True(two[i] - one[i] >= silo[i]);
    }
    [Fact]
    public void TradeGuildDrawsAWeeklyResourceAndRareSpecialisationsAddTwo()
    {
        // FUN_00cce410: ability 0x1d (Dungeon TB_SPECIAL_4) → resource = 008e7840(H(world seed, town id, week), 0, 5),
        // amount 2 for wood / ore else 1; without it −1 / 0. FUN_00cd2b90 (IAdvMapWeeklyUpdate vf 0, run by the new
        // week 00993cd0) pays it (00cce3c0) and +2 for TOWN_BONUS_MERCURY / _SULFUR / _GEMS / _CRYSTALS, then draws
        // again; nothing of it on other days.
        var (g, t) = Town();
        t.Type = "TOWN_DUNGEON";
        t.Built["TB_SPECIAL_4"] = 0;
        g.RollTradeGuild(t);
        Assert.Equal((-1, 0), (t.TradeGuildResource, t.TradeGuildAmount));
        t.Built["TB_SPECIAL_4"] = 1;
        g.RollTradeGuild(t);
        int want = ExeHashRandom.Range(ExeHashRandom.Hash(g.S.WorldSeed, t.Object, g.Db.MoonWeekIndex(g.S.MoonWeek)), 0, 5);
        Assert.Equal(want, t.TradeGuildResource);
        Assert.Equal(want <= Purse.Ore ? 2 : 1, t.TradeGuildAmount);
        t.Specialization = "GameMechanics/TownSpecialization/Dungeon/Random/Balvalak.xdb";
        string bonus = g.SpecializationBonus(t)!;
        int rare = bonus switch { "TOWN_BONUS_GEMS" => Purse.Gem, "TOWN_BONUS_MERCURY" => Purse.Mercury, _ => -1 };
        Assert.True(rare >= 0);
        var p = g.Current;
        var before = (int[])p.Purse.R.Clone();
        var expected = g.ExpectedIncome(p);
        int res = t.TradeGuildResource, amt = t.TradeGuildAmount;
        int day = g.S.Day;
        while (g.S.Day == day) Assert.True(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Ok);
        for (int i = 0; i < 6; i++) Assert.Equal(before[i] + expected[i], p.Purse[i]);   // day 2: income only
        g.S.Moon = null;          // keep the week (and the income factors) fixed across the new week
        g.S.Day = 7;
        before = (int[])p.Purse.R.Clone();
        expected = g.ExpectedIncome(p);
        res = t.TradeGuildResource; amt = t.TradeGuildAmount;
        while (g.S.Day == 7) Assert.True(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Ok);
        var extra = new int[7];
        extra[rare] += 2;
        extra[res] += amt;
        for (int i = 0; i < 6; i++) Assert.Equal(before[i] + expected[i] + extra[i], p.Purse[i]);
    }

    [Fact]
    public void MysticPondGivesAWeeklyRandomResource()
    {
        // FUN_00cce350 (town vf 0x84, from the weekly 00cd2b90): ability 0xc (Preserve TB_SPECIAL_2 level 1):
        // r = 008e7840(H(world seed, town +8, week), 0, 5), +10 for wood / ore, else +5.
        var (g, t) = Town();
        t.Type = "TOWN_PRESERVE";
        t.Specialization = null;
        var inc = new Purse();
        g.MysticPond(t, inc);
        Assert.All(inc.R, v => Assert.Equal(0, v));
        t.Built["TB_SPECIAL_2"] = 1;
        g.MysticPond(t, inc);
        int r = ExeHashRandom.Range(ExeHashRandom.Hash(g.S.WorldSeed, t.Object, g.Db.MoonWeekIndex(g.S.MoonWeek)), 0, 5);
        Assert.Equal(r <= Purse.Ore ? 10 : 5, inc[r]);
        Assert.Equal(inc[r], inc.R.Sum());
    }

    [Fact]
    public void TradeGuildIsAnArtifactMerchantAndPurchasesRaiseItsBonus()
    {
        // FUN_00cce140: abilities 0x17 / 0x1d give the town a CBlackMarket (stocked on a build, 00cd4a40 at 00cd4db7).
        // CBuyBlackMarketArtifactCmd 00432640 → town vf 0x8c = FUN_00cd0190: minor +25 gold (+1 amount for wood / ore),
        // major +50 / +1, relic +100 / +2; the gold is paid with the town income (00cd43f0 → vf 0x88 at 00cd4521).
        var (g, t) = Town();
        t.Type = "TOWN_DUNGEON";
        t.Built["TB_SPECIAL_4"] = 1;
        Assert.True(Engine.Adventure.TownHasArtifactMerchant(t));
        g.StockArtifactMerchants();
        var stock = g.MerchantStock(t.Object);
        Assert.Equal(6, stock.Count);
        g.RollTradeGuild(t);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.InTown = t.Object;
        var incomeBefore = g.TownIncome(t)[Purse.Gold];
        int gold = 0, amount = t.TradeGuildAmount;
        bool woodOre = t.TradeGuildResource <= Purse.Ore;
        for (int slot = 0; slot < 6; slot++)
        {
            if (stock[slot] is not { } art) continue;
            Assert.True(g.Apply(new BuyMerchantArtifactCmd(h.Id, t.Object, slot) { Player = g.Current.Index }).Ok);
            switch (f.Db.ArtifactClass[art])
            {
                case "ARTF_CLASS_MINOR": gold += 25; if (woodOre) amount += 1; break;
                case "ARTF_CLASS_MAJOR": gold += 50; amount += 1; break;
                case "ARTF_CLASS_RELIC": gold += 100; amount += 2; break;
            }
        }
        Assert.True(gold > 0);
        Assert.Equal((gold, amount), (t.TradeGuildGold, t.TradeGuildAmount));
        Assert.Equal(incomeBefore + gold, g.TownIncome(t)[Purse.Gold]);
    }
}
