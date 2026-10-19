using H5.Core.Data;

namespace H5.Combat;

// Read-only query for the combat screen: why a spell can't be cast on a hovered stack. The original shows the
// reason in the prediction box with the can't-cast cursor (exe FUN_00982010 writes a CombatStrings id from RPGRoot
// into the box and returns false). Nothing here changes the state.
public sealed partial class Battle
{
    /// <summary>
    /// The CombatStrings id the original shows for a refused spell target, or null when the target is valid:
    /// COMBAT_CANT_CAST_ONLY_FOR_HOSTILE / _ONLY_FOR_FRIENDLY (00e61608 / 00e6162c), _SPELL_ON_WAR_MACHINE,
    /// _SPELL_IMMUNITY (00e61540: full protection, FUN_0097d2a0 == 100, or an explicit immunity),
    /// _SPELL_ONLY_LIVE (00e615e4) / _ONLY_UNDEAD, COMBAT_CANT_PHANTOM_*; anything else COMBAT_CANT_CAST_SPELL.
    /// </summary>
    public string? SpellTargetRefusalKey(int caster, int side, string spellId, Mastery m, int targetId)
    {
        var sp = Db.Spell(spellId);
        var t = S.Unit(targetId);
        if (sp == null || t == null) return "COMBAT_CANT_CAST_SPELL";
        var tg = TargetingOf(sp);
        string? reason = ValidateSpellTarget(caster, side, sp, m, targetId, tg == SpellTargeting.UnitAndCell ? t.Pos : null);
        string id = BaseSpell(sp.Id);
        switch (reason)
        {
            case null: break;
            case "spell must target an enemy": return "COMBAT_CANT_CAST_ONLY_FOR_HOSTILE";
            case "spell must target a friendly stack": return "COMBAT_CANT_CAST_ONLY_FOR_FRIENDLY";
            case "war machines can only be hit by damage spells": return "COMBAT_CANT_CAST_SPELL_ON_WAR_MACHINE";
            case "cannot resurrect this creature": return "COMBAT_CANT_CAST_SPELL_ONLY_LIVE";
            case "cannot raise this creature": return "COMBAT_CANT_CAST_SPELL_IMMUNITY";
            case "cannot clone this stack":
                if (t.Tier > (int)sp.DamageAt(m).Base) return "COMBAT_CANT_PHANTOM_TOP_TIER_CREATURE";
                return "COMBAT_CANT_PHANTOM_ALREADY_HAS_ONE";
            default: return "COMBAT_CANT_CAST_SPELL";
        }
        if (Controller(t) != side || IsDamageSpell(id))
        {
            bool physical = id is "SPELL_MAGIC_FIST";
            var hero = Actor.IsHero(caster) ? S.Heroes[side] : null;
            bool invocation = hero != null && hero.SkillLevel(Sk.Invocation) > 0;
            if (ExeSpellImmune(t, sp, id, hero) && !(ExeDamageSpell(id, hero) && invocation))
                return LivingOnly.Contains(id) && !IsLiving(t) ? "COMBAT_CANT_CAST_SPELL_ONLY_LIVE" : "COMBAT_CANT_CAST_SPELL_IMMUNITY";
            if (IsDamageSpell(id) && !physical && !invocation && FullyProtectedFrom(t, sp, side)) return "COMBAT_CANT_CAST_SPELL_IMMUNITY";
        }
        return null;
    }
}
