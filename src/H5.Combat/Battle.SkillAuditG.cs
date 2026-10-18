using H5.Core.Data;

namespace H5.Combat;

// Magic-side hero skill rules confirmed against H5_Game.exe by the skills audit, part G
// (docs/rules/audit/combat_heroes_skills_g.md).
public sealed partial class Battle
{
    /// <summary>
    /// Protected amount of a perk's extra elemental damage (exe FUN_0097d740): the perk calls the spell protection
    /// routine FUN_0097d2a0 for a stand-in spell with no caster (EDX = 0) — Ice Bolt (ECX = 4) for Chilling Steel
    /// (00a5c000), Fireball (ECX = 5) for Fiery Wrath and Wildfire (00a5ed90), SPELL_SKILL_CHILLING_BONES (ECX = 0xcf)
    /// for Chilling Bones (00a5bc50). An immune target or a won resistance roll (rolled unless the call only predicts)
    /// takes nothing; otherwise the target side's protections for that spell (FUN_0097af50) give a whole percent p and
    /// the damage is trunc((1 − p/100)·amount + 0.5). No Irresistible Magic (there is no caster) and no Mark of Fire
    /// (that multiplier lives in the spell damage routine FUN_00977a80, which this path does not call).
    /// </summary>
    internal int PerkElementalAmount(double amount, Unit t, string pseudoSpellId, bool simulate)
    {
        var sp = Db.Spell(pseudoSpellId);
        string element = sp?.Element ?? Elements.None;
        bool full = t.Has(Ab.ImmuneMagic) || (element != Elements.None && ElementImmune(t, element));
        if (!full && !simulate) full = RollResistance(t, MagicResistanceChance(t));
        int pct = 100;
        if (!full)
        {
            float mult = sp == null ? 1f
                : (float)(SpellDamageMultiplier(t, sp, -1, casterIsHero: false, protectionOnly: true) * ElementProtectionArtifacts(t, element));
            pct = Math.Clamp((int)((1f - Math.Clamp(mult, 0f, 1f)) * 100f), 0, 100);
        }
        return (int)((1 - pct * (double)0.01f) * (double)(float)amount + 0.5);
    }

    /// <summary>Deals a perk's extra elemental damage through <see cref="PerkElementalAmount"/>.</summary>
    void DealPerkElementalDamage(Unit t, double amount, string pseudoSpellId, int source, string by)
    {
        if (!t.Alive) return;
        int dmg = PerkElementalAmount(amount, t, pseudoSpellId, simulate: false);
        if (dmg > 0) DealDamage(t, dmg, source, AttackKind.Ability, by);
    }
}
