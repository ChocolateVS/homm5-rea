namespace H5.Combat;

// Hero skill / perk rules confirmed against H5_Game.exe by the skills audit
// (docs/rules/audit/combat_heroes_skills.md).
public sealed partial class Battle
{
    /// <summary>
    /// Corrupted Soil damage for an enemy stack that moves (exe FUN_00a61a60, called by the move routine
    /// FUN_00a66050 once per move): none for flying stacks (unit vfunc 0x190, the "moves by endpoints only" test the
    /// flee/move code also uses), otherwise round(level · clamp(SoilBurnDamagePerHeroLevel, 0.1, 1000)).
    /// The damage then goes through the usual stack damage pipeline (FUN_00a61600: Arcane Armor, Last Stand).
    /// </summary>
    internal int SoilBurnDamage(Unit u)
    {
        if (u.Flying) return 0;
        var eh = Enemy(u.Side);
        if (!eh.Present || !(eh.HasPerk(Sk.CorruptedSoil) || eh.HasPerk(Sk.BarbCorruptedSoil))) return 0;
        float perLevel = Math.Clamp((float)Stats.Get("combat/HeroSkills/Ranger/SoilBurnDamagePerHeroLevel", 3), 0.1f, 1000f);
        return (int)Math.Round((double)(eh.Level * perLevel), MidpointRounding.ToEven);
    }

    /// <summary>
    /// Death Scream luck penalty (exe FUN_00989c40, effect 0x3b): round(DeathScream_MoralePenalty · multiplier), with the
    /// multiplier the Dark Emissary factor kept on the effect (Value2). The engine used a flat −1, so a Dark Emissary's
    /// scream only took one luck point instead of two.
    /// </summary>
    internal int DeathScreamLuckPenalty(Effect howl)
    {
        double mult = howl.Value2 > 0 ? howl.Value2 : 1;
        return (int)Math.Round(Stats.Get("combat/HeroSkills/Necromancer/DeathScream_MoralePenalty", 1) * mult, MidpointRounding.ToEven);
    }

    /// <summary>
    /// Amount of the perks' extra elemental damage (Chilling Steel 10 %, Chilling Bones 5 %: exe FUN_00a5c000 /
    /// FUN_00a5bc50 → FUN_0097d740): the float amount is rounded half up — trunc(amount·(1 − p/100) + 0.5) with the FPU in
    /// chop mode (OR AH,0x0C at 0097d777). The engine truncated, so e.g. 9 damage gave 0 instead of 1 extra cold damage.
    /// The protection percent p (FUN_0097d2a0 with no spell) is not modelled here.
    /// </summary>
    internal static int SpellLikeAmount(double amount) => (int)((double)(float)amount + 0.5);
}
