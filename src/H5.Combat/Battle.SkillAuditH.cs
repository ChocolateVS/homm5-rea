using H5.Core.Data;

namespace H5.Combat;

// Hero / artifact rules audited against H5_Game.exe in part H of the hero skills audit
// (docs/rules/audit/combat_heroes_skills_h.md).
public static class SkillAuditH
{
    /// <summary>
    /// A creature's initiative with its hero (exe FUN_004bc190): record initiative (week-adjusted, 00bcdd40) ×
    /// (1 + clamp(Furious_InitiativeBonusBase + level·PerLevel, 0, 5)) for HERO_SPEC_FURIOUS / MASTER_OF_INITIATIVE,
    /// + 2 for Iron/Steel/Obsidian Golems with March of the Machines, at least 1, then × (100 + p)/100 with the
    /// army artifacts' percent p (≥ −100): Ring of Haste +20 (any count), Ring of Celerity +10 each, Dragon Scale Armor
    /// (large creatures) / Dragon Scale Shield (small) +5 each, Dragon Bone Greaves +10 each for walking non-shooting
    /// non-casters, Dragon Wing Mantle +10 each for flyers, Dragon Teeth Necklace +10 each for ABILITY_CASTER /
    /// ABILITY_RANDOM_CASTER creatures, Dragon Eye Ring +10 each for shooters, Ogre Club / Ogre Shield −5 each.
    /// Without a hero only the week applies. (The per-creature permanent bonus byte and the script battle bonus
    /// HERO_BATTLE_BONUS_INITIATIVE are not modelled.)
    /// </summary>
    public static double CreatureInitiative(GameDatabase db, HeroState h, CreatureDef c, string week)
    {
        float init = (float)MoonWeek.Initiative(c.Initiative, c.Town, week);
        if (!h.Present) return init;
        if (h.Specialization is "HERO_SPEC_FURIOUS" or "HERO_SPEC_MASTER_OF_INITIATIVE")
        {
            float f = h.Level * (float)db.Stats.Get("combat/HeroSpecializations/Demonlord/Furious_InitiativeBonusPerLevel", 0.01)
                      + (float)db.Stats.Get("combat/HeroSpecializations/Demonlord/Furious_InitiativeBonusBase", 0);
            init = (Math.Clamp(f, 0f, 5f) + 1f) * init;
        }
        if (h.HasPerk(Sk.MarchOfGolems) && c.Id is "CREATURE_IRON_GOLEM" or "CREATURE_STEEL_GOLEM" or "CREATURE_OBSIDIAN_GOLEM")
            init += 2f;
        if (init < 1f) init = 1f;
        int N(string art) => h.Artifacts.Count(a => a == art);
        bool caster = c.Abilities.Contains("ABILITY_CASTER") || c.Abilities.Contains("ABILITY_RANDOM_CASTER");
        int p = (N("RING_OF_HASTE") != 0 ? 20 : 0) + 10 * N("ARTIFACT_RING_OF_CELERITY")
                + 5 * N(c.CombatSize > 1 ? "DRAGON_SCALE_ARMOR" : "DRAGON_SCALE_SHIELD");
        if (c.Shots <= 0 && !caster && !c.Flying) p += 10 * N("DRAGON_BONE_GRAVES");
        if (c.Flying) p += 10 * N("DRAGON_WING_MANTLE");
        if (caster) p += 10 * N("DRAGON_TEETH_NECKLACE");
        if (c.Shots > 0) p += 10 * N("DRAGON_EYE_RING");
        p -= 5 * N("ARTIFACT_OGRE_CLUB") + 5 * N("ARTIFACT_OGRE_SHIELD");
        if (p < -100) p = -100;
        return (float)((p + 100) * init * 0.01f);
    }

