using H5.Core.Data;

namespace H5.Combat;

/// <summary>
/// Combat effects of the Academy creature artifacts (micro-artifacts). Every reader in H5_Game.exe copies the stack's
/// artifact (army stack +0x2c) and reads its per-effect summary through FUN_00c0a2c0; the ten callers are the army
/// stat getters and two combat sites:
/// <list type="bullet">
/// <item>MAE_HEALTH: 004bc470 hit points = week base + value, before the hero bonuses and the next-battle percentage;</item>
/// <item>MAE_PIERCING: 004bb170 attack = week base + value (then the hero's attack etc.);</item>
/// <item>MAE_DEFENCE: 004bb2a0 defence = week base + value (then the hero's defence etc.);</item>
/// <item>MAE_SPEED: 004bc0b0 speed = record speed + value;</item>
/// <item>MAE_HASTE: 004bc190 initiative × (min(value, 50) · 0.01 + 1), last, with or without a hero;</item>
/// <item>MAE_LUCK / MAE_MORALE: 004ba970 / 004ba9d0, added to the week-adjusted army luck / morale;</item>
/// <item>MAE_MAGIC_PROTECTION: 0097af50 spell damage factor × (1 − value · 0.01) for a creature target;</item>
/// <item>MAE_ARMOR_CRUSHING: 00a629b0 every strike puts SPELL_EFFECT_ARMOR_CRUSHING (0xb3) with the value on the target
/// (vf 0x260, whole battle); the effect is cumulative (004e3990 lists 0xb3, 004e4a80 adds the values) and lowers
/// defence by its value unless the target is ARMORED (00989790 case 0xb3);</item>
/// <item>MAE_WOUNDING: no combat reader (only the AI army value 004e9b90 copies the summary) — and the forge refuses it
/// (FUN_00c0a700 rejects effect 1).</item>
/// </list>
/// </summary>
public static class MicroArtifacts
{
    public const string ArmorCrushingEffect = "SPELL_EFFECT_ARMOR_CRUSHING";

    public static int Bonus(Unit u, MicroEffect e) => u.MicroArtifact is { } m ? m.Bonus(e) : 0;

    /// <summary>The army-getter part (hit points, attack, defence, speed, initiative) on the unit's base values.</summary>
    internal static void ApplyToBase(Unit u, MicroArtifact? micro)
    {
        u.MicroArtifact = MicroArtifact.IsEmpty(micro) ? null : micro!.Clone();
        if (u.MicroArtifact is not { } m) return;
        u.BaseHealth += m.Bonus(MicroEffect.Health);
        u.BaseAttack += m.Bonus(MicroEffect.Piercing);
        u.BaseDefence += m.Bonus(MicroEffect.Defence);
        u.BaseSpeed += m.Bonus(MicroEffect.Speed);
        u.BaseInitiative = HasteInitiative(u.BaseInitiative, m.Bonus(MicroEffect.Haste));
    }

    /// <summary>004bc190 tail: (min(haste, 50) · 0.01f + 1.0f) · initiative (x87, stored as float).</summary>
    public static double HasteInitiative(double initiative, int haste)
    {
        int h = haste < 50 ? haste : 50;
        return (float)((h * (double)0.01f + 1.0) * (float)initiative);
    }

    /// <summary>
    /// Gives a battle stack another artifact (tactics merge FUN_0099a020): its base values are rebuilt as at setup
    /// (BattleSetup.AddCreature) with the new artifact.
    /// </summary>
    internal static void Reapply(GameDatabase db, BattleState s, Unit u, MicroArtifact? micro)
    {
        var t = BattleSetup.AddCreature(db, s, u.Side, u.CreatureId, 1, u.Pos, u.Summoned, micro);
        s.Units.Remove(t);
        s.NextId--;
        u.MicroArtifact = t.MicroArtifact;
        u.BaseHealth = t.BaseHealth; u.BaseAttack = t.BaseAttack; u.BaseDefence = t.BaseDefence;
        u.BaseSpeed = t.BaseSpeed; u.BaseInitiative = t.BaseInitiative;
        u.MaxHealthCache = t.MaxHealthCache; u.TopHealth = t.TopHealth;
        u.ArtifactEffects = u.MicroArtifact?.Effects.Count ?? -1;
    }
}
