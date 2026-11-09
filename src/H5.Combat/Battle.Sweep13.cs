using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 13 (docs/rules/audit/combat_sweep_13.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Resurrect Allies' target check, in the exe's order: the application check FUN_00982010 — the side test for a
    /// TARGET_FRIEND spell (record +0xd0 = 0, FUN_00a34550 / FUN_009776a0 → COMBAT_CANT_CAST_ONLY_FOR_FRIENDLY), the live
    /// test skipped (CanSelectDead, record +0xce = 1, FUN_00a34650), an ability on its own caster refused (FUN_00a34610,
    /// 0098247f) — then FUN_0097f610: the immunity roll for the ids of FUN_00a34130 (0x9d among them: FUN_0097d2a0 at
    /// 100 % → COMBAT_CANT_CAST_SPELL_IMMUNITY), case 0x9d (Undead 10, Mechanical 9, Elemental 12 refused; a charge, else
    /// COMBAT_ABILITY_ALREADY_USED) and FUN_0097a230: a creature (vf 0x6c); a dead stack needs FUN_00977da0 — not a Phantom
    /// (vf 0x28(0x28)) and every one of its cells held by itself (FUN_00b3f330); then vf 0x1fc(1) = FUN_008a40c0: the top
    /// creature below full health or the count below the initial count (else COMBAT_CANT_RESURRECT_ONE_CREATURE).
    /// So a living damaged ally is a valid target. Not ported: FUN_00977da0's vf 0x4c ≠ 1 (a unit state not identified).
    /// </summary>
    internal string? ResurrectAlliesRefusal(Unit caster, Unit? t)
    {
        if (t == null || t.Removed || t.IsObject) return "needs a target";
        if (Controller(t) != Controller(caster)) return "needs a friendly target";
        if (t.Id == caster.Id) return "cannot resurrect itself";
        if (Db.Spell("SPELL_ABILITY_RESURRECT_ALLIES") is { } sp && ExeSpellImmune(t, sp, "SPELL_ABILITY_RESURRECT_ALLIES", null))
            return "target is immune";
        if (t.Has(Ab.Undead) || t.Has(Ab.Mechanical) || t.Has(Ab.Elemental)) return "cannot resurrect this stack";
        if (caster.AbilityUses.ContainsKey(Ab.ResurrectAllies)) return "ability already used";
        if (t.Kind != UnitKind.Creature || t.IsWarMachine) return "cannot resurrect this stack";
        if (!t.Alive)
        {
            if (t.IsClone) return "cannot resurrect this stack";
            if (!Fits(t, t.Pos)) return "the stack's place is occupied";
            return null;
        }
        return t.TopHealth < t.MaxHealthCache || t.Count < t.InitialCount ? null : "nothing to resurrect";
    }
}
