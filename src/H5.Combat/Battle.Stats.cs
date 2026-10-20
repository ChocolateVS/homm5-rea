using H5.Core.Data;

namespace H5.Combat;

// Effective statistics: base values plus hero, spell and ability modifiers.
public sealed partial class Battle
{
    HeroState HeroOf(Unit u) => S.Heroes[u.Side];
    HeroState Enemy(int side) => S.Heroes[1 - side];

    double Fx(Unit u, string id) => u.GetEffect(id)?.Value ?? 0;

    bool IsLiving(Unit u) => !u.Has(Ab.Undead) && !u.Has(Ab.Elemental) && !u.Has(Ab.Mechanical) && !u.IsWarMachine && !u.IsObject
                             && !u.HasEffect("SPELL_VAMPIRISM");
    public bool IsUndeadLike(Unit u) => u.Has(Ab.Undead) || u.HasEffect("SPELL_VAMPIRISM");
    // exe 004ba9d0 / 00a6acd0 / 00a6ba00 test only Undead (dynamic: Vampirism too) and Mechanical — Elementals have morale.
    bool NeutralMorale(Unit u) => u.Has(Ab.Undead) || u.Has(Ab.Mechanical) || u.IsWarMachine || u.IsObject
                                  || u.HasEffect("SPELL_VAMPIRISM");
    /// <summary>Mind immunity: the exe HasAbility(IMMUNITY_TO_MIND_CONTROL) — explicit or derived from Undead / Mechanical /
    /// Elemental / Immunity to Magic, all cancelled by Shawl of the Great Lich (FUN_008a34c0, see HasAbilityExe).</summary>
    public bool MindImmune(Unit u) => HasAbilityExe(u, Ab.ImmuneMind) || u.IsWarMachine || u.IsObject;

    IEnumerable<Unit> Adjacent(Unit u) => S.Living.Where(o => o.Id != u.Id && !o.IsObject && AreAdjacent(u, o));

    SideBonus Side(Unit u) => S.SideBonuses is { Length: 2 } sb ? sb[u.Side] : NoSideBonus;
    static readonly SideBonus NoSideBonus = new();

    public int AttackOf(Unit u, Unit? vs = null)
    {
        var h = HeroOf(u);
        double a = u.BaseAttack;
        if (u.HasEffect(H5.Combat.Fx.RuneDragonform)) a += u.BaseAttack;   // raw value doubled (p.192)
        if (h.Present && !u.IsWarMachine && !u.IsObject) a += h.Attack;
        if (!u.IsWarMachine && !u.IsObject) a += Side(u).Attack;
        a += NativeTerrainBonus(u);   // exe FUN_008a4800: +1 when the battlefield is the creature race's native terrain
        a += Fx(u, "SPELL_BLOODLUST");
        a -= Fx(u, "SPELL_WEAKNESS");                       // Suffering
        a += Fx(u, H5.Combat.Fx.Enraged);
        a += Fx(u, H5.Combat.Fx.BattleRage);
        a += Fx(u, H5.Combat.Fx.Sacrifice);
        a += Fx(u, H5.Combat.Fx.Benediction);
        a += Fx(u, H5.Combat.Fx.Battlecry);
        a -= Fx(u, H5.Combat.Fx.WeakeningStrike);
        if (u.HasEffect(H5.Combat.Fx.RageOfForest)) a += DefenceOf(u, rageFull: true) / 2;   // exe 008a4800: + full defence / 2
        if (u.HasEffect(H5.Combat.Fx.BerserkerRage)) a += DefenceOf(u, rageFull: true, ignoreBerserkerRage: true);   // exe 008a4a94: + FUN_008a22f0 (live defence)
        a += Fx(u, "FX_BLOODDRINKER");
        a += RageBonus(u, "DEMON_RAGE_BONUS_ATTACK");
        // Festering Aura of adjacent Rot Zombies: -2 attack (p.174 / DefaultStats WeakeningAuraOffenceDec).
        if (!IsUndeadLike(u) && Adjacent(u).Any(o => o.Has(Ab.WeakeningAura)))
            a -= Stats.Get("combat/Creatures/Necropolis/WeakeningAuraOffenceDec", 3);
        if (vs != null && u.Has(Ab.AntiGiant) && vs.IsLarge) a += Stats.Get("combat/Creatures/Fortress/AntiGiantAttackInc", 4);
        if (u.IsWarMachine && u.CreatureId == "WAR_MACHINE_BALLISTA" || (u.MaxShots > 0 && !u.IsWarMachine))
            a += AmmoCartAttackBonus(u.Side);
        if (h.HasPerk(Sk.OffensiveFormation)) a += FormationBonus(u, Stats.Get("combat/HeroSkills/Runemage/Offensive_MaxOffenseBonus", 10));
        if (u.HasEffect(NecromancersDebuff)) a = NecromancersDebuffed(Math.Floor(a));
        return Math.Max(0, (int)Math.Floor(a));
    }

