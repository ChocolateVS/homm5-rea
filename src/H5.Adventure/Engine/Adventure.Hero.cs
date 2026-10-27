using H5.Adventure.Data;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Experience and level-ups per H5_Game.exe: level-up driver 00b52b10, offer generator 00b52810, confirm 00b575d0,
// on-learn effects 00b54770 (undone by 00b55d80).
public sealed partial class Adventure
{
    public const int MaxSkills = 6;

    /// <summary>Adds experience (with Enlightenment and artifact bonuses unless already applied) and queues level-up offers.</summary>
    public void GainExperience(HeroState h, long amount, bool modifiersApplied = false)
    {
        if (amount <= 0) return;
        long gained = modifiersApplied ? amount : ExperienceModifiers(h, amount);
        h.Experience += gained;
        Emit(new ExperienceGained(h.Id, gained, h.Experience));
        int maxLevel = Map.HeroMaxLevel > 0 ? Map.HeroMaxLevel : 40;
        while (h.Level < maxLevel && h.Experience >= Core.Experience.For(h.Level + 1))
            LevelUp(h);
    }

    /// <summary>
    /// One level (00b52b10 rolls the attribute with hero vfunc 0x308(0, 1) and the offer with 00b52810; 00b575d0 adds
    /// the attribute, sets the level, learns the choice and re-applies Enlightenment). The attribute is applied here
    /// at once and the choice later (<see cref="DoChooseLevelUp"/>); the exe rolls each level after the previous choice.
    /// </summary>
    void LevelUp(HeroState h)
    {
        h.Level++;
        var offer = new LevelUpOffer { NewLevel = h.Level };
        if (Core.HeroClasses.ContainsKey(h.Class))
        {
            offer.Attribute = RollLevelUpAttribute(h);
            AddStat(h, offer.Attribute, 1);
        }
        ApplyLearningBonus(h);
        ResearcherRune(h);
        BuildOffer(h, offer);
        if (offer.Skills.Count + offer.Perks.Count > 0)
        {
            h.PendingLevelUps.Add(offer);
            Emit(new LevelUpOffered(h.Id, h.Level, offer.Attribute, offer.Skills, offer.Perks));
        }
    }

    /// <summary>
    /// HERO_SPEC_RESEARCHER in 00b575d0 (00b578d8..00b579eb): hero vf 0x190(0x40) = FUN_00b4b5a0 (the specialization,
    /// hero +0xa8, equals 0x40); a roll of hero RNG +0x1a0 vf 8(0, 1) below RPGStats +0x930 =
    /// combat/HeroSpecializations/Runemage/Researcher_ChanceToGetExtraRune (0.25); then circles 1..4 in turn, the runes
    /// of the map pool (manager slot 0x10(circle, 5)) in order: the first one the hero does not know (vf 0xdc) is
    /// learned (vf 0xec, no source) and the search ends.
    /// </summary>
    void ResearcherRune(HeroState h)
    {
        if (h.Specialization != "HERO_SPEC_RESEARCHER") return;
        if (!(S.Rng.NextDouble() < (float)Core.Stats.Get("combat/HeroSpecializations/Runemage/Researcher_ChanceToGetExtraRune", 0.25))) return;
        for (int lvl = 1; lvl < 5; lvl++)
            foreach (var sp in SpellsOf(lvl, SchoolRunic))
                if (!KnowsSpell(h, sp))
                {
                    // vf 0xec = 00b545a0 refuses it without Runelore (00b4b810); the search ends either way.
                    if (SpellFitsRage(h, sp)) LearnSpell(h, sp, force: true);
                    return;
                }
    }

