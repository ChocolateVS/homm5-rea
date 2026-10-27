using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 11 (docs/rules/audit/combat_sweep_11.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// The creature part of the spell power (exe FUN_008a3960, general path) after FUN_004bad20: × the
    /// Magis_2_Casters_SpellpowerMultiplier (RPGStats +0x117c, an int) when the army's hero wears 2+ Magis pieces
    /// (FUN_004d8d60(army, 4)), then × the demon-rage DEMON_RAGE_BONUS_SPELLPOWER_MULT factor (FUN_008d9460 with type 8:
    /// the stack's rage level from effects 0x12e / 0x12f / 0x130 (FUN_008d9270), searched from that level down to 1 in
    /// the creature's DemonRage table (RPGStats +0x4b4, FUN_008d92b0), the first type-8 entry's FloatParam, else 1.0),
    /// truncated (FISTP under chop at 008a3b5f).
    /// </summary>
    internal int CreatureCasterPower(Unit u, int count)
    {
        int p = CreatureSpellPower(count, Math.Max(1, u.WeeklyGrowth));
        if (Specials.Pieces(HeroOf(u), Specials.Magis) >= 2) p *= (int)Stats.Get("ArtifactsSetsEffectsConsts/Magis_2_Casters_SpellpowerMultiplier", 2);
        return (int)(RageSpellPowerMult(u) * p);
    }

    /// <summary>FUN_008d9460(stack, 8): the FloatParam of the first DEMON_RAGE_BONUS_SPELLPOWER_MULT entry from the
    /// stack's rage level down to level 1; 1.0 without rage or without such an entry.</summary>
    internal double RageSpellPowerMult(Unit u)
    {
        if (u.RageLevel <= 0 || RageTable(u.CreatureId) is not { } t) return 1.0;
        for (int lvl = Math.Min(3, u.RageLevel); lvl >= 1; lvl--)
            foreach (var b in t[lvl - 1])
                if (b.Type == "DEMON_RAGE_BONUS_SPELLPOWER_MULT") return (float)b.FloatParam;
        return 1.0;
    }

    /// <summary>
    /// Exploding Corpses (exe FUN_0097b690 with HasSkill(0x5d) → FUN_0085eaa0): the consumed corpse explodes as a
    /// Fireball — the cast record's id 0x11f is replaced by 5 and the ordinary area resolver FUN_0085e7c0 runs (the same
    /// path as a hero Fireball cast from FUN_0097d7b0): FUN_0097a530's Fireball targets around the corpse's cell, the
    /// hero's mastery for that record (vf 0x240), the base damage FUN_00976b90, then per target FUN_0097d600 + Elemental
    /// Chains and Magic Magnetism (FUN_0085e330). The power is the corpse's own spell power (vf 0x244 for record 0x11f at
    /// 0097b72a) + 1 when its creature has a BaseCreature (record +0x100, FUN_004babd0 at 0097b76b). A dead stack's last
    /// count record is 0 (the damage handler FUN_008a6e20 stores count 0 once its health is gone), so FUN_004bad20 gives
    /// max(1, trunc(21·log10(10) − 22 + 0.01)) = 1.
    /// </summary>
    internal int CorpseExplosionPower(Unit corpse)
    {
        int p = CreatureCasterPower(corpse, 0);
        if (Db.Creatures.TryGetValue(corpse.CreatureId, out var d) && d.BaseCreature is { Length: > 0 } b && b != "CREATURE_UNKNOWN") p += 1;
        return p;
    }

    internal void ExplodeCorpse(HeroState h, int side, Unit corpse)
    {
        if (Db.Spell("SPELL_FIREBALL") is not { } fb) return;
        ResolveSpellCore(Actor.Hero(side), side, fb, HeroMastery(h, fb), CorpseExplosionPower(corpse), null, corpse.Pos, fromHero: true);
    }
}