    internal const string NecromancersDebuff = "SPELL_ARTFSET_NECROMANCERS_DEBUFF";
    internal const string LionsDemoralized = "SPELL_ARTFSET_LIONS_DEMORALIZED";

    /// <summary>Necromancers 4-piece debuff: trunc((1 − pct·0.01)·value) (exe 008a4800 / 008a22f0, pct int at RPGStats+0x1198).</summary>
    double NecromancersDebuffed(double value) =>
        (int)((1 - (int)Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_4_EnemyCreature_BadMoralePenaltyToAttackDefencePercents", 20) * 0.01) * value);

    int AmmoCartAttackBonus(int side)
    {
        if (!S.Units.Any(x => x.Alive && x.Side == side && x.CreatureId == "WAR_MACHINE_AMMO_CART")) return 0;
        // +1/+2/+3 by War Machines mastery (p.307), +4 more with Ring of Machine Affinity (exe FUN_008a4800).
        var h = S.Heroes[side];
        return (int)h.SkillLevel(Sk.WarMachines) + (HasMachineAffinityRing(h) ? 4 : 0);
    }

    bool HasAmmoCart(int side) => S.Units.Any(x => x.Alive && x.Side == side && x.CreatureId == "WAR_MACHINE_AMMO_CART");

    public int DefenceOf(Unit u, Unit? vs = null, int attackerCellsWalked = 0, bool rageFull = false, bool ignoreBerserkerRage = false)
    {
        var h = HeroOf(u);
        double raw = u.BaseDefence;
        if (u.HasEffect(H5.Combat.Fx.RuneDragonform)) raw += u.BaseDefence;
        double d = raw;
        if (h.Present && !u.IsWarMachine && !u.IsObject) d += h.Defence;
        if (!u.IsWarMachine && !u.IsObject) d += Side(u).Defence;
        d += Fx(u, H5.Combat.Fx.Benediction);
        d += NativeTerrainBonus(u);
        // Defend / Take Roots posture (exe FUN_008a22f0): bonus = max(1, trunc(d·f + 0.001)) on the base + hero +
        // Prayer + native-terrain defence only (spells and other effects are added afterwards, FUN_008d9390).
        // f is DefenceBonusOnTakeRoots for ABILITY_TAKE_ROOTS creatures, else DefenceBonusOnDefend (not both);
        // Hold Ground multiplies it by the matching HoldGround_* multiplier.
        if (u.Defending || u.HasEffect(H5.Combat.Fx.TakeRoots)) d += DefendPostureBonus(u, h, Math.Floor(d));
        d += Fx(u, "SPELL_STONESKIN");
        d += RageBonus(u, "DEMON_RAGE_BONUS_DEFENCE");
        if (u.Has(Ab.PreparedPosition) && !u.HasMovedFromStart) d += Stats.Get("combat/Creatures/Fortress/PreparedPosition_DefenceBonus", 5);
        if (u.Has(Ab.Agility)) d += Stats.Get("combat/Creatures/Sylvan/AgilityDefenceBoostPerCell", 2) * AgilityCells(u);
        if (vs != null && u.Has(Ab.AntiGiant) && vs.IsLarge) d += Stats.Get("combat/Creatures/Fortress/AntiGiantDefenceInc", 4);
        if (h.HasPerk(Sk.DefensiveFormation)) d += FormationBonus(u, Stats.Get("combat/HeroSkills/Runemage/Defensive_MaxDefenseBonus", 10));
        // Decreases (Armored creatures are immune to defence reduction, p.171).
        if (!u.Has(Ab.Armored))
        {
            d -= Fx(u, "SPELL_DISRUPTING_RAY");
            d -= u.GetEffect("SPELL_CURSE")?.Value2 ?? 0;      // Maledictor: Weakness also lowers Defense
            d -= Fx(u, H5.Combat.Fx.WeakeningStrike);
            if (!IsUndeadLike(u) && Adjacent(u).Any(o => o.Has(Ab.WeakeningAura)))
                d -= Stats.Get("combat/Creatures/Necropolis/WeakeningAuraDeffenceDec", 2);
            d -= Fx(u, H5.Combat.Fx.ArmorCrushed);
            d -= Fx(u, MicroArtifacts.ArmorCrushingEffect);   // MAE_ARMOR_CRUSHING (exe 00989790 case 0xb3, like 0xd / 0xca)   // Master of Fire: fixed half of the defence at application (exe 00989790 case 0xca)
            if (u.HasEffect(H5.Combat.Fx.RageOfForest) && !rageFull) d = RageOfForestDefence(Math.Max(0, (int)Math.Floor(d)));   // exe 008a32b0
            if (u.HasEffect(H5.Combat.Fx.BerserkerRage) && !ignoreBerserkerRage) d = 0;   // exe 008a32b0
            // Rider Charge: -20% of target defence per cell walked (p.179).
            if (vs != null && vs.Has(Ab.RiderCharge) && attackerCellsWalked > 0)
                d *= Math.Max(0, 1 - Stats.Get("combat/Creatures/Dungeon/RiderCharge_DefenceReducePerPathCell", 0.2) * attackerCellsWalked);
        }
        if (u.HasEffect(NecromancersDebuff)) d = NecromancersDebuffed(Math.Floor(d));
        return Math.Max(0, (int)Math.Floor(d));
    }

    /// <summary>Exe FUN_008a22f0 defend bonus: max(1, trunc(defence·f + 0.001)) (FPU truncation mode).</summary>
    int DefendPostureBonus(Unit u, HeroState h, double defence)
    {
        bool roots = u.Has(Ab.TakeRoots);
        double f = roots ? Stats.Get("combat/Creatures/Sylvan/DefenceBonusOnTakeRoots", 0.5)
                         : Stats.Get("combat/Creatures/DefenceBonusOnDefend", 0.3);
        if (h.Present && h.HasPerk(Sk.HoldGround))
            f *= roots ? Stats.Get("combat/HeroSkills/Knight/HoldGround_TakeRootsBonusMultiplier", 2)
                       : Stats.Get("combat/HeroSkills/Knight/HoldGround_DefendBonusMultiplier", 2);
        return Math.Max(1, (int)(defence * f + 0.001));
    }

    /// <summary>
    /// Native terrain (exe FUN_008a4800 / FUN_008a22f0: battlefield vfunc 0x54(race) → +1 attack and +1 defence;
    /// fan manual p.260 for the race ↔ terrain table). Terrain indices per <see cref="BattleState.Terrain"/>.
    /// </summary>
    public int NativeTerrainBonus(Unit u)
    {
        // Only on a real battlefield (an arena was chosen); synthetic battles without an arena have no terrain.
        if (S.Arena == null || u.Kind != UnitKind.Creature || u.IsWarMachine || u.IsObject) return 0;
        bool native = S.Terrain switch
        {
            0 => u.Town == Towns.Necropolis,
            1 => u.Town is Towns.Haven or Towns.Sylvan,
            2 => u.Town == Towns.Academy,
            3 or 7 => u.Town == Towns.Fortress,
            4 => u.Town == Towns.Inferno,
            5 => u.Town == Towns.Dungeon,
            8 or 9 => u.Town == Towns.Stronghold,
            _ => false,
        };
        return native ? 1 : 0;
    }

    /// <summary>Dwarven formations (p.323): floor(10*log10(adjacent dwarven power / own power)), capped.</summary>
    double FormationBonus(Unit u, double cap)
    {
        if (u.Town != Towns.Fortress || u.Kind != UnitKind.Creature) return 0;
        double own = (double)u.Count * u.Power;
        double adj = Adjacent(u).Where(o => o.Side == u.Side && o.Town == Towns.Fortress && o.Kind == UnitKind.Creature).Sum(o => (double)o.Count * o.Power);
        if (adj <= own || own <= 0) return 0;
        return Math.Min(cap, Math.Floor(10 * Math.Log10(adj / own) + 1e-9));
    }

    int AgilityCells(Unit u) => u.AgilityCells < 0 ? u.BaseSpeed : u.AgilityCells;   // exe effect 0x10f value (see Unit.AgilityCells)

    public double InitiativeOf(Unit u)
    {
        // Exe FUN_008a4330: the base initiative is MULTIPLIED by each modifier in turn — enemy Prudent /
        // Master of Initiative, one factor per active effect (FUN_004e4270), Storm Wind, Fog Veil — and the
        // flat bonuses are added afterwards.
        if (u.HasEffect(GatedEffect) && !u.HasEffect(H5.Combat.Fx.Frozen) && !u.HasEffect(FxSeducing)) return GatedInitiative(u);
        var h = HeroOf(u);
        var eh = Enemy(u.Side);
        double mult = 1;
        if (eh.Present && eh.Specialization is "HERO_SPEC_PRUDENT" or "HERO_SPEC_MASTER_OF_INITIATIVE")
            mult *= 1 - Math.Clamp(Stats.Get("combat/HeroSpecializations/Warlock/Prudent_InitiativeDisbonusBase", 0)
                                   + eh.Level * Stats.Get("combat/HeroSpecializations/Warlock/Prudent_InitiativeDisbonusPerLevel", 0.01), 0, 1);
        mult *= EffectInitiativeFactors(u, h);
        if (eh.Present && u.Flying && (eh.HasPerk(Sk.StormWind) || eh.HasPerk(Sk.BarbStormWind)))
            mult *= 1 - Stats.Get("combat/HeroSkills/Ranger/StormWind_InitiativePenalty", 0.2);
        if (eh.Present && u.MaxShots > 0 && !u.IsWarMachine && (eh.HasPerk(Sk.FogVeil) || eh.HasPerk(Sk.BarbFogVeil)))
            mult *= 1 - Stats.Get("combat/HeroSkills/Ranger/FogVeil_InitiativePenalty", 0.1);
        // Staff of Vexings of the enemy hero: × (1 − 0.2 · staffs) (exe 008a4619..008a4647).
        if (eh.Present) mult *= 1 - 0.2f * eh.Artifacts.Count(a => a == "STAFF_OF_VEXINGS");
        double init = u.BaseInitiative * Math.Max(0.1, mult);
        init += Fx(u, H5.Combat.Fx.BattleRage) > 0 ? Stats.Get("combat/Creatures/Fortress/BattleRage_InitiativeBonus", 2) : 0;
        if (u.HasEffect(H5.Combat.Fx.RageOfForest)) init += Stats.Get("combat/Creatures/Sylvan/RageOfTheForestInitiativeInc", 5);
        if (Adjacent(u).Any(o => o.Side == u.Side && o.Has(Ab.PresenceOfCommander)))   // exe 008a4330: flag 0x400, RPGStats +0x4fc
            init += (int)Stats.Get("combat/Creatures/Stronghold/PresenceOfCommanderInitiativeInc", 1);
        init += RageBonus(u, "DEMON_RAGE_BONUS_INITIATIVE");
        if (u.HasEffect(H5.Combat.Fx.Frozen) || u.HasEffect(FxSeducing)) return 0;   // a seducing stack's ATB is frozen (p.181)
        return Math.Max(0.1, init);
    }

    /// <summary>
    /// Product of the per-effect initiative factors (exe FUN_004e4270, keyed by the effect's spell id):
    /// Slow 1−clamp(v,0,1); Haste 1+max(v,0), plus Expediter (own hero spec HERO_SPEC_HASTE) clamp(perLevel,0,100)·level;
    /// Hypnotize clamp(v,0,1); Prayer 1+Prayer_InitiativeBonus (×WhiteKnight_PrayerMultiplier for White Knight);
    /// Dash Dash_InitiativeFactor; Wound 1−clamp(Wound_InitiativeDecrease,0,1); Death Scream 1−DeathScream_InitiativePenalty.
    /// </summary>
    internal double EffectInitiativeFactors(Unit u, HeroState h)
    {
        double f = 1;
        if (u.GetEffect("SPELL_SLOW") is { } slow) f *= 1 - Math.Clamp(slow.Value, 0, 1);
        // 008a4330 passes each effect's own caster hero (effect vf 0xc) to 004e4270, so Expediter and
        // White Knight follow the hero who cast the effect, not the creature's owner.
        HeroState Caster(Effect e) => e.SourceSide >= 0 ? S.Heroes[e.SourceSide] : h;
        if (u.GetEffect("SPELL_HASTE") is { } haste)
        {
            double hf = 1 + Math.Max(0, haste.Value);
            var ch = Caster(haste);
            if (ch.Present && ch.Specialization == "HERO_SPEC_HASTE")
                hf += ch.Level * Math.Clamp(Stats.Get("combat/HeroSpecializations/Knight/Expediter_HasteBonusPerLevel", 0.01), 0, 100);
            f *= hf;
        }
        if (u.GetEffect("SPELL_HYPNOTIZE") is { } hyp) f *= Math.Clamp(hyp.Value, 0, 1);
        if (u.GetEffect(H5.Combat.Fx.Benediction) is { } prayer)
        {
            double bonus = Stats.Get("combat/HeroSkills/Knight/Prayer_InitiativeBonus", 0.1);
            var ch = Caster(prayer);
            if (ch.Present && ch.Specialization == "HERO_SPEC_WHITE_KNIGHT") bonus *= Stats.Get("combat/HeroSkills/Knight/WhiteKnight_PrayerMultiplier", 2);
            f *= 1 + bonus;
        }
        if (u.HasEffect(H5.Combat.Fx.Dash)) f *= Stats.Get("combat/Creatures/Academy/Dash_InitiativeFactor", 2);
        if (u.HasEffect(H5.Combat.Fx.Wound)) f *= 1 - Math.Clamp(Stats.Get("combat/Creatures/Fortress/Wound_InitiativeDecrease", 0.2), 0, 1);
        if (u.GetEffect(H5.Combat.Fx.BansheeHowl) is { } scream)
        {
            var ch = Caster(scream);
            double mult = ch.Present && Specials.Pieces(ch, Specials.Necromancers) > 1
                ? (int)Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_2Necromancer_BansheeWhail_DeInitiativeMultiplier", 2) : 1;
            f *= 1 - mult * Stats.Get("combat/HeroSkills/Necromancer/DeathScream_InitiativePenalty", 0.1);
        }
        return f;
    }

    double HeroInitiative(int side) => Stats.Get("HeroInitiative", 10) * HeroInitiativeMultiplier(S.Heroes[side]);

    public int SpeedOf(Unit u)
    {
        if (u.IsWarMachine || u.IsObject) return 0;
        var h = HeroOf(u);
        double sp = u.BaseSpeed;
        if (h.HasPerk(Sk.AuraOfSwiftness)) sp += 1;
        // Death Tread: the siege attacker's troops get DeathTread/SpeedBonus (exe FUN_008a0ea0).
        if (S.Siege != null && u.Side == 0 && h.HasPerk("HERO_SKILL_DEATH_TREAD"))
            sp += Math.Max(0, Stats.Get("combat/HeroSkills/Necromancer/DeathTread/SpeedBonus", 4));
        sp += RoadHomeSpeedBonus(u);   // Familiar Ground (exe FUN_008a0e30)
        // exe FUN_008a1e50 order: Wound trunc((1 − clamp(dec, 0, 1))·speed) → enemy Storm Wind on flyers → Rune of Charge ×2
        // → enemy Necromancers set → Demon Rage speed bonus → Battlecry ((level−1)/div·per + base); clamped at 0.
        if (u.HasEffect(H5.Combat.Fx.Wound))
            sp = Math.Floor((1 - Math.Clamp((double)(float)Stats.Get("combat/Creatures/Fortress/Wound_SpeedDecrease", 0.4), 0, 1)) * sp);
        var eh = Enemy(u.Side);
        // Storm Wind slows enemy flyers by StormWind_SpeedPenalty; results below 2 become 1 (exe 008a1e50).
        if (eh.Present && u.Flying && (eh.HasPerk(Sk.StormWind) || eh.HasPerk(Sk.BarbStormWind)))
        {
            sp -= Stats.Get("combat/HeroSkills/Ranger/StormWind_SpeedPenalty", 2);
            if (sp < 2) sp = 1;
        }
        if (u.HasEffect(H5.Combat.Fx.RuneCharge)) sp *= 2;
        if (eh.Present && Specials.Pieces(eh, Specials.Necromancers) >= 2) sp -= (int)Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_2_EnemyCreature_DeSpeed", 1);
        sp += RageBonus(u, "DEMON_RAGE_BONUS_SPEED");
        if (u.GetEffect(H5.Combat.Fx.Battlecry) is { } battlecry) sp += battlecry.Value2;
        return Math.Max(0, (int)Math.Floor(sp));
    }

    public (double Min, double Max) DamageRangeOf(Unit u)
    {
        double min = u.BaseMinDamage + RageBonus(u, "DEMON_RAGE_BONUS_DAMAGE");
        double max = u.BaseMaxDamage + RageBonus(u, "DEMON_RAGE_BONUS_DAMAGE");
        if (u.GetEffect("SPELL_BLESS") is { } bless) min = min + bless.Value * (max - min);   // Divine Strength (p.190)
        if (u.GetEffect("SPELL_CURSE") is { } curse) max = min + curse.Value * (max - min);   // Weakness (p.186)
        if (u.GetEffect("SPELL_BERSERK") is { } fr) { min *= 1 + fr.Value; max *= 1 + fr.Value; }
        // Fog Veil scales the enemy shooters' damage range, so it applies to their melee too (exe 008a2630).
        var eh = Enemy(u.Side);
        if (eh.Present && u.MaxShots > 0 && !u.IsWarMachine && (eh.HasPerk(Sk.FogVeil) || eh.HasPerk(Sk.BarbFogVeil)))
        {
            double k = 1 - Stats.Get("combat/HeroSkills/Ranger/FogVeil_DamageDecrease", 0.25);
            min *= k; max *= k;
        }
        if (min > max) min = max;
        return (min, max);
    }

    public int MaxHealthOf(Unit u)
    {
        double hp = u.BaseHealth + RageBonus(u, "DEMON_RAGE_BONUS_HEALTH");
        if (u.HasEffect(H5.Combat.Fx.BearRoar)) hp = BearRoarHealth((int)hp, Stats.Get("combat/Creatures/Fortress/BearRoar_HealthBonus", 0.1));   // exe 008a2130
        if (u.GetEffect(H5.Combat.Fx.ResurrectionPenalty) is { } rp) hp *= Math.Max(0.1, 1 - rp.Value);
        return Math.Max(1, (int)hp);
    }

    /// <summary>Recomputes per-creature max HP after a change of effects, preserving the stack's damage ratio.</summary>
    void RefreshHealth(Unit u)
    {
        int newMax = MaxHealthOf(u);
        if (newMax == u.MaxHealthCache || u.Count <= 0) { u.MaxHealthCache = newMax; return; }
        int total = u.TotalHealth;
        double ratio = newMax / (double)u.MaxHealthCache;
        u.MaxHealthCache = newMax;
        int newTotal = Math.Max(1, (int)Math.Round(total * ratio));
        SetTotalHealth(u, newTotal);
    }

    void SetTotalHealth(Unit u, int total)
    {
        if (total <= 0) { u.Count = 0; u.TopHealth = 0; return; }
        int max = u.MaxHealthCache;
        u.Count = (total + max - 1) / max;
        u.TopHealth = total - (u.Count - 1) * max;
    }

    public int MoraleOf(Unit u)
    {
        if (NeutralMorale(u))
        {
            // Artificial Glory: golems and war machines get positive morale only (p.118).
            if (!(HeroOf(u).HasPerk(Sk.ArtificialGlory) && (u.Has(Ab.Mechanical) || u.IsWarMachine))) return 0;
        }
        var h = HeroOf(u);
        int m = (h.Present ? h.Morale : 0) + Side(u).Morale;
        // Army composition (p.314, rules 2 and 3)
        var army = S.Units.Where(x => x.Side == u.Side && x.Kind == UnitKind.Creature && !x.Summoned && x.InitialCount > 0).ToList();
        if (army.Count >= 2)
        {
            var towns = army.Select(x => x.Town).Where(t => !Towns.IsNeutral(t)).Distinct().ToList();
            if (towns.Count == 1 && army.All(x => x.Town == towns[0])) m += 1;
            else if (towns.Count <= 1) { }
            else
            {
                bool allAllied = towns.All(a => towns.All(b => Towns.Allied(a, b)));
                if (allAllied) m += towns.Count >= 3 ? -1 : 0;
                else m += towns.Count == 2 ? -1 : -2;
            }
        }
        if (h.Present && !Towns.IsNeutral(h.Town))
        {
            if (u.Town == h.Town) m += 1;
            else if (!Towns.Allied(u.Town, h.Town)) m -= 2;
        }
        // Week of the month on the base morale (exe 004ba9d0 → 00bcdd00); Balance also drops the effects (00a6acd0).
        m = MoonWeek.MoraleLuck(m, u.Town, S.Week);
        m += MicroArtifacts.Bonus(u, MicroEffect.Morale);   // exe 004ba9d0: MAE_MORALE after the week adjustment
        // Mechanical with Artificial Glory: the base is max(0, base) already (exe 004ba9d0 at 004baa6f).
        if (NeutralMorale(u)) m = Math.Max(0, m);
        int weekBase = m;
        m -= (int)Fx(u, "SPELL_SORROW");
        if (u.GetEffect(H5.Combat.Fx.Benediction) is { } prayer) m += (int)prayer.Value;
        m += (int)Fx(u, H5.Combat.Fx.RallyingCry);
        if (u.HasEffect(H5.Combat.Fx.BearRoar)) m += (int)Stats.Get("combat/Creatures/Fortress/BearRoar_MoraleBonus", 1);
        if (u.HasEffect(H5.Combat.Fx.RunicAttunement)) m += (int)Stats.Get("combat/HeroSkills/Runemage/RunicAttunement_MoraleBonus", 2);
        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) m -= (int)Fx(u, H5.Combat.Fx.BansheeHowl);
        if (u.HasEffect(LionsDemoralized)) m -= (int)Stats.Get("ArtifactsSetsEffectsConsts/Lions_2_HeroAttack_DeMorale", 2);   // exe 00989fd0 case 0x129
        if (u.HasEffect(H5.Combat.Fx.Sacrifice)) m += (int)Fx(u, H5.Combat.Fx.Sacrifice);
        m += SkillAuditH.EnemyArtifactMorale(Enemy(u.Side));   // exe 00989fd0: Nightmarish Ring, Cloak of Mourning
        if (Adjacent(u).Any(o => o.Side != u.Side && o.Has(Ab.FrightAura))) m -= 3;             // Frightful Aura (p.175)
        if (S.Living.Any(o => o.Side != u.Side && o.Has(Ab.HorrorOfDeath)))                    // Death Stare (p.173)
            m -= (int)Stats.Get("combat/Creatures/Necropolis/HorrorOfDeath_MoraleDecrease", 1);
        if (!IsUndeadLike(u) && Adjacent(u).Any(o => o.Has(Ab.WeakeningAura)))
            m -= (int)Stats.Get("combat/Creatures/Necropolis/WeakeningAuraMoraleDec", 1);
        if (MoonWeek.IsBalance(S.Week)) m = weekBase;
        // exe 00a6acd0 (00a6ae7b): a Mechanical stack returns max(0, m) before the Bravery / Aura of Bravery minimums.
        if (NeutralMorale(u)) return Math.Max(0, m);
        // Minimum-morale effects come last.
        if (u.Has(Ab.Bravery)) m = Math.Max(m, 1);
        if (Adjacent(u).Any(o => o.Side == u.Side && o.Has(Ab.AuraBravery)) || u.Has(Ab.AuraBravery))
            m = Math.Max(m, (int)Stats.Get("combat/Creatures/Dungeon/AuraOfBraveryMoraleMin", 1));
        if (NeutralMorale(u)) m = Math.Max(0, m);
        return m;
    }

    public int LuckOf(Unit u)
    {
        if (u.IsObject) return 0;
        var h = HeroOf(u);
        // Week of the month on the base luck (exe 004ba970 → 00bcdcf0); Balance also drops the effects (00a6abf0).
        int l = MoonWeek.MoraleLuck((h.Present ? h.Luck : 0) + Side(u).Luck, u.Town, S.Week);
        l += MicroArtifacts.Bonus(u, MicroEffect.Luck);   // exe 004ba970: MAE_LUCK after the week adjustment
        if (MoonWeek.IsBalance(S.Week)) return l;
        var eh = Enemy(u.Side);
        if (eh.Present && eh.HasPerk(Sk.DeadLuck)) l -= 1;
        l += SkillAuditH.EnemyArtifactLuck(eh);   // exe 00989c40: Cloak of Mourning, Jinxing Band
        l -= (int)Fx(u, "SPELL_SORROW");
        l -= (int)Fx(u, H5.Combat.Fx.EvilEye);
        l += (int)Fx(u, "FX_WHEEL_OF_FORTUNE");
        if (u.GetEffect(H5.Combat.Fx.BansheeHowl) is { } howl) l -= DeathScreamLuckPenalty(howl);
        return l;
    }

    double MagicResistanceOf(Unit u)
    {
        double r = 0;
        if (HeroOf(u).HasPerk(Sk.MagicResistance)) r += 0.15;
        if (u.Has(Ab.AuraMagicResistance) || Adjacent(u).Any(o => o.Side == u.Side && o.Has(Ab.AuraMagicResistance)))
            r += Stats.Get("combat/Creatures/Sylvan/AuraOfMagicResistance", 0.3);
        r += SpecMagicResistance(u);
        return Math.Min(0.95, r);
    }

    /// <summary>Damage multiplier for incoming spell damage (magic-proof abilities, Protection, Boneward...).</summary>
    /// <remarks>
    /// Hero perks per exe FUN_0097af50 (target side): Boneward (HERO_SKILL_SPELLPROOF_BONES) and Sap Magic
    /// (HERO_SKILL_MAGIC_CUSHION) ×0.8 only for Destructive spells (spell school field +0x88 == 0), Sap Magic only when the
    /// caster is a hero (caster vfunc 0x78); Luck of the Barbarian ×clamp(1 − MagicProofPerLuck, 0, 1) when the stack's
    /// luck is positive. <paramref name="protectionOnly"/> leaves out the damage boosts (vulnerability auras, Mark of
    /// Fire), which the exe applies to raw spell damage in FUN_00977a80 rather than to the protection share.
    /// </remarks>
    internal double SpellDamageMultiplier(Unit target, SpellDef spell, int casterSide, bool casterIsHero = true, bool protectionOnly = false)
    {
        double m = 1;
        if (target.Has(Ab.MagicProof25)) m *= 0.75;
        if (target.Has(Ab.MagicProof50)) m *= 0.5;
        if (target.Has(Ab.MagicProof75)) m *= 0.25;
        if (target.HasEffect(H5.Combat.Fx.RuneDragonform)) m *= 1 - Stats.Get("combat/Spells/Runic/DragonFormSpellPercent", 0.5);
        if (spell.Element == Elements.Fire && target.Has(Ab.FireProof50)) m *= 0.5;
        var th = HeroOf(target);
        if (th.HasPerk(Sk.Protection)) m *= 0.85;                                                // p.112
        if (Specials.Pieces(th, Specials.Dwarven) >= 2) m *= 1 - Stats.Get("ArtifactsSetsEffectsConsts/Dwarven_2_Creature_MagicProofPercents", 40) / 100.0;
        if (th.HasPerk(Sk.SapMagic) && casterIsHero && spell.School == "MAGIC_SCHOOL_DESTRUCTIVE") m *= 0.8;   // exe 0097b009
        if (th.HasPerk(Sk.Boneward) && spell.School == "MAGIC_SCHOOL_DESTRUCTIVE") m *= 0.8;                   // exe 0097afc0
        if ((th.HasPerk(Sk.FireResistance) || th.HasPerk(Sk.BarbFireResistance)) && spell.Element == Elements.Fire) m *= 0.5;
        // MAE_MAGIC_PROTECTION of the target stack's creature artifact: × (1 − value · 0.01f) (exe 0097b328).
        if (MicroArtifacts.Bonus(target, MicroEffect.MagicProtection) is var mp && mp > 0) m *= 1 - mp * (double)0.01f;
        if (th.HasPerk(Sk.LuckOfBarbarian) && LuckOf(target) > 0)                                                // exe 0097b051
            m *= Math.Clamp(1 - (double)(float)Stats.Get("combat/HeroSkills/Barbarian/LuckOfTheBarbarian_MagicProofPerLuck", 0.05), 0, 1);
        if (protectionOnly) return m;
        // Elemental gargoyle vulnerability auras (p.171)
        bool Near(string ab) => target.Has(ab) || Adjacent(target).Any(o => o.Has(ab));
        double vuln = Math.Clamp((float)Stats.Get("combat/Creatures/Academy/VulnerabilityDamageCoef", 1.5), 0f, 1000f);   // exe 00977c1c
        if (spell.Element == Elements.Fire && Near(Ab.AuraFireVuln)) m *= vuln;
        if (spell.Element == Elements.Water && Near(Ab.AuraIceVuln)) m *= vuln;
        if (spell.Element == Elements.Air && Near(Ab.AuraLightningVuln)) m *= vuln;
        if (spell.Element == Elements.Earth && Near("ABILITY_AURA_OF_EARTH_VULNERABILITY")) m *= vuln;
        if (spell.Element == Elements.Fire && target.HasEffect(H5.Combat.Fx.MarkOfFire))
            m *= Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2);
        return m;
    }

