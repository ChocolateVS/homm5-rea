using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Faction town buildings with their own screens (exe creature transformer FUN_00cd36c0 / FUN_00cd07f0).</summary>
[Collection("adv")]
public class TownSpecialTests(Fixture f)
{
    static (Engine.Adventure G, TownState T, HeroState H) HavenWithKnight(Fixture f)
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        t.Type = "TOWN_HEAVEN";
        t.Built["TB_SPECIAL_1"] = 1;
        t.Built["TB_DWELLING_2"] = 1;
        t.Built["TB_DWELLING_3"] = 1;
        t.TrainingPoints = 7;
        h.Class = "HERO_CLASS_KNIGHT";
        h.Specialization = "";
        h.Perks.Clear();
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_TRAINING");
        h.Skills.Add(new SkillEntry("HERO_SKILL_TRAINING", Mastery.Basic));
        h.Pos = g.EntranceOf(t);
        h.InTown = t.Object;
        Array.Fill(h.Army, null);
        h.Army[0] = new Stack("CREATURE_PEASANT", 5);
        g.Current.Purse[Purse.Gold] = 100000;
        return (g, t, h);
    }

    [Fact]
    public void TrainingCostsThreeTimesTheTargetAndSpendsPoints()
    {
        var (g, t, h) = HavenWithKnight(f);
        var q = g.TrainingQuote(t, h, 0);
        Assert.Equal(TrainStatus.Ok, q.Status);
        Assert.Equal("CREATURE_ARCHER", q.Target);
        Assert.Equal(5 * 3 * 50, q.Cost.Gold);
        var r = g.Apply(new TrainCreatureCmd(t.Object, h.Id, 0) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("CREATURE_ARCHER", h.Army[0]!.Creature);
        Assert.Equal(5, h.Army[0]!.Count);
        Assert.Equal(2, t.TrainingPoints);
        Assert.Equal(100000 - 750, g.Current.Purse[Purse.Gold]);
        // Archers → Footmen: only 2 points left for 5 creatures.
        Assert.Equal(TrainStatus.LimitReached, g.TrainingQuote(t, h, 0).Status);
    }

    [Fact]
    public void DiscountsFollowTheExeFormula()
    {
        var (g, t, h) = HavenWithKnight(f);
        // Advanced Training (−15) + Expert Trainer (−35): factor (100 − 50)·3·0.01 = 1.5.
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_TRAINING");
        h.Skills.Add(new SkillEntry("HERO_SKILL_TRAINING", Mastery.Advanced));
        h.Perks.Add("HERO_SKILL_EXPERT_TRAINER");
        Assert.Equal(50, g.TrainingDiscount(h));
        Assert.Equal(375, g.TrainingQuote(t, h, 0).Cost.Gold);
        // Suzerain at level 10: Base 1 + (9 / 3)·1 = 4 more.
        h.Specialization = "HERO_SPEC_SUZERAIN";
        h.Level = 10;
        Assert.Equal(54, g.TrainingDiscount(h));
        // ROUND(50 · (5 · (46·3·0.01f))) = ROUND(345.0) per the x87 path.
        Assert.Equal(345, g.TrainingQuote(t, h, 0).Cost.Gold);
    }

    [Fact]
    public void TrainingChecksFollowTheQuoteOrder()
    {
        var (g, t, h) = HavenWithKnight(f);
        h.Army[1] = new Stack("CREATURE_GRIFFIN", 1);
        Assert.Equal(TrainStatus.NotTrainable, g.TrainingQuote(t, h, 1).Status);
        h.Army[1] = new Stack("CREATURE_PALADIN", 1);
        Assert.Equal(TrainStatus.TopTier, g.TrainingQuote(t, h, 1).Status);
        h.Army[1] = new Stack("CREATURE_SWORDSMAN", 1);
        Assert.Equal(TrainStatus.NoDwelling, g.TrainingQuote(t, h, 1).Status);   // priests need TB_DWELLING_5
        h.Class = "HERO_CLASS_WIZARD";
        Assert.Equal(TrainStatus.NotKnight, g.TrainingQuote(t, h, 0).Status);
        h.Class = "HERO_CLASS_KNIGHT";
        t.Built.Remove("TB_SPECIAL_1");
        Assert.Equal(TrainStatus.NoBuilding, g.TrainingQuote(t, h, 0).Status);
        t.Built["TB_SPECIAL_1"] = 1;
        g.Current.Purse[Purse.Gold] = 10;
        Assert.Equal(TrainStatus.NoResources, g.TrainingQuote(t, h, 0).Status);
    }

    [Fact]
    public void TrainingPointsResetWeeklyAndGrowWithTheHallOfHeroes()
    {
        var (g, t, h) = HavenWithKnight(f);
        Assert.Equal(7, g.TrainingPointsMax(t));
        t.Built["TB_SPECIAL_2"] = 1;
        Assert.Equal(20, g.TrainingPointsMax(t));
        t.TrainingPoints = 0;
        while (g.S.DayOfWeek != 7) g.Apply(new EndTurnCmd { Player = g.Current.Index });
        while (g.S.DayOfWeek != 1) g.Apply(new EndTurnCmd { Player = g.Current.Index });
        Assert.Equal(20, t.TrainingPoints);
    }

    [Fact]
    public void NecropolisShrineTurnsLivingStacksIntoTheUndeadOfTheirTier()
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Type = "TOWN_NECROMANCY";
        Array.Fill(t.Garrison, null);
        t.Garrison[0] = new Stack("CREATURE_ARCHER", 9);       // tier 2
        t.Garrison[1] = new Stack("CREATURE_SKELETON", 5);     // undead
        t.Garrison[2] = new Stack("CREATURE_GRIFFIN", 4);      // tier 4
        Assert.Equal(TrainStatus.NoBuilding, g.TransformQuote(t, null, 0).Status);
        t.Built["TB_SPECIAL_2"] = 1;
        t.Built.Remove("TB_DWELLING_2");
        Assert.Equal(TrainStatus.NoDwelling, g.TransformQuote(t, null, 0).Status);
        t.Built["TB_DWELLING_2"] = 1;
        Assert.Equal(TrainStatus.NotTrainable, g.TransformQuote(t, null, 1).Status);
        int gold = g.Current.Purse[Purse.Gold];
        var r = g.Apply(new TransformCreatureCmd(t.Object, -1, 0) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("CREATURE_WALKING_DEAD", t.Garrison[0]!.Creature);
        Assert.Equal(9, t.Garrison[0]!.Count);
        Assert.Equal(gold, g.Current.Purse[Purse.Gold]);
        t.Built["TB_DWELLING_4"] = 1;
        Assert.Equal("CREATURE_VAMPIRE", g.TransformQuote(t, null, 2).Target);
    }

    [Fact]
    public void InfernoPitTurnsHitPointsIntoExperience()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        t.Type = "TOWN_INFERNO";
        h.Pos = g.EntranceOf(t);
        h.InTown = t.Object;
        h.Perks.Clear();
        h.Skills.Clear();
        h.Artifacts.Clear();
        Array.Fill(h.Army, null);
        h.Army[0] = new Stack("CREATURE_IMP", 10);
        h.Army[1] = new Stack("CREATURE_IMP", 1);
        t.Built.Remove("TB_SPECIAL_5");
        Assert.Equal(TrainStatus.NoBuilding, g.SacrificeQuote(t, h, 0, growth: false).Status);
        t.Built["TB_SPECIAL_5"] = 1;
        int hp = f.Core.Creatures["CREATURE_IMP"].Health;
        var (status, exp) = g.SacrificeQuote(t, h, 0, growth: false);
        Assert.Equal(TrainStatus.Ok, status);
        Assert.Equal(10L * hp, exp);
        Assert.Equal(TrainStatus.NoHero, g.SacrificeQuote(t, null, 0, growth: false).Status);
        long before = h.Experience;
        var r = g.Apply(new SacrificeForExperienceCmd(t.Object, h.Id, 0) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Null(h.Army[0]);
        Assert.True(h.Experience > before);
    }

    [Fact]
    public void RitualPitBonusesFollowTheExeThresholds()
    {
        Assert.Equal((0, 0), Engine.Adventure.RitualPitBonus(299));
        Assert.Equal((1, 0), Engine.Adventure.RitualPitBonus(300));       // 150·2
        Assert.Equal((2, 0), Engine.Adventure.RitualPitBonus(1199));      // 150·4 ≤ 1199 < 150·8 and < 400·3
        Assert.Equal((3, 1), Engine.Adventure.RitualPitBonus(1200));      // 150·8 and 400·3
        Assert.Equal((5, 2), Engine.Adventure.RitualPitBonus(4800));      // 150·32; 400·9 ≤ 4800 < 400·27
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Type = "TOWN_DUNGEON";
        t.Built["TB_SPECIAL_3"] = 1;
        Array.Fill(t.Garrison, null);
        t.Garrison[0] = new Stack("CREATURE_PEASANT", 30);
        int hp = f.Core.Creatures["CREATURE_PEASANT"].Health;
        var r = g.Apply(new SacrificeForGrowthCmd(t.Object, -1, 0) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(30 * hp, t.SacrificedHitPoints);
        Assert.Null(t.Garrison[0]);
    }
}
