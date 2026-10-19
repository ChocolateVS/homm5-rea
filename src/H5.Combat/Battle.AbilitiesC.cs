namespace H5.Combat;

// Activated creature abilities checked against the cast switch of H5_Game.exe (FUN_00984a00, jump table 009886d4,
// index = spell id − 0x9b). Combat audit "abilities C": docs/rules/audit/combat_abilities_c.md.
public sealed partial class Battle
{
    /// <summary>
    /// Harm Touch dispel (exe 0098747f → FUN_004e4120): every effect beneficial to its bearer whose duration is not one
    /// of the permanent markers (FUN_004e3960), except the FUN_004e3ac0 set (Holy Charge, Demonic Strike, Dash, poison,
    /// wound, venom, goblin Sacrifice, Powerful Blow, Prepared Position, Battle Rage, Unlucky). Unlike a dispel spell it
    /// ignores the "cannot be dispelled" flag and the school.
    /// </summary>
    internal bool HarmTouchRemoves(Effect e) =>
        e.Positive && !(double.IsPositiveInfinity(e.Remaining) && e.Actions < 0)
        && !NeverDispelled.Contains(e.Id) && e.Id is not (FxVenom or "SPELL_EFFECT_UNLUCKY" or FxDiving);

    /// <summary>
    /// Ammo Steal (exe 009865a0): with N poltergeists and weekly growth G (creature record +0xa8), q = N / G shots plus
    /// one more when Range(0, 100) &lt; (N mod G)·100 / G; at most the target's shots. May be 0 (the charge is still spent).
    /// </summary>
    internal int AmmoStealAmount(Unit u, Unit t)
    {
        int g = Math.Max(1, u.WeeklyGrowth);
        int n = u.Count / g;
        if (S.Rng.Range(0, 100) < u.Count % g * 100 / g) n++;
        return Math.Min(n, t.Shots);
    }

    /// <summary>
    /// Scavenger heal (exe 0098624f, 009862b9): trunc((creature health vf 0x1a8 × corpse count) × ScavengerCoef) in
    /// single precision (FISTP under OR AH,0x0C), resurrecting.
    /// </summary>
    internal int ScavengerHeal(Unit corpse)
    {
        int hp = corpse.MaxHealthCache > 0 ? corpse.MaxHealthCache : corpse.BaseHealth;
        float v = (float)(hp * Math.Max(1, corpse.InitialCount)) * (float)Stats.Get("combat/Creatures/Stronghold/ScavengerCoef", 0.2);
        return (int)v;
    }

    /// <summary>
    /// Seduce duration in rounds (exe FUN_009774a0): ratio = N·Power / (M·Power) with each creature Power (record +0x84)
    /// at least 1; d = clamp(log2(ratio) + 1.5, 0, 4) in single precision, truncated to 0.001 (FISTP under OR AH,0x0C at
    /// 00977557). The seduction lands only
    /// when d &gt; 0 (exe 00985b4e), the charge being spent first.
    /// </summary>
    internal static double SeduceDuration(int casterCount, int casterPower, int targetCount, int targetPower)
    {
        float ratio = (float)(casterCount * Math.Max(1, casterPower)) / (targetCount * Math.Max(1, targetPower));
        float d = Math.Clamp((float)Math.Log2(ratio) + 1.5f, 0f, 4f);
        return (int)(d * 1000f) * 0.001;
    }
}
