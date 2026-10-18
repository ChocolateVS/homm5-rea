using H5.Core.Data;

namespace H5.Combat;

// Hero direct attacks, hero special abilities and creature activated abilities.
public sealed partial class Battle
{
    public enum AbilityTargeting { None, Friend, Enemy, Any, Cell, DeadFriend }

    public sealed record AbilityInfo(string Id, AbilityTargeting Targeting, bool NeedsAdjacency = false);

    // ------------------------------------------------------------------ hero attack (p.302-303)

    /// <summary>Creatures killed by a hero strike: linear in hero level per tier between DefaultStats level-1 and level-31 values.</summary>
    public double HeroKills(int level, int tier)
    {
        var l1 = Stats.Array("combat/HeroSkills/Melee/HeroDamageLevel1");
        var l30 = Stats.Array("combat/HeroSkills/Melee/HeroDamageLevel30");
        int i = Math.Clamp(tier, 1, 8) - 1;
        double a = l1.Length > i ? l1[i] : 1, b = l30.Length > i ? l30[i] : 1;
        return a + (b - a) * (level - 1) / 30.0;
    }

    /// <summary>
    /// Hero strike base damage (exe FUN_00bc1b90): kills fraction for (level + bonus) x target health, then the
    /// Offender specialisation trunc((level x Offender_HeroAttackDamageBonusPerLevel + 1) x damage); at least 1.
    /// </summary>
    public int HeroStrikeDamage(HeroState h, Unit t, int levelBonus)
    {
        int dmg = Math.Max(1, (int)Math.Floor(HeroKills(h.Level + levelBonus, t.IsObject ? 8 : t.Tier) * MaxHealthOf(t)));
        if (h.Specialization == "HERO_SPEC_OFFENDER")
            dmg = (int)(((float)h.Level * (float)Stats.Get("combat/HeroSpecializations/Barbarian/Offender_HeroAttackDamageBonusPerLevel", 0.05) + 1f) * dmg);
        return Math.Max(1, dmg);
    }

    string? DoHeroAttack(HeroAttackCmd c)
    {
        if (CheckHeroActive(c, out var h) is { } e) return e;
        var t = S.Unit(c.TargetId);
        if (t == null || !t.Alive || Controller(t) == c.Side || IsSiegeTower(t)) return "invalid target";
        if (t.HasEffect(H5.Combat.Fx.Invisible)) return "target is invisible";
        if (t.CreatureId == "BLADE_BARRIER") return "blade barrier cannot be hit by the hero";
        // exe FUN_00bc1b90: Deadeye Shot (HERO_SKILL_SNIPE_DEAD) raises the plain strike's level by 3.
        HeroStrike(h, t, levelBonus: h.HasPerk(Sk.DeadeyeShot) ? 3 : 0, friendly: false, plain: true);
        // Will of Urgash (Ogres) set: the Barbarian loses 30% less ATB when attacking.
        double reset = h.Class == "HERO_CLASS_BARBARIAN" && Specials.Pieces(h, Specials.Ogres) >= 2
            ? Stats.Get("ArtifactsSetsEffectsConsts/Ogres_2Barbarian_HeroAttack_ATBLessPercents", 30) / 100.0 : 0;
        EndAction(Actor.Hero(c.Side), reset, false);
        return null;
    }

    /// <summary>Critical chance of a hero strike in percent (exe 008cf406-008cf443), capped at 100.</summary>
    internal int HeroCritChance(HeroState h, bool powerfulBlow) => Math.Min(100,
        (h.HasPerk(Sk.ExcruciatingStrike) ? (int)Stats.Get("combat/HeroSkills/DemonLord/CriticalStrike_ChanceToWork", 30) : 0)
        + (powerfulBlow ? (int)Stats.Get("combat/HeroSkills/Barbarian/PowerfullBlow_ChanceToCritical", 10) : 0));

    /// <summary>The exe's ability roll with no attacker (FUN_00a5a170(0, …)): ≥100 always, ≤0 never, else Range(0,100) &lt; chance.</summary>
    bool HeroRoll(int chance) => chance >= 100 || (chance > 0 && ObstacleGenerator.RandInclusive(S.Rng, 0, 100) < chance);

