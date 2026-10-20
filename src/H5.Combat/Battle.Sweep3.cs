using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 3 (docs/rules/audit/combat_sweep_3.md): creature abilities checked against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// The derived ABILITY_CASTER (exe FUN_00b61950 case 0x45, reached through the creature HasAbility 008a34c0 and the
    /// record helper FUN_00b61ba0): no shipped creature lists the ability; it holds when one of the creature's known spells
    /// (non-zero id) passes FUN_00a349f0 — school RUNIC (5) or WARCRIES (6), or any other school except SPECIAL (7) and
    /// NONE (8), with SPELL_ABILITY_DARK_RITUAL (0x43) excluded; an unknown spell id fails.
    /// </summary>
    internal static bool ExeCasterSpells(GameDatabase db, IEnumerable<string> spells) => spells.Any(id =>
    {
        if (string.IsNullOrEmpty(id) || id == "SPELL_NONE") return false;
        if (db.Spell(id) is not { } sp) return false;
        if (sp.School is "MAGIC_SCHOOL_RUNIC" or "MAGIC_SCHOOL_WARCRIES") return true;
        if (id == "SPELL_ABILITY_DARK_RITUAL") return false;
        return sp.School is not ("MAGIC_SCHOOL_SPECIAL" or "MAGIC_SCHOOL_NONE" or "");
    });

    internal bool ExeCaster(Unit u) => u.Has("ABILITY_CASTER") || ExeCasterSpells(Db, u.Spells.Select(k => k.Spell));

    /// <summary>
    /// The damage boosts FUN_00977a80 applies to a spell's integer damage before protection (after Cold Death / Exorcism):
    /// Mark of Fire (effect 0xf6) on a Fire spell → trunc(dmg · clamp(MarkOfFire_FireDamageMultiplier, 0, 1000)), then
    /// once, whatever the number of auras, trunc(dmg · clamp(VulnerabilityDamageCoef, 0, 1000)) when the target carries the
    /// vulnerability flag of the spell's element (008a4b70: the stack itself or an adjacent stack of either side with the
    /// aura — fire 0x40, water 0x80, air 0x100, earth 0x200). Both multiplications are FISTP under OR AH,0x0C (chop).
    /// </summary>
    internal double ExeVulnerabilityDamage(double dmg, Unit t, SpellDef sp)
    {
        int d = (int)dmg;
        if (sp.Element == Elements.Fire && t.HasEffect(H5.Combat.Fx.MarkOfFire))
            d = (int)(Math.Clamp((float)Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2), 0f, 1000f) * (double)d);
        string? aura = sp.Element switch
        {
            Elements.Fire => Ab.AuraFireVuln, Elements.Water => Ab.AuraIceVuln, Elements.Air => Ab.AuraLightningVuln,
            Elements.Earth => "ABILITY_AURA_OF_EARTH_VULNERABILITY", _ => null,
        };
        if (aura != null && (t.Has(aura) || Adjacent(t).Any(o => o.Has(aura))))
            d = (int)(Math.Clamp((float)Stats.Get("combat/Creatures/Academy/VulnerabilityDamageCoef", 1.5), 0f, 1000f) * (double)d);
        return d;
    }

    /// <summary>
    /// Mark of Fire is applied (ability strike FUN_00a5e610, Flamestrike FUN_00a60be0) through FUN_00bbf160 with the cast
    /// record's +0x38 = 0, so the application check FUN_00982010 → FUN_0097f610 case 0xf6 refuses targets with Immunity to
    /// Fire or to Magic (derived HasAbility), and FUN_0097d2a0 refuses a 100 % protection (the immunity table FUN_0097c9f0:
    /// war machines, Enchanted Obsidian, Anti-Magic …). The engine applied it to anything.
    /// </summary>
    internal bool MarkOfFireRefused(Unit t) =>
        HasAbilityExe(t, Ab.ImmuneFire) || HasAbilityExe(t, Ab.ImmuneMagic)
        || (Db.Spell("SPELL_EFFECT_MARK_OF_FIRE") is { } mof ? ExeSpellImmune(t, mof, "SPELL_EFFECT_MARK_OF_FIRE", null) : t.IsWarMachine || t.IsObject);

    /// <summary>
    /// Repair's target check (exe FUN_0097f610 case 0xad, 0097f8be..0097f93e): a friendly (FUN_004e5790) MECHANICAL stack —
    /// war machines answer HasAbility(9) too (FUN_00a75160) — the gremlins holding a charge, and something to repair: the
    /// top creature below full health (vf 0x1d4 &lt; vf 0x1a8) or, for a creature stack, fewer creatures than at the start.
    /// </summary>
    /// <summary>Spell damage as dealt to <paramref name="t"/> (tests).</summary>
    internal int DealSpellDamageFor(Unit t, SpellDef sp, double dmg, int side) => DealSpellDamage(t, sp, dmg, side, ignoreResist: false);

    internal static bool RepairHasWork(Unit t) =>
        t.TopHealth < t.MaxHealthCache || !t.IsWarMachine && t.Count < t.InitialCount;
}
