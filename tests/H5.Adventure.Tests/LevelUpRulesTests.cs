using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Hero development per H5_Game.exe: experience table 00b4c6b0, level-up offer 00b52810 (new skill 00b50db0, upgrade
/// 00b4f400, perk 00b4f5c0, open perks 00b50ff0), attribute 00b50a50, on-learn effects 00b54770 / undo 00b55d80.
/// </summary>
[Collection("adv")]
public class LevelUpRulesTests(Fixture f)
{
    [Fact]
    public void ExperienceTableIsTheExeRecurrence()
    {
        var t = new ExperienceTable();
        Assert.Equal(0, t.For(1));
        Assert.Equal(1000, t.For(2));
        Assert.Equal(20600, t.For(12));
        Assert.Equal(24320, t.For(13));
        Assert.Equal(48283, t.For(17));
        Assert.Equal(57540, t.For(18));        // 7714 × 1.2 = 9256.8 → 9257 (the Editor PDF has 57539)
        Assert.Equal(167844, t.For(24));
        Assert.Equal(201013, t.For(25));       // × (25 − 13) × 0.1 from here
        Assert.Equal(786237, t.For(30));
        Assert.Equal(32979435, t.For(36));
        Assert.Equal(1216634591, t.For(40));   // single-precision products (D3D9 FPU mode)
        Assert.Equal(int.MaxValue, t.For(41));
        Assert.Equal(17, t.LevelFor(57539));
        Assert.Equal(18, t.LevelFor(57540));
        Assert.Equal(40, t.LevelFor(int.MaxValue));
    }

    [Fact]
    public void SpellpowerRollTurnsIntoKnowledgeForLowSpellpower()
    {
        Assert.Equal("Attack", Engine.Adventure.LevelUpAttribute(0, 0, 0));
        Assert.Equal("Defence", Engine.Adventure.LevelUpAttribute(1, 0, 0));
        Assert.Equal("Knowledge", Engine.Adventure.LevelUpAttribute(3, 9, 0));
        Assert.Equal("Defence", Engine.Adventure.LevelUpAttribute(-1, 0, 0));
        // Spellpower stays Spellpower when it exceeds 2 or Knowledge + 1 <= 4 · Spellpower.
        Assert.Equal("SpellPower", Engine.Adventure.LevelUpAttribute(2, 3, 50));
        Assert.Equal("SpellPower", Engine.Adventure.LevelUpAttribute(2, 1, 3));
        Assert.Equal("Knowledge", Engine.Adventure.LevelUpAttribute(2, 1, 4));
        Assert.Equal("Knowledge", Engine.Adventure.LevelUpAttribute(2, 0, 0));
        Assert.Equal("SpellPower", Engine.Adventure.LevelUpAttribute(2, 2, 7));
        Assert.Equal("Knowledge", Engine.Adventure.LevelUpAttribute(2, 2, 8));
    }

    static HeroState Knight(Engine.Adventure g)
    {
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.PendingLevelUps.Clear();
        h.Class = "HERO_CLASS_KNIGHT";
        h.Town = "TOWN_HEAVEN";
        h.Skills.Clear();
        h.Perks.Clear();
        h.Artifacts.Clear();
        return h;
    }

    [Fact]
    public void ClassPerksGoToTheStandardSlotAndTheUltimateOpensWithItsPrerequisites()
    {
        var g = f.NewGame("A2M1");
        var h = Knight(g);
        if (!g.Db.Perks.TryGetValue("HERO_SKILL_ABSOLUTE_CHARGE", out var ult) || !ult.IsUltimate) return;
        h.Skills.Add(new SkillEntry("HERO_SKILL_TRAINING", Mastery.Basic));
        var open = g.OpenPerks(h);
        // Class perks of the knight's racial skill are open; another class's are not.
        Assert.Contains("HERO_SKILL_HOLY_CHARGE", open);
        h.Skills.Add(new SkillEntry("HERO_SKILL_AVENGER", Mastery.Basic));
        Assert.DoesNotContain("HERO_SKILL_MULTISHOT", g.OpenPerks(h));
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_AVENGER");

        // Expert Training with its three class perks: the 4th slot (limit 4 for perks with a hero class) needs the
        // ultimate's prerequisites.
        h.Skills.Clear();
        h.Skills.Add(new SkillEntry("HERO_SKILL_TRAINING", Mastery.Expert));
        h.Perks.AddRange(new[] { "HERO_SKILL_HOLY_CHARGE", "HERO_SKILL_PRAYER", "HERO_SKILL_EXPERT_TRAINER" });
        Assert.DoesNotContain("HERO_SKILL_ABSOLUTE_CHARGE", g.OpenPerks(h));
        foreach (var dep in ult.PrereqByClass["HERO_CLASS_KNIGHT"])
        {
            var parent = g.Db.Perks[dep].BasicSkill;
            if (h.Skills.All(s => s.SkillId != parent)) h.Skills.Add(new SkillEntry(parent, Mastery.Expert));
            h.Perks.Add(dep);
        }
        Assert.Contains("HERO_SKILL_ABSOLUTE_CHARGE", g.OpenPerks(h));
    }

    [Fact]
    public void StandardPerksLockAtTheSkillsMastery()
    {
        var g = f.NewGame("A2M1");
        var h = Knight(g);
        const string skill = "HERO_SKILL_LOGISTICS";
        h.Skills.Add(new SkillEntry(skill, Mastery.Basic));
        var std = g.Db.Perks.Values.Where(p => p.IsStandardPerk && p.BasicSkill == skill).Select(p => p.Id).ToList();
        Assert.NotEmpty(std);
        Assert.Contains(std[0], g.OpenPerks(h));
        h.Perks.Add(std[0]);
        Assert.DoesNotContain(g.OpenPerks(h), p => g.Db.Perks[p].BasicSkill == skill);
        h.Skills[0] = new SkillEntry(skill, Mastery.Advanced);
        Assert.Contains(g.OpenPerks(h), p => g.Db.Perks[p].BasicSkill == skill);
    }

    [Fact]
    public void OfferHasTwoUpgradesWhenNoNewSkillAndNoSkillsWhenAllExpert()
    {
        var g = f.NewGame("A2M1");
        var h = Knight(g);
        var six = g.Db.Perks.Values.Where(p => p.IsSkill && (p.HeroClass is "" or "HERO_CLASS_NONE")).Take(6).Select(p => p.Id).ToList();
        foreach (var s in six) h.Skills.Add(new SkillEntry(s, Mastery.Basic));
        h.Experience = 0; h.Level = 1;
        g.GainExperience(h, 1000, modifiersApplied: true);
        var o = Assert.Single(h.PendingLevelUps);
        Assert.Equal(2, o.Skills.Count);
        Assert.All(o.Skills, s => Assert.Equal(Mastery.Advanced, s.Mastery));
        Assert.NotEqual(o.Skills[0].SkillId, o.Skills[1].SkillId);

        h.PendingLevelUps.Clear();
        for (int i = 0; i < h.Skills.Count; i++) h.Skills[i] = new SkillEntry(h.Skills[i].SkillId, Mastery.Expert);
        g.GainExperience(h, g.Core.Experience.For(3) - h.Experience, modifiersApplied: true);
        Assert.All(h.PendingLevelUps, x => Assert.Empty(x.Skills));
    }

    [Fact]
    public void NewSkillsComeFirstAndASecondNewSkillReplacesAMissingUpgrade()
    {
        var g = f.NewGame("A2M1");
        var h = Knight(g);
        h.Skills.Add(new SkillEntry("HERO_SKILL_TRAINING", Mastery.Expert));
        h.Experience = 0; h.Level = 1;
        g.GainExperience(h, 1000, modifiersApplied: true);
        var o = Assert.Single(h.PendingLevelUps);
        Assert.Equal(2, o.Skills.Count);
        Assert.All(o.Skills, s => Assert.Equal(Mastery.Basic, s.Mastery));
        Assert.DoesNotContain(o.Skills, s => s.SkillId == "HERO_SKILL_TRAINING");
    }

    [Fact]
    public void EnlightenmentGivesOneStatPerFourThreeTwoLevelsAndTheMentorTakesThemBack()
    {
        var g = f.NewGame("A2M1");
        var h = Knight(g);
        h.Level = 12;
        int Sum() => h.Attack + h.Defence + h.SpellPower + h.Knowledge;
        int before = Sum();
        h.PendingLevelUps.Add(new LevelUpOffer { NewLevel = 12, Skills = { new SkillEntry("HERO_SKILL_LEARNING", Mastery.Basic) } });
        Assert.True(g.Apply(new ChooseLevelUpCmd(h.Id, 0) { Player = h.Owner }).Ok);
        Assert.Equal(before + 3, Sum());          // 12 / 4
        h.PendingLevelUps.Add(new LevelUpOffer { NewLevel = 12, Skills = { new SkillEntry("HERO_SKILL_LEARNING", Mastery.Expert) } });
        Assert.True(g.Apply(new ChooseLevelUpCmd(h.Id, 0) { Player = h.Owner }).Ok);
        Assert.Equal(before + 6, Sum());          // 12 / 2
        Assert.Equal(6, h.LearningStats.Count);

        // Memory Mentor: Expert → Advanced keeps 12 / 3 = 4.
        g.Current.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 100000));
        Assert.True(g.Apply(new UnlearnHeroSkillCmd(h.Id, "HERO_SKILL_LEARNING") { Player = h.Owner }).Ok);
        Assert.Equal(before + 4, Sum());
    }

    [Fact]
    public void AttributePerksAddTheirStatsAndGraduatePaysExperienceOnce()
    {
        var g = f.NewGame("A2M1");
        var h = Knight(g);
        h.Level = 5; h.Experience = g.Core.Experience.For(5);
        h.Skills.Add(new SkillEntry("HERO_SKILL_DEFENCE", Mastery.Basic));
        h.Skills.Add(new SkillEntry("HERO_SKILL_LEARNING", Mastery.Basic));
        int d = h.Defence, k = h.Knowledge;
        g.ScriptLearnPerk(h, "HERO_SKILL_BODYBUILDING");
        Assert.Equal(d + 2, h.Defence);
        long exp = h.Experience;
        g.ScriptLearnPerk(h, "HERO_SKILL_STUDENT_AWARD");
        Assert.True(h.Knowledge >= k + 2);
        Assert.Equal(exp + g.ExperienceModifiers(h, 1000), h.Experience);

        g.Current.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 100000));
        h.PendingLevelUps.Clear();
        int k2 = h.Knowledge;
        Assert.True(g.Apply(new UnlearnHeroSkillCmd(h.Id, "HERO_SKILL_STUDENT_AWARD") { Player = h.Owner }).Ok);
        Assert.Equal(k2 - 2, h.Knowledge);
        h.PendingLevelUps.Clear();
        long exp2 = h.Experience;
        g.ScriptLearnPerk(h, "HERO_SKILL_STUDENT_AWARD");
        Assert.Equal(exp2, h.Experience);        // the +0x2a8 flag stays set
        Assert.Equal(k2, h.Knowledge);
    }
}