    /// <summary>War machine initiative (exe FUN_004bae60, stored as trunc(x + 0.5) by 004bd7b0): record initiative ×
    /// (0.1 · Dragon Talon Crowns + 1) + RunicMachines_InitiativeBonus with Runic Machines (the script battle bonus
    /// HERO_BATTLE_BONUS_INITIATIVE is not modelled).</summary>
    public static double WarMachineInitiative(GameDatabase db, HeroState h, double baseInit)
    {
        float init = (float)baseInit;
        if (!h.Present) return init;
        init = (h.Artifacts.Count(a => a == "DRAGON_TALON_CROWN") * 0.1f + 1f) * init;
        if (h.HasPerk(Sk.RunicMachines)) init += (int)db.Stats.Get("combat/HeroSkills/Runemage/RunicMachines_InitiativeBonus", 3);
        return (int)(init + 0.5f);
    }

    /// <summary>Erratic Mana (HERO_SKILL_CHAOTIC_SPELLS, exe 00bbf6a6..00bbf751): for a positive cost, an ability roll of
    /// 50 (FUN_00a5a170) and then the hero pays trunc(r · cost) with r uniform in [0.6, 0.9] (floats 0x3f19999a /
    /// 0x3f666666), at most cost − 1 and at least 0.</summary>
    public static int ErraticManaSpend(int cost, bool perk, Func<int, bool> roll, Rng rng)
    {
        if (cost <= 0 || !perk || !roll(50)) return cost;
        float r = (float)rng.RangeD(0.6f, 0.9f);
        int spent = Math.Min(cost - 1, (int)(r * cost));
        return Math.Max(0, spent);
    }

    /// <summary>Hero class → racial skill (exe FUN_00875550).</summary>
    public static string RacialSkill(string heroClass) => heroClass switch
    {
        "HERO_CLASS_KNIGHT" => "HERO_SKILL_TRAINING", "HERO_CLASS_RANGER" => "HERO_SKILL_AVENGER",
        "HERO_CLASS_WIZARD" => "HERO_SKILL_ARTIFICIER", "HERO_CLASS_DEMON_LORD" => "HERO_SKILL_GATING",
        "HERO_CLASS_NECROMANCER" => "HERO_SKILL_NECROMANCY", "HERO_CLASS_WARLOCK" => "HERO_SKILL_INVOCATION",
        "HERO_CLASS_RUNEMAGE" => "HERO_SKILL_RUNELORE", "HERO_CLASS_BARBARIAN" => "HERO_SKILL_DEMONIC_RAGE", _ => "",
    };

    /// <summary>Pendant of Mastery (artifact 15, exe GetSkillMastery FUN_00b4d150): +1 mastery of the class's racial skill
    /// (also from none), results outside 0..4 count as 0; heroes without any skill entry get nothing.</summary>
    public static void ApplyPendantOfMastery(HeroState h)
    {
        string rs = RacialSkill(h.Class);
        if (rs.Length == 0 || !h.Artifacts.Contains("PEDANT_OF_MASTERY") || h.Skills.Count == 0) return;
        int m = (int)h.SkillLevel(rs) + 1;
        h.Skills.RemoveAll(s => s.SkillId == rs);
        h.Skills.Add(new SkillEntry(rs, m is < 0 or > 4 ? Mastery.None : (Mastery)m));
    }

    /// <summary>Enemy hero artifacts in the stack's morale (exe 00989fd0 tail): −2 per Nightmarish Ring and per Cloak of
    /// Mourning of the enemy hero (army vcall 0x10).</summary>
    public static int EnemyArtifactMorale(HeroState enemy) => !enemy.Present ? 0
        : -2 * enemy.Artifacts.Count(a => a == "NIGHTMARISH_RING") - 2 * enemy.Artifacts.Count(a => a == "CLOAK_OF_MOURNING");

    /// <summary>Enemy hero artifacts in the stack's luck (exe 00989c40 tail): −2 per Cloak of Mourning and per Jinxing
    /// Band of the enemy hero.</summary>
    public static int EnemyArtifactLuck(HeroState enemy) => !enemy.Present ? 0
        : -2 * enemy.Artifacts.Count(a => a == "CLOAK_OF_MOURNING") - 2 * enemy.Artifacts.Count(a => a == "ARTIFACT_JINXING_BAND");
}