    /// <summary>
    /// A hero strike (exe FUN_008cf270). <paramref name="plain"/> = the hero's normal attack (no strike spell);
    /// <paramref name="powerfulBlow"/> = the Powerful Blow follow-up (SPELL_EFFECT_POWERFULL_BLOW).
    /// </summary>
    void HeroStrike(HeroState h, Unit t, int levelBonus, bool friendly, double extraMult = 1, bool plain = false, bool powerfulBlow = false)
    {
        int actor = Actor.Hero(h.Side);
        Emit(new AttackStarted(actor, t.Id, AttackKind.Hero));
        double dmg = HeroStrikeDamage(h, t, levelBonus) * extraMult;
        if (!friendly)
        {
            // exe 008cf3f8-008cf470: one critical roll, chance = Critical Strike's CriticalStrike_ChanceToWork (if
            // known) + PowerfullBlow_ChanceToCritical (Powerful Blow strikes), capped at 100 → damage x2.
            if (HeroRoll(HeroCritChance(h, powerfulBlow)))
            { dmg *= 2; if (h.HasPerk(Sk.ExcruciatingStrike)) Emit(new AbilityTriggered(actor, Sk.ExcruciatingStrike, t.Id)); }
            if (plain && h.FavoriteEnemies.Count > 0 && IsFavoredEnemy(h, t))
            {
                // exe FUN_00bc1b90 tail: Deadeye Shot's guaranteed critical applies to the plain strike only
                // (fan manual p.138: doubled, always kills at least one).
                if (h.HasPerk(Sk.DeadeyeShot))
                    dmg = Math.Max(dmg * 2, MaxHealthOf(t));
                // exe 008cf48d-008cf511: a plain strike without Deadeye Shot doubles against a favoured enemy on a
                // fixed 40% (EDX = 0x28) ability roll.
                else if (HeroRoll(40))
                    dmg *= 2;
            }
        }
        DealDamage(t, (int)dmg, actor, AttackKind.Hero, null);
        if (!friendly) ImbuedCast(h, t);
        RageEvent(t, "ReceiveHeroAttackRP");
        if (!t.Alive || friendly) return;
        if (h.HasPerk(Sk.StunningBlow)) AddAtb(t.Id, -Stats.Get("combat/HeroSkills/Barbarian/StunningBlow_ATBDrop", 0.1), cap: 1);
        if ((h.HasPerk(Sk.SufferingStrike) || h.HasPerk(Sk.BarbSufferingStrike)) && Db.Spell("SPELL_WEAKNESS") is { } suf)
            ApplyEffectSpell(actor, h.Side, suf, HeroMastery(h, suf), HeroSpellPower(h, suf), t, h.Level, h);
    }

    // ------------------------------------------------------------------ hero linked abilities

    /// <summary>Mark of the Damned: the marked creature is struck by the Demon Lord before attacking (p.141).</summary>
    void MarkOfTheDamnedTrigger(Unit attacker)
    {
        var e = Enemy(Controller(attacker));
        if (e.Present && e.Links.TryGetValue(Sk.MarkOfDamned, out var id) && id == attacker.Id)
        {
            Emit(new AbilityTriggered(Actor.Hero(e.Side), Sk.MarkOfDamned, attacker.Id));
            HeroStrike(e, attacker, levelBonus: 3, friendly: false);
        }
    }

    /// <summary>Retaliation Strike: the knight strikes every enemy attacking the guarded creature (p.144).</summary>
    void RetaliationStrikeTrigger(Unit attacker, Unit target)
    {
        var h = S.Heroes[target.Side];
        if (!attacker.Alive || !h.Present || !h.Links.TryGetValue(Sk.RetaliationStrike, out var id) || id != target.Id) return;
        Emit(new AbilityTriggered(Actor.Hero(h.Side), Sk.RetaliationStrike, attacker.Id));
        double mult = h.HasPerk(Sk.UnstoppableCharge) ? 3 : 1;
        if (h.Specialization == "HERO_SPEC_JOUSTING") mult *= 1 + Stats.Get("combat/HeroSpecializations/Knight/CavalryCommander_HolyChargeBonus", 0.01) * h.Level;
        HeroStrike(h, attacker, levelBonus: 3, friendly: false, extraMult: mult);
    }

    /// <summary>Powerful Blow: the hero follows up the linked creature's attacks (p.139).</summary>
    void PowerfulBlowFollowUp(Unit u, Unit t, bool melee)
    {
        var h = HeroOf(u);
        if (!t.Alive || !h.Links.TryGetValue(Sk.PowerfulBlow, out var id) || id != u.Id) return;
        // exe FUN_00bc1b90: SPELL_EFFECT_POWERFULL_BLOW strikes always get +3 levels; the critical chance is
        // rolled inside HeroStrike (FUN_008cf270).
        HeroStrike(h, t, levelBonus: 3, friendly: false, powerfulBlow: true);
    }

