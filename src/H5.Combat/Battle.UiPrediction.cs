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
        var kind = ranged ? AttackKind.Ranged : AttackKind.Melee;

        int strikes = ranged
            ? (a.IsWarMachine ? WarMachineShotsPerAction(a) : HasAbilityNow(a, Ab.DoubleShot) ? 2 : 1)
            : (HasAbilityNow(a, Ab.DoubleAttack) ? 2 : 1) + (a.HasEffect(H5.Combat.Fx.RuneBerserking) ? 1 : 0);   // FUN_008a0f90: Berserking +1

        // The exe sums the prediction over every target the attack pattern hits (00a63020 loops the target list
        // from FUN_00a62030), each with its share, the hero's per-target elemental extras (Cold Steel FUN_00a5c000,
        // Fiery Wrath FUN_00a5ed90, both called with the prediction flag) and Vorpal's +1 kill on the primary.
        int dMin = 0, dMax = 0, kMin = 0, kMax = 0, primaryKMin = 0;
        foreach (var (target, factor) in PredictionTargets(a, t, from, kind, walked, ranged))
        {
            bool primary = target.Id == t.Id;
            var hMin = new Hit { Kind = kind, CellsWalked = walked, FixedRoll = min, Secondary = !primary };
            var hMax = new Hit { Kind = kind, CellsWalked = walked, FixedRoll = max, Secondary = !primary };
            int lo = ComputeDamage(a, target, hMin, ranged), hi = ComputeDamage(a, target, hMax, ranged);
            if (hi < lo) (lo, hi) = (hi, lo);
            // ComputeDamage folds Vorpal Sword in as one extra creature's HP; the prediction shows it as +1 kill instead.
            bool vorpal = primary && !ranged && HasAbilityNow(a, Ab.Vorpal) && !target.IsWarMachine && !target.IsObject
                          && !target.Has("ABILITY_CRYSTAL_SCALES");
            if (vorpal) { lo = Math.Max(1, lo - MaxHealthOf(target)); hi = Math.Max(1, hi - MaxHealthOf(target)); }
            if (factor != 1) { lo = (int)(lo * (float)factor); hi = (int)(hi * (float)factor); }   // share truncated (exe 00a67bf3)
            lo += PredictedElementalExtra(a, target, lo);
            hi += PredictedElementalExtra(a, target, hi);
            int klo = KillsFor(lo, target), khi = KillsFor(hi, target);
            if (vorpal) { klo = Math.Min(target.Count, klo + 1); khi = Math.Min(target.Count, khi + 1); }
            dMin += lo; dMax += hi; kMin += klo; kMax += khi;
            if (primary) primaryKMin = klo;
        }

        string key;
        bool retaliates;
        // A target that dies even on the minimum roll cannot retaliate (exe 00a63020).
        if (primaryKMin * Math.Max(1, strikes) >= t.Count) { key = "COMBAT_NO_RETALIATE"; retaliates = false; }
        else (retaliates, key) = RetaliationReason(a, t, ranged);
        return new AttackPrediction(dMin, dMax, kMin, kMax, strikes, retaliates, key);
    }

    /// <summary>
    /// The units an attack would hit (primary first), computed without changing the battle: the attacker is
    /// placed at the strike cell only for the pattern query and the battle RNG is restored afterwards (Prismatic
    /// Breath's radius draws from it), so hovering never consumes random numbers.
    /// </summary>
    public IReadOnlyList<(Unit Unit, double Factor)> PredictionTargets(Unit a, Unit t, Cell? from, AttackKind kind, int walked, bool ranged)
    {
        var list = new List<(Unit, double)> { (t, 1) };
        var savedPos = a.Pos;
        var rng = S.Rng;
        ulong s0 = rng.S0, s1 = rng.S1, s2 = rng.S2, s3 = rng.S3;
        try
        {
            if (!ranged && from is { } f) a.Pos = f;
            list.AddRange(PatternTargets(a, t, new Hit { Kind = kind, CellsWalked = walked }, ranged, false));
        }
        finally
        {
            a.Pos = savedPos;
            rng.S0 = s0; rng.S1 = s1; rng.S2 = s2; rng.S3 = s3;
        }
        return list;
    }

    /// <summary>
    /// Elemental extra damage the hero's perks add to one struck target, as the engine deals it in
    /// AfterHitOnTarget: Fiery Wrath / Cold Steel = 10% of the blow (exe FUN_00a5ed90 / FUN_00a5c000, which clamp the
    /// blow to the target's remaining HP), none against immune targets, doubled fire on Mark of Fire.
    /// Demonic Fire's random proc is not part of the prediction.
    /// </summary>
    int PredictedElementalExtra(Unit a, Unit target, int blow)
    {
        if (blow <= 0 || a.IsWarMachine || target.IsObject) return 0;
        var ah = HeroOf(a);
        int basis = Math.Min(blow, target.TotalHealth), extra = 0;
        if ((ah.HasPerk(Sk.FieryWrath) || ah.HasPerk(Sk.BarbFieryWrath)) && !ElementImmune(target, Elements.Fire))
        {
            int fire = SpellLikeAmount(basis * (double)0.1f);
            if (target.HasEffect(H5.Combat.Fx.MarkOfFire)) fire = (int)(fire * Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2));
            extra += fire;
        }
        if (ah.HasPerk(Sk.ColdSteel) && !ElementImmune(target, Elements.Water))
            extra += SpellLikeAmount(basis * (double)0.1f);
        return extra;
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
        if (HypnotizeBlocksRetaliation(t, a)) return (false, "COMBAT_NO_HYPNOTIZE_RETALIATE");
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
