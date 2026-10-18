namespace H5.Combat;

// Creature ability trigger chance and roll, as H5_Game.exe computes them (docs/rules/audit/combat_abilities.md).
public sealed partial class Battle
{
    /// <summary>
    /// Trigger chance in whole percent (exe FUN_00a59990). With A / T the attacker's / target's total hit points:
    /// base = min(75, 25 + ⌊3A/T⌋) if A > T, else max(5, 25 − ⌊3T/A⌋) (integer division). The ability's ProcFactor is
    /// an exponent on the failure chance: chance = 100 − ⌊100·(0.01·(100 − base))^factor + 0.5⌋. When
    /// <paramref name="soldiersLuckDoubles"/> (the callers that pass 1: Bash, Brutality, Crushing Blow, Flamewave, Taunt,
    /// Paw Strike, Bear Roar) and the attacker's hero has Soldier's Luck, the factor is doubled.
    /// </summary>
    internal int ProcPercent(Unit attacker, Unit target, double procFactor, int? targetHpOverride = null, bool soldiersLuckDoubles = false)
    {
        long a = Math.Max(1, attacker.TotalHealth);
        long t = Math.Max(1, targetHpOverride ?? target.TotalHealth);
        long b = a > t ? Math.Min(75, 25 + 3 * a / t) : Math.Max(5, 25 - 3 * t / a);
        double f = procFactor;
        if (soldiersLuckDoubles && HeroOf(attacker).HasPerk(Sk.SoldiersLuck)) f += f;
        double fail = Math.Pow((100 - b) * (double)0.01f, f);
        return 100 - (int)Math.Floor(fail * 100.0 + 0.5);
    }

    /// <summary>
    /// The ability roll (exe FUN_00a5a170): chance ≥ 100 always succeeds, ≤ 0 never; otherwise succeeds when
    /// Range(0, 100) (inclusive, 101 outcomes) is below the chance. Callers that name the attacker get Soldier's Luck's
    /// second roll on failure (<paramref name="secondRoll"/>); Bash, Crushing Blow, Flamewave, Taunt and Piercing
    /// Arrow pass none.
    /// </summary>
    bool RollPercent(Unit attacker, int chance, bool secondRoll)
    {
        if (chance >= 100) return true;
        if (chance <= 0) return false;
        if (ObstacleGenerator.RandInclusive(S.Rng, 0, 100) < chance) return true;
        return secondRoll && HeroOf(attacker).HasPerk(Sk.SoldiersLuck) && ObstacleGenerator.RandInclusive(S.Rng, 0, 100) < chance;
    }

    /// <summary>
    /// Paw Strike chance for <paramref name="walked"/> tiles (exe FUN_00a5d1b0): p = ProcPercent(PawStrike_ProcFactor, Soldier's
    /// Luck doubles); in single precision fail = Π (100 − p)·0.01 over the tiles; result = round-to-nearest((1 − fail)·100 + 0.5)
    /// (FISTP in the default rounding mode, not truncation). No tiles, no chance.
    /// </summary>
    internal int PawStrikePercent(Unit attacker, Unit target, int walked, int? targetHpOverride = null)
    {
        if (walked <= 0) return 0;
        int p = ProcPercent(attacker, target, Stats.Get("combat/Creatures/Fortress/PawStrike_ProcFactor", 1), targetHpOverride, soldiersLuckDoubles: true);
        float fail = 1f, q = 100f - p;
        for (int i = 0; i < walked; i++) fail = q * fail * 0.01f;
        return (int)Math.Round((double)((1f - fail) * 100f + 0.5f), MidpointRounding.ToEven);
    }

    /// <summary>
    /// Bear Roar chance against one victim (exe FUN_00a5e800): p = ProcPercent(BearRoar_ProcFactor, Soldier's Luck doubles);
    /// a 1×1 victim divides its failure by BearRoar_SmallCreaturesChanceMult: p = trunc(100 − (100 − p)/mult) in single
    /// precision under the truncating FPU mode.
    /// </summary>
    internal int BearRoarPercent(Unit attacker, Unit victim)
    {
        int p = ProcPercent(attacker, victim, Stats.Get("combat/Creatures/Fortress/BearRoar_ProcFactor", 0.9), soldiersLuckDoubles: true);
        if (victim.IsLarge) return p;
        return (int)(100f - (100 - p) / (float)Stats.Get("combat/Creatures/Fortress/BearRoar_SmallCreaturesChanceMult", 2));
    }

    /// <summary>
    /// Deadly Strike's kills for one target hit (exe FUN_00a5bf60): 0 unless the attacker has the ability, the target
    /// lacks Crystal Scales, the blow alone would kill fewer than half the stack (incorporeal targets count as fully
    /// killed, FUN_00a6bd90) and Range(0,100) &lt; 25 (attacker's second roll); then count / 2.
    /// </summary>
    int DeadlyStrikeKills(Unit attacker, Unit target, int damage)
    {
        if (!HasAbilityNow(attacker, Ab.DeadlyStrike) || target.Has("ABILITY_CRYSTAL_SCALES") || target.IsWarMachine || target.IsObject) return 0;
        int half = target.Count / 2;
        int wouldKill = target.Has(Ab.Incorporeal) ? target.Count : KillsFor(damage, target);
        if (wouldKill >= half) return 0;
        return RollPercent(attacker, 25, secondRoll: true) ? half : 0;
    }

    /// <summary>The effect a gated stack carries for the rest of the battle (exe spell id 0x9f).</summary>
    internal const string GatedEffect = "SPELL_ABILITY_GATING";

    /// <summary>
    /// A gated stack ignores every initiative modifier (exe FUN_008a4330 returns early on effect 0x9f): base initiative ×
    /// max(0, Gating/InitiativeFactor).
    /// </summary>
    double GatedInitiative(Unit u) =>
        Math.Max(0.1, u.BaseInitiative * Math.Max(0, Stats.Get("combat/HeroSkills/DemonLord/Gating/InitiativeFactor", 1)));

    /// <summary>Chance + roll in one call.</summary>
    bool ProcRoll(Unit attacker, Unit target, double procFactor, int? targetHpOverride = null, bool soldiersLuckDoubles = false, bool secondRoll = true) =>
        RollPercent(attacker, ProcPercent(attacker, target, procFactor, targetHpOverride, soldiersLuckDoubles), secondRoll);
}
