namespace H5.Combat;

// Combat audit sweep 9 (docs/rules/audit/combat_sweep_9.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Berserker Rage cast (exe FUN_00984a00 @ 00988051 → FUN_00bbc1d0), after effect 0xf5 was added with count 2:
    /// vf 0x200 (FUN_008aa960: the turn-start reset, retaliation available again); FUN_00bbc0e0 — the berserk attack on
    /// a stack of the enemy army (vf 0x10), else of the own army (vf 0xc), through FUN_00bbbc00 (melee FUN_00c542d0, or
    /// a shot when the stack can shoot); when none is possible a Defend event (vtable 00e17db8, vf 0x1c = FUN_004e0280 →
    /// unit vf 0x204 = the Defend handler FUN_008a6be0); finally vf 0x268(0xf5) drops the count to 1. The attack runs
    /// while the count is 2, so it is never retaliated (FUN_00a5fee0 @ 00a6005c); the effect stays (defence 0, attack +
    /// defence) until the stack's next turn start (FUN_00bbc8e0 @ 00bbc951, vf 0x270 force).
    /// </summary>
    internal void BerserkerRageCast(Unit u)
    {
        u.RetaliationsLeft = HasAbilityNow(u, Ab.UnlimitedRetaliation) ? int.MaxValue : 1;
        if (!AutoBerserkAttack(u, anySide: false, approach: false) && u.Alive) DefendEffects(u);
        if (u.GetEffect(H5.Combat.Fx.BerserkerRage) is { } e) e.Value2 = 1;
    }

    /// <summary>exe FUN_00a5fee0 @ 00a6005c: no retaliation against an attacker whose effect 0xf5 has count (+0x20) ≥ 2.</summary>
    internal static bool BerserkerRageBlocksRetaliation(Unit attacker) =>
        attacker.GetEffect(H5.Combat.Fx.BerserkerRage) is { Value2: >= 2 };
}