    /// <summary>Hero special abilities available now (each id is used with UseAbilityCmd and ActorId = hero).</summary>
    public IEnumerable<AbilityInfo> HeroAbilities(HeroState h)
    {
        if (h.HasPerk(Sk.DivineGuidance)) yield return new("DIVINE_GUIDANCE", AbilityTargeting.Friend);
        if (h.HasPerk(Sk.RetaliationStrike)) yield return new("RETALIATION_STRIKE", AbilityTargeting.Friend);
        if (h.HasPerk(Sk.Benediction) && !h.AbilityUses.ContainsKey("BENEDICTION")) yield return new("BENEDICTION", AbilityTargeting.None);
        if (h.HasPerk(Sk.MarkOfDamned)) yield return new("MARK_OF_DAMNED", AbilityTargeting.Enemy);
        if (h.HasPerk(Sk.MarkOfNecromancer)) yield return new("MARK_OF_NECROMANCER", AbilityTargeting.Any);
        if (h.HasPerk(Sk.BansheeHowl) && !h.AbilityUses.ContainsKey("BANSHEE_HOWL")) yield return new("BANSHEE_HOWL", AbilityTargeting.None);
        if (h.HasPerk(Sk.RainOfArrows) && h.FavoriteEnemies.Count > 0) yield return new("RAIN_OF_ARROWS", AbilityTargeting.None);
        if (h.HasPerk(Sk.PowerfulBlow)) yield return new("POWERFUL_BLOW", AbilityTargeting.Friend);
        if (h.HasPerk(Sk.ConsumeCorpse) && S.Units.Any(u => !u.Alive && u.Kind == UnitKind.Creature && !u.Removed)) yield return new("CONSUME_CORPSE", AbilityTargeting.DeadFriend);
        foreach (var more in HeroAbilities2(h)) yield return more;
    }

