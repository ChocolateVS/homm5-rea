using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>Memory Mentor: forget one skill level or one perk (exe CUnlearnHeroSkillCmd, vtable 0xe0ee40, execute 00429d90).</summary>
public sealed record UnlearnHeroSkillCmd(int HeroId, string Skill) : AdvCommand;
/// <summary>Forget several skills/perks in a row (CUnlearnHeroSkillsCmd, vtable 0xe0ee88, execute 00429e80).</summary>
public sealed record UnlearnHeroSkillsCmd(int HeroId, IReadOnlyList<string> Skills) : AdvCommand;
/// <summary>Spend one forgotten point on an option of the relearn offer (CRelearnHeroSkillCmd, vtable 0xe0ee64, execute 00429e50).</summary>
public sealed record RelearnHeroSkillCmd(int HeroId, int Option) : AdvCommand;

// Memory Mentor (BUILDING_MEMORY_MENTOR, building type 0x73): the visit opens the hero screen in its unlearn mode
// (CMemoryMentorDialogLogEntry → 006c4460 → 006cc450).
public sealed partial class Adventure
{
    public const string MemoryMentor = "BUILDING_MEMORY_MENTOR";

    /// <summary>Racial skill per faction, exe 00875550 (hero race vfunc 0x258 = +0x88, 1..8).</summary>
    static readonly Dictionary<string, string> RacialSkills = new()
    {
        ["TOWN_HEAVEN"] = "HERO_SKILL_TRAINING", ["TOWN_PRESERVE"] = "HERO_SKILL_AVENGER", ["TOWN_ACADEMY"] = "HERO_SKILL_ARTIFICIER",
        ["TOWN_INFERNO"] = "HERO_SKILL_GATING", ["TOWN_NECROMANCY"] = "HERO_SKILL_NECROMANCY", ["TOWN_DUNGEON"] = "HERO_SKILL_INVOCATION",
        ["TOWN_FORTRESS"] = "HERO_SKILL_RUNELORE", ["TOWN_STRONGHOLD"] = "HERO_SKILL_DEMONIC_RAGE",
    };

