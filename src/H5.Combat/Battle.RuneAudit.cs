namespace H5.Combat;

// Runes and warcries confirmed against H5_Game.exe (docs/rules/audit/combat_spells_r.md).
public sealed partial class Battle
{
    /// <summary>Exe element enum (FUN_00ad02b0): 1 air, 2 fire, 3 water, 4 earth.</summary>
    internal static string ExeElement(int code) => code switch
    {
        1 => Elements.Air, 2 => Elements.Fire, 3 => Elements.Water, 4 => Elements.Earth, _ => Elements.None,
    };

    /// <summary>
    /// Rune of Elemental Immunity's two elements (exe FUN_00c54620): a = 1 + Random(4), then r = Random(3), b = r + 1,
    /// raised to r + 2 when a ≤ b, so b is uniform among the other three. Stored as b + 8a, checked by FUN_00c546b0.
    /// </summary>
    internal (int A, int B) RollElementalImmunity()
    {
        int a = S.Rng.Next(4) + 1;
        int r = S.Rng.Next(3);
        int b = r + 1;
        if (a <= b) b = r + 2;
        return (a, b);
    }

    static readonly HashSet<string> DragonformBases = new()
    {
        "CREATURE_BONE_DRAGON", "CREATURE_GREEN_DRAGON", "CREATURE_DEEP_DRAGON", "CREATURE_FIRE_DRAGON",
    };

    /// <summary>
    /// Rune of Dragonform refuses dragons (exe FUN_004b4460 → FUN_004bb000): the creature's base creature is one of
    /// creature ids 0x29/0x37/0x53/0x68 (Bone, Green, Deep and Fire Dragon) — every dragon line, by id rather than name.
    /// </summary>
    internal bool DragonformForbidden(Unit t)
    {
        if (!Db.Creatures.TryGetValue(t.CreatureId, out var def)) return false;
        string b = string.IsNullOrEmpty(def.BaseCreature) || def.BaseCreature == "CREATURE_UNKNOWN" ? t.CreatureId : def.BaseCreature;
        return DragonformBases.Contains(b);
    }

    // FUN_004e3ac0: effects no dispel can remove (Holy Charge, Demonic Strike, Dash, Poisonous Attack, Wound, Venom,
    // goblin Sacrifice, Powerful Blow, Prepared Position, Battle Rage).
    static readonly HashSet<string> NeverDispelled = new()
    {
        "SPELL_HOLY_CHARGE", "SPELL_DEMONIC_STRIKE", H5.Combat.Fx.Dash, H5.Combat.Fx.Poison, H5.Combat.Fx.Wound,
        "SPELL_ABILITY_VENOM", H5.Combat.Fx.Sacrifice, "SPELL_EFFECT_POWERFULL_BLOW", "SPELL_EFFECT_PREPARED_POSITION",
        H5.Combat.Fx.BattleRage,
    };

    /// <summary>
    /// Rune of Exorcism (exe FUN_008d9b30 → FUN_004e6470 / FUN_004e4000 with the caster's own side): every harmful
    /// effect that is dispellable, timed (not one of the permanent durations, FUN_004e3960) and not in the
    /// never-dispelled list. There is no spell-level limit. (Warcry effects, spell type 6, are skipped too; none is harmful.)
    /// </summary>
    internal bool ExorcismRemoves(Effect e) =>
        !e.Positive && e.Dispellable && !double.IsPositiveInfinity(e.Remaining) && !NeverDispelled.Contains(e.Id);

    /// <summary>Warcry caster level (exe FUN_009bdb70): hero level, + MightyVoice_HeroLevelBonus with Mighty Voice.</summary>
    internal int WarcryLevel(HeroState hero) =>
        hero.Level + (hero.HasPerk(Sk.MightyShout) ? (int)Stats.Get("combat/HeroSkills/Barbarian/MightyVoice_HeroLevelBonus", 5) : 0);

