using H5.Core.Data;

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

    /// <summary>
    /// Familiar Ground (HERO_SKILL_ROAD_HOME, exe FUN_008a0e30 called by the speed getter FUN_008a1e50): +max(0,
    /// RoadHome/SpeedBonus) speed for the hero's creatures when the battlefield terrain (battle vfunc 0x60) is
    /// RoadHome/NativeTerrain (TT_GRASS in the stock data). Not implemented by the engine before.
    /// </summary>
    internal int RoadHomeSpeedBonus(Unit u)
    {
        if (!HeroOf(u).HasPerk(Sk.FamiliarGround)) return 0;
        int terrain = S.Terrain;
        int native = (Stats.Node("combat/HeroSkills/Knight/RoadHome/NativeTerrain")?.Value.Trim()) switch
        {
            "TT_DIRT" => CombatArenaTable.TerrainDirt, "TT_GRASS" => CombatArenaTable.TerrainGrass,
            "TT_SAND" => CombatArenaTable.TerrainSand, "TT_SNOW" => CombatArenaTable.TerrainSnow,
            "TT_LAVA" => CombatArenaTable.TerrainLava, "TT_SUBTERRANEAN" => CombatArenaTable.TerrainSubterranean,
            "TT_WATER" => CombatArenaTable.TerrainWater, _ => CombatArenaTable.TerrainGrass,
        };
        return terrain == native ? Math.Max(0, (int)Stats.Get("combat/HeroSkills/Knight/RoadHome/SpeedBonus", 2)) : 0;
    }

    /// <summary>
    /// Spell-damage perks applied before protection (exe FUN_0097d600 → FUN_00977a80, then FUN_0097d2a0 on the result):
    /// Cold Death (HERO_SKILL_DEADLY_COLD) adds one creature's hit points (vfunc 0x1a8) for Ice Bolt / Circle of Winter /
    /// Deep Freeze, then Exorcism (HERO_SKILL_ELEMENTAL_OVERKILL) doubles the sum against summoned creatures. The engine
    /// applied both after protection, adding Cold Death's hit points pre-scaled by the protection share.
    /// </summary>
    internal double PreProtectionPerks(double dmg, Unit t, SpellDef sp, HeroState? hero)
    {
        if (hero == null) return dmg;
        string id = BaseSpell(sp.Id);
        if (hero.HasPerk(Sk.ColdDeath) && id is "SPELL_ICE_BOLT" or "SPELL_FROST_RING" or "SPELL_DEEP_FREEZE" && !ElementImmune(t, Elements.Water))
            dmg += MaxHealthOf(t);
        if (hero.HasPerk(Sk.Exorcism) && t.Summoned && sp.School == "MAGIC_SCHOOL_DESTRUCTIVE") dmg *= 2;
        return dmg;
    }

    /// <summary>
    /// Memory of our Blood's battle-start rage (exe FUN_008aaec0): RNG Range(MinRP/Step, MaxRP/Step) (inclusive) times
    /// Step, from DemonRage/MemoryOfOurBlood_* (50..100 in steps of 10 in the stock data).
    /// </summary>
    internal int MemoryOfBloodRage()
    {
        const string k = "combat/Creatures/Stronghold/DemonRage/MemoryOfOurBlood_";
        int step = Math.Max(1, (int)Stats.Get(k + "RPStep", 10));
        return S.Rng.Range((int)Stats.Get(k + "MinRP", 50) / step, (int)Stats.Get(k + "MaxRP", 100) / step) * step;
    }
}
