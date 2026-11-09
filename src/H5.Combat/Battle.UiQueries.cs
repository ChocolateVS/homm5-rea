using H5.Core.Data;

namespace H5.Combat;

/// <summary>
/// What a warcry, damage-over-time spell or timed ability shows in the hover prediction box (exe 00982010 →
/// 009be2a0 / 0097f610). <see cref="Kind"/>: "IMMUNE" (COMBAT_CANT_CAST_SPELL_IMMUNITY), "CALL_OF_BLOOD" (Value = rage
/// points), "FEAR_MY_ROAR" (Value = chance %), "DIRECT_DAMAGE" (Damage / Kills), "DOT" (Damage / Kills / Turns),
/// "DURATION" (Turns).
/// </summary>
public sealed record SpecialPrediction(string Kind, int Value = 0, int Damage = 0, int Kills = 0, double Turns = 0);

// Read-only queries for the combat screen (predictions and log amounts). Nothing here changes the state: anything that
// has to run engine code with side effects runs it on a copy of the battle.
public sealed partial class Battle
{
    /// <summary>
    /// Warcry predictions (exe 009be2a0, called from 00982010 for warcries, FUN_00a345e0): no caster → nothing; no valid
    /// target (009be120) → COMBAT_CANT_CAST_SPELL_IMMUNITY; by id − 0x122 (table 009be6a8): Call of Blood (0x123) rage
    /// points 009bdbd0(caster, 0); Fear My Roar (0x125, creature targets only) chance 009bddb0; Shout of Many (0x127)
    /// DIRECT_DAMAGE_SPELL with trunc(009bde00 · ShoutOfMany_DamagePerCreatureInArmy) and kills 00a6bd90(target, d, 1).
    /// Rallying Cry (0x122), Battlecry (0x126) and Word of the Chief (0x124 → 008cfbc0) are not handled here (null).
    /// </summary>
    public SpecialPrediction? PredictWarcry(int side, string spellId, int targetId)
    {
        if (side is < 0 or > 1 || S.Heroes[side] is not { Present: true } hero || S.Unit(targetId) is not { Alive: true } t) return null;
        if (spellId is not ("SPELL_WARCRY_CALL_OF_BLOOD" or "SPELL_WARCRY_FEAR_MY_ROAR" or "SPELL_WARCRY_SHOUT_OF_MANY")) return null;
        // 009be120 collects the targets the warcry would affect; Shout of Many takes any stack (no validity rule).
        bool fearTarget = spellId != "SPELL_WARCRY_FEAR_MY_ROAR" || t.Kind == UnitKind.Creature;
        if (WarcryTargetInvalid(spellId, t) || !fearTarget) return new SpecialPrediction("IMMUNE");
        switch (spellId)
        {
            case "SPELL_WARCRY_CALL_OF_BLOOD":
                return new SpecialPrediction("CALL_OF_BLOOD", Value: WarcryRagePoints(spellId, hero));
            case "SPELL_WARCRY_FEAR_MY_ROAR":
                return new SpecialPrediction("FEAR_MY_ROAR", Value: FearMyRoarPercent(WarcryLevel(hero), t));
            default:
            {
                int dmg = ShoutOfManyDamage(side);
                return new SpecialPrediction("DIRECT_DAMAGE", Damage: dmg, Kills: KillsFor(dmg, t));
            }
        }
    }

    /// <summary>
    /// Plague (0xe) and Firewall (0xec) cast by a hero on a stack (exe 00982010 @ 00982759): d = base damage 00976b90
    /// through 0097d600 with luck factor 1.0 plus Elemental Chains (00977e60), capped at the target's hit points
    /// (00a5bbf0); nothing when d ≤ 0. Kills 00a6bd90(target, d, 1). Turns = 004e57d0 / 00a2d700: the duration table at the
    /// mastery (00a34cd0 → 00a34c40: Base + PerPower · power, single precision, ≤ 0 → 0) times the turn length,
    /// truncated, divided by the turn length again (ATBBaseTime / SpellInitiative). Null for other spells, for a target
    /// with Enchanted Obsidian (0x99, the 009776e0 branch is not traced) or when the spell would not damage it.
    /// </summary>
    public SpecialPrediction? PredictDotSpell(int side, string spellId, int targetId)
    {
        if (spellId is not ("SPELL_PLAGUE" or "SPELL_FIREWALL")) return null;
        if (side is < 0 or > 1 || S.Heroes[side] is not { Present: true } hero || S.Unit(targetId) is not { Alive: true } t) return null;
        if (Db.Spell(spellId) is not { } sp || t.Has(Ab.EnchantedObsidian)) return null;
        var m = HeroMastery(hero, sp);
        double power = HeroSpellPower(hero, sp);
        int dmg;
        try
        {
            var sim = new Battle(Db, S.Clone());
            var st = sim.S.Unit(targetId)!;
            double baseDmg = sim.SpellBaseDamage(sp, sp, m, power);
            if (sim.SpellDamageFinal(st, sp, baseDmg, side, ignoreResist: false, sim.S.Heroes[side], rollLuck: false) is not double d) return null;
            dmg = Math.Min(Math.Max(0, (int)Math.Floor(d)), t.TotalHealth);
        }
        catch (Exception) { return null; }
        if (dmg <= 0) return null;
        return new SpecialPrediction("DOT", Damage: dmg, Kills: KillsFor(dmg, t), Turns: TableTurns(sp.DurationAt(m), power));
    }