    /// <summary>
    /// Hero vfunc 0x308 = 00b50a50 with (no RNG override, limit on): class AttributeProbs (Offence, Defence, Spellpower,
    /// Knowledge at class +0x20..+0x2c) drawn by 00b509b0 (r = rand[0, sum - 1], first cumulative weight above r). A
    /// Spellpower roll gives Knowledge instead unless Spellpower &gt; 2 or Knowledge + 1 &lt;= 4 * Spellpower (hero +0x3c /
    /// +0x38, read as the base Spellpower / Knowledge in HeroAttribute order). No weight at all reads Defence (switch default).
    /// </summary>
    string RollLevelUpAttribute(HeroState h)
    {
        var w = Core.HeroClasses.TryGetValue(h.Class, out var cls) ? cls.AttributeChances : new int[4];
        int pick = -1, sum = w.Sum();
        if (sum > 0)
        {
            int r = S.Rng.Next(sum), acc = 0;
            for (int i = 0; i < w.Length && pick < 0; i++) if (r < (acc += w[i])) pick = i;
        }
        return LevelUpAttribute(pick, h.SpellPower, h.Knowledge);
    }

    /// <summary>00b50a50's result for the drawn AttributeProbs index (-1: none).</summary>
    public static string LevelUpAttribute(int pick, int spellPower, int knowledge) => pick switch
    {
        0 => "Attack",
        2 => spellPower > 2 || knowledge + 1 <= spellPower * 4 ? "SpellPower" : "Knowledge",
        3 => "Knowledge",
        _ => "Defence",
    };

    /// <summary>
    /// Enlightenment (00b54770 case 3, run on learning the skill and on every level-up by 00b575d0): the hero holds
    /// level / max(2, 5 - mastery) (00b4bea0; Basic every 4 levels, Advanced 3, Expert 2) extra primary stats, each drawn
    /// as a level-up attribute (vfunc 0x308) and remembered (+0x2b0) so that 00b55d80 can take the newest back.
    /// </summary>
    void ApplyLearningBonus(HeroState h)
    {
        int target = LearningBonus(h);
        while (h.LearningStats.Count < target)
        {
            var stat = RollLevelUpAttribute(h);
            h.LearningStats.Add(stat);
            AddStat(h, stat, 1);
        }
    }

    int LearningBonus(HeroState h)
    {
        int mastery = (int)RawMastery(h, "HERO_SKILL_LEARNING");
        return mastery <= 0 ? 0 : h.Level / Math.Max(2, 5 - mastery);
    }

    /// <summary>00b55d80 case 3: drop the newest Enlightenment stats until no more than the current bonus remain.</summary>
    void RevokeLearningBonus(HeroState h)
    {
        int target = LearningBonus(h);
        while (h.LearningStats.Count > target)
        {
            var stat = h.LearningStats[^1];
            h.LearningStats.RemoveAt(h.LearningStats.Count - 1);
            AddStat(h, stat, -1);
        }
    }

    /// <summary>
    /// 00875410: the Barbarian copies of standard perks count as the original (BARBARIAN_LEARNING = LEARNING, ...) when the
    /// hero's skill list is searched (00b4d0b0) and when the on-learn effect is chosen (00b54770).
    /// </summary>
    static string CanonicalSkill(string id) => id switch
    {
        "HERO_SKILL_BARBARIAN_LEARNING" => "HERO_SKILL_LEARNING",
        "HERO_SKILL_BARBARIAN_ANCIENT_SMITHY" => "HERO_SKILL_ANCIENT_SMITHY",
        "HERO_SKILL_BARBARIAN_WEAKENING_STRIKE" => "HERO_SKILL_WEAKENING_STRIKE",
        "HERO_SKILL_BARBARIAN_SOIL_BURN" => "HERO_SKILL_SOIL_BURN",
        "HERO_SKILL_BARBARIAN_FOG_VEIL" => "HERO_SKILL_FOG_VEIL",
        "HERO_SKILL_BARBARIAN_INTELLIGENCE" => "HERO_SKILL_INTELLIGENCE",
        "HERO_SKILL_BARBARIAN_MYSTICISM" => "HERO_SKILL_MYSTICISM",
        "HERO_SKILL_BARBARIAN_ELITE_CASTERS" => "HERO_SKILL_ELITE_CASTERS",
        "HERO_SKILL_BARBARIAN_STORM_WIND" => "HERO_SKILL_STORM_WIND",
        "HERO_SKILL_BARBARIAN_FIRE_PROTECTION" => "HERO_SKILL_FIRE_PROTECTION",
        "HERO_SKILL_BARBARIAN_SUN_FIRE" => "HERO_SKILL_SUN_FIRE",
        "HERO_SKILL_BARBARIAN_DISTRACT" => "HERO_SKILL_DISTRACT",
        "HERO_SKILL_BARBARIAN_DARK_REVELATION" => "HERO_SKILL_DARK_REVELATION",
        "HERO_SKILL_BARBARIAN_MENTORING" => "HERO_SKILL_MENTORING",
        _ => id,
    };