    string? DoHeroAbility(UseAbilityCmd c)
    {
        if (CheckHeroActive(c, out var h) is { } e) return e;
        var info = HeroAbilities(h).FirstOrDefault(a => a.Id == c.Ability);
        if (info == null) return "ability not available";
        var t = c.TargetId is int tid ? S.Unit(tid) : null;
        if (TargetError(info, c.Side, t) is { } te) return te;
        int actor = Actor.Hero(c.Side);
        double reset = 0;
        Emit(new AbilityTriggered(actor, c.Ability, t?.Id));
        if (DoHeroAbility2(h, c, t, ref reset))
        {
            if (reset >= 0) EndAction(actor, reset, false);   // negative: the ability takes no turn
            return null;
        }
        switch (c.Ability)
        {
            case "DIVINE_GUIDANCE":
                AddAtb(t!.Id, Stats.Get("combat/HeroSkills/Knight/Encourage_ATBIncrease", 0.33));    // p.118
                break;
            case "RETALIATION_STRIKE": h.Links[Sk.RetaliationStrike] = t!.Id; reset = 0.5; break;
            case "MARK_OF_DAMNED": h.Links[Sk.MarkOfDamned] = t!.Id; reset = 0.5; break;
            case "MARK_OF_NECROMANCER": h.Links[Sk.MarkOfNecromancer] = t!.Id; reset = 0.5; break;
            case "POWERFUL_BLOW": h.Links[Sk.PowerfulBlow] = t!.Id; reset = 0.5; break;
            case "BENEDICTION":
                h.AbilityUses["BENEDICTION"] = 1;
                // Prayer bonus (attack/defence/morale) = Prayer_DefenceBonus, ×WhiteKnight_PrayerMultiplier (rounded) for the
                // White Knight specialisation (exe FUN_00989fd0 / FUN_00989790 / FUN_0098a430, effect SPELL_PRAYER).
                double prayer = Stats.Get("combat/HeroSkills/Knight/Prayer_DefenceBonus", 1);
                if (h.Specialization == "HERO_SPEC_WHITE_KNIGHT")
                    prayer = ExeRound(prayer * Stats.Get("combat/HeroSkills/Knight/WhiteKnight_PrayerMultiplier", 2));
                foreach (var u in S.LivingOf(c.Side).Where(u => u.Kind == UnitKind.Creature && !IsUndeadLike(u) && !u.Has(Ab.Mechanical)).ToList())
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.Benediction, Value = prayer, Remaining = 2 + 0.25 * h.Level, Positive = true, SourceSide = c.Side });
                break;
            case "BANSHEE_HOWL":
                h.AbilityUses["BANSHEE_HOWL"] = 1;
            {
                double howl = 1;
                if (h.Specialization == "HERO_SPEC_DARK_EMISSARY") howl *= Stats.Get("combat/HeroSkills/Necromancer/DarkEissary_DeathScreamMultiplier", 2);
                if (Specials.Pieces(h, Specials.Necromancers) >= 2) howl = Math.Max(howl, 2);
                foreach (var u in S.LivingOf(1 - c.Side).Where(IsLiving).ToList())
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.BansheeHowl, Value = howl + (h.HasPerk(Sk.HowlOfTerror) ? 6 : 0), Value2 = howl,
                        Remaining = 2 + Stats.Get("combat/HeroSkills/DeathScreamLevelMultiplier", 0.25) * h.Level, SourceSide = c.Side, Dispellable = false });
                if (Specials.Pieces(h, Specials.Necromancers) >= 2) reset = 0.5;
            }
                break;
            case "RAIN_OF_ARROWS":
                foreach (var u in S.LivingOf(1 - c.Side).Where(u => IsFavoredEnemy(h, u)).ToList())
                    HeroStrike(h, u, levelBonus: 3, friendly: false);
                break;
            case "CONSUME_CORPSE":
            {
                var corpse = t!;
                int hp = corpse.InitialCount * corpse.BaseHealth;
                // Mana = round(max(1, HP / clamp(HPForOneMana, 0.1, 1000))), capped by the missing mana (exe FUN_0097b690).
                double perMana = Math.Clamp(Stats.Get("combat/HeroSkills/DemonLord/ConsumeCorpseHPForOneMana", 30), 0.1, 1000);
                int mana = (int)Math.Round(Math.Min(Math.Max(1.0, hp / perMana), Math.Max(0, h.MaxMana - h.Mana)), MidpointRounding.ToEven);
                h.Mana = Math.Min(h.MaxMana, h.Mana + mana);
                corpse.Removed = true;
                Emit(new ManaChanged(actor, h.Mana));
                // Exploding Corpses: the consumed corpse always explodes (FUN_0085eaa0, effect 0x11f) — a fire area
                // attack hitting every unit next to it, friend or foe, for the spell's Base + PerPower·SpellPower.
                if (h.HasPerk("HERO_SKILL_EXPLODING_CORPSES") && Db.Spell("SPELL_EFFECT_EXPLODING_CORPSES") is { } boom)
                {
                    var around = corpse.Cells().SelectMany(cc => Cell.Neighbours8.Select(d => cc + d)).Concat(corpse.Cells()).ToHashSet();
                    double dmg = boom.DamageAt(Mastery.None).Eval(h.SpellPower);
                    foreach (var v in S.Living.Where(v => v.Kind == UnitKind.Creature && v.Cells().Any(cell => around.Contains(cell))).ToList())
                        DealSpellDamage(v, boom, dmg, c.Side, ignoreResist: false, hero: h);
                    CheckEnd();
                }
                break;
            }
        }
        EndAction(actor, reset, false);
        return null;
    }

    string? TargetError(AbilityInfo info, int side, Unit? t)
    {
        switch (info.Targeting)
        {
            case AbilityTargeting.None: return null;
            case AbilityTargeting.Cell: return null;
            case AbilityTargeting.DeadFriend:
                return t == null || t.Alive || t.Removed || t.Kind != UnitKind.Creature ? "needs a corpse" : null;
        }
        if (t == null || !t.Alive || t.IsObject) return "needs a target";
        if (info.Targeting == AbilityTargeting.Friend && Controller(t) != side) return "needs a friendly target";
        if (info.Targeting == AbilityTargeting.Enemy && Controller(t) == side) return "needs an enemy target";
        return null;
    }

    // ------------------------------------------------------------------ creature activated abilities

    /// <summary>Activated abilities the unit can use this turn.</summary>
    public IEnumerable<AbilityInfo> UnitAbilities(Unit u)
    {
        bool Once(string id) => !u.AbilityUses.ContainsKey(id);
        var h = HeroOf(u);
        if (u.CreatureId == "WAR_MACHINE_FIRST_AID_TENT")
        {
            if (u.Shots > 0) yield return new("FIRST_AID", AbilityTargeting.Friend);
            if (u.Shots > 0 && h.HasPerk(Sk.PlagueTent)) yield return new("PLAGUE_TENT", AbilityTargeting.Enemy);   // damage = heal amount
            yield break;
        }
        if (u.CreatureId == "WAR_MACHINE_CATAPULT")
        {
            if (S.Siege != null && CatapultTargets().Count > 0) yield return new("CATAPULT_SHOT", AbilityTargeting.Cell);
            yield break;
        }
        if (u.IsWarMachine || u.IsObject) yield break;
        // No per-combat limit: the Paladin has no ability charges (SpellPoints1 = 0) and Lay Hands costs no mana,
        // unlike charge-limited abilities such as Resurrect Allies (SpellPoints1 = 1). See docs/rules/EXE_FINDINGS.md.
        if (u.Has(Ab.LayHands)) yield return new(Ab.LayHands, AbilityTargeting.Friend, NeedsAdjacency: false);
        if (u.Has("ABILITY_SEDUCE") && Once("ABILITY_SEDUCE")) yield return new("ABILITY_SEDUCE", AbilityTargeting.Enemy);
        if (u.Has(Ab.ResurrectAllies) && Once(Ab.ResurrectAllies)) yield return new(Ab.ResurrectAllies, AbilityTargeting.DeadFriend);
        if (u.Has(Ab.Dash) && Once(Ab.Dash)) yield return new(Ab.Dash, AbilityTargeting.None);
        if (u.Has(Ab.Explosion)) yield return new(Ab.Explosion, AbilityTargeting.None);
        if (u.Has(Ab.HarmTouch)) yield return new(Ab.HarmTouch, AbilityTargeting.Enemy, NeedsAdjacency: true);
        if (u.Has(Ab.DeathWail)) yield return new(Ab.DeathWail, AbilityTargeting.None);
        if (u.Has(Ab.CallLightning)) yield return new(Ab.CallLightning, AbilityTargeting.Enemy);
        if (u.Has(Ab.RageOfForest) && Once(Ab.RageOfForest)) yield return new(Ab.RageOfForest, AbilityTargeting.None);
        if (u.Has(Ab.BerserkerRage)) yield return new(Ab.BerserkerRage, AbilityTargeting.None);
        if (u.Has(Ab.OrderOfChief)) yield return new(Ab.OrderOfChief, AbilityTargeting.Friend);
        if (u.Has(Ab.Repair) && Once(Ab.Repair)) yield return new(Ab.Repair, AbilityTargeting.Friend);
        if (u.Has(Ab.ManaFeed) && u.Mana > 0 && h.Present) yield return new(Ab.ManaFeed, AbilityTargeting.None);
        if (u.Has(Ab.ScatterShot) && CanShoot(u)) yield return new(Ab.ScatterShot, AbilityTargeting.Cell);
        if (u.Has(Ab.Invisibility) && Once(Ab.Invisibility)) yield return new(Ab.Invisibility, AbilityTargeting.None);
        if (u.Has(Ab.Leap)) yield return new(Ab.Leap, AbilityTargeting.Enemy);
        if (u.Has(Ab.SummonOther)) yield return new(Ab.SummonOther, AbilityTargeting.Any);
        if (u.Has(Ab.BalorSummoning) && Once(Ab.BalorSummoning)) yield return new(Ab.BalorSummoning, AbilityTargeting.DeadFriend);
        if (u.Has(Ab.Flamestrike) && Once(Ab.Flamestrike)) yield return new(Ab.Flamestrike, AbilityTargeting.Enemy);
        if (u.Has(Ab.StormBolt) && Once(Ab.StormBolt)) yield return new(Ab.StormBolt, AbilityTargeting.Enemy);
        if (u.Has(Ab.CallStorm)) yield return new(Ab.CallStorm, AbilityTargeting.Cell);
        if (u.Has(Ab.HarpoonStrike) && CanShoot(u)) yield return new(Ab.HarpoonStrike, AbilityTargeting.Enemy);
        if (u.Has(Ab.Sabotage)) yield return new(Ab.Sabotage, AbilityTargeting.Enemy);
        if (u.Has(Ab.SwallowGoblin)) yield return new(Ab.SwallowGoblin, AbilityTargeting.Friend, NeedsAdjacency: true);
        if (u.Has(Ab.SacrificeGoblin)) yield return new(Ab.SacrificeGoblin, AbilityTargeting.Friend, NeedsAdjacency: true);
        if (h.Present && Once(Ab.Gating) && CanGate(u)) yield return new(Ab.Gating, AbilityTargeting.None);
        foreach (var extra in MoreUnitAbilities(u)) yield return extra;
    }

    /// <summary>Demon Lord Gating eligibility by mastery (p.141).</summary>
    bool CanGate(Unit u)
    {
        var h = HeroOf(u);
        var m = h.SkillLevel(Sk.Gating);
        if (m == Mastery.None || u.Town != Towns.Inferno || u.Summoned || u.Kind != UnitKind.Creature) return false;
        return m switch
        {
            Mastery.Basic => u.Tier <= 2,
            Mastery.Advanced => u.Tier <= 4,
            Mastery.Expert => u.Tier <= 6,
            _ => true,
        };
    }

    string? DoAbility(UseAbilityCmd c)
    {
        if (Actor.IsHero(c.ActorId))
        {
            if (Actor.HeroSide(c.ActorId) != c.Side) return "not your hero";
            return DoHeroAbility(c);
        }
        if (CheckActive(c, c.ActorId, out var u) is { } err) return err;
        var info = UnitAbilities(u).FirstOrDefault(a => a.Id == c.Ability);
        if (info == null) return "ability not available";
        var t = c.TargetId is int tid ? S.Unit(tid) : null;
        if (TargetError(info, Controller(u), t) is { } te) return te;
        if (info.NeedsAdjacency && t != null && !AreAdjacent(u, t)) return "target must be adjacent";
        if (info.Targeting == AbilityTargeting.Cell && (c.Cell is not Cell cc || !S.InBounds(cc))) return "needs a target cell";
        if (c.Ability == "CATAPULT_SHOT") return DoCatapultShot(u, c);
        double reset = 0;
        bool allowMorale = true;
        if (DoMoreAbilities(u, c, t, out bool handledExtra) is { } extraErr) return extraErr;
        if (handledExtra) { EndAction(u.Id, 0); return null; }
        switch (c.Ability)
        {
            case "ABILITY_SEDUCE":
                if (Seduce(u, t!) is { } se) return se;
                break;
            case "FIRST_AID":
                if (!IsLiving(t!)) return "target cannot be healed";
                TentHeal(u, t!);
                allowMorale = false;
                break;
            case "PLAGUE_TENT":
                u.Shots = Math.Max(0, u.Shots - 1);
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                DealDamage(t, TentHealAmount(u), u.Id, AttackKind.Ability, c.Ability);
                allowMorale = false;
                break;
            case Ab.LayHands:
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                Heal(t, t.MaxHealthCache, resurrect: true, Ab.LayHands);    // one creature fully healed (p.177)
                foreach (var id in t.Effects.Where(e => !e.Positive && e.School == "MAGIC_SCHOOL_DARK" || e.Id is H5.Combat.Fx.Poison or H5.Combat.Fx.Wound or H5.Combat.Fx.Sleep).Select(e => e.Id).ToList())
                    RemoveEffect(t, id);
                break;
            case Ab.ResurrectAllies:
            {
                if (t!.Side != u.Side || IsUndeadLike(t) || t.Has(Ab.Mechanical) || t.Has(Ab.Elemental)) return "cannot resurrect this stack";
                if (!Fits(t, t.Pos)) return "the stack's place is occupied";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                u.AbilityUses[c.Ability] = 1;
                Resurrect(t, 100 * u.Count);   // TODO: exact Archangel resurrection amount needs exe confirmation
                break;
            }
            case Ab.Dash:
                Emit(new AbilityTriggered(u.Id, c.Ability));
                u.AbilityUses[c.Ability] = 1;
                AddEffect(u, new Effect { Id = H5.Combat.Fx.Dash, Actions = (int)Stats.Get("combat/Creatures/Academy/Dash_Duration", 3) + 1, Positive = true, Dispellable = false });
                allowMorale = false;
                break;
            case Ab.Explosion:
            {
                Emit(new AbilityTriggered(u.Id, c.Ability));
                int power = CreatureSpellPower(u.Count, Math.Max(1, u.WeeklyGrowth));
                double dmg = 9 + 9 * power;                                   // p.306
                foreach (var v in Adjacent(u).ToList())
                {
                    var spell = Db.Spell("SPELL_ABILITY_EXPLOSION") ?? Db.Spell("SPELL_FIREBALL")!;
                    DealDamage(v, (int)(dmg * SpellDamageMultiplier(v, spell, Controller(u))), u.Id, AttackKind.Ability, c.Ability);
                }
                break;
            }
            case Ab.HarmTouch:
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                DealDamage(t, t.TopHealth, u.Id, AttackKind.Ability, c.Ability);
                foreach (var id in t.Effects.Where(e => e.Positive && e.School == "MAGIC_SCHOOL_LIGHT").Select(e => e.Id).ToList()) RemoveEffect(t, id);
                break;
            case Ab.DeathWail:
            {
                Emit(new AbilityTriggered(u.Id, c.Ability));
                int range = (int)Stats.Get("combat/Creatures/Necropolis/DeathWailRange", 3);
                foreach (var v in S.Living.Where(o => Controller(o) != Controller(u) && IsLiving(o) && Distance(u, o) <= range).ToList())
                {
                    int d = Distance(u, v);
                    double factor = d <= 1 ? 1 : d == 2 ? 0.5 : 0.25;          // p.173
                    DealDamage(v, (int)((10 - MoraleOf(v)) * u.Count * factor), u.Id, AttackKind.Ability, c.Ability);
                }
                break;
            }
            case Ab.CallLightning:
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                if (!ElementImmune(t, Elements.Air))
                    DealDamage(t, 30 * u.Count, u.Id, AttackKind.Ability, c.Ability);   // ignores magic protection (p.172)
                break;
            case Ab.RageOfForest:
            {
                Emit(new AbilityTriggered(u.Id, c.Ability));
                u.AbilityUses[c.Ability] = 1;
                double half = Math.Floor(DefenceOf(u) / 2.0);
                AddEffect(u, new Effect { Id = H5.Combat.Fx.RageOfForest, Value = half, Positive = true, Dispellable = false });
                break;
            }
            case Ab.BerserkerRage:
            {
                Emit(new AbilityTriggered(u.Id, c.Ability));
                AddEffect(u, new Effect { Id = H5.Combat.Fx.BerserkerRage, Value = DefenceOf(u), Actions = 2, Positive = true, Dispellable = false });
                AutoBerserkAttack(u, anySide: false);
                break;
            }
            case Ab.OrderOfChief:
                if (t!.Id == u.Id) return "cannot order itself";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                DealDamage(t, u.Count, u.Id, AttackKind.Ability, c.Ability);
                if (t.Alive) AddAtb(t.Id, Math.Min(0.6, u.Count * 0.01));      // p.178
                break;
            case Ab.Repair:
                if (!t!.Has(Ab.Mechanical) && !t.IsWarMachine) return "only mechanical units can be repaired";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                u.AbilityUses[c.Ability] = 1;
                Heal(t, (int)(Stats.Get("combat/Creatures/Academy/GremlinRepairPowerPreCreature", 5) * u.Count), resurrect: true, c.Ability);
                break;
            case Ab.ManaFeed:
            {
                var h = HeroOf(u);
                Emit(new AbilityTriggered(u.Id, c.Ability));
                h.Mana += u.Mana; u.Mana = 0;
                Emit(new ManaChanged(Actor.Hero(u.Side), h.Mana));
                Emit(new ManaChanged(u.Id, 0));
                break;
            }
            case Ab.ScatterShot:
            {
                var center = c.Cell!.Value;
                Emit(new AbilityTriggered(u.Id, c.Ability));
                if (!HasAmmoCart(u.Side)) u.Shots--;
                foreach (var v in S.Living.Where(o => !o.IsObject && o.Cells().Any(x => x.Chebyshev(center) <= 1)).ToList())
                {
                    int dmg = ComputeDamage(u, v, new Hit { Kind = AttackKind.Ranged, Multiplier = 0.5 }, ranged: true, RollLuck(u, v));
                    DealDamage(v, dmg, u.Id, AttackKind.Ranged, c.Ability);
                }
                break;
            }
            case Ab.Invisibility:
                Emit(new AbilityTriggered(u.Id, c.Ability));
                u.AbilityUses[c.Ability] = 1;
                AddEffect(u, new Effect { Id = H5.Combat.Fx.Invisible, Remaining = Stats.Get("combat/Creatures/Dungeon/Invisibility_DurationInRounds", 3), Positive = true, Dispellable = false });
                break;
            case Ab.Leap:
            {
                int dist = Distance(u, t!);
                int maxLeap = SpeedOf(u) * 2;
                if (dist < 2 || dist > maxLeap) return "target out of leap range";
                var land = AttackPositionsAnywhere(u, t!).OrderBy(p => Octile(p, u.Pos)).FirstOrDefault();
                if (land == default && !AdjacentAt(u, land, t!)) return "no landing spot";
                int cells = Math.Max(Math.Abs(land.X - u.Pos.X), Math.Abs(land.Y - u.Pos.Y));
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                Teleport(u, land);
                var hit = new Hit { Kind = AttackKind.Melee, Multiplier = 1 + Stats.Get("combat/Creatures/Inferno/Leap_AttackDamageBonusPerPathCell", 0.1) * cells };
                Strike(u, t!, hit, pattern: true);
                if (u.Alive && t!.Alive && CanRetaliate(t, u))
                {
                    if (t.RetaliationsLeft != int.MaxValue) t.RetaliationsLeft--;
                    Strike(t, u, new Hit { Kind = AttackKind.Retaliation, Multiplier = Stats.Get("combat/Creatures/Inferno/Leap_RetaliateDamageBonusBase", 2) }, pattern: true);
                }
                break;
            }
            case Ab.SummonOther:
            {
                if (t!.Id == u.Id || t.HasEffect("SPELL_ANTI_MAGIC") || t.Has(Ab.HoldGround) && !t.HasMovedFromStart) return "cannot teleport this unit";
                var spot = NearestFree(t, u.Pos);
                if (spot == null) return "no free spot";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                Teleport(t, spot.Value);
                break;
            }
            case Ab.BalorSummoning:
            {
                if (t!.Side != u.Side) return "needs a friendly corpse";
                int hp = t.InitialCount * t.BaseHealth;
                int n = Math.Min(2 * u.Count, hp / 120);                         // p.181
                if (n <= 0) return "corpse too weak";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                u.AbilityUses[c.Ability] = 1;
                t.Removed = true;
                SummonStack(u.Side, "CREATURE_BALOR", n, c.Ability, atb: 0);
                break;
            }
            case Ab.Flamestrike:
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                u.AbilityUses[c.Ability] = 1;
                Strike(u, t, new Hit { Kind = AttackKind.Ability, Multiplier = Stats.Get("combat/Creatures/Fortress/Flamestrike_DamageMultipiler", 1.2) }, pattern: false);
                if (t.Alive) AddEffect(t, new Effect { Id = H5.Combat.Fx.MarkOfFire, Remaining = Stats.Get("combat/Creatures/Fortress/MarkOfFire_EffectDurationInRounds", 2), SourceSide = u.Side });
                break;
            case Ab.StormBolt:
            {
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                u.AbilityUses[c.Ability] = 1;
                int dmg = ComputeDamage(u, t, new Hit { Kind = AttackKind.Ability }, ranged: false);
                DealDamage(t, dmg, u.Id, AttackKind.Ability, c.Ability);
                if (t.Alive) StormStrikeChain(u, t, dmg);
                break;
            }
            case Ab.CallStorm:
            {
                var center = c.Cell!.Value;
                var cells = new List<Cell>();
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) { var p = new Cell(center.X + dx, center.Y + dy); if (S.InBounds(p)) cells.Add(p); }
                Emit(new AbilityTriggered(u.Id, c.Ability));
                var f = AddField("STORM", u.Side, cells, Stats.Get("combat/Creatures/Academy/CallStorm_DurationInRounds", 3),
                    Stats.Get("combat/Creatures/Academy/CallStorm_DamagePerCreature", 10) * u.Count, u.Id);
                f.Power = Math.Min(Stats.Get("combat/Creatures/Academy/CallStorm_MaxShootPenalty", 0.9), Stats.Get("combat/Creatures/Academy/CallStorm_ShootPenaltyPerCreature", 0.01) * u.Count);
                break;
            }
            case Ab.HarpoonStrike:
            {
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                if (!HasAmmoCart(u.Side)) u.Shots--;
                // Damage: the attack special SPELL_ABILITY_HARPOON_STRIKE scales by 1 + HarpoonStrike_DamageBonus in the
                // attacker stage (exe FUN_00a58de0, special 0x14b).
                Strike(u, t, new Hit { Kind = AttackKind.Ranged, Multiplier = 1 + Stats.Get("combat/Creatures/Fortress/HarpoonStrike_DamageBonus", 0) },
                    pattern: false, rangedRetaliation: false);
                if (t.Alive && HarpoonPullDestination(u, t) is { } pull && pull != t.Pos) Teleport(t, pull);
                break;
            }
            case Ab.Sabotage:
                if (!(t!.Has(Ab.Mechanical) || t.IsWarMachine) || t.TotalHealth >= u.TotalHealth) return "cannot sabotage this target";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                AddEffect(t, new Effect { Id = H5.Combat.Fx.Sabotaged, Actions = 1, SourceSide = u.Side, Dispellable = false });
                break;
            case Ab.SwallowGoblin:
            case Ab.SacrificeGoblin:
            {
                if (!t!.CreatureId.StartsWith("CREATURE_GOBLIN")) return "needs an adjacent goblin stack";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                int goblinHp = t.TopHealth;
                DealDamage(t, t.TopHealth, u.Id, AttackKind.Ability, c.Ability);
                if (c.Ability == Ab.SwallowGoblin) Heal(u, u.MaxHealthCache, resurrect: false, c.Ability);
                else
                {
                    u.Mana += 2 * goblinHp;
                    var e = u.GetEffect(H5.Combat.Fx.Sacrifice);
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.Sacrifice, Value = Math.Min(5, (e?.Value ?? 0) + 1), Positive = true, Dispellable = false });
                }
                break;
            }
            case Ab.Gating:
            {
                var h = HeroOf(u);
                u.AbilityUses[c.Ability] = 1;
                var portions = Stats.Array("combat/HeroSkills/DemonLord/Gating/PortionOfSummonedByGatingLevel");
                int lvl = Math.Clamp((int)h.SkillLevel(Sk.Gating) - 1, 0, Math.Max(0, portions.Length - 1));
                double portion = portions.Length > 0 ? portions[lvl] : 0.3;
                if (h.HasPerk(Sk.GateMaster)) portion += Stats.Get("combat/HeroSkills/DemonLord/Gating/GatingMasteryPortionBonus", 0.2);
                if (h.Specialization == "HERO_SPEC_GATE_KEEPER") portion += Stats.Get("combat/HeroSkills/DemonLord/Gating/GatekeeperHeroSpecPerLevelPortionBonus", 0.01) * h.Level;
                if (h.HasPerk(Sk.SwarmingGate) && S.Rng.Chance(0.1 + Math.Max(0, h.Luck) * 0.05)) portion *= 2;
                if (Specials.Pieces(h, Specials.Demonic) >= 2) portion *= 1 + Stats.Get("ArtifactsSetsEffectsConsts/Demonic_2DemonLord_GatingBonusPercents", 25) / 100.0;
                int n = Math.Max(1, (int)(u.Count * portion));
                Emit(new AbilityTriggered(u.Id, c.Ability));
                var g = SummonStackNear(u, u.CreatureId, n, c.Ability, Stats.Get("combat/HeroSkills/DemonLord/Gating/GatedStartATB", 0));
                reset = h.HasPerk(Sk.QuickGating) ? Stats.Get("combat/HeroSkills/DemonLord/Gating/QuickGatingStartATB", 0.75)
                                                   : Stats.Get("combat/HeroSkills/DemonLord/Gating/CallerStartATB", 0.5);
                if (g == null) return null;
                AddEffect(g, new Effect { Id = GatedEffect, Positive = true, Dispellable = false });
                break;
            }
            default: return "ability not implemented";
        }
        EndAction(u.Id, reset, allowMorale);
        return null;
    }

    IEnumerable<Cell> AttackPositionsAnywhere(Unit u, Unit t)
    {
        for (int x = 0; x <= S.Width - u.Size; x++)
        for (int y = 0; y <= S.Height - u.Size; y++)
        {
            var p = new Cell(x, y);
            if ((p == u.Pos || Fits(u, p)) && AdjacentAt(u, p, t)) yield return p;
        }
    }

    Unit? SummonStackNear(Unit near, string creature, int count, string by, double atb)
    {
        var probe = new Unit { Size = Db.Creature(creature).CombatSize };
        var pos = NearestFree(probe, near.Pos);
        if (pos == null) return null;
        var u = BattleSetup.AddCreature(Db, S, near.Side, creature, count, pos.Value, summoned: true);
        u.Atb = atb;
        Emit(new UnitSummoned(u.Id, creature, count, near.Side, u.Pos, by));
        return u;
    }
}
