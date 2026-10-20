using H5.Core.Data;

namespace H5.Combat;

// Hero perks and specialisations: Magic Mirror (+Radiant "Spell Twister"), Counterspell, Mark of the Wizard,
// Imbue Arrow / Imbue Ballista, Elemental Vision, Empowered Spells, Banish, Remote Control, Plague Tent,
// Matron Salvo ("Coven Mistress") and Seduction. Exe evidence in docs/rules/EXE_FINDINGS.md.
public sealed partial class Battle
{
    const string LinkCounterspell = "COUNTERSPELL", LinkMarkOfWizard = Sk.MarkOfWizard, FxSeducing = "FX_SEDUCING";
    double _spellDamageScale = 1;

    IEnumerable<AbilityInfo> HeroAbilities2(HeroState h)
    {
        if (h.HasPerk("HERO_SKILL_COUNTERSPELL") && !h.Links.ContainsKey(LinkCounterspell) && CounterspellArmable(h)) yield return new(LinkCounterspell, AbilityTargeting.None);
        if (h.HasPerk(Sk.MarkOfWizard)) yield return new("MARK_OF_WIZARD", AbilityTargeting.Enemy);
        if (h.HasPerk(Sk.Banish) && S.Living.Any(u => u.Summoned && Controller(u) != h.Side)) yield return new("BANISH", AbilityTargeting.None);
        if (h.HasPerk("HERO_SKILL_MELT_ARTIFACT") && S.Units.Any(u => u.Side == h.Side && u.Kind == UnitKind.Creature && u.ArtifactEffects >= 0 && !u.Removed))
            yield return new("CONSUME_ARTIFACT", AbilityTargeting.Friend);
        // Imbuing takes no turn, so it may be changed at most once per hero turn.
        if (h.HasPerk(Sk.ImbueArrow) && h.AbilityUses.GetValueOrDefault("IMBUE_TURN", -1) != S.TurnCounter)
            foreach (var s in HeroSpells(h).Where(s => s != h.ImbuedSpell))
                if (Db.Spell(s) is { } sp && sp.Target == "TARGET_HOSTILE" && !IsMass(s) && TargetingOf(sp) == SpellTargeting.Unit
                    && sp.School is "MAGIC_SCHOOL_DESTRUCTIVE" or "MAGIC_SCHOOL_DARK")
                    yield return new("IMBUE_ARROW:" + s, AbilityTargeting.None);
    }

    /// <summary>Returns true when the hero ability was one of these; sets the ATB reset.</summary>
    bool DoHeroAbility2(HeroState h, UseAbilityCmd c, Unit? t, ref double reset)
    {
        switch (c.Ability)
        {
            case LinkCounterspell:
                h.Links[LinkCounterspell] = 1;
                return true;
            case "CONSUME_ARTIFACT":
                if (t == null || t.Side != h.Side || t.ArtifactEffects < 0) return false;
                ConsumeArtifact(h, t, ref reset);
                return true;
            case "MARK_OF_WIZARD":
                h.Links[LinkMarkOfWizard] = t!.Id;
                reset = 0.5;                                          // fan manual p.312
                return true;
            case "BANISH":
            {
                // 10*(Level + Summoning mastery) to every enemy summoned or gated stack (fan manual, Banish).
                int dmg = (int)(Stats.Get("combat/HeroSkills/Wizard/Unsummon_DamagePerHeroLevel", 10) * h.Level
                              + Stats.Get("combat/HeroSkills/Wizard/Unsummon_DamagePerSkillMastery", 10) * (int)h.SkillLevel(Sk.Summoning));
                foreach (var u in S.Living.Where(u => u.Summoned && Controller(u) != h.Side).ToList())
                    DealDamage(u, dmg, Actor.Hero(h.Side), AttackKind.Ability, "BANISH");
                return true;
            }
        }
        if (c.Ability.StartsWith("IMBUE_ARROW:"))
        {
            h.ImbuedSpell = c.Ability["IMBUE_ARROW:".Length..];
            h.AbilityUses["IMBUE_TURN"] = S.TurnCounter;
            reset = -1;                                               // Imbue Arrow takes no ATB (p.312)
            return true;
        }
        return false;
    }

    /// <summary>Imbue Arrow / Imbue Ballista: cast the imbued spell on the struck target, paying its mana.</summary>
    void ImbuedCast(HeroState h, Unit target)
    {
        if (h.ImbuedSpell == null || !target.Alive || Db.Spell(h.ImbuedSpell) is not { } sp) return;
        int cost = HeroManaCost(h, sp);
        if (h.Mana < cost) { h.ImbuedSpell = null; return; }
        h.Mana -= cost;
        Emit(new ManaChanged(Actor.Hero(h.Side), h.Mana));
        ResolveSpell(Actor.Hero(h.Side), h.Side, sp, HeroMastery(h, sp), HeroSpellPower(h, sp), target, null, fromHero: true);
        if (target.Alive) SetAtb(target.Id, GetAtb(target.Id) - Stats.Get("combat/HeroSkills/Ranger/ImbueArrow_ATBDropOnCast", 0));
    }