    /// <summary>00b4d0b0: stored mastery of a skill (a perk the hero has reads 1), matching Barbarian copies.</summary>
    static Mastery RawMastery(HeroState h, string id)
    {
        foreach (var s in h.Skills)
            if (s.Mastery > Mastery.None && (s.SkillId == id || CanonicalSkill(s.SkillId) == id)) return s.Mastery;
        foreach (var p in h.Perks)
            if (p == id || CanonicalSkill(p) == id) return Mastery.Basic;
        return Mastery.None;
    }

    /// <summary>
    /// 00b52810: skill 1 is a new skill (00b50db0); without one, two upgrades (00b4f400, the second excluding the
    /// first); with one, an upgrade, or a second new skill when nothing can be upgraded. Perk 1 is a standard perk
    /// (00b4f5c0(-, 1, 0)), else any perk; perk 2 a special perk (00b4f5c0(perk 1, 0, 1)), else any perk but perk 1.
    /// </summary>
    void BuildOffer(HeroState h, LevelUpOffer offer)
    {
        var n1 = DrawNewSkill(h, null);
        if (n1 == null)
        {
            var u1 = DrawUpgrade(h, null);
            if (u1 != null) offer.Skills.Add(new SkillEntry(u1, RawMastery(h, u1) + 1));
            var u2 = DrawUpgrade(h, u1);
            if (u2 != null) offer.Skills.Add(new SkillEntry(u2, RawMastery(h, u2) + 1));
        }
        else
        {
            offer.Skills.Add(new SkillEntry(n1, Mastery.Basic));
            var u = DrawUpgrade(h, null);
            if (u != null) offer.Skills.Add(new SkillEntry(u, RawMastery(h, u) + 1));
            else if (DrawNewSkill(h, n1) is { } n2) offer.Skills.Add(new SkillEntry(n2, Mastery.Basic));
        }

        var candidates = PerkCandidates(h);
        var p1 = DrawPerk(candidates, null, standard: true, special: false) ?? DrawPerk(candidates, null, true, true);
        if (p1 != null) offer.Perks.Add(p1);
        var p2 = DrawPerk(candidates, p1, standard: false, special: true) ?? DrawPerk(candidates, p1, true, true);
        if (p2 != null) offer.Perks.Add(p2);
    }

    /// <summary>The hero's skills (hero vfunc 0x17c = 00b4f020: list entries of type skill with mastery &gt; 0).</summary>
    List<string> KnownSkills(HeroState h) =>
        h.Skills.Where(s => s.Mastery > Mastery.None && (!Db.Perks.TryGetValue(s.SkillId, out var d) || d.IsSkill)).Select(s => s.SkillId).ToList();

