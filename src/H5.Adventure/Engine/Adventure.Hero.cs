using H5.Adventure.Data;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Experience and level-ups (fan manual "Hero Development", p.294-298).
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

    void LevelUp(HeroState h)
    {
        h.Level++;
        var offer = new LevelUpOffer { NewLevel = h.Level };
        // Primary attribute by class probability (HeroClass.xdb AttributeProbs, p.294).
        // TODO(exe): Enlightenment's extra attribute points every few levels.
        if (Core.HeroClasses.TryGetValue(h.Class, out var cls))
        {
            string[] names = { "Attack", "Defence", "SpellPower", "Knowledge" };
            int pick = PickWeighted(cls.AttributeChances);
            offer.Attribute = names[Math.Max(0, pick)];
            AddStat(h, offer.Attribute, 1);
        }
        BuildOffer(h, offer);
        if (offer.Skills.Count + offer.Perks.Count > 0)
        {
            h.PendingLevelUps.Add(offer);
            Emit(new LevelUpOffered(h.Id, h.Level, offer.Attribute, offer.Skills, offer.Perks));
        }
    }

    /// <summary>
    /// Up to two skills (one new, one improvement) and two perks (one common, one class-specific);
    /// missing categories are replaced by a second option of the other kind within skills or perks,
    /// never across (p.294). New skills are drawn with the class probabilities rescaled over unknown
    /// skills; improvements and perks are uniform (p.295).
    /// </summary>
    void BuildOffer(HeroState h, LevelUpOffer offer)
    {
        var known = h.Skills.Where(s => s.Mastery > Mastery.None).ToDictionary(s => s.SkillId, s => s.Mastery);
        var probs = Db.SkillProbs.GetValueOrDefault(h.Class) ?? new();
        var newPool = probs.Where(p => !known.ContainsKey(p.Skill) && p.Prob > 0).ToList();
        var improvePool = known.Where(k => k.Value < Mastery.Expert).Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        bool slotFree = known.Count < MaxSkills;

        string? DrawNew(IList<(string Skill, int Prob)> pool)
        {
            if (!slotFree || pool.Count == 0) return null;
            int i = PickWeighted(pool.Select(p => p.Prob).ToList());
            var s = pool[i].Skill;
            pool.RemoveAt(i);
            return s;
        }
        string? DrawImprove(IList<string> pool)
        {
            if (pool.Count == 0) return null;
            int i = S.Rng.Next(pool.Count);
            var s = pool[i];
            pool.RemoveAt(i);
            return s;
        }

        var n1 = DrawNew(newPool);
        var i1 = DrawImprove(improvePool);
        if (i1 == null) n1 ??= DrawNew(newPool);
        var n2 = i1 == null ? DrawNew(newPool) : null;
        var i2 = n1 == null ? DrawImprove(improvePool) : null;
        foreach (var s in new[] { n1, n2 }.Where(s => s != null)) offer.Skills.Add(new SkillEntry(s!, Mastery.Basic));
        foreach (var s in new[] { i1, i2 }.Where(s => s != null)) offer.Skills.Add(new SkillEntry(s!, known[s!] + 1));

        var common = EligiblePerks(h, known, special: false);
        var adv = EligiblePerks(h, known, special: true);
        string? Take(List<string> pool)
        {
            if (pool.Count == 0) return null;
            int i = S.Rng.Next(pool.Count);
            var p = pool[i];
            pool.RemoveAt(i);
            return p;
        }
        var c1 = Take(common);
        var a1 = Take(adv);
        if (c1 == null) a1 ??= Take(adv);
        var c2 = a1 == null ? Take(common) : null;
        var a2 = c1 == null ? Take(adv) : null;
        foreach (var p in new[] { c1, c2, a1, a2 }.Where(p => p != null)) offer.Perks.Add(p!);
    }

    /// <summary>
    /// Perks a hero may be offered: parent skill known, fewer perks taken in that branch than the
    /// branch's mastery level (Basic allows 1, Advanced 2, Expert 3), and the class prerequisites met.
    /// Standard perks are common to all classes; special and class perks need the hero's class listed.
    /// </summary>
    List<string> EligiblePerks(HeroState h, Dictionary<string, Mastery> known, bool special)
    {
        var list = new List<string>();
        foreach (var p in Db.Perks.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (p.IsSkill || p.IsUltimate || h.Perks.Contains(p.Id)) continue;
            if (special != (p.IsSpecialPerk || p.IsClassPerk)) continue;
            if (!known.TryGetValue(p.BasicSkill, out var mastery)) continue;
            int taken = h.Perks.Count(id => Db.Perks.TryGetValue(id, out var d) && d.BasicSkill == p.BasicSkill);
            if (taken >= (int)mastery) continue;
            if (special)
            {
                if (!p.PrereqByClass.TryGetValue(h.Class, out var deps) && p.HeroClass != h.Class) continue;
                if (deps != null && !deps.All(h.Perks.Contains)) continue;
            }
            list.Add(p.Id);
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

    /// <summary>One-time effects of perks when learned (p.298).</summary>
    void ApplyPerkOnLearn(HeroState h, string perk)
    {
        switch (perk)
        {
            case "HERO_SKILL_STUDENT_AWARD":     // Graduate: +1000 experience once
                GainExperience(h, 1000);
                break;
            case "HERO_SKILL_DARK_REVELATION":   // Dark Revelation: a free level
            case "HERO_SKILL_BARBARIAN_DARK_REVELATION":
                GainExperience(h, Math.Max(0, Core.Experience.For(h.Level + 1) - h.Experience));
                break;
        }
    }
}
