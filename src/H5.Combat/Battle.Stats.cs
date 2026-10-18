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
    bool NeutralMorale(Unit u) => u.Has(Ab.Undead) || u.Has(Ab.Elemental) || u.Has(Ab.Mechanical) || u.IsWarMachine || u.IsObject
                                  || u.HasEffect("SPELL_VAMPIRISM");
    public bool MindImmune(Unit u) => u.Has(Ab.ImmuneMind) || u.Has(Ab.Undead) || u.Has(Ab.Elemental) || u.Has(Ab.Mechanical) || u.IsWarMachine || u.IsObject;

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
        a += Fx(u, "SPELL_BLOODLUST");
        a -= Fx(u, "SPELL_WEAKNESS");                       // Suffering
        a += Fx(u, H5.Combat.Fx.Enraged);
        a += Fx(u, H5.Combat.Fx.BattleRage);
        a += Fx(u, H5.Combat.Fx.Sacrifice);
        a += Fx(u, H5.Combat.Fx.Benediction);
        a += Fx(u, H5.Combat.Fx.Battlecry);
        a -= Fx(u, H5.Combat.Fx.WeakeningStrike);
        a += Fx(u, H5.Combat.Fx.RageOfForest);
        a += Fx(u, H5.Combat.Fx.BerserkerRage);
        a += Fx(u, "FX_BLOODDRINKER");
        a += RageBonus(u, "DEMON_RAGE_BONUS_ATTACK");
        // Festering Aura of adjacent Rot Zombies: -2 attack (p.174 / DefaultStats WeakeningAuraOffenceDec).
        if (!IsUndeadLike(u) && Adjacent(u).Any(o => o.Has(Ab.WeakeningAura)))
            a -= Stats.Get("combat/Creatures/Necropolis/WeakeningAuraOffenceDec", 3);
        if (vs != null && u.Has(Ab.AntiGiant) && vs.IsLarge) a += Stats.Get("combat/Creatures/Fortress/AntiGiantAttackInc", 4);
        if (u.IsWarMachine && u.CreatureId == "WAR_MACHINE_BALLISTA" || (u.MaxShots > 0 && !u.IsWarMachine))
            a += AmmoCartAttackBonus(u.Side);
        if (h.HasPerk(Sk.OffensiveFormation)) a += FormationBonus(u, Stats.Get("combat/HeroSkills/Runemage/Offensive_MaxOffenseBonus", 10));
        return Math.Max(0, (int)Math.Floor(a));
    }

    int AmmoCartAttackBonus(int side)
    {
        if (!S.Units.Any(x => x.Alive && x.Side == side && x.CreatureId == "WAR_MACHINE_AMMO_CART")) return 0;
        // +1/+2/+3 by War Machines mastery (p.307), +4 more with Ring of Machine Affinity (exe FUN_008a4800).
        var h = S.Heroes[side];
        return (int)h.SkillLevel(Sk.WarMachines) + (HasMachineAffinityRing(h) ? 4 : 0);
    }

    bool HasAmmoCart(int side) => S.Units.Any(x => x.Alive && x.Side == side && x.CreatureId == "WAR_MACHINE_AMMO_CART");

    public int DefenceOf(Unit u, Unit? vs = null, int attackerCellsWalked = 0)
    {
        var h = HeroOf(u);
        double raw = u.BaseDefence;
        if (u.HasEffect(H5.Combat.Fx.RuneDragonform)) raw += u.BaseDefence;
        double d = raw;
        if (h.Present && !u.IsWarMachine && !u.IsObject) d += h.Defence;
        if (!u.IsWarMachine && !u.IsObject) d += Side(u).Defence;
        d += Fx(u, "SPELL_STONESKIN");
        d += Fx(u, H5.Combat.Fx.Benediction);
        d += RageBonus(u, "DEMON_RAGE_BONUS_DEFENCE");
        if (u.Has(Ab.PreparedPosition) && !u.HasMovedFromStart) d += Stats.Get("combat/Creatures/Fortress/PreparedPosition_DefenceBonus", 5);
        if (u.Has(Ab.Agility)) d += Stats.Get("combat/Creatures/Sylvan/AgilityDefenceBoostPerCell", 2) * AgilityCells(u);
        if (vs != null && u.Has(Ab.AntiGiant) && vs.IsLarge) d += Stats.Get("combat/Creatures/Fortress/AntiGiantDefenceInc", 4);
        if (h.HasPerk(Sk.DefensiveFormation)) d += FormationBonus(u, Stats.Get("combat/HeroSkills/Runemage/Defensive_MaxDefenseBonus", 10));
        double mult = 1;
        if (u.Defending)
        {
            double bonus = Stats.Get("combat/Creatures/DefenceBonusOnDefend", 0.3);
            if (h.HasPerk(Sk.HoldGround)) bonus *= Stats.Get("combat/HeroSkills/Knight/HoldGround_DefendBonusMultiplier", 2);
            mult += bonus;
        }
        if (u.HasEffect(H5.Combat.Fx.TakeRoots))
        {
            double tr = Stats.Get("combat/Creatures/Sylvan/DefenceBonusOnTakeRoots", 0.5);
            if (h.HasPerk(Sk.HoldGround)) tr *= Stats.Get("combat/HeroSkills/Knight/HoldGround_TakeRootsBonusMultiplier", 2);
            mult += tr;
        }
        d *= mult;
        // Decreases (Armored creatures are immune to defence reduction, p.171).
        if (!u.Has(Ab.Armored))
        {
            d -= Fx(u, "SPELL_DISRUPTING_RAY");
            d -= u.GetEffect("SPELL_CURSE")?.Value2 ?? 0;      // Maledictor: Weakness also lowers Defense
            d -= Fx(u, H5.Combat.Fx.WeakeningStrike);
            if (!IsUndeadLike(u) && Adjacent(u).Any(o => o.Has(Ab.WeakeningAura)))
                d -= Stats.Get("combat/Creatures/Necropolis/WeakeningAuraDeffenceDec", 2);
            if (u.HasEffect(H5.Combat.Fx.ArmorCrushed)) d *= 0.5;   // Master of Fire (p.113)
            if (u.HasEffect(H5.Combat.Fx.RageOfForest)) d -= Fx(u, H5.Combat.Fx.RageOfForest);
            if (u.HasEffect(H5.Combat.Fx.BerserkerRage)) d = 0;
            // Rider Charge: -20% of target defence per cell walked (p.179).
            if (vs != null && vs.Has(Ab.RiderCharge) && attackerCellsWalked > 0)
                d *= Math.Max(0, 1 - Stats.Get("combat/Creatures/Dungeon/RiderCharge_DefenceReducePerPathCell", 0.2) * attackerCellsWalked);
        }
        return Math.Max(0, (int)Math.Floor(d));
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

    int AgilityCells(Unit u) => u.HasActed ? u.CellsWalked : u.BaseSpeed;   // first turn counts as full move (p.171)

    public double InitiativeOf(Unit u)
    {
        var h = HeroOf(u);
        double mult = 1 + Fx(u, "SPELL_HASTE") - Fx(u, "SPELL_SLOW");
        if (u.HasEffect(H5.Combat.Fx.Benediction)) mult += Stats.Get("combat/HeroSkills/Knight/Prayer_InitiativeBonus", 0.1);
        if (u.HasEffect(H5.Combat.Fx.Wound)) mult -= Stats.Get("combat/Creatures/Fortress/Wound_InitiativeDecrease", 0.2);
        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) mult -= Stats.Get("combat/HeroSkills/Necromancer/DeathScream_InitiativePenalty", 0.1);
        var eh = Enemy(u.Side);
        if (eh.Present && u.Flying && (eh.HasPerk(Sk.StormWind) || eh.HasPerk(Sk.BarbStormWind)))
            mult -= Stats.Get("combat/HeroSkills/Ranger/StormWind_InitiativePenalty", 0.2);
        if (eh.Present && u.MaxShots > 0 && !u.IsWarMachine && (eh.HasPerk(Sk.FogVeil) || eh.HasPerk(Sk.BarbFogVeil)))
            mult -= Stats.Get("combat/HeroSkills/Ranger/FogVeil_InitiativePenalty", 0.1);
        if (u.HasEffect(H5.Combat.Fx.Dash)) mult *= Stats.Get("combat/Creatures/Academy/Dash_InitiativeFactor", 2);
        if (u.HasEffect("SPELL_HYPNOTIZE")) mult *= Math.Min(1, Fx(u, "SPELL_HYPNOTIZE"));
        double init = u.BaseInitiative * Math.Max(0.1, mult);
        init += Fx(u, H5.Combat.Fx.BattleRage) > 0 ? Stats.Get("combat/Creatures/Fortress/BattleRage_InitiativeBonus", 2) : 0;
        if (u.HasEffect(H5.Combat.Fx.RageOfForest)) init += Stats.Get("combat/Creatures/Sylvan/RageOfTheForestInitiativeInc", 5);
        if (Adjacent(u).Any(o => o.Side == u.Side && o.Has(Ab.PresenceOfCommander))) init += 1;
        init += RageBonus(u, "DEMON_RAGE_BONUS_INITIATIVE");
        if (u.HasEffect(H5.Combat.Fx.Frozen) || u.HasEffect(FxSeducing)) return 0;   // a seducing stack's ATB is frozen (p.181)
        return Math.Max(0.1, init);
    }

    double HeroInitiative(int side) => Stats.Get("HeroInitiative", 10) * HeroInitiativeMultiplier(S.Heroes[side]);

    public int SpeedOf(Unit u)
    {
        if (u.IsWarMachine || u.IsObject) return 0;
        var h = HeroOf(u);
        double sp = u.BaseSpeed;
        if (h.HasPerk(Sk.AuraOfSwiftness)) sp += 1;
        if (u.HasEffect(H5.Combat.Fx.Battlecry)) sp += Stats.Get("combat/Spells/Warcries/Battlecry_SpeedBonus/Base", 1);
        sp += RageBonus(u, "DEMON_RAGE_BONUS_SPEED");
        var eh = Enemy(u.Side);
        // Storm Wind slows enemy flyers by StormWind_SpeedPenalty; results below 2 become 1 (exe 008a1e50).
        if (eh.Present && u.Flying && (eh.HasPerk(Sk.StormWind) || eh.HasPerk(Sk.BarbStormWind)))
        {
            sp -= Stats.Get("combat/HeroSkills/Ranger/StormWind_SpeedPenalty", 2);
            if (sp < 2) sp = 1;
        }
        if (eh.Present && Specials.Pieces(eh, Specials.Necromancers) >= 2) sp -= (int)Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_2_EnemyCreature_DeSpeed", 1);
        if (u.HasEffect(H5.Combat.Fx.RuneCharge)) sp *= 2;
        if (u.HasEffect(H5.Combat.Fx.Wound)) sp *= 1 - Stats.Get("combat/Creatures/Fortress/Wound_SpeedDecrease", 0.4);
        return Math.Max(1, (int)Math.Floor(sp));
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
        if (u.HasEffect(H5.Combat.Fx.BearRoar)) hp *= 1 + Stats.Get("combat/Creatures/Fortress/BearRoar_HealthBonus", 0.1);
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
        m -= (int)Fx(u, "SPELL_SORROW");
        if (u.GetEffect(H5.Combat.Fx.Benediction) is { } prayer) m += (int)prayer.Value;
        m += (int)Fx(u, H5.Combat.Fx.RallyingCry);
        if (u.HasEffect(H5.Combat.Fx.BearRoar)) m += (int)Stats.Get("combat/Creatures/Fortress/BearRoar_MoraleBonus", 1);
        if (u.HasEffect(H5.Combat.Fx.RunicAttunement)) m += (int)Stats.Get("combat/HeroSkills/Runemage/RunicAttunement_MoraleBonus", 2);
        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) m -= (int)Fx(u, H5.Combat.Fx.BansheeHowl);
        if (u.HasEffect(H5.Combat.Fx.Sacrifice)) m += (int)Fx(u, H5.Combat.Fx.Sacrifice);
        if (Adjacent(u).Any(o => o.Side != u.Side && o.Has(Ab.FrightAura))) m -= 3;             // Frightful Aura (p.175)
        if (S.Living.Any(o => o.Side != u.Side && o.Has(Ab.HorrorOfDeath)))                    // Death Stare (p.173)
            m -= (int)Stats.Get("combat/Creatures/Necropolis/HorrorOfDeath_MoraleDecrease", 1);
        if (!IsUndeadLike(u) && Adjacent(u).Any(o => o.Has(Ab.WeakeningAura)))
            m -= (int)Stats.Get("combat/Creatures/Necropolis/WeakeningAuraMoraleDec", 1);
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
        int l = (h.Present ? h.Luck : 0) + Side(u).Luck;
        var eh = Enemy(u.Side);
        if (eh.Present && eh.HasPerk(Sk.DeadLuck)) l -= 1;
        l -= (int)Fx(u, "SPELL_SORROW");
        l -= (int)Fx(u, H5.Combat.Fx.EvilEye);
        l += (int)Fx(u, "FX_WHEEL_OF_FORTUNE");
        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) l -= 1;
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
    double SpellDamageMultiplier(Unit target, SpellDef spell, int casterSide)
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
        if (th.HasPerk(Sk.SapMagic)) m *= 0.8;                                                   // p.114
        if (th.HasPerk(Sk.Boneward) && spell.School == "MAGIC_SCHOOL_DESTRUCTIVE" && IsUndeadLike(target)) m *= 0.8;
        if ((th.HasPerk(Sk.FireResistance) || th.HasPerk(Sk.BarbFireResistance)) && spell.Element == Elements.Fire) m *= 0.5;
        if (th.HasPerk(Sk.LuckOfBarbarian))
            m *= Math.Max(0, 1 - Stats.Get("combat/HeroSkills/Barbarian/LuckOfTheBarbarian_MagicProofPerLuck", 0.05) * Math.Max(0, LuckOf(target)));
        // Elemental gargoyle vulnerability auras (p.171)
        bool Near(string ab) => target.Has(ab) || Adjacent(target).Any(o => o.Has(ab));
        if (spell.Element == Elements.Fire && Near(Ab.AuraFireVuln)) m *= 1.5;
        if (spell.Element == Elements.Water && Near(Ab.AuraIceVuln)) m *= 1.5;
        if (spell.Element == Elements.Air && Near(Ab.AuraLightningVuln)) m *= 1.5;
        if (spell.Element == Elements.Fire && target.HasEffect(H5.Combat.Fx.MarkOfFire))
            m *= Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2);
        return m;
    }

    bool ElementImmune(Unit u, string element)
    {
        if (u.GetEffect(H5.Combat.Fx.RuneElementalImmunity) is { } ri)
        {
            string[] els = { Elements.Fire, Elements.Water, Elements.Air, Elements.Earth };
            if (els[(int)ri.Value] == element || els[(int)ri.Value2] == element) return true;
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