    bool ElementImmune(Unit u, string element)
    {
        if (u.GetEffect(H5.Combat.Fx.RuneElementalImmunity) is { } ri)
        {
            // exe FUN_00c546b0: the effect holds two exe element codes (1 air, 2 fire, 3 water, 4 earth).
            if (ExeElement((int)ri.Value) == element || ExeElement((int)ri.Value2) == element) return true;
        }
        return element switch
        {
            Elements.Fire => u.Has(Ab.ImmuneFire),
            Elements.Air => u.Has(Ab.ImmuneAir),
            Elements.Earth => u.Has(Ab.ImmuneEarth),
            Elements.Water => u.Has(Ab.ImmuneWater),
            _ => false,
        };
    }

    /// <summary>Blood Rage bonuses (DefaultStats combat/Creatures/Stronghold/DemonRage/Bonuses) for the unit's current rage level.</summary>
    double RageBonus(Unit u, string type)
    {
        if (u.RageLevel <= 0) return 0;
        var table = RageTable(u.CreatureId);
        if (table == null) return 0;
        double v = 0;
        for (int lvl = 1; lvl <= Math.Min(3, u.RageLevel); lvl++)
            foreach (var b in table[lvl - 1])
                if (b.Type == type) v = Math.Max(v, b.IntParam);    // later levels supersede (values are cumulative in data)
        return v;
    }