    /// <summary>
    /// Rage points of a warcry (exe FUN_009bdbd0): Call of Blood gives its target trunc(level·CallOfBlood_RPPerCasterLevel·m),
    /// the others give the caster's army trunc(RPToArmyOnWarcryLevel{1,2,3}·m) (Rallying Cry 1, Word of the Chief / Fear
    /// My Roar 2, Battlecry / Shout of Many 3), m = Voice of Rage's multiplier (CallOfBlood or Other), all + hero level
    /// for the War Leader specialisation.
    /// </summary>
    internal int WarcryRagePoints(string id, HeroState hero)
    {
        double m = 1;
        if (hero.HasPerk(Sk.ShoutOfRage))
            m = (float)Stats.Get(id == "SPELL_WARCRY_CALL_OF_BLOOD" ? "combat/HeroSkills/Barbarian/VoiceOfRage_RageMultCallOfBlood"
                : "combat/HeroSkills/Barbarian/VoiceOfRage_RageMultOther", id == "SPELL_WARCRY_CALL_OF_BLOOD" ? 1.5 : 2);
        int rp = id switch
        {
            "SPELL_WARCRY_RALLING_CRY" => (int)((int)Stats.Get("combat/Spells/Warcries/RPToArmyOnWarcryLevel1", 20) * m),
            "SPELL_WARCRY_CALL_OF_BLOOD" => (int)(WarcryLevel(hero) * (int)Stats.Get("combat/Spells/Warcries/CallOfBlood_RPPerCasterLevel", 10) * m),
            "SPELL_WARCRY_WORD_OF_THE_CHIEF" or "SPELL_WARCRY_FEAR_MY_ROAR" => (int)((int)Stats.Get("combat/Spells/Warcries/RPToArmyOnWarcryLevel2", 20) * m),
            "SPELL_WARCRY_BATTLECRY" or "SPELL_WARCRY_SHOUT_OF_MANY" => (int)((int)Stats.Get("combat/Spells/Warcries/RPToArmyOnWarcryLevel3", 20) * m),
            _ => 0,
        };
        if (hero.Specialization == "HERO_SPEC_WAR_LEADER") rp += hero.Level;
        return rp;
    }

    /// <summary>
    /// Fear My Roar's chance (exe FUN_009bddb0): 0 against HOLD_GROUND, else clamp((caster level − target level + 10)·5, 0, 100).
    /// The roll is the ability roll FUN_00a5a170 without an attacker (no Soldier's Luck second roll).
    /// </summary>
    internal int FearMyRoarPercent(int casterLevel, Unit t) =>
        t.Has(Ab.HoldGround) ? 0 : Math.Clamp((casterLevel - t.Level + 10) * 5, 0, 100);

    /// <summary>Warcry target validity (exe FUN_009bdaa0; true = invalid).</summary>
    internal bool WarcryTargetInvalid(string id, Unit t) => id switch
    {
        // Rallying Cry and Word of the Chief: Undead (incl. Vampirism, via the dynamic HasAbility) and Mechanical.
        "SPELL_WARCRY_RALLING_CRY" or "SPELL_WARCRY_WORD_OF_THE_CHIEF" => t.Has(Ab.Undead) || t.Has(Ab.Mechanical) || t.IsWarMachine || t.IsObject,
        "SPELL_WARCRY_CALL_OF_BLOOD" => !t.Has(Ab.DemonRaged),
        // Fear My Roar: only IMMUNITY_TO_MIND_CONTROL (removed by the enemy's Shawl of the Great Lich, FUN_008a34c0).
        "SPELL_WARCRY_FEAR_MY_ROAR" => HasAbilityExe(t, Ab.ImmuneMind),
        _ => false,
    };

    /// <summary>
    /// Shout of Many's damage (exe FUN_009be6c0 case 0x127, FUN_009bde00): trunc(count·ShoutOfMany_DamagePerCreatureInArmy)
    /// in single precision, count = creatures of the caster's army not controlled by the other side (FUN_00977670).
    /// It goes straight into the stack damage pipeline (FUN_00a616e0 → FUN_00a61600): no resistance, protection or immunity.
    /// </summary>
    internal int ShoutOfManyDamage(int side)
    {
        int n = S.LivingOf(side).Where(u => u.Kind == UnitKind.Creature && Controller(u) == u.Side).Sum(u => u.Count);
        return (int)(n * (float)Stats.Get("combat/Spells/Warcries/ShoutOfMany_DamagePerCreatureInArmy", 1));
    }

    /// <summary>Battlecry's speed bonus (exe FUN_008a1e50, effect 0x126 level): ((level − 1)/div)·per + base.</summary>
    internal int BattlecrySpeedBonus(int level) => WarcryLevelBonus("combat/Spells/Warcries/Battlecry_SpeedBonus", level);
}