    /// <summary>
    /// Magic Mirror (exe 00983150): a hostile single-target spell on a stack whose hero has Magic Mirror is
    /// reflected with chance round(level·MagicMirrorLevelMultiplier + 10)%. With Spell Twister (HERO_SPEC_RADIANT)
    /// a second roll of round((Radiant_MirrorProb + level·Radiant_MirrorProbPerLevel)·100)% sends it to one of the
    /// caster's own stacks; otherwise the new target is any random stack.
    /// </summary>
    Unit? MagicMirror(int casterSide, SpellDef sp, Unit target)
    {
        if (sp.Target != "TARGET_HOSTILE" || IsMass(sp.Id) || TargetingOf(sp) != SpellTargeting.Unit) return target;
        var h = S.Heroes[Controller(target)];
        if (!h.Present || Controller(target) == casterSide || !h.HasPerk(Sk.MagicMirror)) return target;
        int chance = (int)Math.Round(h.Level * Stats.Get("combat/HeroSkills/MagicMirrorLevelMultiplier", 2) + 10);
        if (!RollPercent(chance)) return target;
        List<Unit> pool = new();
        if (h.Specialization == "HERO_SPEC_RADIANT")
        {
            int twist = (int)Math.Round((Stats.Get("combat/HeroSpecializations/Wizard/Radiant_MirrorProb", 0.4)
                                        + Stats.Get("combat/HeroSpecializations/Wizard/Radiant_MirrorProbPerLevel", 0.02) * h.Level) * 100);
            if (RollPercent(twist)) pool = S.Living.Where(u => !u.IsObject && !u.IsWarMachine && Controller(u) == casterSide).ToList();
        }
        if (pool.Count == 0) pool = S.Living.Where(u => !u.IsObject && !u.IsWarMachine && u.Id != target.Id).ToList();
        if (pool.Count == 0) return target;
        var next = pool[S.Rng.Next(pool.Count)];
        Emit(new AbilityTriggered(Actor.Hero(h.Side), Sk.MagicMirror, next.Id));
        return next;
    }

    /// <summary>Counterspell: the armed enemy hero negates the next spell by paying twice its mana cost (exe FUN_00978670:
    /// any caster, hero or creature; cost = the caster's normal cost FUN_009781e0(0); not warcries / special spells).</summary>
    internal bool Counterspelled(int casterSide, SpellDef sp, int cost)
    {
        var e = Enemy(casterSide);
        if (!Counterable(sp) || !e.Present || !e.Links.ContainsKey(LinkCounterspell) || e.Mana < 2 * cost) return false;
        e.Links.Remove(LinkCounterspell);
        e.Mana -= 2 * cost;
        Emit(new ManaChanged(Actor.Hero(e.Side), e.Mana));
        Emit(new AbilityTriggered(Actor.Hero(e.Side), LinkCounterspell));
        return true;
    }

    /// <summary>
    /// Elemental Chains on a damage spell (exe FUN_00977e60 → FUN_004b12c0 → FUN_004b0ef0): only for a Warlock
    /// (hero class 6) with Elemental Vision (skill 0x48), when the spell's element is the opposite of the target's
    /// (FUN_004b0d40: Air↔Earth, Fire↔Water). The multiplier (FUN_004b0d80, single precision) is
    /// m = 1 + x with x = Invocation·ElementalChainBonusPerInvocationTrained + altars·ElementalChainBonusPerAltarOfChaos,
    /// x doubled by Absolute Chains (0x96) and multiplied by Runic_2Warlock_ElementalVisionMultiplier (RPGStats+0x11bc)
    /// with two or more Runic set pieces (set 9). The extra damage is trunc(D·m − D) of the damage after protection
    /// (FPU chop, 00977f58); the target's element is then re-rolled (FUN_004b0e90).
    /// </summary>
    internal int ElementalChainExtra(HeroState? hero, SpellDef sp, Unit t, int damage)
    {
        if (hero == null || damage <= 0 || hero.Class != "HERO_CLASS_WARLOCK" || !hero.HasPerk(Sk.ElementalVision)) return 0;
        string opposite = sp.Element switch
        {
            Elements.Air => Elements.Earth, Elements.Earth => Elements.Air,
            Elements.Fire => Elements.Water, Elements.Water => Elements.Fire, _ => "",
        };
        if (opposite.Length == 0 || t.Element1 != opposite) return 0;
        float x = (int)hero.SkillLevel(Sk.Invocation) * (float)Stats.Get("combat/HeroSkills/Warlock/ElementalChainBonusPerInvocationTrained", 0.1)
                  + hero.ElementalAltars * (float)Stats.Get("combat/HeroSkills/Warlock/ElementalChainBonusPerAltarOfChaos", 0.1);
        if (hero.HasPerk("HERO_SKILL_ABSOLUTE_CHAINS")) x += x;
        float m = Specials.Pieces(hero, Specials.Runic) >= 2
            ? x * (float)Stats.Get("ArtifactsSetsEffectsConsts/Runic_2Warlock_ElementalVisionMultiplier", 2) + 1f
            : x + 1f;
        if (m <= 1f) return 0;
        int extra = ExeChop((double)damage * m - damage);
        var all = new[] { Elements.Air, Elements.Earth, Elements.Fire, Elements.Water };
        t.Element1 = all[S.Rng.Next(all.Length)];
        Emit(new AbilityTriggered(Actor.Hero(hero.Side), Sk.ElementalVision, t.Id));
        return Math.Max(0, extra);
    }

