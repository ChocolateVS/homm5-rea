using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Battle-end experience per H5_Game.exe (docs/rules/audit/combat_heroes_skills_h.md).
public sealed partial class Adventure
{
    /// <summary>
    /// Hero experience modifier (exe CHero vcall 0x19c, FUN_00b4bbb0): exp + (5·Enlightenment mastery + 10·Turban of
    /// Enlightenment + 20·Chain Mail of Enlightenment + Educational_2_GainExperienceBonusPercents with 2+ pieces of the
    /// Educational set)·exp / 100, integer arithmetic.
    /// </summary>
    public long ExperienceModifiers(HeroState h, long exp)
    {
        int pct = 5 * Math.Clamp((int)h.MasteryOf("HERO_SKILL_LEARNING"), 0, 4)
                  + 10 * h.Artifacts.Count(a => a == "HELM_OF_ENLIGHTMENT") + 20 * h.Artifacts.Count(a => a == "CHAIN_MAIL_OF_ENLIGHTMENT");
        if (Core.ArtifactSets.TryGetValue("ARTFSET_EFFECT_EDUCATIONAL", out var set) && set.Count(h.Artifacts.Contains) >= 2)
            pct += (int)Core.Stats.Get("ArtifactsSetsEffectsConsts/Educational_2_GainExperienceBonusPercents", 15);
        return pct * exp / 100 + exp;
    }

    /// <summary>
    /// Experience of a winning hero (exe FUN_00af3430): killed creatures' experience, +500 when the enemy hero is
    /// lost (not fled / surrendered, 004d99b0), × trunc(level·0.02 + 1) for HERO_SPEC_TALENTED_WARRIOR (double, chop),
    /// × the neutral-army coefficient (chop), the hero modifier (vcall 0x19c), then Week of Folly / 2 and Week of
    /// Honor × 2 (FUN_00bcdc60) on the final amount.
    /// </summary>
    public long BattleExperience(HeroState h, long exp, bool opponentHeroLost, double neutralCoef)
    {
        if (opponentHeroLost) exp += 500;
        if (h.Specialization == "HERO_SPEC_TALENTED_WARRIOR") exp = (long)((h.Level * 0.02 + 1.0) * exp);
        if (neutralCoef != 1) exp = (long)(exp * (double)(float)neutralCoef);
        exp = ExperienceModifiers(h, exp);
        return S.MoonWeek switch { "WEEK_OF_FOLLY" => exp / 2, "WEEK_OF_HONOR" => exp * 2, _ => exp };
    }
}
