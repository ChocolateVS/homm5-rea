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

    static string BaseSpell(string id) =>
        MassToBase.TryGetValue(id, out var b) ? b : id.StartsWith("SPELL_EMPOWERED_") ? "SPELL_" + id["SPELL_EMPOWERED_".Length..] : id;
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
        // Empowered Spells: empowered versions of the known damaging Destructive spells (double mana, +50% damage).
        if (h.HasPerk(Sk.EmpoweredSpells))
            foreach (var s in set.ToList())
                if (Db.Spell("SPELL_EMPOWERED_" + s["SPELL_".Length..]) is { } emp) set.Add(emp.Id);
        if (h.SkillLevel(Sk.Runelore) > Mastery.None)
            foreach (var r in Db.Spells.Values.Where(x => x.School == "MAGIC_SCHOOL_RUNIC"))
                if (h.Spells.Contains(r.Id)) set.Add(r.Id);
        // Mind Cleaner blocks (exe 00983790 → caster hero vf 0x2b8): unusable for the rest of the combat.
        return set.Distinct().Where(s => !h.AbilityUses.ContainsKey(MindCleanerBlock + s));
    }

    const string MindCleanerBlock = "MIND_CLEANER:";

    /// <summary>
    /// Mind Cleaner (exe 00983790 @ 0098431f-009843f2): chance = 100 / max(1, circle + 1) − caster level + Mind Cleaner
    /// level (integer division), exe ability roll Range(0,100) &lt; chance; success blocks the spell for the caster.
    /// </summary>
    internal void MindCleanerCheck(HeroState caster, SpellDef sp)
    {
        var mc = S.Heroes[1 - caster.Side];
        if (!mc.Present || mc.Specialization != "HERO_SPEC_MIND_CLEANER") return;
        int chance = 100 / Math.Max(1, sp.Level + 1) - caster.Level + mc.Level;
        var anyUnit = S.Units.FirstOrDefault(x => x.Alive);
        if (anyUnit == null || !RollPercent(anyUnit, chance, secondRoll: false)) return;
        caster.AbilityUses[MindCleanerBlock + sp.Id] = 1;
        Emit(new AbilityTriggered(Actor.Hero(mc.Side), "HERO_SPEC_MIND_CLEANER", Actor.Hero(caster.Side)));
    }

    /// <summary>The hero's own mastery for a spell before Absolute Wizardry, the week and Shatter.</summary>
    Mastery SchoolMastery(HeroState h, SpellDef sp)
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
        return m;
    }

    public Mastery HeroMastery(HeroState h, SpellDef sp)
    {
        Mastery m = SchoolMastery(h, sp);
        // Absolute Wizardry: every spell at Expert (exe 00b4b440 returns 3 before the school lookup).
        if (h.HasPerk("HERO_SKILL_ABSOLUTE_WIZARDY")) m = Mastery.Expert;
        m = MoonWeek.SpellMastery(m, sp.School, S.Week);   // exe 00a6a700 → 00bcde10, after the caster's own mastery
        // Enemy Shatter (exe 004e6190, after the week): mastery − SpellTrainedDecrease[spell level − 1], clamped 0..3.
        int below = ShatterMasteryAfterDrop(h, sp, m);
        return below == int.MinValue ? m : (Mastery)Math.Clamp(below, 0, 3);
    }

    string ShatterSkill(string school) => school switch
    {
        "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.ShatterDestructive, "MAGIC_SCHOOL_DARK" => Sk.ShatterDark,
        "MAGIC_SCHOOL_LIGHT" => Sk.ShatterLight, "MAGIC_SCHOOL_SUMMONING" => Sk.ShatterSummoning, _ => "",
    };

    string ShatterTable(Mastery shatter) => "combat/HeroSkills/Barbarian/"
        + (shatter == Mastery.Basic ? "Basic" : shatter == Mastery.Advanced ? "Advanced" : "Expert") + "ShatterMagic/";

    /// <summary>Exe 004e6190: the caster's mastery minus the enemy Shatter drop (unclamped), or int.MinValue when the
    /// enemy hero has no Shatter skill of the spell's school.</summary>
    int ShatterMasteryAfterDrop(HeroState h, SpellDef sp, Mastery m)
    {
        var enemy = Enemy(h.Side);
        string shatter = ShatterSkill(sp.School);
        if (!enemy.Present || shatter.Length == 0) return int.MinValue;
        var sm = enemy.SkillLevel(shatter);
        if (sm == Mastery.None) return int.MinValue;
        var items = Stats.Node(ShatterTable(sm) + "SpellTrainedDecrease")?.Elements("Item").ToList();
        int drop = items != null && sp.Level >= 1 && sp.Level <= items.Count ? (int)XmlHelpers.ParseDouble(items[sp.Level - 1].Value, 0) : 0;
        return (int)m - drop;
    }

    /// <summary>
    /// Enemy Shatter / Weaken on the caster's spell power (exe 004e6190 from 00a6a7e0): when the drop takes the mastery
    /// to −1 / −2 or below, trunc(p · SpellpowerDecreaseForTrainedDrop1 / 2BelowNone) (at least 1) is subtracted; Weaken
    /// of the school subtracts trunc(p · WeakenMagic_SpellPowerDecrease) (at least 1); the result is at least 1. Only
    /// when the enemy hero has the school's Shatter skill.
    /// </summary>
    double ShatterWeakenPower(HeroState h, SpellDef sp, double p)
    {
        Mastery m = h.HasPerk("HERO_SKILL_ABSOLUTE_WIZARDY") ? Mastery.Expert : SchoolMastery(h, sp);
        m = MoonWeek.SpellMastery(m, sp.School, S.Week);
        int below = ShatterMasteryAfterDrop(h, sp, m);
        if (below == int.MinValue) return p;
        var enemy = Enemy(h.Side);
        string tbl = ShatterTable(enemy.SkillLevel(ShatterSkill(sp.School)));
        int pi = (int)p, shatterLoss = 0, weakenLoss = 0;
        if (below == -1) shatterLoss = Math.Max(1, (int)(pi * (float)Stats.Get(tbl + "SpellpowerDecreaseForTrainedDrop1BelowNone", 0.25)));
        else if (below <= -2) shatterLoss = Math.Max(1, (int)(pi * (float)Stats.Get(tbl + "SpellpowerDecreaseForTrainedDrop2BelowNone", 0.5)));
        string weaken = sp.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.WeakenDestructive, "MAGIC_SCHOOL_DARK" => Sk.WeakenDark,
            "MAGIC_SCHOOL_LIGHT" => Sk.WeakenLight, "MAGIC_SCHOOL_SUMMONING" => Sk.WeakenSummoning, _ => "",
        };
        if (enemy.HasPerk(weaken))
            weakenLoss = Math.Max(1, (int)(pi * (float)Stats.Get("combat/HeroSkills/Barbarian/WeakenMagic_SpellPowerDecrease", 0.25)));
        return Math.Max(1, pi - weakenLoss - shatterLoss);
    }

    public double HeroSpellPower(HeroState h, SpellDef sp)
    {
        double p = h.SpellPower;
        string id = BaseSpell(sp.Id);
        if (h.HasPerk(Sk.Twilight) && sp.School is "MAGIC_SCHOOL_DARK" or "MAGIC_SCHOOL_LIGHT") p += 3;
        if (h.HasPerk(Sk.FallenKnight) && sp.School == "MAGIC_SCHOOL_DARK") p += 5;
        // exe 00bc00d0 switches on the RAW spell id (empowered variants get none of these): Master of Sickness /
        // Wrath / Animation / Quakes / Creatures +4, Band of Conjurer +4 on Summon Elementals, Weilder of Fire on
        // Fireball, Master of Elements on Summon Elementals / Conjure Phoenix, Runic Armor on Celestial Shield.
        string raw = sp.Id;
        if (h.HasPerk(Sk.MasterWrath) && raw == "SPELL_HOLY_WORD") p += 4;
        if (h.HasPerk(Sk.MasterPain) && raw == "SPELL_UNHOLY_WORD") p += 4;
        if (h.HasPerk(Sk.MasterLife) && raw is "SPELL_MAGIC_FIST" or "SPELL_ANIMATE_DEAD") p += 4;
        if (h.HasPerk(Sk.MasterEarthblood) && raw is "SPELL_LAND_MINE" or "SPELL_ARCANE_CRYSTAL" or "SPELL_BLADE_BARRIER") p += 4;
        if (raw == "SPELL_SUMMON_ELEMENTALS" && h.Artifacts.Contains("ARTIFACT_BAND_OF_CONJURER")) p += 4;
        if (h.HasPerk(Sk.MasterConjuration) && raw is "SPELL_CONJURE_PHOENIX" or "SPELL_SUMMON_ELEMENTALS") p += 4;
        if (h.Specialization == "HERO_SPEC_WEILDER_OF_FIRE" && raw == "SPELL_FIREBALL")
        {
            const string k = "combat/HeroSpecializations/Wizard/WeilderOfFire_FireballSpellpowerBonus/";
            p += (h.Level - 1) / Math.Max(1, (int)Stats.Get(k + "LevelDivisor", 1)) * (int)Stats.Get(k + "PerDividedLevel", 0) + (int)Stats.Get(k + "Base", 0);
        }
        // Master of Elements: +((level−1)/div)·per + base spell power for Summon Elementals / Conjure Phoenix
        // (DefaultStats MasterOfElements_SpellpowerBonus; same level formula as its elemental count, exe FUN_00a5a5a0).
        if (h.Specialization == "HERO_SPEC_MASTER_OF_ELEMENTS" && raw is "SPELL_CONJURE_PHOENIX" or "SPELL_SUMMON_ELEMENTALS")
        {
            const string k = "combat/HeroSpecializations/Wizard/MasterOfElements_SpellpowerBonus/";
            p += (h.Level - 1) / Math.Max(1, (int)Stats.Get(k + "LevelDivisor", 3)) * (int)Stats.Get(k + "PerDividedLevel", 1) + (int)Stats.Get(k + "Base", 1);
        }
        // Reanimator: Raise (Animate) Dead gets +((level−1)/divisor) + bonus spell power (exe FUN_009789b0).
        if (h.Specialization == "HERO_SPEC_REANIMATOR" && id == "SPELL_ANIMATE_DEAD")
            p += (h.Level - 1) / Math.Max(1, (int)Stats.Get("combat/HeroSpecializations/Necromancer/Reanimator_SpellpowerBonusLevelDivisor", 5))
                 + Stats.Get("combat/HeroSpecializations/Necromancer/Reanimator_SpellpowerBonus", 1);
        if (h.HasPerk(Sk.RuneArmour) && raw == "SPELL_CELESTIAL_SHIELD") p += Stats.Get("combat/HeroSkills/Runemage/RunicArmor_SpellpowerBonus", 4);
        var e = Enemy(h.Side);
        if (e.Present)
        {
            if (e.HasPerk(Sk.MightOverMagic)) p -= e.SpellPower / 2.0;
            if (e.HasPerk(Sk.ShrugDarkness) && sp.School == "MAGIC_SCHOOL_DARK")
                p = (int)(p * (float)Stats.Get("combat/HeroSkills/Runemage/ShrugDarkness_EnemySpellpowerMultiplier", 0.5));   // chop
        }
        p += PowerFeedBonus(h);           // exe 00bc0590: Power Feed effect, then at least 1
        p = Math.Max(1, p);
        return ShatterWeakenPower(h, sp, p);   // exe 004e6190 from 00a6a7e0 (Shatter / Weaken, subtractive)
    }

    // Rounding helpers matching the x87 code: ROUND is round-half-even, int division truncates.
    static int ExeRound(double v) => (int)Math.Round(v, MidpointRounding.ToEven);
    // The spell code converts float → int with the FPU in chop mode (FNSTCW; OR AH,0x0C; FLDCW; FISTP — exe
    // 00976c0d, 0097d6ad, 0097d5c4, 00a34836, 00978290), i.e. truncation toward zero, not round-to-nearest.
    static int ExeChop(double v) => (int)Math.Truncate(v);

    /// <summary>
    /// Hero spell cost: base cost (exe FUN_00a34740) then combat modifiers (FUN_009781e0).
    /// Base: warcries — Voice Training min(cost×(1.001−discount), cost−1), War Leader −(lvl/div·per+base), ≥1;
    /// other spells — Rusher (HERO_SPEC_BEATER) halves Teleport, Ring of Magi halves, Power Bargain
    /// ×clamp(1−reduce−lvl·perLevel, 0.5, 1), Arcane Training round(min(cost×0.8, cost−1)), ≥0.
    /// Then: Energy Channel ×0.75 (at least −1), enemy Suppress Dark/Light ×2, White Knight −1 on Light,
    /// enemy Corrupt Magic ×(1+increase); never below 0.
    /// </summary>
    public int HeroManaCost(HeroState h, SpellDef sp)
    {
        if (sp.School == "MAGIC_SCHOOL_RUNIC") return 0;
        if (sp.School == "MAGIC_SCHOOL_WARCRIES") return HeroBaseManaCost(h, sp);
        int c = HeroBaseManaCost(h, sp);
        int adjust = 0;
        if (S.LivingOf(h.Side).Any(u => u.Has(Ab.EnergyChannel)))
        {
            int r = ExeChop(c * 0.75);
            adjust = r != c ? 0 : -1;    // guarantees at least one point of discount
            c = r;
        }
        var e = Enemy(h.Side);
        if (e.Present && (sp.School == "MAGIC_SCHOOL_DARK" && e.HasPerk(Sk.SealOfDarkness) || sp.School == "MAGIC_SCHOOL_LIGHT" && e.HasPerk(Sk.SuppressLight)))
        {
            if (c * 2 == c) adjust++;
            c *= 2;
        }
        if (sp.School == "MAGIC_SCHOOL_LIGHT" && h.Specialization == "HERO_SPEC_WHITE_KNIGHT") c -= 1;
        if (e.Present && CorruptPerk(sp.School) is { Length: > 0 } corrupt && e.HasPerk(corrupt))
            c = ExeChop((1 + Stats.Get("combat/HeroSkills/Barbarian/CorruptMagic_SpellCostIncrease", 0.5)) * c);
        return Math.Max(0, c + adjust);
    }

    /// <summary>The base part of the hero's spell cost (exe FUN_00a34740): week, warcry or school discounts — no combat
    /// modifiers. A Summon Hive hive pays its spell with this (FUN_008b07f0 / FUN_008b08a0).</summary>
    internal int HeroBaseManaCost(HeroState h, SpellDef sp)
    {
        int c = MoonWeek.SpellCost(sp.ManaCost, S.Week);   // exe 00a347b9 → 00bcddf0, before every discount
        if (sp.School == "MAGIC_SCHOOL_WARCRIES")
        {
            if (h.HasPerk(Sk.ShoutTraining))
                c = ExeChop(Math.Min((1.001 - Stats.Get("combat/HeroSkills/Barbarian/VoiceTraining_WarcryCostDiscount", 0.2)) * c, c - 1.0));
            if (h.Specialization == "HERO_SPEC_WAR_LEADER")
            {
                int div = Math.Max(1, (int)Stats.Get("combat/HeroSpecializations/Barbarian/WarLeader_WarcryCostReducePerHeroLevel/LevelDivisor", 1));
                c -= h.Level / div * (int)Stats.Get("combat/HeroSpecializations/Barbarian/WarLeader_WarcryCostReducePerHeroLevel/PerDividedLevel", 0)
                     + (int)Stats.Get("combat/HeroSpecializations/Barbarian/WarLeader_WarcryCostReducePerHeroLevel/Base", 0);
            }
            return Math.Max(1, c);
        }
        if (sp.Id == "SPELL_TELEPORT" && h.Specialization == "HERO_SPEC_BEATER") c /= 2;
        if (h.Artifacts.Contains("RING_OF_MAGI")) c /= 2;
        if (h.Specialization == "HERO_SPEC_POWER_BARGAIN" && sp.Id.StartsWith("SPELL_EMPOWERED_"))   // empowered spells only
        {
            double f = 1 - Stats.Get("combat/HeroSpecializations/Warlock/PowerBargain_CostReduce", 0)
                         - h.Level * Stats.Get("combat/HeroSpecializations/Warlock/PowerBargain_CostReducePerLevel", 0);
            c = ExeChop(Math.Clamp(f, 0.5, 1.0) * c);
        }
        if (h.HasPerk(Sk.ArcaneTraining)) c = ExeChop(Math.Min(c * (double)0.8f, c - 1.0));
        return Math.Max(0, c);
    }

    static string CorruptPerk(string school) => school switch
    {
        "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.CorruptDestructive, "MAGIC_SCHOOL_DARK" => Sk.CorruptDark,
        "MAGIC_SCHOOL_LIGHT" => Sk.CorruptLight, "MAGIC_SCHOOL_SUMMONING" => Sk.CorruptSummoning, _ => "",
    };

    /// <summary>Creature caster spell cost (FUN_009781e0 creature path): Elite Casters halves with integer
    /// division; enemy Suppress/Corrupt apply as for heroes.</summary>
    int CreatureManaCost(Unit u, SpellDef sp)
    {
        int c = MoonWeek.SpellCost(sp.ManaCost, S.Week), adjust = 0;   // exe 009781e0 → 00a34740 → 00bcddf0
        var e = Enemy(u.Side);
        if (e.Present && (sp.School == "MAGIC_SCHOOL_DARK" && e.HasPerk(Sk.SealOfDarkness) || sp.School == "MAGIC_SCHOOL_LIGHT" && e.HasPerk(Sk.SuppressLight)))
        {
            if (c * 2 == c) adjust++;
            c *= 2;
        }
        var h = HeroOf(u);
        if (h.HasPerk(Sk.RefinedMana) || h.HasPerk(Sk.BarbRefinedMana)) c /= 2;
        if (e.Present && CorruptPerk(sp.School) is { Length: > 0 } corrupt && e.HasPerk(corrupt))
            c = ExeChop((1 + Stats.Get("combat/HeroSkills/Barbarian/CorruptMagic_SpellCostIncrease", 0.5)) * c);
        return Math.Max(0, c + adjust);
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
            var target = c.TargetId is int tid ? S.Unit(tid) : null;
            // Mark of the Wizard: spells on the marked stack are doubled when the hero can pay the cost twice.
            bool doubled = target != null && h.Links.TryGetValue(LinkMarkOfWizard, out var markedId) && markedId == target.Id && h.Mana >= 2 * cost;
            if (doubled) cost *= 2;
            cost = SkillAuditH.ErraticManaSpend(cost, h.HasPerk(Sk.ErraticMana), HeroRoll, S.Rng);   // exe 00bbf6a6
            h.Mana -= cost;
            Emit(new ManaChanged(c.ActorId, h.Mana));
            // Pendant of Conflux (ARTIFACT_TWISTING_NEITHER) on the enemy hero: + half the spent points (exe 00983790).
            if (Enemy(h.Side) is { Present: true } conflux && conflux.Artifacts.Contains("ARTIFACT_TWISTING_NEITHER") && cost > 0)
            {
                conflux.Mana += cost / 2;
                Emit(new ManaChanged(Actor.Hero(conflux.Side), conflux.Mana));
            }
            if (Counterspelled(h, cost))
            {
                Emit(new SpellResisted(target?.Id ?? c.ActorId, sp.Id));
                // exe 00978670 / FUN_00977260: a countered caster with Dark Renewal (HERO_SKILL_PAYBACK) and a
                // non-mass spell keeps ATB 0.2 + 0.1·Sorcery mastery ("Payback!"); otherwise the normal reset.
                double counteredReset = h.HasPerk(Sk.DarkRenewal) && !IsMass(sp.Id)
                    ? 0.2f + 0.1f * (int)h.SkillLevel("HERO_SKILL_SORCERY") : HeroCastAtbReset(h, sp);
                EndAction(c.ActorId, counteredReset, false);
                return null;
            }
            if (doubled) _spellDamageScale *= 2;
            ResolveSpell(c.ActorId, h.Side, sp, mastery, power, target, c.Cell, fromHero: true);
            _spellDamageScale = 1;
            MindCleanerCheck(h, sp);
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
        if (Specials.Pieces(HeroOf(u), Specials.Magis) >= 2) cp *= (int)Stats.Get("ArtifactsSetsEffectsConsts/Magis_2_Casters_SpellpowerMultiplier", 2);
        if (ValidateSpellTarget(u.Id, Controller(u), sp, MoonWeek.SpellMastery(known.Mastery, sp.School, S.Week), c.TargetId, c.Cell) is { } te2) return te2;
        var castTarget = c.TargetId is int t2 ? S.Unit(t2) : null;
        var castCell = c.Cell;
        // Defile Magic (p.324): 1 fails, 2 halves spell power, 3 doubles the cost, 4 backfires onto the goblins.
        int defile = DefileOutcome(u, sp);
        // Halved spell power is truncated with no floor (exe FUN_00a6a7e0, FISTP under chop at 00a6a8eb).
        if (defile == 2) cp = (int)(cp * (float)Stats.Get("combat/Creatures/Stronghold/DefileMagicSpellPowerCoef", 0.5));
        if (defile == 4 && sp.Target == "TARGET_HOSTILE" && !sp.IsAreaAttack && castTarget != null
            && S.Units.Find(g => g.Alive && g.Has(Ab.DefileMagic) && Controller(g) != Controller(u)) is { } defilers)
        {
            castTarget = defilers; ucost = 0;
        }
        if (u.Mana < ucost) { EndAction(u.Id, 0); return null; }
        // Defile Magic's extra cost (exe FUN_008a11c0): only the base cost is required; the stack then loses another
        // (DefileMagicManaCoef − 1)·cost in single precision, truncated, clamped at 0.
        if (defile == 3)
            u.Mana = Math.Max(0, (int)((float)(u.Mana - ucost) - ((float)Stats.Get("combat/Creatures/Stronghold/DefileMagicManaCoef", 2) - 1f) * ucost));
        else u.Mana -= ucost;
        Emit(new ManaChanged(u.Id, u.Mana));
        RageEvent(u, "CastSpellRP");
        if (defile == 1) { Emit(new SpellResisted(castTarget?.Id ?? u.Id, sp.Id)); EndAction(u.Id, 0); return null; }
        _castingCreature = u;
        try { ResolveSpell(u.Id, Controller(u), sp, MoonWeek.SpellMastery(known.Mastery, sp.School, S.Week), cp, castTarget, castCell, fromHero: false); }
        finally { _castingCreature = null; }
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

    internal double HeroCastAtbReset(HeroState h, SpellDef sp)
    {
        double reset;
        if (IsMass(sp.Id) && h.HasPerk(MassSpells[MassToBase[sp.Id]].Perk)) reset = 0.5;   // p.313
        else
        {
            var mastery = sp.School == "MAGIC_SCHOOL_WARCRIES" ? h.SkillLevel(Sk.Shout) : h.SkillLevel(Sk.Sorcery);
            reset = Math.Min(3, (int)mastery) * 0.1;                                           // p.312
        }
        // Magis set (exe 00bbf610): 4+ pieces, or 2+ for a Wizard: reset + (1 − reset)·percent·0.01.
        int magis = Specials.Pieces(h, Specials.Magis);
        if (magis >= 4 || magis >= 2 && h.Class == "HERO_CLASS_WIZARD")
            reset = (1f - (float)reset) * (int)Stats.Get("ArtifactsSetsEffectsConsts/Magis_4_2Wizard_AfterCastATBLessPercents", 10) * 0.01f + (float)reset;
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
            if (t.IsWarMachine && !ExeWarMachineSpell(BaseSpell(sp.Id), Actor.IsHero(caster) ? S.Heroes[side] : null)) return "war machines can only be hit by damage spells";
            if (t.HasEffect(H5.Combat.Fx.Invisible) && !friendly) return "target is invisible";
            if (!t.Alive && sp.Id is not ("SPELL_RESURRECT" or "SPELL_ANIMATE_DEAD")) return "target is dead";
            if (sp.Id == "SPELL_RESURRECT" && (t.Has(Ab.Elemental) || t.Has(Ab.Mechanical) || IsUndeadLike(t))) return "cannot resurrect this creature";
            // exe FUN_0097c9f0 (spell 0x2a): Elemental and Mechanical creatures are immune to Raise Dead.
            if (sp.Id == "SPELL_ANIMATE_DEAD" && (t.Has(Ab.Elemental) || t.Has(Ab.Mechanical))) return "cannot raise this creature";
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
    internal void ResolveSpell(int caster, int side, SpellDef sp, Mastery m, double power, Unit? target, Cell? cell, bool fromHero)
    {
        double prevScale = _spellDamageScale;
        if (target != null) target = MagicMirror(side, sp, target);
        try { ResolveSpellCore(caster, side, sp, m, power, target, cell, fromHero); }
        finally { _spellDamageScale = prevScale; }
        // Mark of the Wizard: a spell striking another creature affects the marked stack as well.
        var mh = fromHero ? S.Heroes[side] : null;
        if (mh != null && target != null && mh.Links.TryGetValue(LinkMarkOfWizard, out var mid) && mid != target.Id
            && S.Unit(mid) is { Alive: true } marked && TargetingOf(sp) == SpellTargeting.Unit && !IsMass(sp.Id))
            ResolveSpellCore(caster, side, sp, m, power, marked, cell, fromHero);
        if (mh != null && target != null && IsDamageSpell(BaseSpell(sp.Id))) MatronSalvo(mh, target);
    }

    /// <summary>
    /// Base damage of a damaging spell (exe 00976b90): trunc(max(1, Base + PerPower·SP)) for the mastery,
    /// then trunc(×1.5) for an empowered variant. Both conversions run with the FPU in chop mode
    /// (00976c0d / 00976c53), so they truncate.
    /// </summary>
    internal double SpellBaseDamage(SpellDef baseDef, SpellDef cast, Mastery m, double power)
    {
        double dmg = ExeChop(Math.Max(1, baseDef.DamageAt(m).Eval(power)));
        if (cast.Id.StartsWith("SPELL_EMPOWERED_")) dmg = ExeChop(dmg * 1.5);
        return MoonWeek.SpellDamage((int)dmg, cast.Element, S.Week);   // exe 00976c73 → 00bcde80
    }

    void ResolveSpellCore(int caster, int side, SpellDef sp, Mastery m, double power, Unit? target, Cell? cell, bool fromHero)
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
                double dmg = SpellBaseDamage(baseDef, sp, m, power);
                if (id == "SPELL_DIVINE_VENGEANCE") dmg *= Math.Sqrt(target.Frags);                        // p.191
                // Call of the Wasp (exe 00976db0, spec 0x1a): trunc((level·perLevel + 1) · damage).
                if (id == "SPELL_WASP_SWARM" && hero != null && Spec(hero, "HERO_SPEC_CALL_OF_THE_WASP") is var wl && wl >= 0)
                    dmg = ExeChop((wl * (double)(float)Stats.Get("combat/HeroSpecializations/Ranger/CallOfTheWasp_DamageBonusPerLevel", 0.05) + 1) * dmg);
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
                double dmg = SpellBaseDamage(baseDef, sp, m, power);
                var victims = S.Living.Where(u => !u.IsObject && u.Cells().Any(cells.Contains)).ToList();
                // exe FUN_0085e7c0: every victim's damage is computed first, Magic Magnetism (FUN_0085e330) redistributes
                // the amounts, then they are dealt.
                var hit = new List<(Unit V, double D)>();
                foreach (var v in victims)
                {
                    if (!SpellHits(side, baseDef, v, hostile: true, hero, area: true)) continue;
                    affected.Add(v.Id);
                    hit.Add((v, SpellDamageFinal(v, baseDef, dmg, side, false, hero) ?? double.NaN));
                }
                var amounts = MagnetismRedistribute(hit);
                for (int i = 0; i < hit.Count; i++)
                {
                    var v = hit[i].V;
                    int dealt = double.IsNaN(hit[i].D) ? 0 : ApplySpellDamageFinal(v, baseDef, amounts[i], side, false);
                    if (v.Alive) SpellSideEffects(id, v, m, power, hero, first: true, side, victims.Count, dealt);
                }
                break;
            }
            case "SPELL_CHAIN_LIGHTNING":
            {
                if (target == null) return;
                // exe FUN_0085edb0: the whole chain is chosen first (FUN_00a5dbb0), then damage is dealt along it.
                var chain = ChainLightningTargets(target, baseDef, side);
                // The float base halves per hop (×0.5f @00e0ab70); each hop's base is truncated (FISTP in chop mode,
                // 0085ef1f) before protection (FUN_0097d600).
                var bases = ChainHopBases((int)SpellBaseDamage(baseDef, sp, m, power), chain.Count);
                // The chain resolver FUN_0085edb0 also runs Magic Magnetism (FUN_0085e330) over the computed amounts.
                var hops = new List<(Unit V, double D)>();
                var hopIndex = new List<int>();
                for (int i = 0; i < chain.Count; i++)
                {
                    var cur = chain[i];
                    if (cur.Alive && SpellHits(side, baseDef, cur, hostile: true, hero, area: i > 0))
                    {
                        affected.Add(cur.Id);
                        hops.Add((cur, SpellDamageFinal(cur, baseDef, bases[i], side, false, hero) ?? double.NaN));
                        hopIndex.Add(i);
                    }
                }
                var hopAmounts = MagnetismRedistribute(hops);
                for (int k = 0; k < hops.Count; k++)
                {
                    var cur = hops[k].V;
                    if (!double.IsNaN(hops[k].D)) ApplySpellDamageFinal(cur, baseDef, hopAmounts[k], side, false);
                    if (cur.Alive && hopIndex[k] == 0) SpellSideEffects(id, cur, m, power, hero, true, side);
                }
                break;
            }
            case "SPELL_ARMAGEDDON":
            {
                double dmg = SpellBaseDamage(baseDef, sp, m, power);
                double rock = baseDef.DurationAt(m).Eval(power);   // central 4x4 physical rock damage (p.189)
                int cx = S.Width / 2 - 2, cy = S.Height / 2 - 2;
                foreach (var v in S.Living.Where(u => !u.IsObject).ToList())
                {
                    if (SpellHits(side, baseDef, v, hostile: true, hero, area: true))
                    {
                        affected.Add(v.Id);
                        int dealt = DealSpellDamage(v, baseDef, dmg, side, false, hero, m);
                        if (v.Alive) SpellSideEffects(id, v, m, power, hero, true, side, dealt: dealt);
                    }
                    if (v.Alive && v.Cells().All(x => x.X >= cx && x.X < cx + 4 && x.Y >= cy && x.Y < cy + 4))
                        DealDamage(v, (int)rock, caster, AttackKind.Spell, "ARMAGEDDON_ROCK");
                }
                break;
            }
            case "SPELL_HOLY_WORD":
            case "SPELL_UNHOLY_WORD":
            {
                double dmg = SpellBaseDamage(baseDef, sp, m, power);
                foreach (var v in S.Living.Where(u => !u.IsObject && !u.IsWarMachine).ToList())
                {
                    // exe FUN_00977600: Holy Word affects Undead, Demonic and Demon-Raged (orc) creatures; Unholy Word
                    // everything that is neither Undead nor Demonic.
                    bool evilKind = id == "SPELL_HOLY_WORD" ? IsUndeadLike(v) || v.Has(Ab.Demonic) || v.Has(Ab.DemonRaged)
                                                            : IsUndeadLike(v) || v.Has(Ab.Demonic);
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
                double dmg = SpellBaseDamage(baseDef, sp, m, power);
                var f = AddField("FIREWALL", side, cells, baseDef.DurationAt(m).Eval(power), dmg);
                f.Power = power; f.Mastery = m; f.HeroCaster = hero != null;
                // exe FUN_0085cda0 → FUN_0085b790: every creature stack (vf 0x6c) or spawn (vf 0x8c) on the cells — not war
                // machines or siege objects — gets the field's damage (vf 0x1c, resistance rolled) and the fire rider (vf 0x24).
                foreach (var v in S.Living.Where(u => FirewallVictim(u) && u.Cells().Any(cells.Contains)).ToList())
                    if (SpellHits(side, baseDef, v, true, hero, area: true)) { affected.Add(v.Id); FirewallHit(v, f, hero, rollLuck: true); }
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
                double dmg = SpellBaseDamage(baseDef, sp, m, power);
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
                // Fire Affinity always summons Fire Elementals; otherwise the kind comes from the battlefield terrain
                // (exe FUN_00861630 → FUN_00861120).
                string kind = hero != null && hero.HasPerk("HERO_SKILL_FIRE_AFFINITY") ? "CREATURE_FIRE_ELEMENTAL" : TerrainElemental();
                int count = SummonElementalsCount(baseDef, m, power, hero);
                if (SummonStack(side, kind, MoonWeek.SummonCount(count, Db.Creature(kind).Element1, S.Week), sp.Id, atb: S.Rng.Next(6) * 0.05) != null)
                {
                    // Elemental Balance on the opposing hero: the opposite elemental, same count (exe 00861630 → 00861500).
                    string opp = OppositeElemental(kind);
                    ElementalBalanceSummon(side, opp, Math.Max(1, MoonWeek.SummonCount(count, Db.Creature(opp).Element1, S.Week)), sp.Id);
                }
                break;
            }
            case "SPELL_CONJURE_PHOENIX":
            {
                if (S.Units.Any(u => u.Alive && u.Side == side && u.CreatureId == "CREATURE_PHOENIX" && u.Summoned)) return;
                double div = Math.Max(1, baseDef.DamageAt(m).Base);
                double p = Math.Ceiling(power / div);
                void PhoenixStats(Unit ph)
                {
                    ph.BaseAttack = ph.BaseDefence = 10 + 2 * casterLevel;      // p.194
                    ph.BaseMinDamage = (int)(10 * p); ph.BaseMaxDamage = (int)(15 * p);
                    ph.BaseHealth = (int)(300 + 30 * p); ph.MaxHealthCache = ph.TopHealth = ph.BaseHealth;
                }
                if (SummonStack(side, "CREATURE_PHOENIX", 1, sp.Id, atb: S.Rng.Next(6) * 0.05) is { } ph)
                {
                    PhoenixStats(ph);
                    // Elemental Balance on the opposing hero: a phoenix with the caster's stats (exe 00862130).
                    if (ElementalBalanceSummon(side, "CREATURE_PHOENIX", 1, sp.Id) is { } ph2) PhoenixStats(ph2);
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
                SiegeEarthquake(side, hero, baseDef, m, power);   // only affects town fortifications (sieges)
                break;
            case "SPELL_TELEPORT":
            {
                if (target == null || cell is not Cell to) return;
                Teleport(target, to);
                ApplyTeleportAssault(target);
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
                if (target != null && ApplyEffectSpell(caster, side, baseDef, m, power, target, casterLevel, hero))
                {
                    affected.Add(target.Id);
                    if (hero != null && baseDef.School == "MAGIC_SCHOOL_LIGHT" && Controller(target) == side) SpecAfterFriendlyLightSpell(hero, target);
                    // Disrupter: Vulnerability (SPELL_DISRUPTING_RAY, id 0xd — its in-game name; base damage 0) also deals
                    // trunc((level−1)·PerLevel / trunc(Divisor) + Disrupter_Damage), then the usual spell damage
                    // modifiers (exe FUN_00976db0, spell 0xd + spec 0x2a; chop-mode conversions).
                    if (id == "SPELL_DISRUPTING_RAY" && hero != null && Spec(hero, "HERO_SPEC_DISRUPTER") is var dl && dl >= 0 && target.Alive)
                    {
                        double div = Math.Max(1, ExeChop(Stats.Get("combat/HeroSpecializations/Wizard/Disrupter_DamageLevelDivisor", 3)));
                        double dmg = ExeChop((dl - 1) * Stats.Get("combat/HeroSpecializations/Wizard/Disrupter_DamagePerLevel", 10) / div
                                              + Stats.Get("combat/HeroSpecializations/Wizard/Disrupter_Damage", 10));
                        DealSpellDamage(target, baseDef, dmg, side, ignoreResist: false, hero: hero, mastery: m);
                    }
                }
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
    internal bool SpellHits(int side, SpellDef sp, Unit t, bool hostile, HeroState? hero, bool area = false)
    {
        string id = BaseSpell(sp.Id);
        bool physical = id is "SPELL_MAGIC_FIST";
        if (t.HasEffect(H5.Combat.Fx.Invisible) && Controller(t) != side && !area) return false;
        // Immunities (exe FUN_0097c9f0 → 100): Irresistible Magic lets only a damage spell's share through (FUN_0097d2a0).
        if (ExeSpellImmune(t, sp, id, hero))
        {
            if (ExeDamageSpell(id, hero) && hero != null && hero.SkillLevel(Sk.Invocation) > Mastery.None) { _resistedFullProtection = true; return true; }
            Emit(new SpellResisted(t.Id, sp.Id));
            return false;
        }
        // Stormcaller: the caster's Air (lightning) spells skip the resistance roll; immunities still apply
        // (exe FUN_0097c9f0, element 1 = Air). Staff of Magi on the caster skips it too (0097cf89).
        bool irresistible = hero != null && hero.Specialization == "HERO_SPEC_STORMCALLER" && sp.Element == Elements.Air
                            || IgnoresResistance(hero);
        // The resistance chance is 0 only for a non-hostile spell (Target ≠ TARGET_HOSTILE, FUN_00a34520) on a stack of the
        // caster's side (FUN_004e5750) — exe FUN_0097c9f0 @ 0097cf3a; a hostile spell hitting own stacks (Fireball,
        // Armageddon, Firewall) is resisted like on enemies. The damage resolvers pass the roll flag to FUN_0097d600 → FUN_0097d2a0
        // (0097da71 / 0097dec0 single target, 0085e8bf area, 0085ef3c chain, 0085f630 Armageddon / Words, 0098abf0 Firewall cast).
        // The engine rolled for enemy stacks only. (The caller's `hostile` no longer decides it.)
        bool resistible = sp.Target == "TARGET_HOSTILE" || Controller(t) != side;
        if (resistible && !physical && !irresistible && RollResistance(t, MagicResistanceChance(t)))
        {
            // A resisted damage spell is 100 % protection; Irresistible Magic still lets its share through (0097d2a0,
            // fan manual p.322).
            if (IsDamageSpell(id) && hero != null && hero.SkillLevel(Sk.Invocation) > Mastery.None) { _resistedFullProtection = true; return true; }
            Emit(new SpellResisted(t.Id, sp.Id));
            return false;
        }
        return true;
    }

    static bool IsDamageSpell(string id) => id is "SPELL_MAGIC_ARROW" or "SPELL_LIGHTNING_BOLT" or "SPELL_ICE_BOLT" or "SPELL_IMPLOSION"
        or "SPELL_FIREBALL" or "SPELL_METEOR_SHOWER" or "SPELL_STONE_SPIKES" or "SPELL_FROST_RING" or "SPELL_CHAIN_LIGHTNING"
        or "SPELL_ARMAGEDDON" or "SPELL_FIREWALL" or "SPELL_LAND_MINE" or "SPELL_DEEP_FREEZE" or "SPELL_HOLY_WORD" or "SPELL_UNHOLY_WORD"
        or "SPELL_WASP_SWARM" or "SPELL_DIVINE_VENGEANCE" or "SPELL_PLAGUE";

    /// <summary>Spell damage with magic protection, Irresistible Magic, Warlock's Luck, Exorcism and Cold Death.</summary>
    int DealSpellDamage(Unit t, SpellDef sp, double dmg, int side, bool ignoreResist, HeroState? hero = null, Mastery mastery = Mastery.None,
        bool rollLuck = true)
    {
        if (!t.Alive) return 0;
        return SpellDamageFinal(t, sp, dmg, side, ignoreResist, hero, rollLuck) is double d ? ApplySpellDamageFinal(t, sp, d, side, ignoreResist) : 0;
    }

    /// <summary>The damage a spell would deal to <paramref name="t"/> after protection, luck and chains (null: none).</summary>
    double? SpellDamageFinal(Unit t, SpellDef sp, double dmg, int side, bool ignoreResist, HeroState? hero, bool rollLuck = true)
    {
        string id = BaseSpell(sp.Id);
        bool fullProtection = _resistedFullProtection;
        _resistedFullProtection = false;
        dmg *= _spellDamageScale;
        // Caster-side modifiers before protection (exe FUN_00976db0): elemental artifacts ×1.5, and spells cast by
        // elementals ×Neutral/SpellDamageMultiplier.
        dmg = ElementArtifactDamage(hero, sp.Element, dmg);
        if (IsElementalCaster(_castingCreature))
            dmg = ExeChop(dmg * (double)(float)Stats.Get("combat/Creatures/Neutral/SpellDamageMultiplier", 0.3));
        dmg = PreProtectionPerks(dmg, t, sp, hero);
        if (!ignoreResist)
        {
            double mult = SpellDamageMultiplier(t, sp, side, casterIsHero: hero != null) * ElementProtectionArtifacts(t, sp.Element);
            bool immune = (sp.Element != Elements.None && ElementImmune(t, sp.Element)) || t.Has(Ab.ImmuneMagic);
            var inv = hero != null ? hero.SkillLevel(Sk.Invocation) : Mastery.None;
            if ((immune || fullProtection) && inv == Mastery.None) return null;
            // Irresistible Magic (p.140, p.322): reduces protection, and immune / resisted targets still take a share.
            dmg = ApplyProtectionPercent(dmg, mult, immune || fullProtection, inv);
        }
        if (hero != null && rollLuck)
        {
            if (hero.HasPerk(Sk.WarlocksLuck) && sp.School == "MAGIC_SCHOOL_DESTRUCTIVE")
            {
                int luck = hero.Luck;
                if (luck > 0 && S.Rng.Chance(Math.Min(0.5, luck * 0.1))) { dmg *= 2; Emit(new LuckProc(Actor.Hero(side), true)); }
            }
        }
        // Elemental Chains add their share on top of the protected damage (exe: FUN_0097d600 + FUN_00977e60 in the
        // bolt/chain/area resolvers 0085e7c0/0085edb0/0085f330); the Holy/Unholy Word resolver (00977600) has none.
        if (id is not ("SPELL_HOLY_WORD" or "SPELL_UNHOLY_WORD"))
            dmg += ElementalChainExtra(hero, sp, t, Math.Max(0, (int)Math.Floor(dmg)));
        return dmg;
    }

    int ApplySpellDamageFinal(Unit t, SpellDef sp, double dmg, int side, bool ignoreResist)
    {
        if (!t.Alive) return 0;
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

    static bool FirewallVictim(Unit u) =>
        !u.IsWarMachine && (!u.IsObject || !u.CreatureId.StartsWith("SIEGE_", StringComparison.Ordinal));

    /// <summary>
    /// One Firewall hit (the field object of exe FUN_0098ade0, interface vtable 00e61818): vf 0x1c (FUN_0098abf0) takes the
    /// base damage stored at the cast (+0x78, FUN_00976b90) through FUN_0097d600 with the caster kept in the field (+0x64) —
    /// caster artifacts, Exorcism / Cold Death, protection, Irresistible Magic — plus Elemental Chains (FUN_00977e60); vf 0x24
    /// (FUN_0098a920) deals it through the fire rider FUN_004e6770 (Master of Fire, Set Afire). Same path on the cast
    /// (FUN_0085b790) and on entering (FUN_00a66050 → FUN_0085bfe0 / FUN_0085c0c0 → FUN_0085b530); the engine's end-of-turn
    /// hit uses it too (its exe trigger was not traced). The engine's entry / turn hits used no caster at all.
    /// </summary>
    internal int FirewallHit(Unit u, FieldObject f, HeroState? hero, bool rollLuck = false)
    {
        var sp = Db.Spell("SPELL_FIREWALL")!;
        int dealt = DealSpellDamage(u, sp, f.Damage, f.Side, false, hero, f.Mastery, rollLuck);
        if (u.Alive) SpellSideEffects(sp.Id, u, f.Mastery, f.Power, hero, true, f.Side, dealt: dealt);
        return dealt;
    }

    internal int FirewallHit(Unit u, FieldObject f) => FirewallHit(u, f, f.HeroCaster ? S.Heroes[f.Side] : null);

    void DealSpellDamageRaw(Unit t, int dmg, string by) => DealDamage(t, dmg, -1, AttackKind.Spell, by);

    /// <summary>Freeze/stun/armour-crush/ATB riders of damage spells.</summary>
    void SpellSideEffects(string id, Unit t, Mastery m, double power, HeroState? hero, bool first, int side, int targetsHit = 1, int dealt = 0)
    {
        if (id == "SPELL_WASP_SWARM")
        {
            var sp = Db.Spell(id)!;
            // exe FUN_0097d7b0 (spell 0x27): ATB −= duration value, ×(1 + ((level+2)/3)·ATBReducePerLevel3) with Call of
            // the Wasp (spec 0x1a); the result is clamped to 0..1.
            double drop = WaspAtbDrop(sp, m, power, hero);
            if (drop > 0) SetAtb(t.Id, Math.Clamp(GetAtb(t.Id) - drop, 0, 1));
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
        // Master of Fire and Set Afire live in the fire rider (exe FUN_004e6770), which only the area / Armageddon / fire
        // field resolvers call — Magic Arrow's single-target path (0097d7b0 case 1) deals plain damage (004e6b60).
        bool fireRider = id is "SPELL_FIREBALL" or "SPELL_FIREWALL" or "SPELL_ARMAGEDDON";
        if (hero.HasPerk(Sk.MasterFire) && fireRider
            && !HeroOf(t).HasPerk(Sk.FireResistance) && !HeroOf(t).HasPerk(Sk.BarbFireResistance))
            ApplyArmorCrush(t, side);
        if (hero.HasPerk(Sk.Ignite) && fireRider && dealt > 0 && Controller(t) != side)
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Ignite, Value = SetAfireDamage(t, dealt),
                Remaining = Stats.Get("combat/HeroSkills/Runemage/SetAfire_DamageDurationInRounds", 3), SourceSide = side });
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
        var eff = MakeSpellEffect(id, sp, m, power, side, casterLevel, value, duration, positive);
        if (id == "SPELL_CURSE" && hero != null && Spec(hero, "HERO_SPEC_MALEDICTOR") is var ml && ml >= 0) eff.Value2 = (ml + 2) / 3;
        AddEffect(t, eff);
        if (!positive) RageEvent(t, "ReceiveCurseRP");
        return true;
    }

    /// <summary>
    /// Teleport Assault (exe FUN_0097d7b0 case 0x20): when the teleported stack's own hero has the perk (skill 0x8a) and the
    /// stack is not disabled (FUN_004e4a20: Blind 0x13, Frozen 0xc9, Sabotage 0x14a, Sleeping Strike 0x150, Seduce 0x148),
    /// its ATB gains 0.5 (DAT_00e0ab70) clamped to 0..1.25 (FUN_004738f0(x, 0, 1.25)) — not capped at 1 as the fan manual says.
    /// </summary>
    internal void ApplyTeleportAssault(Unit t)
    {
        if (!HeroOf(t).HasPerk(Sk.TeleportAssault)) return;
        if (t.HasEffect("SPELL_BLIND") || t.HasEffect(H5.Combat.Fx.Frozen) || t.HasEffect(H5.Combat.Fx.Sabotaged)
            || t.HasEffect(H5.Combat.Fx.Sleep) || t.HasEffect(H5.Combat.Fx.Seduced)) return;
        AddAtb(t.Id, 0.5, 1.25);
    }

    /// <summary>
    /// Cleansing chance per effect (exe FUN_00bbe8b0, 00bbe976..00bbe9dc): pct = trunc(clamp(base·100 + 3·(dispeller level −
    /// effect caster level), 0, 100)) in chop mode, then halved (integer) for an Eternal Light effect; rolled with
    /// FUN_00a5a170 (Range(0,100) &lt; pct, no second roll).
    /// </summary>
    internal static int DispelPercent(double baseChance, int dispellerLevel, int effectCasterLevel, bool eternalLight)
    {
        float v = (float)baseChance * 100f + 3 * (dispellerLevel - effectCasterLevel);
        int pct = (int)Math.Clamp(v, 0f, 100f);
        return eternalLight ? pct / 2 : pct;
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
            bool eternal = e.SourceSide >= 0 && S.Heroes[e.SourceSide].HasPerk(Sk.EternalLight) && e.School == "MAGIC_SCHOOL_LIGHT";
            if (RollPercent(t, DispelPercent(baseChance, dispellerLevel, e.CasterLevel, eternal), false)) RemoveEffect(t, e.Id);
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

    /// <summary>
    /// Elemental kind for the battlefield terrain (exe FUN_00861120): candidate list per terrain type, one picked
    /// with the battle RNG. Dirt/subterranean: water or earth; grass: air or earth; sand: fire or air; snow: water
    /// or air; lava: fire or earth; anything else: earth.
    /// </summary>
    internal string TerrainElemental()
    {
        string[] c = S.Terrain switch
        {
            0 or 5 => new[] { "CREATURE_WATER_ELEMENTAL", "CREATURE_EARTH_ELEMENTAL" },
            1 => new[] { "CREATURE_AIR_ELEMENTAL", "CREATURE_EARTH_ELEMENTAL" },
            2 => new[] { "CREATURE_FIRE_ELEMENTAL", "CREATURE_AIR_ELEMENTAL" },
            3 => new[] { "CREATURE_WATER_ELEMENTAL", "CREATURE_AIR_ELEMENTAL" },
            4 => new[] { "CREATURE_FIRE_ELEMENTAL", "CREATURE_EARTH_ELEMENTAL" },
            _ => new[] { "CREATURE_EARTH_ELEMENTAL" },
        };
        return c[S.Rng.Next(c.Length)];
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
                foreach (var e in t.Effects.Where(ExorcismRemoves).Select(e => e.Id).ToList())   // exe FUN_008d9b30 → FUN_004e4000
                    RemoveEffect(t, e);
                break;
            case "SPELL_RUNE_OF_ELEMENTAL_IMMUNITY":
            {
                var (a, b) = RollElementalImmunity();   // exe FUN_00c54620; exe element codes (1 air, 2 fire, 3 water, 4 earth)
                AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneElementalImmunity, Value = a, Value2 = b, Positive = true, Dispellable = false });
                break;
            }
            case "SPELL_RUNE_OF_ETHEREALNESS": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneEtherealness, Remaining = 1, Positive = true, Dispellable = false }); break;
            // exe FUN_00c9d290: vf 0x25c(id, 1) — lasts one action, like Rune of Charge (FUN_00b40aa0).
            case "SPELL_RUNE_OF_STUNNING": AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneThunderclap, Actions = 1, Positive = true, Dispellable = false }); break;
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
                if (DragonformForbidden(t)) return false;   // exe FUN_004b4460 → FUN_004bb000
                AddEffect(t, new Effect { Id = H5.Combat.Fx.RuneDragonform, Remaining = 1, Positive = true, Dispellable = false });
                break;
            default: return false;
        }
        return true;
    }

    void Warcry(string id, int side, HeroState? hero, Unit? target, List<int> affected, SpellDef sp)
    {
        if (hero == null) return;
        int level = WarcryLevel(hero);
        double rageMult = hero.HasPerk(Sk.ShoutOfRage) ? (id == "SPELL_WARCRY_CALL_OF_BLOOD"
            ? Stats.Get("combat/HeroSkills/Barbarian/VoiceOfRage_RageMultCallOfBlood", 1.5) : Stats.Get("combat/HeroSkills/Barbarian/VoiceOfRage_RageMultOther", 2)) : 1;
        // exe FUN_009be6c0 → FUN_008a56f0: every warcry but Call of Blood gives the caster's army FUN_009bdbd0's rage points.
        if (id != "SPELL_WARCRY_CALL_OF_BLOOD")
        {
            int armyRp = WarcryRagePoints(id, hero);
            foreach (var u in S.LivingOf(side).Where(u => u.Has(Ab.DemonRaged)).ToList()) AddRage(u, armyRp);
        }
        switch (id)
        {
            case "SPELL_WARCRY_RALLING_CRY":
            {
                int bonus = WarcryLevelBonus("combat/Spells/Warcries/RallingCry_MoraleBonus", level);
                foreach (var u in S.LivingOf(side).Where(u => !WarcryTargetInvalid(id, u)).ToList())
                {
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.RallyingCry, Value = bonus, Remaining = Stats.Get("combat/Spells/Warcries/RallingCry_Duration", 3), Positive = true, SourceSide = side });
                    affected.Add(u.Id);
                }
                break;
            }
            case "SPELL_WARCRY_CALL_OF_BLOOD":
                if (target != null && !WarcryTargetInvalid(id, target)) { AddRage(target, WarcryRagePoints(id, hero)); affected.Add(target.Id); }
                break;
            case "SPELL_WARCRY_WORD_OF_THE_CHIEF":
                if (target != null && !WarcryTargetInvalid(id, target))
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
                if (target != null && !WarcryTargetInvalid(id, target) && RollPercent(target, FearMyRoarPercent(level, target), secondRoll: false))
                {
                    Emit(new AbilityTriggered(Actor.Hero(side), "FEAR_MY_ROAR", target.Id));
                    SetAtb(target.Id, 0);
                    affected.Add(target.Id);
                }
                break;
            case "SPELL_WARCRY_BATTLECRY":
            {
                int att = WarcryLevelBonus("combat/Spells/Warcries/Battlecry_OffenceBonus", level);
                foreach (var u in S.LivingOf(side).Where(u => u.Kind == UnitKind.Creature).ToList())
                {
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.Battlecry, Value = att, Value2 = BattlecrySpeedBonus(level), Remaining = 1, Positive = true, SourceSide = side });
                    affected.Add(u.Id);
                }
                break;
            }
            case "SPELL_WARCRY_SHOUT_OF_MANY":
                if (target != null)
                {
                    int dmg = ShoutOfManyDamage(side);
                    if (dmg > 0) DealDamage(target, dmg, Actor.Hero(side), AttackKind.Spell, sp.Id);
                    affected.Add(target.Id);
                }
                break;
        }
    }
}