    /// <summary>
    /// 00b50db0: none once the hero has six skills. Candidates (00875e20) are the skills without a hero class in
    /// enum order, then the hero's racial skill (00875550); those the hero does not know and other than
    /// <paramref name="except"/>. 00b50be0 weighs them by the class SkillsProbs (0 when not listed) and draws
    /// with 00b509b0; when every weight is 0 it returns the first candidate.
    /// </summary>
    string? DrawNewSkill(HeroState h, string? except)
    {
        if (KnownSkills(h).Count > MaxSkills - 1) return null;
        var list = Db.Perks.Values.Where(d => d.IsSkill && (d.HeroClass.Length == 0 || d.HeroClass == "HERO_CLASS_NONE"))
            .OrderBy(d => d.Index).Select(d => d.Id).ToList();
        if (RacialSkills.GetValueOrDefault(h.Town) is { } racial) list.Add(racial);
        var cands = list.Where(s => RawMastery(h, s) == Mastery.None && s != except).ToList();
        if (cands.Count == 0) return null;
        var probs = Db.SkillProbs.GetValueOrDefault(h.Class) ?? new();
        var weights = cands.Select(s => probs.FirstOrDefault(p => p.Skill == s).Prob).ToList();
        return cands[PickWeighted(weights)];
    }

