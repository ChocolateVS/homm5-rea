using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Town dwelling growth per H5_Game.exe (growth record FUN_00cd5180 / FUN_00bce140, weekly update 00cd27b0).</summary>
[Collection("adv")]
public class TownGrowthTests(Fixture f)
{
    static (Engine.Adventure G, TownState T) Haven(Fixture f)
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Type = "TOWN_HEAVEN";
        t.Specialization = null;
        t.Built.Clear();
        t.Built["TB_DWELLING_1"] = 1;
        t.Built["TB_DWELLING_7"] = 1;
        foreach (var h in g.S.Heroes) if (h.InTown == t.Object) h.InTown = -1;
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        return (g, t);
    }

    static int Base(Engine.Adventure g, string c) => g.Core.Creatures[c].WeeklyGrowth;

    [Fact]
    public void CastleHordeAndGrailFollowTheGrowthRecord()
    {
        var (g, t) = Haven(f);
        int p = Base(g, "CREATURE_PEASANT"), a = Base(g, "CREATURE_ANGEL");
        Assert.Equal(new TownGrowth(p, 0, 0, 0, 0, 0, 0, 0), g.GrowthOf(t, 1));
        t.Built["TB_FORT"] = 2;                                   // Citadel: + weekly / 2 (integer)
        Assert.Equal(a / 2, g.GrowthOf(t, 7).Castle);
        t.Built["TB_FORT"] = 3;                                   // Castle: + weekly
        Assert.Equal(a, g.GrowthOf(t, 7).Castle);
        t.Built["TB_SPECIAL_5"] = 1;                              // Farms: peasants +5
        Assert.Equal(5, g.GrowthOf(t, 1).Horde);
        Assert.Equal(0, g.GrowthOf(t, 7).Horde);
        t.Built["TB_GRAIL"] = 1;                                  // grail: half of weekly + castle + horde + spec
        Assert.Equal((p + p + 5) / 2, g.GrowthOf(t, 1).Grail);
        Assert.Equal((a + a) / 2, g.GrowthOf(t, 7).Grail);
        Assert.Equal(p + p + 5 + (p + p + 5) / 2, g.GrowthOf(t, 1).Total);
    }

    [Fact]
    public void StatBasedHordesReadTownBuildingBonuses()
    {
        var (g, t) = Haven(f);
        t.Type = "TOWN_STRONGHOLD";
        t.Built["TB_SPECIAL_2"] = 1;                              // Garbage Pile: goblins
        int goblins = (int)g.Core.Stats.Get("adventure/TownBuildingBonuses/Stronghold_GarbagePile_GoblinGrowthAddition", -1);
        Assert.True(goblins > 0);
        Assert.Equal(goblins, g.GrowthOf(t, 1).Horde);
    }

    [Fact]
    public void RecruitmentAndTownSpecialisationAddToLowTiers()
    {
        var (g, t) = Haven(f);
        t.Built["TB_DWELLING_3"] = 1;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == t.Owner);
        h.InTown = t.Object;
        h.Perks.Add("HERO_SKILL_RECRUITMENT");
        Assert.Equal(3, g.GrowthOf(t, 1).Specialization);
        Assert.Equal(1, g.GrowthOf(t, 3).Specialization);
        Assert.Equal(0, g.GrowthOf(t, 7).Specialization);
        h.InTown = -1;
        t.Specialization = "GameMechanics/TownSpecialization/Academy/Random/Tirith.xdb";   // TOWN_BONUS_TIER1_CREATURE
        Assert.Equal(2, g.GrowthOf(t, 1).Specialization);
        t.Type = "TOWN_FORTRESS";                                 // +1 more in a Fortress town (FUN_00cd35c0 == 9)
        Assert.Equal(3, g.GrowthOf(t, 1).Specialization);
    }

    [Fact]
    public void MoonWeeksScaleTheSumAndCreatureWeeksDoubleTheirLine()
    {
        var g = f.NewGame();
        Assert.Equal(0, g.MoonWeekGrowth(0x2b, 9, "CREATURE_PEASANT"));     // plague
        Assert.Equal(3, g.MoonWeekGrowth(0x2c, 9, "CREATURE_PEASANT"));     // disease: max(9 / 3, 1)
        Assert.Equal(1, g.MoonWeekGrowth(0x2c, 2, "CREATURE_PEASANT"));
        Assert.Equal(1, g.MoonWeekGrowth(0x2d, 1, "CREATURE_PEASANT"));     // fever: max(1 / 2, 1)
        Assert.Equal(18, g.MoonWeekGrowth(0x36, 9, "CREATURE_PEASANT"));    // life ×2
        Assert.Equal(27, g.MoonWeekGrowth(0x37, 9, "CREATURE_PEASANT"));    // conjunction ×3
        // WEEK_OF_PEASANT (0x54 → creature 1): the peasant line (base creature) doubles, others do not.
        Assert.Equal(18, g.MoonWeekGrowth(0x54, 9, "CREATURE_PEASANT"));
        Assert.Equal(18, g.MoonWeekGrowth(0x54, 9, "CREATURE_MILITIAMAN"));
        Assert.Equal(9, g.MoonWeekGrowth(0x54, 9, "CREATURE_ARCHER"));
    }

    [Fact]
    public void MoonWeekPartExcludesGrailAndSacrifice()
    {
        var (g, t) = Haven(f);
        int p = Base(g, "CREATURE_PEASANT");
        t.Built["TB_GRAIL"] = 1;
        g.S.MoonWeek = "WEEK_OF_PEASANT";
        var gr = g.GrowthOf(t, 1);
        Assert.Equal(p / 2, gr.Grail);
        Assert.Equal(p, gr.MoonWeek);                             // ×2 of weekly only
        Assert.Equal(2 * p + p / 2, gr.Total);
    }

    [Fact]
    public void PlagueWeeksReduceThePoolBeforeGrowth()
    {
        Assert.Equal(0, Engine.Adventure.MoonWeekPool(0x2b, 30));
        Assert.Equal(20, Engine.Adventure.MoonWeekPool(0x2c, 30));          // 30 − 30 / 3
        Assert.Equal(1, Engine.Adventure.MoonWeekPool(0x2c, 2));            // 2 − max(0, 1)
        Assert.Equal(0, Engine.Adventure.MoonWeekPool(0x2d, 1));            // 1 − max(0, 1)
        Assert.Equal(15, Engine.Adventure.MoonWeekPool(0x2d, 30));
        Assert.Equal(30, Engine.Adventure.MoonWeekPool(0, 30));
    }

    [Fact]
    public void NewDwellingStartsWithBaseWeeklyGrowth()
    {
        var (g, t) = Haven(f);
        t.Built["TB_FORT"] = 3;
        t.Built["TB_TOWN_HALL"] = 4;
        t.BuiltToday = false;
        t.MaxLevel["TB_DWELLING_2"] = 2;
        foreach (var (type, level) in g.Db.Building(t.Type, "TB_DWELLING_2", 1)!.Dependencies)
            t.Built[type] = Math.Max(t.Level(type), level);
        t.Available[1] = 0;
        g.Current.Purse.Add(new Resources(100, 100, 100, 100, 100, 100, 100000));
        var r = g.Apply(new BuildCmd(t.Object, "TB_DWELLING_2") { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Base(g, "CREATURE_ARCHER"), t.Available[1]);
    }
}
