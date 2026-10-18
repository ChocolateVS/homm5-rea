using H5.Core.Data;

namespace H5.Combat;

// Spells cast by heroes and creatures (fan manual p.183-194). Numeric parameters come from each
// spell's xdb (damage/duration arrays per mastery); the meaning of those parameters is spell-specific.
public sealed partial class Battle
{
    public enum SpellTargeting { None, Unit, Cell, UnitAndCell }

    /// <summary>Mass variants granted by the matching "Master of" perk: base spell → (mass spell, perk).</summary>
    static readonly Dictionary<string, (string Mass, string Perk)> MassSpells = new()
    {
        ["SPELL_CURSE"] = ("SPELL_MASS_CURSE", Sk.MasterCurses),
        ["SPELL_WEAKNESS"] = ("SPELL_MASS_WEAKNESS", Sk.MasterCurses),
        ["SPELL_SLOW"] = ("SPELL_MASS_SLOW", Sk.MasterMind),
        ["SPELL_FORGETFULNESS"] = ("SPELL_MASS_FORGETFULNESS", Sk.MasterMind),
        ["SPELL_DISRUPTING_RAY"] = ("SPELL_MASS_DISRUPTING_RAY", Sk.MasterPain),
        ["SPELL_PLAGUE"] = ("SPELL_MASS_PLAGUE", Sk.MasterPain),
        ["SPELL_BLESS"] = ("SPELL_MASS_BLESS", Sk.MasterBlessing),
        ["SPELL_DISPEL"] = ("SPELL_MASS_DISPEL", Sk.MasterBlessing),
        ["SPELL_STONESKIN"] = ("SPELL_MASS_STONESKIN", Sk.MasterAbjuration),
        ["SPELL_DEFLECT_ARROWS"] = ("SPELL_MASS_DEFLECT_ARROWS", Sk.MasterAbjuration),
        ["SPELL_BLOODLUST"] = ("SPELL_MASS_BLOODLUST", Sk.MasterWrath),
        ["SPELL_HASTE"] = ("SPELL_MASS_HASTE", Sk.MasterWrath),
    };

    static readonly Dictionary<string, string> MassToBase = MassSpells.ToDictionary(kv => kv.Value.Mass, kv => kv.Key);
    static readonly HashSet<string> AreaMass = new() { "SPELL_MASS_DISRUPTING_RAY", "SPELL_MASS_PLAGUE", "SPELL_MASS_DISPEL" }; // 4x4 areas (p.186, p.190)

    static readonly HashSet<string> MindSpells = new() { "SPELL_BLIND", "SPELL_FORGETFULNESS", "SPELL_MASS_FORGETFULNESS", "SPELL_BERSERK", "SPELL_HYPNOTIZE" };
    static readonly HashSet<string> LivingOnly = new()
    {
        "SPELL_PLAGUE", "SPELL_MASS_PLAGUE", "SPELL_BLIND", "SPELL_FORGETFULNESS", "SPELL_MASS_FORGETFULNESS", "SPELL_BERSERK",
        "SPELL_HYPNOTIZE", "SPELL_WASP_SWARM", "SPELL_REGENERATION",
    };

    static readonly Dictionary<string, string> Opposites = new()
    {
        ["SPELL_HASTE"] = "SPELL_SLOW", ["SPELL_SLOW"] = "SPELL_HASTE",
        ["SPELL_BLESS"] = "SPELL_CURSE", ["SPELL_CURSE"] = "SPELL_BLESS",
        ["SPELL_BLOODLUST"] = "SPELL_WEAKNESS", ["SPELL_WEAKNESS"] = "SPELL_BLOODLUST",
    };

    static string BaseSpell(string id) => MassToBase.TryGetValue(id, out var b) ? b : id;
    static bool IsMass(string id) => MassToBase.ContainsKey(id);

    public SpellTargeting TargetingOf(SpellDef sp)
    {
        string id = sp.Id;
        if (IsMass(id)) return AreaMass.Contains(id) ? SpellTargeting.Cell : SpellTargeting.None;
        return id switch
        {
            "SPELL_ARMAGEDDON" or "SPELL_HOLY_WORD" or "SPELL_UNHOLY_WORD" or "SPELL_SUMMON_ELEMENTALS" or "SPELL_CONJURE_PHOENIX"
                or "SPELL_EARTHQUAKE" or "SPELL_WARCRY_RALLING_CRY" or "SPELL_WARCRY_BATTLECRY" => SpellTargeting.None,
            "SPELL_FIREBALL" or "SPELL_METEOR_SHOWER" or "SPELL_STONE_SPIKES" or "SPELL_FROST_RING" or "SPELL_FIREWALL"
                or "SPELL_LAND_MINE" or "SPELL_ARCANE_CRYSTAL" or "SPELL_BLADE_BARRIER" or "SPELL_SUMMON_HIVE" => SpellTargeting.Cell,
            "SPELL_TELEPORT" => SpellTargeting.UnitAndCell,
            _ => SpellTargeting.Unit,
        };
    }

    // ------------------------------------------------------------------ spell books

    /// <summary>Spells the hero may cast now, including mass variants unlocked by perks.</summary>
    public IEnumerable<string> HeroSpells(HeroState h)
    {
        var set = new List<string>();
        foreach (var s in h.Spells)
        {
            var sp = Db.Spell(s);
            if (sp == null || !sp.IsCombatSchool) continue;
            set.Add(s);
            if (MassSpells.TryGetValue(s, out var mass) && h.HasPerk(mass.Perk)) set.Add(mass.Mass);
        }
        // Perk-granted spells (Power of Speed / Power of Endurance / Teleport Assault, p.108/p.111/p.123).
        if (h.HasPerk(Sk.PowerOfSpeed)) set.Add("SPELL_MASS_HASTE");
        if (h.HasPerk(Sk.PowerOfEndurance)) set.Add("SPELL_MASS_STONESKIN");
        if (h.HasPerk(Sk.TeleportAssault)) set.Add("SPELL_TELEPORT");
        if (h.HasPerk(Sk.RuneArmour)) set.Add("SPELL_CELESTIAL_SHIELD");
        if (h.SkillLevel(Sk.Runelore) > Mastery.None)
            foreach (var r in Db.Spells.Values.Where(x => x.School == "MAGIC_SCHOOL_RUNIC"))
                if (h.Spells.Contains(r.Id)) set.Add(r.Id);
        return set.Distinct();
    }