    /// <summary>00b4f400: a uniform pick (00b4f2f0) among the hero's skills below Expert, other than <paramref name="except"/>.</summary>
    string? DrawUpgrade(HeroState h, string? except)
    {
        var pool = KnownSkills(h).Where(s => RawMastery(h, s) < Mastery.Expert && s != except).ToList();
        return pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)];
    }

    /// <summary>
    /// 00b4f5c0: a uniform pick among the open perks other than <paramref name="except"/>; types 1 (standard) and 3
    /// (class) are allowed by <paramref name="standard"/>, types 2 (special) and 4 (ultimate) by <paramref name="special"/>.
    /// </summary>
    string? DrawPerk(List<PerkDef> candidates, string? except, bool standard, bool special)
    {
        var pool = candidates.Where(p => p.Id != except && ((p.IsStandardPerk || p.IsClassPerk) ? standard : special)).ToList();
        return pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)].Id;
    }

    /// <summary>
    /// The perks 00b50ff0 leaves open in the hero's list. Unlocked (00876160 -> 008766a0 -> 00875b30): a perk of a skill
    /// the hero knows that he does not have; standard always, class perk when its HeroClass is the hero's, special and
    /// ultimate when Skills.xdb SkillPrerequisites has an entry for the hero's class whose every dependency he knows
    /// (00875a40). Locked (00875f20): the hero has as many perks of that skill as its mastery (vfunc 0x174, the Pendant
    /// of Mastery adds 1 to the racial skill), the limit being 4 for perks with a hero class once the skill is Expert.
    /// </summary>
    public List<string> OpenPerks(HeroState h) => PerkCandidates(h).Select(p => p.Id).ToList();

    List<PerkDef> PerkCandidates(HeroState h)
    {
        var known = KnownSkills(h).Select(CanonicalSkill).ToHashSet();
        var list = new List<PerkDef>();
        foreach (var p in Db.Perks.Values.OrderBy(p => p.Index))
        {
            if (p.IsSkill || RawMastery(h, p.Id) > Mastery.None || !known.Contains(p.BasicSkill)) continue;
            bool open = p.IsStandardPerk
                || (p.IsClassPerk && p.HeroClass == h.Class)
                || ((p.IsSpecialPerk || p.IsUltimate) && p.PrereqByClass.TryGetValue(h.Class, out var deps)
                    && deps.All(d => RawMastery(h, d) > Mastery.None));
            if (!open) continue;
            int limit = EffectiveMastery(h, p.BasicSkill);
            if (limit > 2 && p.HeroClass.Length > 0 && p.HeroClass != "HERO_CLASS_NONE") limit = 4;
            int taken = h.Perks.Count(id => Db.Perks.TryGetValue(id, out var d) && d.BasicSkill == p.BasicSkill);
            if (taken >= limit) continue;
            list.Add(p);
        }
        return list;
    }

    string? DoChooseLevelUp(ChooseLevelUpCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || h.Owner != Current.Index) return "not your hero";
        if (h.PendingLevelUps.Count == 0) return "no pending level-up";
        var offer = h.PendingLevelUps[0];
        int n = offer.Skills.Count + offer.Perks.Count;
        // Nothing left to offer (every skill/perk known): only the attribute point applies.
        if (n == 0) { h.PendingLevelUps.RemoveAt(0); return null; }
        if (c.Option < 0 || c.Option >= n) return "invalid option";
        h.PendingLevelUps.RemoveAt(0);
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
        // Later offers were rolled before this choice; refresh them against the new skill set.
        for (int i = 0; i < h.PendingLevelUps.Count; i++)
        {
            var o = h.PendingLevelUps[i];
            o.Skills.Clear(); o.Perks.Clear();
            BuildOffer(h, o);
        }
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        return null;
    }

    /// <summary>Primary stats a perk gives while known (00b54770 -> 00b4cbc0; undone by 00b55d80 -> vfunc 0x1bc).</summary>
    static (string Stat, int Amount)? PerkStat(string perk) => CanonicalSkill(perk) switch
    {
        "HERO_SKILL_STUDENT_AWARD" or "HERO_SKILL_SECRETS_OF_DESTRUCTION" => ("Knowledge", 2),
        "HERO_SKILL_LORD_OF_UNDEAD" => ("Knowledge", 1),
        "HERO_SKILL_CASTER_CERTIFICATE" or "HERO_SKILL_MASTER_OF_SECRETS" or "HERO_SKILL_ACADEMY_AWARD" or "HERO_SKILL_INSIGHTS" => ("SpellPower", 2),
        "HERO_SKILL_FOREST_GUARD_EMBLEM" => ("Attack", 2),
        "HERO_SKILL_SEAL_OF_PROTECTION" or "HERO_SKILL_DEFEND_US_ALL" or "HERO_SKILL_BODYBUILDING" => ("Defence", 2),
        _ => null,
    };

    /// <summary>
    /// On-learn effects (00b54770, called by vfunc 0x1c8 after the skill/perk is stored): Enlightenment's stats; the
    /// attribute perks of <see cref="PerkStat"/>; once per hero (flags never cleared): Graduate +1000 experience through
    /// the experience modifiers (+0x2a8), Academy Award +1000 gold (+0x2a9), Dark Revelation (+0x2ab) - with L the level
    /// of the current experience (00b4c840) and M the map's level cap: L + 1 &lt; M adds one level's width
    /// (T(L + 1) - T(L)), L + 1 = M sets the experience to T(M), else nothing.
    /// </summary>
    void ApplyPerkOnLearn(HeroState h, string perk)
    {
        // vf 0x1c8 = 00b57ab0: 00b54770, then the book rebuild 00b53b10 (00b57dbd).
        OnLearnEffects(h, perk);
        RebuildBook(h);
    }

    void OnLearnEffects(HeroState h, string perk)
    {
        var id = CanonicalSkill(perk);
        if (id == "HERO_SKILL_LEARNING") { ApplyLearningBonus(h); return; }
        if (PerkStat(id) is { } st) AddStat(h, st.Stat, st.Amount);
        PerkSpellsOnLearn(h, id);
        if (id is not ("HERO_SKILL_STUDENT_AWARD" or "HERO_SKILL_ACADEMY_AWARD" or "HERO_SKILL_DARK_REVELATION")) return;
        if (!h.PerkRewardsClaimed.Add(id)) return;
        switch (id)
        {
            case "HERO_SKILL_STUDENT_AWARD":
                GainExperience(h, 1000);
                break;
            case "HERO_SKILL_ACADEMY_AWARD":
                if (PlayerByIndex(h.Owner) is { } p) GiveOne(p, Purse.Gold, 1000);
                break;
            case "HERO_SKILL_DARK_REVELATION":
            {
                int maxLevel = Map.HeroMaxLevel > 0 ? Map.HeroMaxLevel : 40;
                int l = Core.Experience.LevelFor(h.Experience);
                if (l + 1 < maxLevel) GainExperience(h, Core.Experience.For(l + 1) - Core.Experience.For(l), modifiersApplied: true);
                else if (l + 1 == maxLevel) GainExperience(h, Core.Experience.For(maxLevel) - h.Experience, modifiersApplied: true);
                break;
            }
        }
    }

    /// <summary>
    /// The spells 00b54770 gives, each learned with the perk as the source (vf 0xec(spell, param_3[1])), from the map's
    /// spell manager (world vf 0x34, slot 0x14 = the main list +0x24 of a circle):
    /// Insights (case 0x77): circles 1, 2, 3 in turn, the first holding spells the hero can learn (vf 0xe4) and does
    /// not know (vf 0xdc) gives one of them at random (hero RNG +0x1a0); Secrets of Destruction (case 0x92): one random
    /// unknown DESTRUCTIVE spell (FUN_00a34350 = 0) of circles 1..3 together; Absolute Wizardry (case 0x89): every spell of
    /// circles 1..5 (vf 0xec, no can-learn test), then falls through into the mass grant below (00b5527a) with its own id,
    /// which FUN_00a344c0 never matches — nothing more.
    /// The Master perks (cases 0x2e..0x33, 00b5527a..00b5533a): for each spell of the known list (+0x204, in order) whose
    /// Master perk (FUN_00a344c0: FUN_00a33cf0(spell) = the perk) is this one, its mass version (FUN_00a33eb0, when not 0)
    /// is collected, then all are learned. Empowered Spells (case 0x46): the same over FUN_00a34060.
    /// </summary>
    void PerkSpellsOnLearn(HeroState h, string id)
    {
        switch (id)
        {
            case "HERO_SKILL_INSIGHTS":
                for (int lvl = 1; lvl <= 3; lvl++)
                {
                    var cand = Buckets.All[lvl].Where(sp => CanLearnSpell(h, sp) && !KnowsSpell(h, sp)).ToList();
                    if (cand.Count == 0) continue;
                    LearnSpell(h, cand[S.Rng.Next(cand.Count)], force: true, source: id);
                    return;
                }
                break;
            case "HERO_SKILL_SECRETS_OF_DESTRUCTION":
            {
                var cand = new List<string>();
                for (int lvl = 1; lvl <= 3; lvl++)
                    cand.AddRange(Buckets.All[lvl].Where(sp => SchoolNo(Core.Spell(sp)!.School) == 0 && !KnowsSpell(h, sp)));
                if (cand.Count > 0) LearnSpell(h, cand[S.Rng.Next(cand.Count)], force: true, source: id);
                break;
            }
            case "HERO_SKILL_ABSOLUTE_WIZARDY":
                for (int lvl = 1; lvl <= 5; lvl++)
                    foreach (var sp in Buckets.All[lvl].ToList()) LearnSpell(h, sp, force: true, source: id);
                break;
            case "HERO_SKILL_MASTER_OF_CURSES" or "HERO_SKILL_MASTER_OF_MIND" or "HERO_SKILL_MASTER_OF_SICKNESS"
                or "HERO_SKILL_MASTER_OF_BLESSING" or "HERO_SKILL_MASTER_OF_ABJURATION" or "HERO_SKILL_MASTER_OF_WRATH":
                foreach (var mass in h.Spells.Where(s => KnowsSpell(h, s)).Select(MassVersion).ToList())
                    if (mass != null && MassSpellPerk[mass] == id) LearnSpell(h, mass, force: true, source: id);
                break;
            case "HERO_SKILL_EMPOWERED_SPELLS":
                foreach (var emp in h.Spells.Where(s => KnowsSpell(h, s)).Select(EmpoweredVersion).ToList())
                    if (emp != null) LearnSpell(h, emp, force: true, source: id);
                break;
        }
    }

    /// <summary>00b55d80 (Memory Mentor): takes back the stats of <see cref="ApplyPerkOnLearn"/>; one-time rewards stay.</summary>
    void RevertOnUnlearn(HeroState h, string skill)
    {
        var id = CanonicalSkill(skill);
        if (id == "HERO_SKILL_LEARNING") { RevokeLearningBonus(h); return; }
        if (PerkStat(id) is { } st) AddStat(h, st.Stat, -st.Amount);
    }
}
