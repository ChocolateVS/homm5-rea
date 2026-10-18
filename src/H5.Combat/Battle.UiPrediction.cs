using H5.Core.Data;

namespace H5.Combat;

/// <summary>
/// What the original shows when the player hovers an attack (exe 00a63020): the damage range,
/// kills range, number of strikes and whether / why the target will retaliate.
/// <see cref="RetaliateKey"/> is a CombatStrings id from RPGRoot (COMBAT_WILL_RETALIATE,
/// COMBAT_NO_SHOOTER_RETALIATE…), the text shown after the damage in the prediction box.
/// </summary>
public sealed record AttackPrediction(int MinDamage, int MaxDamage, int MinKills, int MaxKills, int Strikes,
    bool Retaliates, string RetaliateKey);

// Read-only queries for the combat screen's hover predictions. Nothing here changes the state.
public sealed partial class Battle
{
    /// <summary>
    /// Prediction for a melee attack from <paramref name="from"/> (null = current cell) or a shot.
    /// Damage per strike uses the creature's min and max roll with no luck (exe passes a 1.0 luck
    /// factor), kills per exe 00a6bd90, retaliation reasons in the order of exe 00a5fee0.
    /// </summary>
    public AttackPrediction? PredictAttack(int attackerId, int targetId, Cell? from, bool ranged)
    {
        var a = S.Unit(attackerId);
        var t = S.Unit(targetId);
        if (a == null || t == null || !a.Alive || !t.Alive) return null;

        int walked = 0;
        if (!ranged && from is { } f && f != a.Pos && PathTo(a.Id, f) is { } path) walked = path.Count;
        var (min, max) = DamageRangeOf(a);
        int dMin = ComputeDamage(a, t, new Hit { Kind = ranged ? AttackKind.Ranged : AttackKind.Melee, CellsWalked = walked, FixedRoll = min }, ranged);
        int dMax = ComputeDamage(a, t, new Hit { Kind = ranged ? AttackKind.Ranged : AttackKind.Melee, CellsWalked = walked, FixedRoll = max }, ranged);
        if (dMax < dMin) (dMin, dMax) = (dMax, dMin);

        int strikes = ranged
            ? (a.IsWarMachine ? WarMachineShotsPerAction(a) : HasAbilityNow(a, Ab.DoubleShot) ? 2 : 1)
            : (HasAbilityNow(a, Ab.DoubleAttack) ? 2 : 1) * (a.HasEffect(H5.Combat.Fx.RuneBerserking) ? 2 : 1);

        int kMin = KillsFor(dMin, t), kMax = KillsFor(dMax, t);
        // Vorpal Sword always takes one more creature unless the target has Crystal Scales (exe 00a63020).
        if (!ranged && HasAbilityNow(a, "ABILITY_VORPAL_SWORD") && !t.Has("ABILITY_CRYSTAL_SCALES"))
        {
            kMin = Math.Min(t.Count, kMin + 1);
            kMax = Math.Min(t.Count, kMax + 1);
        }

        string key;
        bool retaliates;
        // A target that dies even on the minimum roll cannot retaliate (exe 00a63020).
        if (kMin * Math.Max(1, strikes) >= t.Count) { key = "COMBAT_NO_RETALIATE"; retaliates = false; }
        else (retaliates, key) = RetaliationReason(a, t, ranged);
        return new AttackPrediction(dMin, dMax, kMin, kMax, strikes, retaliates, key);
    }

    /// <summary>Creatures killed by <paramref name="damage"/> (exe 00a6bd90).</summary>
    public static int KillsFor(int damage, Unit t)
    {
        int hp = Math.Max(1, t.MaxHealthCache > 0 ? t.MaxHealthCache : t.BaseHealth);
        int k = damage / hp + (t.TopHealth <= damage % hp ? 1 : 0);
        return Math.Clamp(k, 0, t.Count);
    }

    /// <summary>The retaliation verdict and its CombatStrings id, in the order of exe 00a5fee0.</summary>
    (bool Will, string Key) RetaliationReason(Unit a, Unit t, bool ranged)
    {
        if (!t.Alive || t.IsWarMachine || t.IsObject) return (false, "COMBAT_NO_RETALIATE");
        if (a.HasEffect(H5.Combat.Fx.RuneBattleRage)) return (false, "COMBAT_NO_RETALIATE");
        // A hypnotized stack never retaliates (engine CanRetaliate; exe 00a5fee0 reports it with this string).
        if (t.HasEffect("SPELL_HYPNOTIZE")) return (false, "COMBAT_NO_HYPNOTIZE_RETALIATE");
        if (t.HasEffect("SPELL_ABILITY_GATING")) return (false, "COMBAT_NO_GATING_RETALIATE");
        if (ranged && !(HasAbilityNow(t, "ABILITY_RANGED_RETALIATE") && t.MaxShots > 0))
            return (false, "COMBAT_NO_SHOOTER_RETALIATE");
        if (t.GetEffect("SPELL_BLIND") is { Mastery: >= Mastery.Expert }) return (false, "COMBAT_NO_BLIND_RETALIATE");
        if (HasAbilityNow(a, Ab.NoEnemyRetaliation)) return (false, "COMBAT_NO_ENEMY_RETALIATE");
        if (a.HasEffect(H5.Combat.Fx.Invisible)) return (false, "COMBAT_NO_RETALIATE_TO_INVISIBLE");
        if (HasAbilityNow(a, Ab.FastAttack) && t.HasEffect("SPELL_SLOW")) return (false, "COMBAT_NO_RETALIATE_TO_FAST_ATTACKER");
        bool can = t.RetaliationsLeft > 0 && !t.HasEffect(H5.Combat.Fx.Sleep) && !t.HasEffect(H5.Combat.Fx.Stunned)
                   && !t.HasEffect(H5.Combat.Fx.Frozen);   // lesser Blind does not stop retaliation (only Expert, above)
        if (!can) return (false, "COMBAT_NO_RETALIATE");
        if (!ranged && t.Has("ABILITY_COWARDICE") && TotalHealthOf(a) > TotalHealthOf(t))
            return (false, "COMBAT_NO_RETALIATE_COVARDICE");
        return (true, "COMBAT_WILL_RETALIATE");
    }

    static long TotalHealthOf(Unit u) => u.TotalHealth;
}