    internal sealed record RageBonusDef(string Type, int IntParam, List<string> Abilities);
    readonly Dictionary<string, List<RageBonusDef>[]?> _rageTables = new();

    List<RageBonusDef>[]? RageTable(string creature)
    {
        if (_rageTables.TryGetValue(creature, out var t)) return t;
        t = null;
        var bonuses = Stats.Node("combat/Creatures/Stronghold/DemonRage/Bonuses");
        if (bonuses != null)
            foreach (var item in bonuses.Elements("Item"))
            {
                if (item.Str("Creature") != creature) continue;
                t = new List<RageBonusDef>[3];
                for (int i = 0; i < 3; i++)
                {
                    t[i] = new();
                    var b = item.Element($"Bonus{i + 1}");
                    if (b == null) continue;
                    // A bonus is either inline fields or a list of Items.
                    var entries = b.Elements("Item").Any() ? b.Elements("Item") : new[] { b };
                    foreach (var e in entries)
                        t[i].Add(new RageBonusDef(e.Str("Type"), e.Int("IntParam"), e.StrItems("Abilities")));
                }
                break;
            }
        _rageTables[creature] = t;
        return t;
    }

    bool HasAbilityNow(Unit u, string ability)
    {
        if (u.Has(ability)) return !RageDisabled(u, ability);
        if (u.RageLevel > 0 && RageTable(u.CreatureId) is { } t)
            for (int lvl = 1; lvl <= Math.Min(3, u.RageLevel); lvl++)
                if (t[lvl - 1].Any(b => b.Type == "DEMON_RAGE_BONUS_ABILITY" && b.Abilities.Contains(ability))) return true;
        return false;
    }