    /// <summary>
    /// Seduce (0x148) prediction (exe 0097f610 case 0x148): only when the target lacks IMMUNITY_TO_MIND_CONTROL (0x13),
    /// is a creature that is not a succubus (004babf0 ≠ 0x15) and not hypnotized (0x14), the caster is a creature with
    /// count &gt; 0 and the duration 009774a0 is &gt; 0: COMBAT_PREDICTION_SPELL_DURATION with that duration.
    /// </summary>
    public SpecialPrediction? PredictSeduce(int actorId, int targetId)
    {
        if (S.Unit(actorId) is not { Alive: true } u || S.Unit(targetId) is not { Alive: true } t) return null;
        if (u.Kind != UnitKind.Creature || u.Count <= 0 || t.Kind != UnitKind.Creature) return null;
        if (HasAbilityExe(t, Ab.ImmuneMind) || t.CreatureId.Contains("SUCCUBUS") || t.HasEffect("SPELL_HYPNOTIZE")) return null;
        double d = SeduceDuration(u.Count, u.Power, t.Count, t.Power);
        return d > 0 ? new SpecialPrediction("DURATION", Turns: (float)d) : null;
    }

    /// <summary>Turn length in combat time used by 00a2d700: ATBBaseTime (≤ 0 → 100000) / SpellInitiative (≤ 0 → 1).</summary>
    double SpellTurnLength()
    {
        int baseTime = (int)Stats.Get("ATBBaseTime", 100000);
        if (baseTime < 1) baseTime = 100000;
        float init = (int)Stats.Get("SpellInitiative", 10);
        if (init <= 0) init = 1;
        return (double)baseTime / init;
    }

    /// <summary>004e57d0 for a duration-table spell divided back by 00a2d700: trunc(T · turns) / T in single precision.</summary>
    double TableTurns(PowerFormula f, double power)
    {
        float turns = (float)power * (float)f.PerPower + (float)f.Base;
        if (turns <= 0) return 0;
        double len = SpellTurnLength();
        int ticks = (int)((double)(float)len * turns);
        return (float)(ticks / (float)len);
    }

    /// <summary>
    /// The Elemental Chains multiplier m of exe FUN_004b0d80 for the side's hero (see <c>ElementalChainExtra</c>); 1 when
    /// the hero is not a Warlock with Elemental Vision. Used to split the bonus off the reported spell damage for the
    /// CCombatLogBonusElementalDamage entry.
    /// </summary>
    public float ElementalVisionMultiplier(int side)
    {
        if (side is < 0 or > 1 || S.Heroes[side] is not { Present: true } hero) return 1f;
        if (hero.Class != "HERO_CLASS_WARLOCK" || !hero.HasPerk(Sk.ElementalVision)) return 1f;
        float x = (int)hero.SkillLevel(Sk.Invocation) * (float)Stats.Get("combat/HeroSkills/Warlock/ElementalChainBonusPerInvocationTrained", 0.1)
                  + hero.ElementalAltars * (float)Stats.Get("combat/HeroSkills/Warlock/ElementalChainBonusPerAltarOfChaos", 0.1);
        if (hero.HasPerk("HERO_SKILL_ABSOLUTE_CHAINS")) x += x;
        return Specials.Pieces(hero, Specials.Runic) >= 2
            ? x * (float)Stats.Get("ArtifactsSetsEffectsConsts/Runic_2Warlock_ElementalVisionMultiplier", 2) + 1f
            : x + 1f;
    }

    /// <summary>
    /// The Elemental Chains share of a reported spell damage <paramref name="total"/> = D + trunc(D·m − D) (exe 00977e60,
    /// FPU chop): the unique D that gives the total, the bonus being total − D; 0 when none fits.
    /// </summary>
    public static int ElementalVisionBonus(int total, float m)
    {
        if (m <= 1f || total <= 0) return 0;
        int guess = (int)(total / (double)m);
        for (int d = Math.Max(0, guess - 2); d <= guess + 2 && d <= total; d++)
        {
            int extra = Math.Max(0, (int)((double)d * m - d));
            if (d + extra == total) return extra;
        }
        return 0;
    }
}