    public Mastery HeroMastery(HeroState h, SpellDef sp)
    {
        Mastery m = sp.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => h.SkillLevel(Sk.Destructive),
            "MAGIC_SCHOOL_DARK" => h.SkillLevel(Sk.Dark),
            "MAGIC_SCHOOL_LIGHT" => h.SkillLevel(Sk.Light),
            "MAGIC_SCHOOL_SUMMONING" => h.SkillLevel(Sk.Summoning),
            "MAGIC_SCHOOL_RUNIC" => h.SkillLevel(Sk.Runelore),
            "MAGIC_SCHOOL_WARCRIES" => h.SkillLevel(Sk.Shout),
            _ => Mastery.None,
        };
        if (m > Mastery.Expert) m = Mastery.Expert;
        // Power of Speed / Endurance cast at Advanced unless Expert in Light Magic.
        if ((sp.Id == "SPELL_MASS_HASTE" && h.HasPerk(Sk.PowerOfSpeed) || sp.Id == "SPELL_MASS_STONESKIN" && h.HasPerk(Sk.PowerOfEndurance)) && m < Mastery.Advanced)
            m = Mastery.Advanced;
        // Shatter magic of the enemy hero (p.126-129).
        var enemy = Enemy(h.Side);
        string shatter = sp.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.ShatterDestructive, "MAGIC_SCHOOL_DARK" => Sk.ShatterDark,
            "MAGIC_SCHOOL_LIGHT" => Sk.ShatterLight, "MAGIC_SCHOOL_SUMMONING" => Sk.ShatterSummoning, _ => "",
        };
        if (enemy.Present && shatter.Length > 0)
        {
            var sm = enemy.SkillLevel(shatter);
            int drop = 0;
            if (sm == Mastery.Basic) drop = sp.Level <= 3 ? 1 : 0;
            else if (sm == Mastery.Advanced) drop = sp.Level <= 3 ? 2 : sp.Level == 4 ? 1 : 0;
            else if (sm >= Mastery.Expert) drop = sp.Level <= 3 ? 2 : 1;
            m = (Mastery)Math.Max(0, (int)m - drop);
        }
        return m;
    }

    /// <summary>Spell power reduction from enemy shatter below "None" mastery (p.126).</summary>
    double ShatterPowerFactor(HeroState h, SpellDef sp)
    {
        var enemy = Enemy(h.Side);
        if (!enemy.Present) return 1;
        string shatter = sp.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.ShatterDestructive, "MAGIC_SCHOOL_DARK" => Sk.ShatterDark,
            "MAGIC_SCHOOL_LIGHT" => Sk.ShatterLight, "MAGIC_SCHOOL_SUMMONING" => Sk.ShatterSummoning, _ => "",
        };
        if (shatter.Length == 0 || enemy.SkillLevel(shatter) == Mastery.None) return 1;
        int raw = Math.Min(3, (int)(sp.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => h.SkillLevel(Sk.Destructive), "MAGIC_SCHOOL_DARK" => h.SkillLevel(Sk.Dark),
            "MAGIC_SCHOOL_LIGHT" => h.SkillLevel(Sk.Light), _ => h.SkillLevel(Sk.Summoning),
        }));
        var sm = enemy.SkillLevel(shatter);
        int drop = sm == Mastery.Basic ? (sp.Level <= 3 ? 1 : 0) : sm == Mastery.Advanced ? (sp.Level <= 3 ? 2 : sp.Level == 4 ? 1 : 0) : (sp.Level <= 3 ? 2 : 1);
        int below = drop - raw;
        return below >= 2 ? 0.5 : below == 1 ? 0.75 : 1;
    }

    public double HeroSpellPower(HeroState h, SpellDef sp)
    {
        double p = h.SpellPower;
        string id = BaseSpell(sp.Id);
        if (h.HasPerk(Sk.Twilight) && sp.School is "MAGIC_SCHOOL_DARK" or "MAGIC_SCHOOL_LIGHT") p += 3;
        if (h.HasPerk(Sk.FallenKnight) && sp.School == "MAGIC_SCHOOL_DARK") p += 5;
        if (h.HasPerk(Sk.MasterWrath) && id == "SPELL_HOLY_WORD") p += 4;
        if (h.HasPerk(Sk.MasterPain) && id == "SPELL_UNHOLY_WORD") p += 4;
        if (h.HasPerk(Sk.MasterLife) && id is "SPELL_MAGIC_FIST" or "SPELL_ANIMATE_DEAD") p += 4;
        if (h.HasPerk(Sk.MasterEarthblood) && id is "SPELL_LAND_MINE" or "SPELL_EARTHQUAKE") p += 4;
        if (h.HasPerk(Sk.MasterConjuration) && id is "SPELL_CONJURE_PHOENIX" or "SPELL_SUMMON_ELEMENTALS") p += 4;
        if (h.HasPerk(Sk.RuneArmour) && id == "SPELL_CELESTIAL_SHIELD") p += Stats.Get("combat/HeroSkills/Runemage/RunicArmor_SpellpowerBonus", 4);
        var e = Enemy(h.Side);
        if (e.Present)
        {
            if (e.HasPerk(Sk.MightOverMagic)) p -= e.SpellPower / 2.0;
            if (e.HasPerk(Sk.ShrugDarkness) && sp.School == "MAGIC_SCHOOL_DARK") p *= Stats.Get("combat/HeroSkills/Runemage/ShrugDarkness_EnemySpellpowerMultiplier", 0.5);
            string weaken = sp.School switch
            {
                "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.WeakenDestructive, "MAGIC_SCHOOL_DARK" => Sk.WeakenDark,
                "MAGIC_SCHOOL_LIGHT" => Sk.WeakenLight, "MAGIC_SCHOOL_SUMMONING" => Sk.WeakenSummoning, _ => "",
            };
            if (weaken.Length > 0 && e.HasPerk(weaken)) p *= 1 - Stats.Get("combat/HeroSkills/Barbarian/WeakenMagic_SpellPowerDecrease", 0.25);
        }
        p *= ShatterPowerFactor(h, sp);
        return Math.Max(0, p);
    }

    public int HeroManaCost(HeroState h, SpellDef sp)
    {
        if (sp.School == "MAGIC_SCHOOL_RUNIC") return 0;
        double c = sp.ManaCost;
        if (h.HasPerk(Sk.ArcaneTraining)) c *= 0.8;                                      // p.131
        if (S.LivingOf(h.Side).Any(u => u.Has(Ab.EnergyChannel))) c *= 0.75;            // p.174
        if (sp.School == "MAGIC_SCHOOL_WARCRIES" && h.HasPerk(Sk.ShoutTraining)) c *= 1 - Stats.Get("combat/HeroSkills/Barbarian/VoiceTraining_WarcryCostDiscount", 0.2);
        var e = Enemy(h.Side);
        if (e.Present)
        {
            if (sp.School == "MAGIC_SCHOOL_DARK" && e.HasPerk(Sk.SealOfDarkness)) c *= 2;
            if (sp.School == "MAGIC_SCHOOL_LIGHT" && e.HasPerk(Sk.SuppressLight)) c *= 2;
            string corrupt = sp.School switch
            {
                "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.CorruptDestructive, "MAGIC_SCHOOL_DARK" => Sk.CorruptDark,
                "MAGIC_SCHOOL_LIGHT" => Sk.CorruptLight, "MAGIC_SCHOOL_SUMMONING" => Sk.CorruptSummoning, _ => "",
            };
            if (corrupt.Length > 0 && e.HasPerk(corrupt)) c *= 1 + Stats.Get("combat/HeroSkills/Barbarian/CorruptMagic_SpellCostIncrease", 0.5);
        }
        return Math.Max(1, (int)Math.Ceiling(c - 1e-9));
    }

    int CreatureManaCost(Unit u, SpellDef sp)
    {
        double c = sp.ManaCost;
        var h = HeroOf(u);
        if (h.HasPerk(Sk.RefinedMana) || h.HasPerk(Sk.BarbRefinedMana)) c *= 0.5;       // p.120
        return Math.Max(1, (int)Math.Ceiling(c - 1e-9));
    }

    bool CanCastWarcry(HeroState h, SpellDef sp)
    {
        if (sp.School != "MAGIC_SCHOOL_WARCRIES") return true;
        int need = sp.Level switch { 1 => 2, 2 => 6, _ => 11 };   // required hero level (p.185)
        return h.Level >= need;
    }

    // ------------------------------------------------------------------ commands

    string? DoCast(CastSpellCmd c)
    {
        var sp = Db.Spell(c.Spell);
        if (sp == null) return "unknown spell";
        if (Actor.IsHero(c.ActorId))
        {
            if (Actor.HeroSide(c.ActorId) != c.Side) return "not your hero";
            if (CheckHeroActive(c, out var h) is { } e) return e;
            if (!HeroSpells(h).Contains(sp.Id)) return "spell not known";
            if (!CanCastWarcry(h, sp)) return "hero level too low for this warcry";
            int cost = HeroManaCost(h, sp);
            if (h.Mana < cost) return "not enough mana";
            var mastery = HeroMastery(h, sp);
            double power = HeroSpellPower(h, sp);
            if (sp.School == "MAGIC_SCHOOL_RUNIC" && RuneError(h, sp, c.TargetId) is { } re) return re;
            if (ValidateSpellTarget(c.ActorId, h.Side, sp, mastery, c.TargetId, c.Cell) is { } te) return te;
            h.Mana -= cost;
            Emit(new ManaChanged(c.ActorId, h.Mana));
            var target = c.TargetId is int tid ? S.Unit(tid) : null;
            ResolveSpell(c.ActorId, h.Side, sp, mastery, power, target, c.Cell, fromHero: true);
            if (sp.School == "MAGIC_SCHOOL_RUNIC") { h.AbilityUses[$"{sp.Id}:{c.TargetId}"] = 1; TapRunes(h); }
            double reset = HeroCastAtbReset(h, sp);
            EndAction(c.ActorId, reset, false);
            return null;
        }
        if (CheckActive(c, c.ActorId, out var u) is { } err) return err;
        var known = u.Spells.Find(s => s.Spell == sp.Id);
        if (known == null) return "creature does not know this spell";
        int ucost = CreatureManaCost(u, sp);
        if (u.Mana < ucost) return "not enough mana";
        int cp = CreatureSpellPower(u.Count, Math.Max(1, u.WeeklyGrowth));
        if (ValidateSpellTarget(u.Id, Controller(u), sp, known.Mastery, c.TargetId, c.Cell) is { } te2) return te2;
        u.Mana -= ucost;
        Emit(new ManaChanged(u.Id, u.Mana));
        RageEvent(u, "CastSpellRP");
        ResolveSpell(u.Id, Controller(u), sp, known.Mastery, cp, c.TargetId is int t2 ? S.Unit(t2) : null, c.Cell, fromHero: false);
        ManaBurst(u);
        EndAction(u.Id, 0);
        return null;
    }

    string? RuneError(HeroState h, SpellDef sp, int? target)
    {
        int need = sp.Level <= 2 ? 1 : sp.Level <= 4 ? 2 : 3;   // Runelore circles (p.143)
        if ((int)h.SkillLevel(Sk.Runelore) < need) return "runelore mastery too low";
        if (h.AbilityUses.ContainsKey($"{sp.Id}:{target}") && !h.HasPerk(Sk.StrongRune)) return "rune already used on this stack";
        if (sp.Id == "SPELL_RUNE_OF_BERSERKING" && target is int t && S.Unit(t) is { } tu && tu.Count >= tu.InitialCount)
            return "rune of berserking requires casualties in the stack";
        return null;
    }

    void TapRunes(HeroState h)
    {
        if (!h.HasPerk(Sk.TapRunes)) return;
        h.Mana = Math.Min(h.MaxMana, h.Mana + (int)(h.Knowledge * Stats.Get("combat/HeroSkills/Runemage/TapRunes_ManaRestorePerKnowledge", 0.5)));
        Emit(new ManaChanged(Actor.Hero(h.Side), h.Mana));
    }

    double HeroCastAtbReset(HeroState h, SpellDef sp)
    {
        double reset;
        if (IsMass(sp.Id) && h.HasPerk(MassSpells[MassToBase[sp.Id]].Perk)) reset = 0.5;   // p.313
        else
        {
            var mastery = sp.School == "MAGIC_SCHOOL_WARCRIES" ? h.SkillLevel(Sk.Shout) : h.SkillLevel(Sk.Sorcery);
            reset = Math.Min(3, (int)mastery) * 0.1;                                           // p.312
        }
        var e = Enemy(h.Side);
        if (e.Present && (e.HasPerk(Sk.Distract) || e.HasPerk(Sk.BarbDistract))) reset -= Stats.Get("combat/HeroSkills/Runemage/Distract_EnemyHeroInitiativeDrop", 0.15);
        string detain = sp.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.DetainDestructive, "MAGIC_SCHOOL_DARK" => Sk.DetainDark,
            "MAGIC_SCHOOL_LIGHT" => Sk.DetainLight, "MAGIC_SCHOOL_SUMMONING" => Sk.DetainSummoning, _ => "",
        };
        if (e.Present && detain.Length > 0 && e.HasPerk(detain)) reset -= Stats.Get("combat/HeroSkills/Barbarian/DetainMagic_CasterExtraATBDrop", 0.25);
        return reset;
    }

    void ManaBurst(Unit caster)
    {
        var e = Enemy(caster.Side);
        if (caster.Alive && e.Present && (e.HasPerk(Sk.ManaBurst) || e.HasPerk(Sk.BarbManaBurst)))
            DealElementalDamage(caster, (int)(Stats.Get("combat/HeroSkills/Ranger/SunFireDamagePerHeroLevel", 10) * e.Level), Elements.Fire, Actor.Hero(e.Side), "MANA_BURST");
    }

    public string? ValidateSpellTarget(int caster, int side, SpellDef sp, Mastery m, int? targetId, Cell? cell)
    {
        var tg = TargetingOf(sp);
        if (tg is SpellTargeting.Cell or SpellTargeting.UnitAndCell)
        {
            if (cell is not Cell c || !S.InBounds(c)) return "spell needs a target cell";
            if (sp.Id is "SPELL_ARCANE_CRYSTAL" or "SPELL_SUMMON_HIVE" && (S.At(c) != null || S.Obstacles.Contains(c))) return "cell occupied";
        }
        if (tg is SpellTargeting.Unit or SpellTargeting.UnitAndCell)
        {
            if (targetId is not int tid || S.Unit(tid) is not { } t || (!t.Alive && !sp.CanSelectDead && BaseSpell(sp.Id) is not ("SPELL_RESURRECT" or "SPELL_ANIMATE_DEAD")))
                return "spell needs a target unit";
            bool friendly = Controller(t) == side;
            string target = sp.Target;
            if (target == "TARGET_HOSTILE" && friendly && sp.Id != "SPELL_DISPEL") return "spell must target an enemy";
            if (target == "TARGET_FRIEND" && !friendly) return "spell must target a friendly stack";
            if (t.IsObject) return "invalid target";
            if (t.HasEffect(H5.Combat.Fx.Invisible) && !friendly) return "target is invisible";
            if (!t.Alive && sp.Id is not ("SPELL_RESURRECT" or "SPELL_ANIMATE_DEAD")) return "target is dead";
            if (sp.Id == "SPELL_RESURRECT" && (t.Has(Ab.Elemental) || t.Has(Ab.Mechanical) || IsUndeadLike(t))) return "cannot resurrect this creature";
            if (sp.Id == "SPELL_ANIMATE_DEAD" && t.Count >= t.InitialCount && t.TopHealth >= t.MaxHealthCache) return "nothing to raise";
            if (sp.Id == "SPELL_PHANTOM")
            {
                int maxTier = (int)sp.DamageAt(m).Base;
                if (t.Tier > maxTier || t.IsClone || S.Units.Any(x => x.IsClone && x.Alive && x.Side == side)) return "cannot clone this stack";
            }
            if (sp.Id == "SPELL_HYPNOTIZE" && t.Tier >= 7 && m < Mastery.Expert) return "needs expert dark magic for tier 7";
            if (sp.Id == "SPELL_TELEPORT")
            {
                if (cell is not Cell to || !Fits(t, to)) return "teleport destination blocked";
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ resolution

    /// <summary>Applies a spell. Caster is an actor id (hero or unit); side is the caster's controlling side.</summary>
    void ResolveSpell(int caster, int side, SpellDef sp, Mastery m, double power, Unit? target, Cell? cell, bool fromHero)
    {
        string id = BaseSpell(sp.Id);
        var baseDef = Db.Spell(id) ?? sp;
        var affected = new List<int>();
        var hero = fromHero ? S.Heroes[side] : null;
        int casterLevel = fromHero ? S.Heroes[side].Level : (S.Unit(caster)?.Level ?? 1);
        Emit(new SpellCast(caster, sp.Id, target?.Id, cell, affected));

        // Mass spells: all stacks of one side, or a 4x4 area.
        if (IsMass(sp.Id))
        {
            bool hostile = baseDef.Target == "TARGET_HOSTILE";
            IEnumerable<Unit> victims = AreaMass.Contains(sp.Id) && cell is Cell ac
                ? S.Living.Where(u => !u.IsObject && u.Cells().Any(x => x.X >= ac.X - 1 && x.X <= ac.X + 2 && x.Y >= ac.Y - 1 && x.Y <= ac.Y + 2))
                : S.Living.Where(u => !u.IsObject && !u.IsWarMachine && (hostile ? Controller(u) != side : Controller(u) == side));
            if (AreaMass.Contains(sp.Id) && sp.Id != "SPELL_MASS_DISPEL") victims = victims.Where(u => Controller(u) != side);
            foreach (var v in victims.ToList())
                if (ApplyEffectSpell(caster, side, baseDef, m, power, v, casterLevel, hero)) affected.Add(v.Id);
            return;
        }

        switch (id)
        {
            // ---- direct damage, single target
            case "SPELL_MAGIC_ARROW":
            case "SPELL_LIGHTNING_BOLT":
            case "SPELL_ICE_BOLT":
            case "SPELL_IMPLOSION":
            case "SPELL_MAGIC_FIST":
            case "SPELL_WASP_SWARM":
            case "SPELL_DEEP_FREEZE":
            case "SPELL_DIVINE_VENGEANCE":
            {
                if (target == null) return;
                double dmg = baseDef.DamageAt(m).Eval(power);
                if (id == "SPELL_DIVINE_VENGEANCE") dmg *= Math.Sqrt(target.Frags);                        // p.191
                if (SpellHits(side, baseDef, target, hostile: true, hero))
                {
                    affected.Add(target.Id);
                    DealSpellDamage(target, baseDef, dmg, side, ignoreResist: id == "SPELL_MAGIC_FIST", hero: hero, mastery: m);
                    if (target.Alive) SpellSideEffects(id, target, m, power, hero, first: true, side);
                }
                break;
            }
            // ---- area damage
            case "SPELL_FIREBALL":
            case "SPELL_METEOR_SHOWER":
            case "SPELL_STONE_SPIKES":
            case "SPELL_FROST_RING":
            {
                if (cell is not Cell c) return;
                var cells = AreaCells(id, c);
                double dmg = baseDef.DamageAt(m).Eval(power);
                var victims = S.Living.Where(u => !u.IsObject && u.Cells().Any(cells.Contains)).ToList();
                foreach (var v in victims)
                {
                    if (!SpellHits(side, baseDef, v, hostile: true, hero, area: true)) continue;
                    affected.Add(v.Id);
                    DealSpellDamage(v, baseDef, dmg, side, false, hero, m);
                    if (v.Alive) SpellSideEffects(id, v, m, power, hero, first: true, side, victims.Count);
                }
                break;
            }
            case "SPELL_CHAIN_LIGHTNING":
            {
                if (target == null) return;
                double dmg = baseDef.DamageAt(m).Eval(power);
                var cur = target;
                var hitIds = new HashSet<int>();
                for (int i = 0; i < 4 && cur != null; i++)
                {
                    hitIds.Add(cur.Id);
                    if (SpellHits(side, baseDef, cur, hostile: true, hero, area: i > 0))
                    {
                        affected.Add(cur.Id);
                        DealSpellDamage(cur, baseDef, dmg, side, false, hero, m);
                        if (cur.Alive && i == 0) SpellSideEffects(id, cur, m, power, hero, true, side);
                    }
                    dmg /= 2;
                    var from = cur;
                    // The chain jumps to the nearest stack, friend or foe (p.188).
                    cur = S.Living.Where(u => !u.IsObject && !hitIds.Contains(u.Id)).OrderBy(u => Distance(u, from)).ThenBy(u => u.Id).FirstOrDefault();
                }
                break;
            }
            case "SPELL_ARMAGEDDON":
            {
                double dmg = baseDef.DamageAt(m).Eval(power);
                double rock = baseDef.DurationAt(m).Eval(power);   // central 4x4 physical rock damage (p.189)
                int cx = S.Width / 2 - 2, cy = S.Height / 2 - 2;
                foreach (var v in S.Living.Where(u => !u.IsObject).ToList())
                {
                    if (SpellHits(side, baseDef, v, hostile: true, hero, area: true))
                    {
                        affected.Add(v.Id);
                        DealSpellDamage(v, baseDef, dmg, side, false, hero, m);
                        if (v.Alive) SpellSideEffects(id, v, m, power, hero, true, side);
                    }
                    if (v.Alive && v.Cells().All(x => x.X >= cx && x.X < cx + 4 && x.Y >= cy && x.Y < cy + 4))
                        DealDamage(v, (int)rock, caster, AttackKind.Spell, "ARMAGEDDON_ROCK");
                }
                break;
            }
            case "SPELL_HOLY_WORD":
            case "SPELL_UNHOLY_WORD":
            {
                double dmg = baseDef.DamageAt(m).Eval(power);
                foreach (var v in S.Living.Where(u => !u.IsObject && !u.IsWarMachine).ToList())
                {
                    bool evilKind = IsUndeadLike(v) || v.Has(Ab.Demonic);
                    if (id == "SPELL_HOLY_WORD" ? !evilKind : evilKind) continue;
                    if (!SpellHits(side, baseDef, v, hostile: true, hero, area: true)) continue;
                    affected.Add(v.Id);
                    DealSpellDamage(v, baseDef, dmg, side, false, hero, m);
                }
                break;
            }
            case "SPELL_FIREWALL":
            {
                if (cell is not Cell c) return;
                var cells = Enumerable.Range(-1, 3).Select(dy => new Cell(c.X, c.Y + dy)).Where(S.InBounds).ToList();
                double dmg = baseDef.DamageAt(m).Eval(power);
                var f = AddField("FIREWALL", side, cells, baseDef.DurationAt(m).Eval(power), dmg);
                f.Power = power; f.Mastery = m;
                foreach (var v in S.Living.Where(u => !u.IsObject && u.Cells().Any(cells.Contains)).ToList())
                    if (SpellHits(side, baseDef, v, true, hero, area: true)) { affected.Add(v.Id); DealSpellDamage(v, baseDef, dmg, side, false, hero, m); }
                break;
            }
            case "SPELL_LAND_MINE":
            {
                if (cell is not Cell c) return;
                int n = (int)baseDef.DurationAt(m).Base;   // number of mines (p.193)
                var free = new List<Cell>();
                for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++)
                {
                    var p = new Cell(c.X + dx, c.Y + dy);
                    if (S.InBounds(p) && S.At(p) == null && !S.Obstacles.Contains(p) && !S.Fields.Any(f => f.Kind == "MINE" && f.Cells.Contains(p))) free.Add(p);
                }
                double dmg = baseDef.DamageAt(m).Eval(power);
                for (int i = 0; i < n && free.Count > 0; i++)
                {
                    int k = S.Rng.Next(free.Count);
                    var f = AddField("MINE", side, new List<Cell> { free[k] }, double.PositiveInfinity, dmg);
                    f.HiddenFromEnemy = true; f.Power = power; f.Mastery = m;
                    free.RemoveAt(k);
                }
                break;
            }
            // ---- summoning
            case "SPELL_ANIMATE_DEAD":
            case "SPELL_RESURRECT":
            {
                if (target == null) return;
                double hp = baseDef.DamageAt(m).Eval(power);
                Resurrect(target, (int)hp);
                var pen = target.GetEffect(H5.Combat.Fx.ResurrectionPenalty);
                AddEffect(target, new Effect { Id = H5.Combat.Fx.ResurrectionPenalty, Value = (pen?.Value ?? 0) + baseDef.DurationAt(m).Base, Dispellable = false });
                affected.Add(target.Id);
                break;
            }
            case "SPELL_PHANTOM":
            {
                if (target == null) return;
                var pos = NearestFree(target, target.Pos);
                if (pos == null) return;
                var clone = BattleSetup.AddCreature(Db, S, side, target.CreatureId, target.Count, pos.Value, summoned: true);
                clone.IsClone = true;
                clone.Shots = target.Shots;
                clone.Spells.Clear();
                clone.Mana = 0;
                clone.Atb = 0.4 + 0.02 * casterLevel;    // p.312
                RefreshHealth(clone);
                Emit(new UnitSummoned(clone.Id, clone.CreatureId, clone.Count, side, clone.Pos, sp.Id));
                affected.Add(clone.Id);
                break;
            }
            case "SPELL_SUMMON_ELEMENTALS":
            {
                string kind = hero != null && hero.HasPerk("HERO_SKILL_FIRE_AFFINITY") ? "CREATURE_FIRE_ELEMENTAL" : "CREATURE_EARTH_ELEMENTAL";
                double count = baseDef.DamageAt(m).Eval(power);
                if (hero != null && hero.HasPerk("HERO_SKILL_FIRE_AFFINITY")) count *= 1.4;
                SummonStack(side, kind, Math.Max(1, (int)count), sp.Id, atb: S.Rng.Next(6) * 0.05);
                break;
            }
            case "SPELL_CONJURE_PHOENIX":
            {
                if (S.Units.Any(u => u.Alive && u.Side == side && u.CreatureId == "CREATURE_PHOENIX" && u.Summoned)) return;
                double div = Math.Max(1, baseDef.DamageAt(m).Base);
                double p = Math.Ceiling(power / div);
                var ph = SummonStack(side, "CREATURE_PHOENIX", 1, sp.Id, atb: S.Rng.Next(6) * 0.05);
                if (ph != null)
                {
                    ph.BaseAttack = ph.BaseDefence = 10 + 2 * casterLevel;      // p.194
                    ph.BaseMinDamage = (int)(10 * p); ph.BaseMaxDamage = (int)(15 * p);
                    ph.BaseHealth = (int)(300 + 30 * p); ph.MaxHealthCache = ph.TopHealth = ph.BaseHealth;
                }
                break;
            }
            case "SPELL_ARCANE_CRYSTAL":
            case "SPELL_SUMMON_HIVE":
            case "SPELL_BLADE_BARRIER":
            {
                if (cell is not Cell c) return;
                SpawnObject(id, side, c, m, power, casterLevel, baseDef);
                break;
            }
            case "SPELL_EARTHQUAKE":
                break;   // only affects town fortifications (sieges)
            case "SPELL_TELEPORT":
            {
                if (target == null || cell is not Cell to) return;
                Teleport(target, to);
                if (hero != null && hero.HasPerk(Sk.TeleportAssault)) AddAtb(target.Id, 0.5);   // p.123
                affected.Add(target.Id);
                break;
            }
            // ---- warcries (p.185)
            case "SPELL_WARCRY_RALLING_CRY":
            case "SPELL_WARCRY_CALL_OF_BLOOD":
            case "SPELL_WARCRY_WORD_OF_THE_CHIEF":
            case "SPELL_WARCRY_FEAR_MY_ROAR":
            case "SPELL_WARCRY_BATTLECRY":
            case "SPELL_WARCRY_SHOUT_OF_MANY":
                Warcry(id, side, hero, target, affected, baseDef);
                break;
            default:
                if (baseDef.School == "MAGIC_SCHOOL_RUNIC")
                {
                    if (target != null && Rune(id, target)) affected.Add(target.Id);
                    if (hero != null && hero.HasPerk(Sk.RunicAttunement) && target != null)
                        AddEffect(target, new Effect { Id = H5.Combat.Fx.RunicAttunement, Remaining = Stats.Get("combat/HeroSkills/Runemage/RunicAttunement_EffectDurationInRounds", 1), Positive = true });
                    break;
                }
                if (target != null && ApplyEffectSpell(caster, side, baseDef, m, power, target, casterLevel, hero)) affected.Add(target.Id);
                break;
        }
    }

    static HashSet<Cell> AreaCells(string id, Cell c)
    {
        var set = new HashSet<Cell>();
        switch (id)
        {
            case "SPELL_FIREBALL":
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) set.Add(new Cell(c.X + dx, c.Y + dy));
                break;
            case "SPELL_METEOR_SHOWER":
                for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++) set.Add(new Cell(c.X + dx, c.Y + dy));
                break;
            case "SPELL_STONE_SPIKES":
                set.Add(c); foreach (var d in new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) }) set.Add(c + d);
                break;
            case "SPELL_FROST_RING":
                foreach (var d in Cell.Neighbours8) set.Add(c + d);   // surrounding the target spot (p.188)
                break;
        }
        return set;
    }

    /// <summary>Immunity and magic resistance check for a spell on a unit. Emits SpellResisted on failure.</summary>
    bool SpellHits(int side, SpellDef sp, Unit t, bool hostile, HeroState? hero, bool area = false)
    {
        string id = BaseSpell(sp.Id);
        bool physical = id is "SPELL_MAGIC_FIST";
        if (t.HasEffect(H5.Combat.Fx.Invisible) && Controller(t) != side && !area) return false;
        if (!physical && t.Has(Ab.ImmuneMagic) && !(hero != null && hero.SkillLevel(Sk.Invocation) > 0)) { Emit(new SpellResisted(t.Id, sp.Id)); return false; }
        if (t.GetEffect("SPELL_ANTI_MAGIC") is { } am && sp.Level <= am.Value && !physical) { Emit(new SpellResisted(t.Id, sp.Id)); return false; }
        if (!physical && sp.Element != Elements.None && sp.Damage.Length > 0 && ElementImmune(t, sp.Element) && IsDamageSpell(id)
            && !(hero != null && hero.SkillLevel(Sk.Invocation) > 0)) { Emit(new SpellResisted(t.Id, sp.Id)); return false; }
        if (MindSpells.Contains(id) && MindImmune(t)) { Emit(new SpellResisted(t.Id, sp.Id)); return false; }
        if (LivingOnly.Contains(id) && !IsLiving(t)) { Emit(new SpellResisted(t.Id, sp.Id)); return false; }
        if (id == "SPELL_SLOW" && t.Has(Ab.ImmuneSlow) || id == "SPELL_BLIND" && t.Has(Ab.ImmuneBlind)
            || id == "SPELL_BERSERK" && t.Has(Ab.ImmuneBerserk) || id == "SPELL_HYPNOTIZE" && t.Has(Ab.ImmuneHypnotize)
            || id == "SPELL_CURSE" && t.Has(Ab.ImmuneCurse))
        { Emit(new SpellResisted(t.Id, sp.Id)); return false; }
        if (hostile && Controller(t) != side && !physical)
        {
            double res = MagicResistanceOf(t);
            bool resisted = S.Rng.Chance(res) || HeroOf(t).HasPerk(Sk.DwarvenLuck) && S.Rng.Chance(res);
            if (resisted) { Emit(new SpellResisted(t.Id, sp.Id)); return false; }
        }
        return true;
    }

    static bool IsDamageSpell(string id) => id is "SPELL_MAGIC_ARROW" or "SPELL_LIGHTNING_BOLT" or "SPELL_ICE_BOLT" or "SPELL_IMPLOSION"
        or "SPELL_FIREBALL" or "SPELL_METEOR_SHOWER" or "SPELL_STONE_SPIKES" or "SPELL_FROST_RING" or "SPELL_CHAIN_LIGHTNING"
        or "SPELL_ARMAGEDDON" or "SPELL_FIREWALL" or "SPELL_LAND_MINE" or "SPELL_DEEP_FREEZE" or "SPELL_HOLY_WORD" or "SPELL_UNHOLY_WORD"
        or "SPELL_WASP_SWARM" or "SPELL_DIVINE_VENGEANCE" or "SPELL_PLAGUE";

    /// <summary>Spell damage with magic protection, Irresistible Magic, Warlock's Luck, Exorcism and Cold Death.</summary>
    int DealSpellDamage(Unit t, SpellDef sp, double dmg, int side, bool ignoreResist, HeroState? hero = null, Mastery mastery = Mastery.None)
    {
        if (!t.Alive) return 0;
        string id = BaseSpell(sp.Id);
        if (!ignoreResist)
        {
            double mult = SpellDamageMultiplier(t, sp, side);
            bool immune = (sp.Element != Elements.None && ElementImmune(t, sp.Element)) || t.Has(Ab.ImmuneMagic);
            if (hero != null && hero.SkillLevel(Sk.Invocation) is var inv && inv > Mastery.None)
            {
                // Irresistible Magic (p.140, p.322): reduces protection, and immune targets still take a share.
                double red = inv switch { Mastery.Basic => 0.2, Mastery.Advanced => 0.4, Mastery.Expert => 0.5, _ => 0.75 };
                if (immune) mult = red;
                else mult = 1 - (1 - mult) * (1 - red);
            }
            else if (immune) return 0;
            dmg *= mult;
        }
        if (hero != null)
        {
            if (hero.HasPerk(Sk.Exorcism) && t.Summoned && sp.School == "MAGIC_SCHOOL_DESTRUCTIVE") dmg *= 2;
            if (hero.HasPerk(Sk.WarlocksLuck) && sp.School == "MAGIC_SCHOOL_DESTRUCTIVE")
            {
                int luck = hero.Luck;
                if (luck > 0 && S.Rng.Chance(Math.Min(0.5, luck * 0.1))) { dmg *= 2; Emit(new LuckProc(Actor.Hero(side), true)); }
            }
            if (hero.HasPerk(Sk.ColdDeath) && id is "SPELL_ICE_BOLT" or "SPELL_FROST_RING" or "SPELL_DEEP_FREEZE" && !ElementImmune(t, Elements.Water))
                dmg += MaxHealthOf(t) * (ignoreResist ? 1 : SpellDamageMultiplier(t, sp, side));
        }
        // Enchanted Obsidian: magical damage heals instead (p.174).
        if (t.Has(Ab.EnchantedObsidian) && !ignoreResist)
        {
            Heal(t, (int)(dmg * Stats.Get("combat/Creatures/Academy/EnchantedObsidian_DamageToHealCoef", 0.5)), true, Ab.EnchantedObsidian);
            return 0;
        }
        int amount = Math.Max(0, (int)Math.Floor(dmg));
        DealDamage(t, amount, Actor.Hero(side), AttackKind.Spell, sp.Id);
        RageEvent(t, "ReceiveDDSpellRP");
        return amount;
    }

    void DealSpellDamageRaw(Unit t, int dmg, string by) => DealDamage(t, dmg, -1, AttackKind.Spell, by);

    /// <summary>Freeze/stun/armour-crush/ATB riders of damage spells.</summary>
    void SpellSideEffects(string id, Unit t, Mastery m, double power, HeroState? hero, bool first, int side, int targetsHit = 1)
    {
        if (id == "SPELL_WASP_SWARM")
        {
            var sp = Db.Spell(id)!;
            double drop = sp.DurationAt(m).Base;
            if (drop > 0) SetAtb(t.Id, GetAtb(t.Id) - drop);
        }
        if (id == "SPELL_DEEP_FREEZE")
        {
            var mult = Stats.Node("combat/Spells/DeepFreeze/DamageMultiplier")?.Elements("Item").ElementAtOrDefault((int)m);
            double v = mult != null ? (mult.Dbl("Base") + mult.Dbl("PerPower") * power) / 100.0 : 1 + (power + 1) * 0.03;
            AddEffect(t, new Effect { Id = H5.Combat.Fx.DeepFreeze, Value = v, Remaining = 1, SourceSide = side });
        }
        if (hero == null) return;
        if (hero.HasPerk(Sk.MasterIce) && id is "SPELL_ICE_BOLT" or "SPELL_FROST_RING" or "SPELL_DEEP_FREEZE")
        {
            double dur = Stats.Get("combat/HeroSkills/EmpoweredIceFreezeDuration", 0.3) / (id == "SPELL_FROST_RING" ? Math.Max(1, targetsHit) : 1);
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Frozen, Remaining = dur, SourceSide = side });
        }
        if (hero.HasPerk(Sk.MasterStorms) && first && id is "SPELL_LIGHTNING_BOLT" or "SPELL_CHAIN_LIGHTNING")
            SetAtb(t.Id, GetAtb(t.Id) * (1 - Stats.Get("combat/HeroSkills/EmpoweredLightningATBDrop", 0.3)));
        if (hero.HasPerk(Sk.MasterFire) && id is "SPELL_FIREBALL" or "SPELL_FIREWALL" or "SPELL_ARMAGEDDON"
            && !HeroOf(t).HasPerk(Sk.FireResistance) && !HeroOf(t).HasPerk(Sk.BarbFireResistance))
            AddEffect(t, new Effect { Id = H5.Combat.Fx.ArmorCrushed, Remaining = 1, SourceSide = side });
        if (hero.HasPerk(Sk.Ignite) && Db.Spell(id)?.Element == Elements.Fire && id is not "SPELL_ARMAGEDDON")
        {
            var sp = Db.Spell(id)!;
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Ignite, Value = sp.DamageAt(m).Eval(power) / 3.0,
                Remaining = Stats.Get("combat/HeroSkills/Runemage/SetAfire_DamageDurationInRounds", 3), SourceSide = side });
        }
    }

    /// <summary>Buffs, curses and other non-damage spells. Returns true if the target was affected.</summary>
    bool ApplyEffectSpell(int caster, int side, SpellDef sp, Mastery m, double power, Unit t, int casterLevel, HeroState? hero)
    {
        string id = sp.Id;
        bool hostile = sp.Target == "TARGET_HOSTILE" || (id is "SPELL_BERSERK" or "SPELL_HYPNOTIZE" && Controller(t) != side);
        if (id == "SPELL_DISPEL") hostile = Controller(t) != side;
        if (!SpellHits(side, sp, t, hostile, hero)) return false;
        double value = sp.DamageAt(m).Eval(power);
        double duration = sp.Duration.Length > 0 ? sp.DurationAt(m).Eval(power) : double.PositiveInfinity;
        if (duration <= 0) duration = 1;

        switch (id)
        {
            case "SPELL_DISPEL":
                Cleanse(t, side, m, casterLevel, value);
                return true;
            case "SPELL_ANTI_MAGIC":
                foreach (var e in t.Effects.Where(e => e.SpellLevel > 0 && (e.SpellLevel <= value)).Select(e => e.Id).ToList()) RemoveEffect(t, e);
                break;
            case "SPELL_DISRUPTING_RAY":
                value += t.GetEffect(id)?.Value ?? 0;    // cumulative (p.186)
                duration = double.PositiveInfinity;
                break;
            case "SPELL_BERSERK":
            {
                int actions = (int)sp.DurationAt(m).Base;
                var e = MakeSpellEffect(id, sp, m, power, side, casterLevel, value, double.PositiveInfinity, positive: false);
                e.Actions = Math.Max(1, actions);
                e.Dispellable = false;
                AddEffect(t, e);
                return true;
            }
            case "SPELL_HYPNOTIZE":
            {
                // Initiative multiplied by 0.03*Power while controlled (0.01*Power with no mastery), max 1 (p.187).
                var e = MakeSpellEffect(id, sp, m, power, side, casterLevel, Math.Min(1, value), duration, positive: false);
                foreach (var other in S.Units.Where(u => u.Id != t.Id && u.GetEffect(id)?.SourceSide == side).ToList()) RemoveEffect(other, id);
                AddEffect(t, e);
                return true;
            }
            case "SPELL_CELESTIAL_SHIELD":
            {
                var e = MakeSpellEffect(id, sp, m, power, side, casterLevel, sp.DamageAt(m).Base, double.PositiveInfinity, positive: true);
                e.Value2 = sp.DurationAt(m).Eval(power);    // shield hit points
                AddEffect(t, e);
                return true;
            }
            case "SPELL_REGENERATION":
                value = sp.DamageAt(m).Eval(power);
                duration = sp.DurationAt(m).Base;
                break;
            case "SPELL_PLAGUE":
                duration = sp.DurationAt(m).Base;
                break;
            case "SPELL_VAMPIRISM":
                value = sp.DamageAt(m).Eval(power) / 100.0;
                break;
            case "SPELL_BLIND":
                break;
        }
        // Opposite spells dispel each other (p.315); Eternal Light gives 50% to resist.
        if (Opposites.TryGetValue(id, out var opp) && t.GetEffect(opp) is { } oe)
        {
            var oppCaster = oe.SourceSide >= 0 ? S.Heroes[oe.SourceSide] : null;
            if (oppCaster != null && oppCaster.HasPerk(Sk.EternalLight) && oe.School == "MAGIC_SCHOOL_LIGHT" && S.Rng.Chance(0.5))
            {
                Emit(new SpellResisted(t.Id, id));
                return false;
            }
            RemoveEffect(t, opp);
        }
        bool positive = sp.Target == "TARGET_FRIEND";
        AddEffect(t, MakeSpellEffect(id, sp, m, power, side, casterLevel, value, duration, positive));
        if (!positive) RageEvent(t, "ReceiveCurseRP");
        return true;
    }

    Effect MakeSpellEffect(string id, SpellDef sp, Mastery m, double power, int side, int casterLevel, double value, double duration, bool positive) => new()
    {
        Id = id, Value = value, Remaining = duration, SourceSide = side, CasterLevel = casterLevel, Power = power, Mastery = m,
        Positive = positive, SpellLevel = sp.Level, School = sp.School,
    };

    /// <summary>Cleansing: removes negative effects from friends / positive from foes, chance per effect (p.190).</summary>
    void Cleanse(Unit t, int side, Mastery m, int dispellerLevel, double baseChance)
    {
        bool friendly = Controller(t) == side;
        foreach (var e in t.Effects.Where(e => e.Dispellable && e.Positive != friendly && e.SpellLevel > 0).ToList())
        {
            double chance = baseChance + (dispellerLevel - e.CasterLevel) * 0.03;
            if (e.SourceSide >= 0 && S.Heroes[e.SourceSide].HasPerk(Sk.EternalLight) && e.School == "MAGIC_SCHOOL_LIGHT") chance /= 2;
            if (S.Rng.Chance(chance)) RemoveEffect(t, e.Id);
        }
        if (friendly)
            foreach (var id in new[] { H5.Combat.Fx.Wound, H5.Combat.Fx.MarkOfFire, H5.Combat.Fx.EvilEye, H5.Combat.Fx.WeakeningStrike, H5.Combat.Fx.ArmorCrushed, H5.Combat.Fx.Sleep })
                if (t.HasEffect(id)) RemoveEffect(t, id);
    }

    /// <summary>Spell effect delivered by a creature ability (Blinding Attack, Whip Strike...).</summary>
    void ApplySpellEffectFromCreature(Unit a, Unit t, string spellId, Mastery m, int power)
    {
        var sp = Db.Spell(spellId);
        if (sp == null || !t.Alive) return;
        Emit(new AbilityTriggered(a.Id, spellId, t.Id));
        ApplyEffectSpell(a.Id, Controller(a), sp, m, power, t, a.Level, null);
    }

    Unit? SummonStack(int side, string creature, int count, string by, double atb)
    {
        if (!Db.Creatures.ContainsKey(creature)) return null;
        var probe = new Unit { Size = Db.Creature(creature).CombatSize };
        Cell around = side == 0 ? new Cell(1, S.Height / 2) : new Cell(S.Width - 2, S.Height / 2);
        var pos = NearestFree(probe, around);
        if (pos == null) return null;
        var u = BattleSetup.AddCreature(Db, S, side, creature, count, pos.Value, summoned: true);
        u.Atb = atb;
        u.HasMovedFromStart = true;
        Emit(new UnitSummoned(u.Id, creature, count, side, u.Pos, by));
        return u;
    }

    void SpawnObject(string id, int side, Cell c, Mastery m, double power, int casterLevel, SpellDef sp)
    {
        var cells = new List<Cell> { c };
        if (id == "SPELL_BLADE_BARRIER") cells = new List<Cell> { new(c.X, c.Y - 1), c, new(c.X, c.Y + 1) };
        foreach (var cell in cells)
        {
            if (!S.InBounds(cell) || S.At(cell) != null || S.Obstacles.Contains(cell)) continue;
            var u = new Unit
            {
                Id = S.NextId++, Side = side, Kind = UnitKind.Obstacle, Pos = cell, StartPos = cell, Count = 1, InitialCount = 1,
                Town = Towns.Neutral, Summoned = true, Abilities = new List<string> { Ab.Mechanical },
            };
            switch (id)
            {
                case "SPELL_ARCANE_CRYSTAL":
                    u.CreatureId = "ARCANE_CRYSTAL"; u.Name = "Arcane Crystal"; u.Tier = 1;
                    u.BaseHealth = (int)Stats.Get("combat/Spells/ArcaneCrystal/Health", 1);
                    u.BaseDefence = (int)Stats.Get("combat/Spells/ArcaneCrystal/Defence", 0);
                    u.Power = (int)sp.DamageAt(m).Eval(power);     // explosion damage
                    break;
                case "SPELL_SUMMON_HIVE":
                {
                    u.CreatureId = "SUMMON_HIVE"; u.Name = "Hive"; u.Tier = (int)Stats.Get("combat/Spells/SummonHive/Tier", 8);
                    var hp = Stats.Node("combat/Spells/SummonHive/Health")?.Elements("Item").ElementAtOrDefault((int)m);
                    var ini = Stats.Node("combat/Spells/SummonHive/Initiative")?.Elements("Item").ElementAtOrDefault((int)m);
                    u.BaseHealth = (int)(hp?.Dbl("Base") ?? 10);
                    u.BaseInitiative = (ini?.Dbl("Base") ?? 5) + (ini?.Dbl("PerPower") ?? 0.25) * power;
                    u.BaseDefence = (int)(Stats.Get("combat/Spells/SummonHive/DefenseBase", 0) + Stats.Get("combat/Spells/SummonHive/DefensePerCasterLevel", 1) * casterLevel);
                    u.Power = (int)power;
                    u.Abilities.Add(Ab.ImmuneMagic);
                    u.Atb = 0.5 + 0.5 * S.Rng.NextDouble();
                    break;
                }
                case "SPELL_BLADE_BARRIER":
                {
                    u.CreatureId = "BLADE_BARRIER"; u.Name = "Blade Barrier"; u.Tier = 1;
                    var hp = Stats.Node("combat/Spells/BladeBarrier/Health")?.Elements("Item").ElementAtOrDefault((int)m);
                    u.BaseHealth = (int)((hp?.Dbl("Base") ?? 20) + (hp?.Dbl("PerPower") ?? 5) * power);
                    u.Abilities.Add(Ab.MagicProof75);
                    break;
                }
            }
            u.MaxHealthCache = u.TopHealth = Math.Max(1, u.BaseHealth);
            u.RetaliationsLeft = 0;
            S.Units.Add(u);
            Emit(new UnitSummoned(u.Id, u.CreatureId, 1, side, cell, id));
        }
    }

    // ------------------------------------------------------------------ runes (p.192) and warcries (p.185)

    bool Rune(string id, Unit t)
    {
        switch (id)
        {
            case "SPELL_RUNE_OF_CHARGE": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneCharge, Actions = 1, Positive = true, Dispellable = false }); break;
            case "SPELL_RUNE_OF_BERSERKING": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneBerserking, Positive = true, Dispellable = false }); break;
            case "SPELL_RUNE_OF_MAGIC_CONTROL": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneMagicControl, Positive = true, Dispellable = false }); break;
            case "SPELL_RUNE_OF_EXORCISM":
                foreach (var e in t.Effects.Where(e => !e.Positive && e.Dispellable && e.Id != H5.Combat.Fx.Poison && (e.SpellLevel <= 3)).Select(e => e.Id).ToList())
                    RemoveEffect(t, e);
                break;
            case "SPELL_RUNE_OF_ELEMENTAL_IMMUNITY":
            {
                int a = S.Rng.Next(4), b = (a + 1 + S.Rng.Next(3)) % 4;
                AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneElementalImmunity, Value = a, Value2 = b, Positive = true, Dispellable = false });
                break;
            }
            case "SPELL_RUNE_OF_ETHEREALNESS": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneEtherealness, Remaining = 1, Positive = true, Dispellable = false }); break;
            case "SPELL_RUNE_OF_STUNNING": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneThunderclap, Positive = true, Dispellable = false }); break;
            case "SPELL_RUNE_OF_REVIVE":
            {
                int dead = t.InitialCount - t.Count;
                if (dead <= 0) return false;
                int n = (int)(dead * Stats.Get("combat/Spells/Runic/ReviveSpellPercent", 0.4));
                Heal(t, n * t.MaxHealthCache, true, id);
                break;
            }
            case "SPELL_RUNE_OF_BATTLERAGE": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneBattleRage, Positive = true, Dispellable = false }); break;
            case "SPELL_RUNE_OF_DRAGONFORM":
                if (t.Name.Contains("Dragon", StringComparison.OrdinalIgnoreCase)) return false;
                AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneDragonform, Remaining = 1, Positive = true, Dispellable = false });
                break;
            default: return false;
        }
        return true;
    }

    void Warcry(string id, int side, HeroState? hero, Unit? target, List<int> affected, SpellDef sp)
    {
        if (hero == null) return;
        int level = hero.Level + (hero.HasPerk(Sk.MightyShout) ? (int)Stats.Get("combat/HeroSkills/Barbarian/MightyVoice_HeroLevelBonus", 5) : 0);
        double rageMult = hero.HasPerk(Sk.ShoutOfRage) ? (id == "SPELL_WARCRY_CALL_OF_BLOOD"
            ? Stats.Get("combat/HeroSkills/Barbarian/VoiceOfRage_RageMultCallOfBlood", 1.5) : Stats.Get("combat/HeroSkills/Barbarian/VoiceOfRage_RageMultOther", 2)) : 1;
        int armyRp = (int)(Stats.Get($"combat/Spells/Warcries/RPToArmyOnWarcryLevel{Math.Clamp(sp.Level, 1, 3)}", 20) * rageMult);
        foreach (var u in S.LivingOf(side).Where(u => u.Has(Ab.DemonRaged)).ToList()) AddRage(u, armyRp);
        switch (id)
        {
            case "SPELL_WARCRY_RALLING_CRY":
            {
                int bonus = 1 + level / (int)Stats.Get("combat/Spells/Warcries/RallingCry_MoraleBonus/LevelDivisor", 8);
                foreach (var u in S.LivingOf(side).Where(u => !NeutralMorale(u)).ToList())
                {
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.RallyingCry, Value = bonus, Remaining = Stats.Get("combat/Spells/Warcries/RallingCry_Duration", 3), Positive = true, SourceSide = side });
                    affected.Add(u.Id);
                }
                break;
            }
            case "SPELL_WARCRY_CALL_OF_BLOOD":
                if (target != null && target.Has(Ab.DemonRaged)) { AddRage(target, (int)(level * Stats.Get("combat/Spells/Warcries/CallOfBlood_RPPerCasterLevel", 10) * rageMult)); affected.Add(target.Id); }
                break;
            case "SPELL_WARCRY_WORD_OF_THE_CHIEF":
                if (target != null && IsLiving(target))
                {
                    HeroStrike(hero, target, levelBonus: 0, friendly: true);
                    if (target.Alive)
                    {
                        AddAtb(target.Id, Stats.Get("combat/Spells/Warcries/WordOfTheChief_ATBBonusBase", 0.1) + Stats.Get("combat/Spells/Warcries/WordOfTheChief_ATBBonusPerCasterLevel", 0.02) * level);
                        if (target.Has(Ab.DemonRaged)) AddRage(target, (int)(Stats.Get("combat/Spells/Warcries/WordOfTheChief_RPBonus", 30) * rageMult));
                    }
                    affected.Add(target.Id);
                }
                break;
            case "SPELL_WARCRY_FEAR_MY_ROAR":
                if (target != null && !MindImmune(target) && S.Rng.Chance(0.5 + (level - target.Level) * 0.05))
                {
                    Emit(new AbilityTriggered(Actor.Hero(side), "FEAR_MY_ROAR", target.Id));
                    SetAtb(target.Id, 0);
                    affected.Add(target.Id);
                }
                break;
            case "SPELL_WARCRY_BATTLECRY":
            {
                int att = 1 + level / (int)Stats.Get("combat/Spells/Warcries/Battlecry_OffenceBonus/LevelDivisor", 2);
                foreach (var u in S.LivingOf(side).Where(u => u.Kind == UnitKind.Creature).ToList())
                {
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.Battlecry, Value = att, Remaining = 1, Positive = true, SourceSide = side });
                    affected.Add(u.Id);
                }
                break;
            }
            case "SPELL_WARCRY_SHOUT_OF_MANY":
                if (target != null)
                {
                    int n = S.LivingOf(side).Where(u => u.Kind == UnitKind.Creature).Sum(u => u.Count);
                    DealSpellDamage(target, sp, n * Stats.Get("combat/Spells/Warcries/ShoutOfMany_DamagePerCreatureInArmy", 1), side, false, null);
                    affected.Add(target.Id);
                }
                break;
        }
    }
}
