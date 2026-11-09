using H5.Core.Data;
namespace H5.Combat;

// Combat audit sweep 17 (docs/rules/audit/combat_sweep_17.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// FUN_00a34740(spell, hero, week) — the cost the combat cost 009781e0 starts with (ECX = the entry's spell, EDX = the
    /// caster's vf 0x78 hero interface, null for a creature), the hive's 008b07f0 / 008b08a0 and the adventure cast
    /// (<c>Adventure.AdventureSpellCost(hero, spell)</c>, the same chain). The record is the base of a mass (00a33e20) or
    /// empowered (00a33f70) version, else the spell (00a3474a..00a34768; no record → 0); cost = its +0x90 (TrainedCost);
    /// SPELL_ABILITY_AVATAR_OF_DEATH (0xc8) with a hero: hero level · RPGStats +0x888 (AvatarOfDeath_ManaCostPerLevel,
    /// 00a34785..00a3479b); × 2 for a mass / empowered version (00a347b1); the week (00bcddf0). Warcries (the spell's own
    /// record +0x88 = 6): with a hero, Voice Training (vf 0x174(0xbc) &gt; 0) chop(min((1.001f − d) · c, c − 1)), War Leader
    /// (vf 0x190(0x4b)) − (level / div · per + base); a result ≤ 0 gives 0 (00a34886 SETLE). Other spells with a hero:
    /// Teleport + HERO_SPEC_BEATER / 2, a worn RING_OF_MAGI / 2, Power Bargain on an empowered spell, Arcane Training;
    /// a result &lt; 0 gives 0 (00a349d4 SETL).
    /// </summary>
    internal int ExeSpellBaseCost(HeroState? h, SpellDef sp)
    {
        string baseId = BaseSpell(sp.Id);
        var rec = baseId != sp.Id ? Db.Spell(baseId) : sp;
        if (rec == null) return 0;                                                    // 008496a0 null → 0
        int c = rec.ManaCost;
        if (h != null && sp.Id == "SPELL_ABILITY_AVATAR_OF_DEATH")
            c = h.Level * (int)Stats.Get("combat/HeroSpecializations/Necromancer/AvatarOfDeath_ManaCostPerLevel", 2);
        if (baseId != sp.Id) c += c;                                                  // 0x00a347b1 ADD ESI,ESI
        c = MoonWeek.SpellCost(c, S.Week);                                            // 00a347b9 → 00bcddf0
        if (sp.School == "MAGIC_SCHOOL_WARCRIES")
        {
            if (h != null)
            {
                if (h.HasPerk(Sk.ShoutTraining))
                    c = ExeChop(Math.Min((double)(float)((1.001f - (float)Stats.Get("combat/HeroSkills/Barbarian/VoiceTraining_WarcryCostDiscount", 0.2)) * c), c - 1.0));
                if (h.Specialization == "HERO_SPEC_WAR_LEADER")
                {
                    const string w = "combat/HeroSpecializations/Barbarian/WarLeader_WarcryCostReducePerHeroLevel/";
                    int div = (int)Stats.Get(w + "LevelDivisor", 1);
                    c -= (div == 0 ? 0 : h.Level / div) * (int)Stats.Get(w + "PerDividedLevel", 0) + (int)Stats.Get(w + "Base", 0);
                }
            }
            return c <= 0 ? 0 : c;                                                    // 00a34886 SETLE: ≤ 0 → 0
        }
        if (h != null)
        {
            if (sp.Id == "SPELL_TELEPORT" && h.Specialization == "HERO_SPEC_BEATER") c /= 2;
            if (h.Artifacts.Contains("RING_OF_MAGI")) c /= 2;
            if (h.Specialization == "HERO_SPEC_POWER_BARGAIN" && sp.Id.StartsWith("SPELL_EMPOWERED_", StringComparison.Ordinal))
            {
                float r = (float)Stats.Get("combat/HeroSpecializations/Warlock/PowerBargain_CostReduce", 0);
                float per = (float)Stats.Get("combat/HeroSpecializations/Warlock/PowerBargain_CostReducePerLevel", 0);
                float f = (float)(1.0f - (double)r - h.Level * (double)per);
                c = ExeChop(Math.Clamp((double)f, 0.5, 1.0) * c);
            }
            if (h.HasPerk(Sk.ArcaneTraining)) c = ExeChop(Math.Min((double)(float)(c * (double)0.8f), (double)(float)(c - 1.0)));
        }
        return Math.Max(0, c);                                                        // 00a349d4 SETL: < 0 → 0
    }
}
