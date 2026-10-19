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
}