    /// <summary>Unlearn price, hero vfunc 0x33c = 00b4c300: max(1, ceil(√level)) × 500 gold (level = hero +0x64).</summary>
    public static int UnlearnCost(HeroState h) => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(0, h.Level)))) * 500;

    /// <summary>
    /// Mastery as hero vfunc 0x174 (00b4d150) reports it: the stored level, +1 for the racial skill while the hero wears
    /// PEDANT_OF_MASTERY (artifact 15, 00ca6680 on the worn artifacts); outside 0..4 it reads 0.
    /// </summary>
    public static int EffectiveMastery(HeroState h, string skill)
    {
        int m = (int)h.MasteryOf(skill);
        if (RacialSkills.GetValueOrDefault(h.Town) == skill && h.Artifacts.Contains("PEDANT_OF_MASTERY")) m++;
        return m is < 0 or > 4 ? 0 : m;
    }

    /// <summary>
    /// Hero vfunc 0x334 = 00b4edc0: whether the hero may forget <paramref name="skill"/> (a skill level or a perk).
    /// The hero must pass vfunc 0x2ec (00b5ab80: level ≥ hero +0xd8; read here as "no level-up choice pending" —
    /// UNVERIFIED). A skill (type 0): with mastery m, the racial skill needs m &gt; 1 (+1 more with the Pendant of
    /// Mastery), and the hero's perks of that skill (vfunc 0x180) must number fewer than m. A perk (types 1..4): no
    /// other perk the hero has of type 2 or 4 (special / unique) may list it among its class prerequisites.
    /// </summary>
    public bool CanUnlearn(HeroState h, string skill)
    {
        if (h.PendingLevelUps.Count > 0) return false;
        if (!Db.Perks.TryGetValue(skill, out var def)) return false;
        if (def.IsSkill)
        {
            int m = EffectiveMastery(h, skill);
            if (m <= 0) return false;
            if (RacialSkills.GetValueOrDefault(h.Town) == skill && m <= 1 + (h.Artifacts.Contains("PEDANT_OF_MASTERY") ? 1 : 0)) return false;
            int perks = h.Perks.Count(p => Db.Perks.TryGetValue(p, out var pd) && pd.BasicSkill == skill);
            return perks < m;
        }
        if (!h.Perks.Contains(skill)) return false;
        foreach (var other in h.Perks)
        {
            if (other == skill || !Db.Perks.TryGetValue(other, out var od) || !(od.IsSpecialPerk || od.IsUltimate)) continue;
            if (od.PrereqByClass.TryGetValue(h.Class, out var deps) && deps.Contains(skill)) return false;
        }
        return true;
    }

    /// <summary>
    /// 00429d90: the hero exists; vfunc 0x334 allows it; the player's purse must cover the price (009d0720), it is paid
    /// (0xe0), then vfunc 0x340 (00b56050) forgets it: a skill loses one level and goes at 0, a perk goes; the hero's
    /// unlearn counter (+0x2a4) rises and a relearn offer is rolled with the level-up generator (00b52810).
    /// GAP: 00b55d80 also undoes one-time perk effects (attribute perks, e.g. ids 0x4f/0x51/0x65/0x73/0x92/0xba;
    /// Logistics/Leadership/Luck refreshes), which the engine does not apply when learning either.
    /// </summary>
    string? DoUnlearnHeroSkill(UnlearnHeroSkillCmd c) => Unlearn(c.HeroId, new[] { c.Skill });

    /// <summary>00429e80: the price is computed once (vfunc 0x33c), then each entry in turn as 00429d90; the first refusal stops.</summary>
    string? DoUnlearnHeroSkills(UnlearnHeroSkillsCmd c) => Unlearn(c.HeroId, c.Skills);

    string? Unlearn(int heroId, IReadOnlyList<string> skills)
    {
        var h = S.Hero(heroId);
        var p = Current;
        if (h == null || !h.Alive || h.Owner != p.Index) return "not your hero";
        int cost = UnlearnCost(h);
        foreach (var skill in skills)
        {
            if (!CanUnlearn(h, skill)) return "cannot forget this";
            if (p.Purse[Purse.Gold] < cost) return "not enough gold";
            GiveOne(p, Purse.Gold, -cost);
            ForgetSkill(h, skill);
        }
        return null;
    }

    void ForgetSkill(HeroState h, string skill)
    {
        var def = Db.Perks[skill];
        if (def.IsSkill)
        {
            var entry = h.Skills.First(s => s.SkillId == skill);
            int i = h.Skills.IndexOf(entry);
            var lower = entry.Mastery - 1;
            if (lower <= Mastery.None)
            {
                h.Skills.RemoveAt(i);
                // Perks of a forgotten skill go with it (00b56050 cleanup loop: perks whose +0x5c parent is the skill).
                h.Perks.RemoveAll(p => Db.Perks.TryGetValue(p, out var pd) && pd.BasicSkill == skill);
            }
            else h.Skills[i] = new SkillEntry(skill, lower);
        }
        else h.Perks.Remove(skill);
        RevertOnUnlearn(h, skill);
        Emit(new SkillLearned(h.Id, skill, def.IsSkill ? h.MasteryOf(skill) : Mastery.None));
        h.UnlearnedPoints++;
        h.RelearnOffer = new LevelUpOffer { NewLevel = h.Level };
        BuildOffer(h, h.RelearnOffer);
        h.MaxMovePoints = ComputeMaxMovePoints(h);
    }

    /// <summary>
    /// 00429e50 → hero vfunc 0x34c (00b53670): with forgotten points left (vfunc 0x344 = +0x2a4 &gt; 0) the hero learns the
    /// chosen skill (vfunc 0x1c8), the counter drops, and a new offer is rolled while points remain (else cleared).
    /// </summary>
    string? DoRelearnHeroSkill(RelearnHeroSkillCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (h.UnlearnedPoints <= 0 || h.RelearnOffer is not { } offer) return "nothing to relearn";
        int n = offer.Skills.Count + offer.Perks.Count;
        if (n > 0)
        {
            if (c.Option < 0 || c.Option >= n) return "invalid option";
            if (c.Option < offer.Skills.Count)
            {
                var s = offer.Skills[c.Option];
                h.Skills.RemoveAll(x => x.SkillId == s.SkillId);
                h.Skills.Add(s);
                Emit(new SkillLearned(h.Id, s.SkillId, s.Mastery));
                ApplyPerkOnLearn(h, s.SkillId);
            }
            else
            {
                var perk = offer.Perks[c.Option - offer.Skills.Count];
                h.Perks.Add(perk);
                Emit(new SkillLearned(h.Id, perk, Mastery.Basic));
                ApplyPerkOnLearn(h, perk);
            }
        }
        h.UnlearnedPoints--;
        if (h.UnlearnedPoints > 0)
        {
            h.RelearnOffer = new LevelUpOffer { NewLevel = h.Level };
            BuildOffer(h, h.RelearnOffer);
        }
        else h.RelearnOffer = null;
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        return null;
    }
}