    /// <summary>Matron Salvo (Coven Mistress): after the hero hits with a damaging spell, each witch stack may shoot the target for free.</summary>
    void MatronSalvo(HeroState h, Unit target)
    {
        if (h.Specialization != "HERO_SPEC_MATRON_SALVO" || !target.Alive) return;
        // exe FUN_00978e10: every stack of creature 0x51 / 0x52 (Matron, Matriarch — not the Shadow Mistress) rolls the
        // ability roll (no attacker) with chance 10 + 2·level; the stacks that pass then attack the target together.
        var shooters = S.LivingOf(h.Side).Where(w => w.CreatureId is "CREATURE_MATRON" or "CREATURE_MATRIARCH").ToList()
            .Where(_ => HeroRoll(MatronSalvoChance(h))).ToList();
        foreach (var w in shooters)
        {
            if (!target.Alive) break;
            if (!w.Alive) continue;
            Emit(new AbilityTriggered(w.Id, "MATRON_SALVO", target.Id));
            Strike(w, target, new Hit { Kind = AttackKind.Ranged }, pattern: true);
        }
    }

    /// <summary>Remote Control: at combat start one random enemy war machine other than the Catapult switches sides.</summary>
    void RemoteControlAtStart()
    {
        for (int side = 0; side < 2; side++)
        {
            var h = S.Heroes[side];
            if (!h.Present || !h.HasPerk("HERO_SKILL_REMOTE_CONTROL")) continue;
            var machines = S.LivingOf(1 - side).Where(u => u.IsWarMachine && u.CreatureId != "WAR_MACHINE_CATAPULT").ToList();
            if (machines.Count == 0) continue;
            var m = machines[S.Rng.Next(machines.Count)];
            m.Side = side;
            Emit(new AbilityTriggered(Actor.Hero(side), "HERO_SKILL_REMOTE_CONTROL", m.Id));
        }
    }

    /// <summary>Seduction (fan manual p.181): duration 2 + log2(seducer power / target power) turns; unusable if ≤ 0.</summary>
    string? Seduce(Unit u, Unit t)
    {
        // exe 0097f610 case 0x148: not Immunity to Mind Control (derived flag), a creature not of the CREATURE_SUCCUBUS family
        // (FUN_004babf0 != 0x15) and not hypnotized (effect 0x14) — Immunity to Hypnotize is not tested.
        if (MindImmune(t) || t.HasEffect("SPELL_HYPNOTIZE") || t.CreatureId.Contains("SUCCUBUS")) return "target cannot be seduced";
        // exe 00985b4e: the charge is spent, then FUN_009774a0 gives the duration; nothing happens when it is 0.
        u.AbilityUses["ABILITY_SEDUCE"] = 1;
        double duration = SeduceDuration(u.Count, u.Power, t.Count, t.Power);
        if (duration <= 0) { Emit(new AbilityTriggered(u.Id, "ABILITY_SEDUCE", t.Id)); return null; }
        Emit(new AbilityTriggered(u.Id, "ABILITY_SEDUCE", t.Id));
        AddEffect(t, new Effect { Id = H5.Combat.Fx.Seduced, SourceSide = Controller(u), SourceUnit = u.Id, Remaining = duration, Positive = false, SpellLevel = 3, School = "MAGIC_SCHOOL_DARK" });
        AddEffect(u, new Effect { Id = FxSeducing, SourceUnit = t.Id, Remaining = duration, Positive = true, Dispellable = false });
        return null;
    }

    /// <summary>Seduction ends when the seducing stack dies.</summary>
    void EndSeductionOf(Unit dead)
    {
        foreach (var v in S.Units.Where(v => v.GetEffect(H5.Combat.Fx.Seduced)?.SourceUnit == dead.Id).ToList())
            RemoveEffect(v, H5.Combat.Fx.Seduced);
    }
}
