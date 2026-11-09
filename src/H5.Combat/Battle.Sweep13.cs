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
    /// So a living damaged ally is a valid target. FUN_00977da0's vf 0x4c ≠ 1 is the placement state (sweep 14): state 1
    /// on a dead stack comes from the corpse consumers, which mark it <c>Removed</c> here.
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

    /// <summary>
    /// A creature caster's mastery (unit vf 0x240 = FUN_00a6a700): the primary vf 0x20 = FUN_008a22b0 → FUN_004bba60 —
    /// the mastery of the spell's entry in the creature record's spell list (+0x68, 12-byte entries), 2 (Advanced) when
    /// the spell is not in it — then the week (FUN_00bcde10) and, for a school spell (FUN_00a349f0), the enemy hero's
    /// Shatter (FUN_004e6190 with the opposing army's hero, vf 0x10 → vf 0xc), clamped 0..3 as for a hero.
    /// </summary>
    internal Mastery CreatureCastMastery(Unit u, SpellDef sp)
    {
        var m = MoonWeek.SpellMastery(u.Spells.Find(s => s.Spell == sp.Id)?.Mastery ?? Mastery.Advanced, sp.School, S.Week);
        int below = ShatterMasteryAfterDrop(S.Heroes[u.Side], sp, m);
        return below == int.MinValue ? m : (Mastery)Math.Clamp(below, 0, 3);
    }

    /// <summary>
    /// A creature caster's spell power (unit vf 0x244 = FUN_00a6a7e0): the primary vf 0x24 = FUN_008a3960
    /// (CreatureCasterPower: Magis, demon rage), then enemy Shatter / Weaken (FUN_004e6190) with the mastery of
    /// CreatureCastMastery before the Shatter drop.
    /// </summary>
    internal double CreatureCastPower(Unit u, SpellDef sp)
    {
        var m = MoonWeek.SpellMastery(u.Spells.Find(s => s.Spell == sp.Id)?.Mastery ?? Mastery.Advanced, sp.School, S.Week);
        return ShatterWeakenPowerAt(S.Heroes[u.Side], sp, CreatureCasterPower(u, u.Count), m);
    }

    /// <summary>
    /// The hive's mastery (its vf 0x240 is FUN_00a6a700 as well, thunk 008b1e20): primary vf 0x20 = FUN_008b0b50 —
    /// +0xe0, SummonHive/Spell/Mastery (RPGStats +0xa0c) for its spell entry — then the week and the enemy hero's Shatter.
    /// </summary>
    internal Mastery HiveCastMastery(Unit hive, SpellDef sp)
    {
        var m = MoonWeek.SpellMastery(HiveListMastery(), sp.School, S.Week);
        int below = ShatterMasteryAfterDrop(S.Heroes[hive.Side], sp, m);
        return below == int.MinValue ? m : (Mastery)Math.Clamp(below, 0, 3);
    }

    /// <summary>
    /// The hive's spell power (vf 0x244 = FUN_00a6a7e0, thunk 008b1e30): primary vf 0x24 = FUN_008b07d0 — +0xc0, the
    /// summoning caster's vf 0x244 for Summon Hive stored by the constructor FUN_008b1090 — then Shatter / Weaken.
    /// </summary>
    internal double HiveCastPower(Unit hive, SpellDef sp) =>
        ShatterWeakenPowerAt(S.Heroes[hive.Side], sp, hive.Power, MoonWeek.SpellMastery(HiveListMastery(), sp.School, S.Week));

    Mastery HiveListMastery() =>
        MasteryParse.Parse((string?)Stats.Node("combat/Spells/SummonHive/Spell")?.Element("Mastery") ?? "MASTERY_EXPERT");

    /// <summary>
    /// The rune records' per-turn flag (list +0x28, bit 1): the rune-cast event's execute FUN_004e0c20 builds its cast
    /// object with flag 1 (FUN_004e1de0), and FUN_0041a1a0, after the cast, ORs bit 1 into the stack's records
    /// (FUN_00989120, the only writer); the stack's turn start FUN_008a6c40 → FUN_00a6e400 clears it (FUN_00989130(1));
    /// the turn-start rune of FUN_008a6c40 itself (Mountain King) passes flag 0. While it is set FUN_00989190 refuses a
    /// rune (FUN_00a345b0) whose record the stack holds (COMBAT_RUNIC_SPELL_ALREADY_USED_THIS_TURN) — a Fortress stack
    /// holds one for every rune of its hero's book (FUN_0041a250), so it gets one rune per turn of its own.
    /// Bits 2 / 4 (abilities, school spells) have no writer.
    /// </summary>
    internal const string RuneTurnKey = "RUNE_THIS_TURN";

    internal bool RuneUsedThisTurn(int? target) =>
        target is int t && S.Unit(t) is { } u && u.Town == Towns.Fortress && u.AbilityUses.ContainsKey(RuneTurnKey);

    internal void MarkRuneTurn(int? target)
    {
        if (target is int t && S.Unit(t) is { } u) u.AbilityUses[RuneTurnKey] = 1;
    }
}