    bool RageDisabled(Unit u, string ability)
    {
        if (u.RageLevel <= 0 || RageTable(u.CreatureId) is not { } t) return false;
        for (int lvl = 1; lvl <= Math.Min(3, u.RageLevel); lvl++)
            if (t[lvl - 1].Any(b => b.Type == "DEMON_RAGE_BONUS_DISABLE_ABILITY" && b.Abilities.Contains(ability))) return true;
        return false;
    }

    /// <summary>Creature caster spell power (fan manual p.303).</summary>
    public static int CreatureSpellPower(int stackSize, int weeklyGrowth)
    {
        if (stackSize <= 0) return 1;
        int g = Math.Max(1, weeklyGrowth);
        double inner = 10 + Math.Floor(10.0 * stackSize / g);
        return Math.Max(1, (int)Math.Floor(21 * Math.Log10(inner) - 22));
    }

    /// <summary>Ability trigger chance (fan manual p.315), scaled by the per-ability ProcFactor from DefaultStats.</summary>
    double ProcChance(Unit attacker, Unit target, double procFactor = 1, int? targetHpOverride = null)
    {
        double a = Math.Max(1, attacker.TotalHealth);
        double t = Math.Max(1, targetHpOverride ?? target.TotalHealth);
        double c = a > t ? 0.25 + 0.03 * (a / t) : 0.25 - 0.03 * (t / a);
        c = Math.Clamp(c, 0.05, 0.75);
        return Math.Clamp(c * procFactor, 0, 1);
    }

    bool RollProc(Unit attacker, double chance)
    {
        if (S.Rng.Chance(chance)) return true;
        // Soldier's Luck: a second chance (p.124 / p.315).
        return HeroOf(attacker).HasPerk(Sk.SoldiersLuck) && S.Rng.Chance(chance);
    }
}
