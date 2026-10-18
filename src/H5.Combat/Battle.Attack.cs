using H5.Core.Data;

namespace H5.Combat;

// Physical attacks: melee, ranged, retaliation, attack patterns and on-hit creature abilities.
public sealed partial class Battle
{
    /// <summary>Context for one damage computation.</summary>
    public sealed class Hit
    {
        public AttackKind Kind;
        public int CellsWalked;
        public double Multiplier = 1;
        public double? FixedRoll;          // deterministic roll for previews/AI (null = random)
        public bool Secondary;             // splash/secondary target of a pattern attack
        public bool IgnoreDefence;
    }

    // ------------------------------------------------------------------ commands

    string? DoAttack(AttackCmd c)
    {
        if (CheckActive(c, c.UnitId, out var u) is { } err) return err;
        var t = S.Unit(c.TargetId);
        if (t == null || !t.Alive || t.IsObject && t.Side == u.Side || IsSiegeTower(t)) return "invalid target";
        if (t.Id == u.Id) return "cannot attack self";
        if (Controller(t) == Controller(u) && !u.HasEffect("SPELL_BERSERK")) return "cannot attack own unit";
        if (t.HasEffect(H5.Combat.Fx.Invisible) && t.Side != u.Side) return "target is invisible";
        var from = c.From ?? u.Pos;
        if (from != u.Pos)
        {
            if (!CanMove(u)) return "unit cannot move";
            if (!Reachable(u).ContainsKey(from)) return "attack position not reachable";
        }
        if (!AdjacentAt(u, from, t)) return "target not adjacent to attack position";
        t = TauntRedirect(u, t, from);
        RageEvent(u, "MeleeAtackRP");
        PerformMeleeAttack(u, t, from);
        EndAction(u.Id, 0);
        return null;
    }

    string? DoShoot(ShootCmd c)
    {
        if (CheckActive(c, c.UnitId, out var u) is { } err) return err;
        var t = S.Unit(c.TargetId);
        if (t == null || !t.Alive || Controller(t) == Controller(u) || IsSiegeTower(t)) return "invalid target";
        if (t.HasEffect(H5.Combat.Fx.Invisible)) return "target is invisible";
        if (!CanShoot(u)) return "unit cannot shoot";
        RageEvent(u, "RangedAttackRP");
        PerformRangedAttack(u, t);
        // Archer's Dream set: shooters lose 30% less ATB when shooting.
        double reset = !u.IsWarMachine && Specials.Pieces(HeroOf(u), Specials.Hunters) >= 2 ? Stats.Get("ArtifactsSetsEffectsConsts/Hunters_2_ShootersATBLessPercents", 30) / 100.0 : 0;
        EndAction(u.Id, reset);
        return null;
    }

    public bool CanShoot(Unit u)
    {
        if (u.MaxShots <= 0 || u.CreatureId is "WAR_MACHINE_FIRST_AID_TENT" or "WAR_MACHINE_AMMO_CART" or "WAR_MACHINE_CATAPULT") return false;
        if (u.Shots <= 0 && !HasAmmoCart(u.Side)) return false;
        if (u.IsWarMachine) return true;
        // Shooters cannot shoot while an enemy stands next to them (p.180).
        return !S.Living.Any(o => Controller(o) != Controller(u) && !o.IsObject && !o.IsWarMachine && AreAdjacent(u, o)
                                  && !o.HasEffect(H5.Combat.Fx.Invisible));
    }

    /// <summary>
    /// Shots a war machine fires in ONE action at the chosen target (exe FUN_00a75bf0, war machine vtable
    /// slot 0x1c0, looped by the attack routine FUN_00a65260 / catapult FUN_00b3bb50): 1, Triple Ballista
    /// (Triple Catapult) makes it 2, the Ballista (Catapult) perk adds 1, Ring of Machine Affinity adds 1.
    /// The player never gets extra actions — the extra shots are consecutive strikes.
    /// </summary>
    public int WarMachineShotsPerAction(Unit u)
    {
        var h = HeroOf(u);
        if (!h.Present) return 1;
        int n = 1;
        switch (u.CreatureId)
        {
            case "WAR_MACHINE_BALLISTA":
                if (h.HasPerk(Sk.TripleBallista)) n = 2;
                if (h.HasPerk(Sk.Ballista)) n++;
                break;
            case "WAR_MACHINE_CATAPULT":
                if (h.HasPerk("HERO_SKILL_TRIPLE_CATAPULT")) n = 2;
                if (h.HasPerk(Sk.Catapult)) n++;
                break;
            default:
                return 1;
        }
        if (HasMachineAffinityRing(h)) n++;
        return n;
    }

    /// <summary>Ring of Machine Affinity (hero vcall +0x314 in FUN_00a75bf0 / FUN_00977190 / FUN_008a4800).</summary>
    static bool HasMachineAffinityRing(HeroState h) => h.Present && h.Artifacts.Contains("ARTIFACT_RING_OF_MACHINE_AFFINITY");

    // ------------------------------------------------------------------ melee

    void PerformMeleeAttack(Unit u, Unit t, Cell from)
    {
        var origin = u.Pos;
        int walked = 0;
        var path = new List<Cell>();
        if (from != u.Pos)
        {
            path = MoveUnit(u, from);
            walked = u.CellsWalked;
            if (!u.Alive) return;
        }
        else u.CellsWalked = 0;
        RideByAttack(u, path, t);
        if (!u.Alive || !t.Alive) return;
        RemoveEffect(u, H5.Combat.Fx.Invisible);
        MarkOfTheDamnedTrigger(u);
        if (!u.Alive) return;

        var th = HeroOf(t);
        bool preparation = t.Defending && th.HasPerk(Sk.Preparation) && AreAdjacent(u, t);
        bool retaliated = false;
        if (preparation && CanRetaliate(t, u, ignoreNoRetaliation: true))
        {
            Retaliate(t, u);
            retaliated = true;
            if (!u.Alive) return;
        }

        var hit = new Hit { Kind = AttackKind.Melee, CellsWalked = walked };
        // Strikes per action (creature vtable slot 0x1c0 = FUN_008a0f90): 1, Double Attack 2, Rune of Berserking +1.
        int strikes = 1;
        if (HasAbilityNow(u, Ab.DoubleAttack)) strikes = 2;
        if (u.HasEffect(H5.Combat.Fx.RuneBerserking)) strikes += 1;
        bool runeBattleRage = u.HasEffect(H5.Combat.Fx.RuneBattleRage);
        bool stunned = false;
        int killedTotal = 0;
        // Targets that flee from melee (Cowardice / Manoeuvre) react after the whole attack instead of retaliating.
        bool fleeingTarget = HasAbilityNow(t, Ab.Cowardice) || HasAbilityNow(t, Ab.Maneure);
        // The retaliation verdict is taken before the blow (exe FUN_00a5fee0), so an Expert-blinded target stays silent
        // even though the hit removes its blindness.
        bool noRetBase = HasAbilityNow(u, Ab.NoEnemyRetaliation) || runeBattleRage
                         || t.GetEffect("SPELL_BLIND") is { Mastery: >= Mastery.Expert };
        for (int i = 0; i < strikes && u.Alive && t.Alive; i++)
        {
            var r = Strike(u, t, hit, pattern: true, extraAdjacentAll: runeBattleRage);
            stunned |= r.Stunned;
            killedTotal += r.Killed;
            // Retaliation follows the strike that allows it, inside the strike loop (exe FUN_00a65260 → FUN_00a64e90 →
            // FUN_00a646b0): a double attack is answered after its first blow.
            if (!fleeingTarget && u.Alive && t.Alive && !noRetBase && !r.Stunned
                && !(preparation && retaliated && !HasAbilityNow(t, Ab.UnlimitedRetaliation)) && CanRetaliate(t, u))
            {
                Retaliate(t, u);
                retaliated = true;
            }
            // Assault (Brutality): after the first strike, a proc adds one more strike to the loop (FUN_00a65260).
            if (i == 0 && HasAbilityNow(u, Ab.Brutality) && t.Alive && u.Alive
                && ProcRoll(u, t, Stats.Get("combat/Creatures/Stronghold/Brutality_ProcFactor", 1), soldiersLuckDoubles: true))
            {
                Emit(new AbilityTriggered(u.Id, Ab.Brutality, t.Id));
                strikes++;
            }
            // Cleave: an extra strike once a creature is killed (p.172).
            if (i == strikes - 1 && r.Killed > 0 && HasAbilityNow(u, Ab.Cleave) && t.Alive && u.Alive)
            {
                Emit(new AbilityTriggered(u.Id, Ab.Cleave, t.Id));
                Strike(u, t, new Hit { Kind = AttackKind.Melee, CellsWalked = walked }, pattern: false);
            }
        }
        // Rune of Berserking is spent by the attack unless the first blow already killed the target (p.192).
        if (u.HasEffect(H5.Combat.Fx.RuneBerserking) && t.Alive) RemoveEffect(u, H5.Combat.Fx.RuneBerserking);
        if (runeBattleRage) RemoveEffect(u, H5.Combat.Fx.RuneBattleRage);

        if (u.Alive && t.Alive) LizardBites(u, t);
        if (u.Alive && t.Alive) PackHunterAssist(u, t);
        if (u.Alive && t.Alive) Flamewave(u, t);
        bool fled = u.Alive && ReactiveRetreat(u, t);
        bool noRet = noRetBase || stunned || fled;
        if (fleeingTarget && u.Alive && t.Alive && !noRet && !(preparation && retaliated && !HasAbilityNow(t, Ab.UnlimitedRetaliation)) && CanRetaliate(t, u))
            Retaliate(t, u);

        PowerfulBlowFollowUp(u, t, melee: true);
        RetaliationStrikeTrigger(u, t);

        // Strike and Return (p.181).
        if (u.Alive && HasAbilityNow(u, Ab.StrikeAndReturn) && origin != u.Pos && Fits(u, origin))
        {
            Emit(new AbilityTriggered(u.Id, Ab.StrikeAndReturn));
            Teleport(u, origin);
        }
    }

    bool CanRetaliate(Unit t, Unit attacker, bool ignoreNoRetaliation = false)
    {
        if (!t.Alive || !attacker.Alive || t.IsWarMachine || t.IsObject) return false;
        if (t.RetaliationsLeft <= 0) return false;
        if (!AreAdjacent(t, attacker)) return false;
        if (!ignoreNoRetaliation && HasAbilityNow(attacker, Ab.NoEnemyRetaliation)) return false;
        if (t.HasEffect(H5.Combat.Fx.Sleep) || t.HasEffect(H5.Combat.Fx.Stunned) || t.HasEffect("SPELL_HYPNOTIZE") || t.HasEffect(H5.Combat.Fx.Frozen)) return false;
        // Only Expert Blind stops retaliation (exe FUN_00a5fee0: blind effect level > 2; manual p.186).
        if (t.GetEffect("SPELL_BLIND") is { Mastery: >= Mastery.Expert }) return false;
        if (attacker.HasEffect(H5.Combat.Fx.Invisible)) return false;
        if (HasAbilityNow(attacker, Ab.FastAttack) && t.HasEffect("SPELL_SLOW")) return false;    // Swift Attack (p.181)
        return true;
    }

    void Retaliate(Unit t, Unit attacker)
    {
        if (t.RetaliationsLeft != int.MaxValue) t.RetaliationsLeft--;
        MarkOfTheDamnedTrigger(t);
        if (!t.Alive) return;
        var hit = new Hit { Kind = AttackKind.Retaliation, Multiplier = t.FrenzyMultiplier };
        Strike(t, attacker, hit, pattern: true);
        if (HasAbilityNow(t, Ab.BattleFrenzy))
            t.FrenzyMultiplier *= Stats.Get("combat/Creatures/Haven/BattleFrenzyBoostFactor", 1.5);   // p.172
    }

    // ------------------------------------------------------------------ ranged

    void PerformRangedAttack(Unit u, Unit t)
    {
        RemoveEffect(u, H5.Combat.Fx.Invisible);
        MarkOfTheDamnedTrigger(u);
        if (!u.Alive) return;
        // Strikes per action (vtable slot 0x1c0): Double Shot for creatures, perks/ring for the Ballista.
        int shots = u.IsWarMachine ? WarMachineShotsPerAction(u) : HasAbilityNow(u, Ab.DoubleShot) ? 2 : 1;
        int targetHpBefore = t.TotalHealth;
        for (int i = 0; i < shots && u.Alive && t.Alive; i++)
        {
            if (u.Shots <= 0 && !HasAmmoCart(u.Side)) break;
            if (!HasAmmoCart(u.Side) || u.IsWarMachine) u.Shots = Math.Max(0, u.Shots - 1);
            var hit = new Hit { Kind = AttackKind.Ranged };
            var r = Strike(u, t, hit, pattern: true);
            // Warding Arrows: -0.2 ATB on shot (p.181).
            // exe FUN_00a5b000: every shot, ATB = max(0, ATB − clamp(WardingArrowsATBAffect, 0, 1)).
            if (t.Alive && HasAbilityNow(u, Ab.WardingArrows)
                && ProcRoll(u, t, Stats.Get("combat/Creatures/Sylvan/WardingArrows_ProcFactor", 1), targetHpBefore))
            {
                Emit(new AbilityTriggered(u.Id, Ab.WardingArrows, t.Id));
                SetAtb(t.Id, Math.Max(0, GetAtb(t.Id) - Math.Clamp(Stats.Get("combat/Creatures/Sylvan/WardingArrowsATBAffect", 0.2), 0, 1)));
            }
            if (u.IsWarMachine && u.Alive && t.Alive) SpecBallistaShot(u, t);
            if (u.IsWarMachine && u.Alive && t.Alive && HeroOf(u).HasPerk("HERO_SKILL_IMBUE_BALLISTA")) ImbuedCast(HeroOf(u), t);
            if (u.IsWarMachine && u.Alive)
            {
                if (HeroOf(u).HasPerk("HERO_SKILL_WILDFIRE") && t.Alive)
                    DealElementalDamage(t, 50, Elements.Fire, u.Id, "FLAMING_ARROWS");
            }
        }
        // Ranged Retaliation (succubi, p.179).
        if (t.Alive && u.Alive && HasAbilityNow(t, Ab.RangedRetaliate) && t.RetaliationsLeft > 0 && CanShoot(t) && !u.IsObject)
        {
            if (t.RetaliationsLeft != int.MaxValue) t.RetaliationsLeft--;
            if (!HasAmmoCart(t.Side)) t.Shots--;
            Strike(t, u, new Hit { Kind = AttackKind.Retaliation }, pattern: true, rangedRetaliation: true);
        }
        PowerfulBlowFollowUp(u, t, melee: false);
    }

    // ------------------------------------------------------------------ strike resolution

    public readonly record struct StrikeResult(int Damage, int Killed, bool Stunned);

    /// <summary>One blow (with its attack pattern) from attacker to target, including luck and on-hit effects.</summary>
    StrikeResult Strike(Unit a, Unit t, Hit hit, bool pattern, bool extraAdjacentAll = false, bool rangedRetaliation = false)
    {
        bool ranged = hit.Kind == AttackKind.Ranged || rangedRetaliation;
        Emit(new AttackStarted(a.Id, t.Id, hit.Kind));
        int targetHpBefore = t.TotalHealth;

        // Luck: rolled once per attack before the target loop, while the damage roll is
        // repeated for every target (exe 00a673c0; Elves' Double Shot re-rolls per shot).
        double luckMult = RollLuck(a, t);

        var targets = new List<(Unit U, double Factor)> { (t, 1) };
        if (pattern) targets.AddRange(PatternTargets(a, t, hit, ranged, extraAdjacentAll));

        int primaryDamage = 0, primaryKilled = 0;
        bool stunned = false;
        foreach (var (target, factor) in targets)
        {
            if (!target.Alive) continue;
            bool primary = target.Id == t.Id;
            if (IncorporealMiss(a, target, ranged))
            {
                Emit(new Missed(a.Id, target.Id, "incorporeal"));
                continue;
            }
            if (EldritchProtected(a, target, ranged))
            {
                Emit(new Missed(a.Id, target.Id, "eldritch_aura"));
                continue;
            }
            var h = new Hit
            {
                Kind = hit.Kind == AttackKind.Retaliation && ranged ? AttackKind.Retaliation : hit.Kind,
                CellsWalked = hit.CellsWalked, Multiplier = hit.Multiplier, Secondary = !primary,
                FixedRoll = hit.FixedRoll, IgnoreDefence = hit.IgnoreDefence,
            };
            int dmg = ComputeDamage(a, target, h, ranged, luckMult);
            // The pattern share is applied to the already truncated damage and truncated again (exe 00a67bf3).
            if (factor != 1) dmg = (int)(dmg * (float)factor);
            int hpBefore = target.TotalHealth;
            int killed = DealDamage(target, dmg, a.Id, ranged ? AttackKind.Ranged : hit.Kind, null);
            a.Frags += killed * target.Tier;
            if (primary) { primaryDamage = dmg; primaryKilled = killed; }
            AfterHitOnTarget(a, target, hit, ranged, dmg, killed, primary, hpBefore, targetHpBefore, ref stunned);
            if (primary) OnHitExtras(a, target, !ranged, hit.Kind == AttackKind.Retaliation, dmg);
            if (!a.Alive) break;
        }
        return new StrikeResult(primaryDamage, primaryKilled, stunned);
    }

    double RollLuck(Unit a, Unit t)
    {
        if (a.IsWarMachine) return 1;
        int luck = LuckOf(a);
        var ah = HeroOf(a); var th = HeroOf(t);
        bool absoluteProt = th.HasPerk(Sk.AbsoluteProtection) && !ah.HasPerk(Sk.NaturesLuck);
        bool naturesLuck = ah.HasPerk(Sk.NaturesLuck) && !th.HasPerk(Sk.AbsoluteProtection);
        double goodMult = Stats.Get("combat/LuckAndMorale/GoodLuck_DamageMultiplier", 2);
        if (ah.HasPerk(Sk.ElvenLuck)) goodMult = Stats.Get("combat/HeroSkills/Ranger/ElvenLuck_DamageMultiplier", 2.25);
        double badMult = Stats.Get("combat/LuckAndMorale/BadLuck_DamageMultiplier", 0.5);
        if (naturesLuck) { Emit(new LuckProc(a.Id, true)); return goodMult; }
        // Luck is clamped to [-5, 5]; chance = 10% per point against a 0..99 roll (exe 00a6b820, 00a5a170).
        luck = Math.Clamp(luck, -5, 5);
        double result = 1;
        if (luck > 0 && RollPercent(luck * 10)) result = goodMult;
        else if (luck < 0 && RollPercent(-luck * 10)) result = badMult;
        // Absolute Protection (exe 008a3ef0): a good roll becomes normal, anything else is bad luck.
        if (absoluteProt) result = result > 1 ? 1 : badMult;
        if (result > 1) Emit(new LuckProc(a.Id, true));
        else if (result < 1) Emit(new LuckProc(a.Id, false));
        return result;
    }

    /// <summary>The engine's generic "combat ability roll": succeeds when rand(0..99) &lt; chance (exe 00a5a170).</summary>
    bool RollPercent(int chance) => chance >= 100 || chance > 0 && S.Rng.Next(101) < chance;   // rand(0,100) is inclusive (exe 008c04c0)

    bool IncorporealMiss(Unit a, Unit t, bool ranged)
    {
        bool incorporeal = t.Has(Ab.Incorporeal) || t.HasEffect(H5.Combat.Fx.RuneEtherealness) || t.IsClone && t.Has(Ab.Incorporeal);
        if (!incorporeal) return false;
        // At most 3 hits or 3 misses in a row globally (p.176).
        bool miss;
        if (S.IncorporealStreak >= 3) miss = true;
        else if (S.IncorporealStreak <= -3) miss = false;
        else miss = S.Rng.Chance(Stats.Get("combat/Creatures/Necropolis/ChanceToMissToIncorporeal", 0.5));
        S.IncorporealStreak = miss ? Math.Min(-1, S.IncorporealStreak - 1) : Math.Max(1, S.IncorporealStreak + 1);
        return miss;
    }

    /// <summary>
    /// The stack's base damage roll (exe 00a597a0): with min == max it is Count*min; otherwise up to
    /// three creatures roll individually and larger stacks average three rolls: (r1+r2+r3)/3 * Count.
    /// Confusion (Forgetfulness) removes part of the stack from ranged attacks and retaliations.
    /// </summary>
    double BaseDamageRoll(Unit a, Unit t, Hit hit, bool ranged)
    {
        var (min, max) = DamageRangeOf(a);
        int count = a.IsWarMachine ? 1 : a.Count;
        if (a.GetEffect("SPELL_FORGETFULNESS") is { } conf && (ranged || hit.Kind == AttackKind.Retaliation))
            count = (int)Math.Round((1 - conf.Value) * count);
        if (t.HasEffect(H5.Combat.Fx.Sleep) && a.CreatureId == "CREATURE_VAMPIRE_PRINCE") min = max;    // Sleeping Strike
        if (hit.FixedRoll is double fr) return count * (fr < 0 ? (min + max) / 2 : fr);           // previews / AI
        if (min == max) return count * min;
        if (count > 3)
            return count * (S.Rng.RangeD(min, max) + S.Rng.RangeD(min, max) + S.Rng.RangeD(min, max)) * (float)(1.0 / 3);
        double sum = 0;
        for (int i = 0; i < count; i++) sum += S.Rng.RangeD(min, max);
        return sum;
    }

    /// <summary>
    /// Physical damage of attacker vs one target, ported from the executable:
    /// attacker multipliers (exe 00a58de0) × range penalty (00a5c5a0) × defender stage (00a5a900),
    /// truncated with a 1e-4 epsilon and never below 1. The attack/defence factor is uncapped
    /// (DamageIncreaseCap/DamageDecreaseCap are loaded but never read by the game).
    /// </summary>
    public int ComputeDamage(Unit a, Unit t, Hit hit, bool ranged, double luckMult = 1)
    {
        var ah = HeroOf(a); var th = HeroOf(t);
        bool melee = !ranged;
        bool isShooter = a.MaxShots > 0 && !a.IsWarMachine;
        double baseDmg = BaseDamageRoll(a, t, hit, ranged);

        // Avenger critical (exe 00a5ae40 / 00a673c0): replaces the luck multiplier with luck+1
        // when luck is normal or good, and with 1.0 on bad luck.
        if (!hit.Secondary && ah.FavoriteEnemies.Count > 0 && IsFavoredEnemy(ah, t) && hit.FixedRoll == null
            && S.Rng.Chance(0.4 + (ah.HasPerk("HERO_SKILL_CUNNING_OF_THE_WOODS") ? 0.1 : 0)))
            luckMult = luckMult >= 1 ? luckMult + 1 : 1;

        // ---- attacker stage (exe 00a58de0), starting from the luck multiplier
        double m = luckMult * hit.Multiplier;
        if (melee ? isShooter && !HasAbilityNow(a, Ab.NoMeleePenalty) : HasAbilityNow(a, Ab.RangePenalty)) m *= 0.5;
        if (melee) m *= 1 + Math.Clamp(OffenceBonus(ah), 0, 10);
        else if (ah.HasPerk(Sk.Archery)) m *= 1 + Math.Clamp(Stats.Get("combat/HeroSkills/SkillOffence/Archery_DamageBonus", 0.2), 0, 10);
        if (hit.Kind == AttackKind.Retaliation)
        {
            if (ah.Class == "HERO_CLASS_KNIGHT" || ah.SkillLevel(Sk.Training) > 0) m *= 1 + Math.Clamp(TrainingBonus(ah), 0, 10);
        }
        else if (ah.HasPerk(Sk.Retribution))
        {
            int morale = MoraleOf(a);
            if (morale > 0) m *= 1 + Math.Clamp(Stats.Get("combat/HeroSkills/Knight/Retribution_DamageBonusPerMorale", 0.05) * morale, 0, 10);
        }
        if (melee && HasAbilityNow(a, Ab.Jousting) && hit.Kind == AttackKind.Melee && hit.CellsWalked > 0)
        {
            double per = Math.Clamp(Stats.Get("combat/Creatures/Haven/Jousting_DamageBonusPerPathCell", 0.05), 0, 10);
            double j = 1 + Math.Clamp(per, 0, 100) * hit.CellsWalked;
            // Cavalry Commander multiplies the jousting factor by (1 + bonus + perLevel*level), exe 00a58f60.
            if (ah.Specialization == "HERO_SPEC_JOUSTING")
                j *= 1 + Math.Clamp(Stats.Get("combat/HeroSpecializations/Knight/CavalryCommander_JoustingBonus", 0.1), 0, 1000)
                       + Math.Clamp(Stats.Get("combat/HeroSpecializations/Knight/CavalryCommander_JoustingBonusPerLevel", 0.01), 0, 1000) * ah.Level;
            m *= j;
        }
        if (hit.Kind == AttackKind.Retaliation)
        {
            if (HasAbilityNow(a, Ab.FierceRetaliation)) m *= Stats.Get("combat/Creatures/Stronghold/FierceRetaliationCoef", 1.5);
        }
        if (a.HasEffect("FX_STORM_PENALTY") && ranged) m *= 1 - Fx(a, "FX_STORM_PENALTY");

        // ---- range penalty (exe 00a5c5a0): nearest-cell squared distance > (width/2)^2
        if (ranged && !a.IsWarMachine && !HasAbilityNow(a, Ab.NoRangePenalty) && !HasAbilityNow(a, Ab.RangePenalty))
        {
            int half = S.Width / 2;
            if (NearestCellDistanceSq(a, t) > half * half) m *= 0.5;
        }
        // Shooting across intact town walls (same exe function; not affected by No Range Penalty).
        if (ranged && ShotCrossesWalls(a, t)) m *= 0.5;

        // ---- defender stage (exe 00a5a900)
        int A = AttackOf(a, t), D = DefenceOf(t, a);
        if (ranged && HasAbilityNow(a, Ab.PiercingArrow)) D = (int)(D * Stats.Get("combat/Creatures/Sylvan/PiercingArrowCoef", 0.5));
        if (!t.Has(Ab.Armored))
        {
            int range = (int)Stats.Get("combat/Creatures/Haven/PreciseShotRange", 3);
            if (ranged && HasAbilityNow(a, Ab.PreciseShot) && NearestCellDistanceSq(a, t) <= range * range) D = 0;
            if (melee && HasAbilityNow(a, Ab.RiderCharge) && hit.CellsWalked > 0)
                D = (int)(D * Math.Clamp(1 - hit.CellsWalked * Stats.Get("combat/Creatures/Dungeon/RiderCharge_DefenceReducePerPathCell", 0.2), 0, 1));
            if (a.IsWarMachine && a.CreatureId == "WAR_MACHINE_BALLISTA" && ah.HasPerk("HERO_SKILL_WILDFIRE")) D /= 2;
        }
        if (hit.IgnoreDefence) D = 0;
        double ad = A > D
            ? 1 + Math.Max(0, Stats.Get("combat/DamageIncrease", 0.05)) * (A - D)
            : 1 / (1 - Math.Max(0, Stats.Get("combat/DamageDecrease", 0.05)) * (A - D));
        double dm = ad;
        if (t.GetEffect(H5.Combat.Fx.DeepFreeze) is { } df) dm *= Math.Max(df.Value, 1);
        if (ranged)
        {
            if (t.Has(Ab.LargeShield) || Adjacent(t).Any(o => o.Side == t.Side && o.Has(Ab.ShieldOther))) dm *= 0.5;
            if (th.HasPerk(Sk.Evasion)) dm *= 1 - Math.Clamp(Stats.Get("combat/HeroSkills/SkillDefence/Evasion_DamageReduce", 0.2), 0, 1);
            if (th.Present && th.Specialization == "HERO_SPEC_EVASIVE")
                dm *= 1 - Math.Clamp(Stats.Get("combat/HeroSpecializations/Warlock/Evasive_DamageReduceBase", 0)
                                     + Stats.Get("combat/HeroSpecializations/Warlock/Evasive_DamageReducePerLevel", 0.01) * th.Level, 0, 1);
            if (t.GetEffect("SPELL_DEFLECT_ARROWS") is { } da) dm *= 1 - Math.Clamp(da.Value, 0, 0.95);
        }
        else
        {
            if (t.Has(Ab.ShieldWall) && hit.Kind == AttackKind.Melee && hit.CellsWalked > 0)
            {
                double r = Math.Clamp(hit.CellsWalked * Stats.Get("combat/Creatures/Fortress/ShieldWall_DamageReducePerPathCell", 0.1), 0, 1);
                r = Math.Clamp(r, 0, Stats.Get("combat/Creatures/Fortress/ShieldWall_MaxDamageReduce", 0.9));
                dm *= 1 - r;
            }
            dm *= 1 - Math.Clamp(DefenceReduction(th), 0, 1);
        }
        if (t.Summoned && ah.HasPerk(Sk.BackToTheVoid)) dm *= Stats.Get("combat/HeroSkills/Barbarian/DeathToNonexistent_DamageToSummonsMult", 3);

        // Forest Guardian (exe 00a59340): Sylvan creatures vs the hero's favoured enemies.
        if (ah.Present && ah.Specialization == "HERO_SPEC_FOREST_GUARDIAN" && a.Town == Towns.Sylvan && IsFavoredEnemy(ah, t))
            dm *= 1 + Math.Clamp(Stats.Get("combat/HeroSpecializations/Ranger/ForestGuardian_DamageBonus", 0)
                                 + Stats.Get("combat/HeroSpecializations/Ranger/ForestGuardian_DamageBonusPerHeroLevel", 0.02) * ah.Level, 0, 10);

        double dmg = baseDmg * m * dm;
        int result = Math.Max(1, (int)(dmg + 1e-4));
        // Blade of Slaughter: +2 per creature in the target stack, after multipliers (p.172).
        if (HasAbilityNow(a, Ab.AxeOfSlaughter) && melee) result += (int)Stats.Get("combat/Creatures/Inferno/AxeOfSlaughterDamageHits", 2) * t.Count;
        // Vorpal Sword: one extra creature killed (exe 00a673c0 / 00a63020: +1 kill unless the target is not a creature or
        // has CRYSTAL_SCALES).
        if (HasAbilityNow(a, Ab.Vorpal) && melee && !hit.Secondary && !t.IsWarMachine && !t.IsObject && !t.Has("ABILITY_CRYSTAL_SCALES"))
            result += MaxHealthOf(t);
        return Math.Max(baseDmg > 0 ? 1 : 0, result);
    }

    bool IsFavoredEnemy(HeroState h, Unit t)
    {
        if (!Db.Creatures.TryGetValue(t.CreatureId, out var c)) return false;
        string baseId = c.Upgrade && c.BaseCreature is { Length: > 0 } b && b != "CREATURE_UNKNOWN" ? b : c.Id;
        return h.FavoriteEnemies.Contains(c.Id) || h.FavoriteEnemies.Contains(baseId);
    }

    // exe 00a58de0 / 00a5a900: the mastery switch has cases 1..3 only — any other level gives no bonus.
    double OffenceBonus(HeroState h) => h.SkillLevel(Sk.Offence) switch
    {
        Mastery.Basic => Stats.Get("combat/HeroSkills/SkillOffence/BasicDamageBonus", 0.05),
        Mastery.Advanced => Stats.Get("combat/HeroSkills/SkillOffence/AdvancedDamageBonus", 0.1),
        Mastery.Expert => Stats.Get("combat/HeroSkills/SkillOffence/ExpertDamageBonus", 0.15),
        _ => 0,
    };

    double DefenceReduction(HeroState h) => h.SkillLevel(Sk.Defence) switch
    {
        Mastery.Basic => Stats.Get("combat/HeroSkills/SkillDefence/BasicDamageReduce", 0.1),
        Mastery.Advanced => Stats.Get("combat/HeroSkills/SkillDefence/AdvancedDamageReduce", 0.2),
        Mastery.Expert => Stats.Get("combat/HeroSkills/SkillDefence/ExpertDamageReduce", 0.3),
        _ => 0,
    };

    double TrainingBonus(HeroState h) => h.SkillLevel(Sk.Training) switch
    {
        Mastery.Basic => Stats.Get("combat/HeroSkills/Knight/Training/BasicDamageBonus", 0.05),
        Mastery.Advanced => Stats.Get("combat/HeroSkills/Knight/Training/AdvancedDamageBonus", 0.1),
        Mastery.Expert => Stats.Get("combat/HeroSkills/Knight/Training/ExpertDamageBonus", 0.2),
        Mastery.Ultimate => Stats.Get("combat/HeroSkills/Knight/Training/ExtraExpertDamageBonus", 0.25),
        _ => 0,
    };

    static (int X, int Y) Dir(Unit a, Unit t)
    {
        var (ax, ay) = a.Center; var (tx, ty) = t.Center;
        return (Math.Sign(Math.Round(tx - ax, 3)), Math.Sign(Math.Round(ty - ay, 3)));
    }

    /// <summary>Additional units hit by an attack pattern, with damage factors.</summary>
    IEnumerable<(Unit, double)> PatternTargets(Unit a, Unit t, Hit hit, bool ranged, bool allAdjacent)
    {
        var list = new List<(Unit, double)>();
        var seen = new HashSet<int> { t.Id, a.Id };
        void Add(Unit? u, double f, bool enemiesOnly = true)
        {
            if (u == null || !u.Alive || u.IsObject && u.Side == a.Side || seen.Contains(u.Id)) return;
            if (enemiesOnly && Controller(u) == Controller(a)) return;
            seen.Add(u.Id);
            list.Add((u, f));
        }
        var (dx, dy) = Dir(a, t);
        if (!ranged)
        {
            if (allAdjacent || HasAbilityNow(a, Ab.SixHeaded))
                foreach (var o in Adjacent(a)) Add(o, 1);
            if (HasAbilityNow(a, Ab.WarDance))
                foreach (var o in Adjacent(a)) Add(o, 0.5);
            if (HasAbilityNow(a, Ab.ThreeHeaded) || HasAbilityNow(a, Ab.Spray))
                foreach (var o in Adjacent(a).Where(o => AreAdjacent(o, t))) Add(o, 1);
            if (HasAbilityNow(a, Ab.Whirlwind))
                foreach (var o in Adjacent(a))
                {
                    var (ox, oy) = Dir(a, o);
                    if (ox * dx + oy * dy >= 0) Add(o, 1);
                }
            if (HasAbilityNow(a, Ab.CrushingBlow))
            {
                // 2x2 area including the target cell nearest the attacker, any side (p.173).
                var anchor = t.Cells().OrderBy(c => Octile(c, a.Pos)).First();
                for (int ox = 0; ox < 2; ox++) for (int oy = 0; oy < 2; oy++)
                    Add(S.At(new Cell(anchor.X + (dx < 0 ? -ox : ox), anchor.Y + (dy < 0 ? -oy : oy))), 1, enemiesOnly: false);
            }
            if (HasAbilityNow(a, Ab.FireBreath))
                Add(BehindTarget(t, dx, dy, 1), 1, enemiesOnly: false);
            if (HasAbilityNow(a, Ab.ChampionCharge) && hit.Kind == AttackKind.Melee
                && hit.CellsWalked >= Stats.Get("combat/Creatures/Haven/ChampionChargeMinimalDistance", 2))
                Add(BehindTarget(t, dx, dy, 1), 0.5);
            if (HasAbilityNow(a, Ab.AcidBreath) || HasAbilityNow(a, Ab.RainbowBreath))
            {
                // Prismatic Breath: 1..5 cells behind the target (p.179).
                int n = Math.Min(5, 1 + (S.Rng.Range(1, 10) + LuckOf(a)) / 2);
                for (int i = 1; i <= n; i++) Add(BehindTarget(t, dx, dy, i), 1, enemiesOnly: false);
            }
            if (HasAbilityNow(a, Ab.LightningBreath))
            {
                var near = S.Living.Where(o => Controller(o) != Controller(a) && o.Id != t.Id && !o.IsObject && AreAdjacent(o, t)).ToList();
                foreach (var o in near) Add(o, 1.0 / (near.Count + 1));
            }
        }
        else
        {
            if (HasAbilityNow(a, Ab.DeathCloud))
                foreach (var o in S.Living.Where(o => o.Id != t.Id && !IsUndeadLike(o) && !o.IsObject && Distance(o, t) <= 1 && CellsWithin(o, t, 1)))
                    Add(o, 0.5, enemiesOnly: false);
            if (HasAbilityNow(a, Ab.CrossAttack))
                foreach (var d in new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) })
                    foreach (var c in t.Cells()) Add(S.At(c + d), 1);
            if (HasAbilityNow(a, Ab.MagicAttack))
                foreach (var c in LineCells(a.Pos, t.Pos)) Add(S.At(c), 1, enemiesOnly: false);
            if (HasAbilityNow(a, Ab.ChainShot) && !t.Has(Ab.Demonic))
            {
                double f = 0.5; var last = t;
                for (int i = 0; i < 3; i++)
                {
                    var next = S.Living.Where(o => Controller(o) != Controller(a) && !seen.Contains(o.Id) && !o.Has(Ab.Demonic) && !o.IsObject)
                        .OrderBy(o => Distance(o, last)).ThenBy(o => o.Id).FirstOrDefault();
                    if (next == null) break;
                    Add(next, f); f *= 0.5; last = next;
                }
            }
        }
        return list;
    }

    bool CellsWithin(Unit o, Unit t, int r) => o.Cells().Any(c => t.Cells().Any(tc => tc.Chebyshev(c) <= r));

    Unit? BehindTarget(Unit t, int dx, int dy, int depth)
    {
        if (dx == 0 && dy == 0) return null;
        foreach (var c in t.Cells())
        {
            var p = new Cell(c.X + dx * depth, c.Y + dy * depth);
            var u = S.At(p);
            if (u != null && u.Id != t.Id) return u;
        }
        return null;
    }

    static IEnumerable<Cell> LineCells(Cell a, Cell b)
    {
        int x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            yield return new Cell(x0, y0);
            if (x0 == x1 && y0 == y1) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>On-hit ability effects of the attacker on a struck target, and the target's reactive abilities.</summary>
    void AfterHitOnTarget(Unit a, Unit t, Hit hit, bool ranged, int dmg, int killed, bool primary, int hpBefore, int primaryHpBefore, ref bool stunned)
    {
        bool retaliation = hit.Kind == AttackKind.Retaliation;
        var ah = HeroOf(a);
        if (dmg > 0 && a.Alive)
        {
            // Extra elemental damage from hero perks.
            if (t.Alive && (ah.HasPerk(Sk.FieryWrath) || ah.HasPerk(Sk.BarbFieryWrath)) && !a.IsWarMachine)
                DealElementalDamage(t, (int)(dmg * 0.1), Elements.Fire, a.Id, "FIERY_WRATH");
            if (t.Alive && ah.HasPerk(Sk.ColdSteel) && !a.IsWarMachine)
                DealElementalDamage(t, (int)(dmg * 0.1), Elements.Water, a.Id, "COLD_STEEL");
            if (t.Alive && ah.HasPerk(Sk.Hellfire) && !a.IsWarMachine && (!retaliation || ah.HasPerk(Sk.Hellwrath)) && ah.Mana >= 5
                && S.Rng.Chance(Stats.Get("combat/HeroSkills/DemonLord/CriticalStrike_ChanceToWork", 30) / 100.0))
            {
                double dmgH = Stats.Get("combat/HeroSkills/DemonLord/DemonicFire/BaseDamage", 10) + Stats.Get("combat/HeroSkills/DemonLord/DemonicFire/DamagePerSpellPower", 10) * ah.SpellPower;
                if (ah.HasPerk(Sk.SearingFires)) dmgH *= 1.5;
                ah.Mana -= (int)Stats.Get("combat/HeroSkills/DemonLord/DemonicFire/ManaCost", 5);
                Emit(new ManaChanged(Actor.Hero(a.Side), ah.Mana));
                DealElementalDamage(t, (int)dmgH, Elements.Fire, a.Id, "HELLFIRE");
            }
            // Life Drain: heals half the damage dealt, resurrecting (p.178).
            if (HasAbilityNow(a, Ab.LifeDrain) || a.GetEffect("SPELL_VAMPIRISM") is { })
            {
                double frac = HasAbilityNow(a, Ab.LifeDrain) ? Stats.Get("combat/Creatures/Necropolis/LifeDrainIntensity", 0.5) : 0;
                if (a.GetEffect("SPELL_VAMPIRISM") is { } vamp) frac += vamp.Value;
                if (!t.Has(Ab.Elemental) && !t.Has(Ab.Mechanical) && !t.IsWarMachine && !t.IsObject)
                    Heal(a, (int)(Math.Min(dmg, hpBefore) * frac), resurrect: true, "LIFE_DRAIN");
            }
            // Fire/Magma Shield on melee (p.174, p.177).
            if (!ranged && a.Alive)
            {
                // Fire / Magma Shield (exe 00a5bcf0 / 00a5bdd0): clamp(multiplier, 0, 10) × the damage taken, capped at the
                // shield holder's hit points before the blow (overkill doesn't count).
                int taken = Math.Min(dmg, hpBefore);
                if (t.Has(Ab.FireShield)) DealElementalDamage(a, (int)(taken * Math.Clamp(Stats.Get("combat/Creatures/Abilities/FireShield_Damage_Multiplier", 0.2), 0, 10)), Elements.Fire, t.Id, Ab.FireShield);
                if (t.Has(Ab.MagmaShield) && a.Alive) DealElementalDamage(a, (int)(taken * Math.Clamp(Stats.Get("combat/Creatures/Abilities/MagmaShield_Damage_Multiplier", 0.4), 0, 10)), Elements.Fire, t.Id, Ab.MagmaShield);
                var th = HeroOf(t);
                if (th.HasPerk(Sk.ChillingBones) && IsUndeadLike(t) && a.Alive) DealElementalDamage(a, (int)(dmg * 0.05), Elements.Water, t.Id, "CHILLING_BONES");
                if (t.Has(Ab.AcidBlood) && a.Alive && !retaliation)
                {
                    Emit(new AbilityTriggered(t.Id, Ab.AcidBlood, a.Id));
                    DealDamage(a, ComputeDamage(t, a, new Hit { Kind = AttackKind.Ability, Multiplier = 0.25 }, false), t.Id, AttackKind.Ability, Ab.AcidBlood);
                }
            }
        }
        if (!t.Alive || dmg <= 0 || !a.Alive) return;

        // Target reactions
        if (HasAbilityNow(t, Ab.BattleRage) && !t.HasEffect(H5.Combat.Fx.BattleRage))
            AddEffect(t, new Effect { Id = H5.Combat.Fx.BattleRage, Value = Stats.Get("combat/Creatures/Fortress/BattleRage_AttackBonus", 5),
                Remaining = Stats.Get("combat/Creatures/Fortress/BattleRage_EffectDuration", 2), Positive = true, Dispellable = false });
        if (t.HasEffect("SPELL_BLIND")) RemoveEffect(t, "SPELL_BLIND");   // blinded creatures regain sight when attacked (p.186)
        if (t.HasEffect(H5.Combat.Fx.Sleep)) RemoveEffect(t, H5.Combat.Fx.Sleep);
        if (t.HasEffect("SPELL_HYPNOTIZE")) RemoveEffect(t, "SPELL_HYPNOTIZE");
        if (t.IsClone) { Kill(t, a.Id); return; }

        if (!primary) return;
        bool melee = !ranged;
        bool living = IsLiving(t);

        if (melee && !retaliation && HasAbilityNow(a, Ab.Bash) && !t.Has(Ab.Mechanical) && !t.IsWarMachine
            && ProcRoll(a, t, Stats.Get("combat/Creatures/Haven/Bash_ProcFactor", 1.5), primaryHpBefore, soldiersLuckDoubles: true, secondRoll: false))
        {
            Emit(new AbilityTriggered(a.Id, Ab.Bash, t.Id));
            SetAtb(t.Id, 0);
            stunned = true;
            RageEvent(t, "ReceiveBashRP");
        }
        // Crushing Blow (exe FUN_00a5af70): besides its 2x2 area, a Bash-like chance (CrushingBlow_ProcFactor, never on a
        // retaliation, no Soldier's Luck second roll) to zero the target's ATB and cancel its retaliation.
        if (melee && !retaliation && HasAbilityNow(a, Ab.CrushingBlow) && !t.IsWarMachine && !t.IsObject
            && ProcRoll(a, t, Stats.Get("combat/Creatures/Stronghold/CrushingBlow_ProcFactor", 1), primaryHpBefore, soldiersLuckDoubles: true, secondRoll: false))
        {
            Emit(new AbilityTriggered(a.Id, Ab.CrushingBlow, t.Id));
            SetAtb(t.Id, 0);
            stunned = true;
        }
        // Force Arrow (ABILITY_PIERCING_ARROW, exe FUN_00a5d420): ranged chance (PiercingArrow_ProcFactor, no second roll)
        // to push a non-HOLD_GROUND creature back one tile and stun it (ATB 0). The destination is the exe's cell search
        // (FUN_00a5ceb0, ±π/3 around "away from the shooter"); with no free cell the ability doesn't trigger.
        if (ranged && !retaliation && HasAbilityNow(a, Ab.PiercingArrow) && !t.IsWarMachine && !t.IsObject && !t.Has(Ab.HoldGround)
            && ProcRoll(a, t, Stats.Get("combat/Creatures/Sylvan/PiercingArrow_ProcFactor", 1), secondRoll: false)
            && PushDestination(a, t, ForceArrowPushTolerance) is { } back)
        {
            Emit(new AbilityTriggered(a.Id, Ab.PiercingArrow, t.Id));
            SetAtb(t.Id, 0);
            stunned = true;
            if (back != t.Pos) Teleport(t, back);
        }
        // Fear (exe FUN_00a629b0): only on the attacker's own strike (never a retaliation) and only while the target would
        // still retaliate; the target runs instead of retaliating.
        if (!retaliation && melee && HasAbilityNow(a, Ab.Fear) && !MindImmune(t) && CanRetaliate(t, a)
            && ProcRoll(a, t, Stats.Get("combat/Creatures/Inferno/Fear_ProcFactor", 0.8)))
        {
            Emit(new AbilityTriggered(a.Id, Ab.Fear, t.Id));
            SetAtb(t.Id, 0);
            Flee(t, a);
            RageEvent(t, "ReceiveFearRP");
        }
        if (!t.Alive) return;
        if (melee && HasAbilityNow(a, Ab.BlindingAttack) && living && !t.Has(Ab.ImmuneBlind)
            && ProcRoll(a, t, Stats.Get("combat/Creatures/Sylvan/BlindingAttack_ProcFactor", 1)))
        {
            Emit(new AbilityTriggered(a.Id, Ab.BlindingAttack, t.Id));
            ApplySpellEffectFromCreature(a, t, "SPELL_BLIND", Mastery.Basic, (int)Stats.Get("combat/Creatures/Sylvan/BlindingAttackSpellPower", 8));
        }
        if (HasAbilityNow(a, Ab.Poison) && living)
        {
            double per = a.CreatureId.Contains("MANTICORE") ? Stats.Get("combat/Creatures/Neutral/ManticorePoisonDamagePerPower", 4)
                                                            : Stats.Get("combat/Creatures/Dungeon/AssasinPoisonDamagePerPower", 1);
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Poison, Value = per * a.Count, Actions = (int)Stats.Get("combat/Creatures/Dungeon/AssasinPoisonCounter", 3), SourceSide = a.Side });
        }
        if (HasAbilityNow(a, Ab.Venom) && living)
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Poison, Value = 5 * a.Count, Actions = 3, SourceSide = a.Side, Dispellable = false });
        if (HasAbilityNow(a, Ab.WeakeningStrike) && melee)
        {
            var e = t.GetEffect(H5.Combat.Fx.WeakeningStrike);
            double v = (e?.Value ?? 0) + Stats.Get("combat/Creatures/Necropolis/ZombieWeakeningStrike", 2);
            AddEffect(t, new Effect { Id = H5.Combat.Fx.WeakeningStrike, Value = v, SourceSide = a.Side });
        }
        if (HasAbilityNow(a, Ab.CursingAttack) && !t.Has(Ab.ImmuneCurse))
            ApplySpellEffectFromCreature(a, t, "SPELL_CURSE", Mastery.Advanced, CreatureSpellPower(a.Count, Math.Max(1, a.WeeklyGrowth)));
        if (HasAbilityNow(a, Ab.HexingAttack))
        {
            string[] hex = { "SPELL_CURSE", "SPELL_SLOW", "SPELL_WEAKNESS", "SPELL_DISRUPTING_RAY" };
            ApplySpellEffectFromCreature(a, t, hex[S.Rng.Next(hex.Length)], Mastery.Expert, CreatureSpellPower(a.Count, Math.Max(1, a.WeeklyGrowth)));
        }
        if (melee && HasAbilityNow(a, Ab.WhipStrike) && living
            && ProcRoll(a, t, Stats.Get("combat/Creatures/Dungeon/WhipStrike_ProcFactor", 1.5)))
        {
            // exe FUN_00a5e2c0: r = Rng(100); r < 40 → Slow, r < 75 → Weakness, else Berserk.
            int r = S.Rng.Next(100);
            string whip = r < 40 ? "SPELL_SLOW" : r < 75 ? "SPELL_WEAKNESS" : "SPELL_BERSERK";
            ApplySpellEffectFromCreature(a, t, whip, Mastery.Basic, (int)Stats.Get("combat/Creatures/Dungeon/WhipStrikeSpellPower", 3));
        }
        // Wound (exe FUN_00a629b0): creature targets only, never mechanical or elemental.
        if (HasAbilityNow(a, Ab.Wound) && !t.IsWarMachine && !t.IsObject && !t.Has(Ab.Mechanical) && !t.Has(Ab.Elemental) && ProcRoll(a, t, Stats.Get("combat/Creatures/Fortress/Wound_ProcFactor", 0.7)))
        {
            Emit(new AbilityTriggered(a.Id, Ab.Wound, t.Id));
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Wound, Remaining = Stats.Get("combat/Creatures/Fortress/Wound_EffectDurationInRounds", 2), SourceSide = a.Side });
        }
        // Mark of Fire (exe FUN_00a629b0): ranged strikes only.
        if (ranged && HasAbilityNow(a, Ab.MarkOfFire) && ProcRoll(a, t, Stats.Get("combat/Creatures/Fortress/MarkOfFire_ProcFactor", 1)))
        {
            Emit(new AbilityTriggered(a.Id, Ab.MarkOfFire, t.Id));
            AddEffect(t, new Effect { Id = H5.Combat.Fx.MarkOfFire, Remaining = Stats.Get("combat/Creatures/Fortress/MarkOfFire_EffectDurationInRounds", 2), SourceSide = a.Side });
        }
        if (HasAbilityNow(a, Ab.EvilEye))
            AddEffect(t, new Effect { Id = H5.Combat.Fx.EvilEye, Value = 3, SourceSide = a.Side });
        if (HasAbilityNow(a, Ab.SorrowStrike))
            ApplySpellEffectFromCreature(a, t, "SPELL_SORROW", Mastery.Basic, CreatureSpellPower(a.Count, Math.Max(1, a.WeeklyGrowth)));
        // Sleeping Strike (exe FUN_00a629b0 → FUN_00a5e4f0): immune are IMMUNITY_TO_BLIND, mechanical and elemental
        // (undead are NOT immune); no melee/ranged restriction.
        if (HasAbilityNow(a, Ab.SleepingStrike) && !t.Has(Ab.ImmuneBlind) && !t.Has(Ab.Mechanical) && !t.Has(Ab.Elemental)
            && !t.IsWarMachine && !t.IsObject
            && ProcRoll(a, t, Stats.Get("combat/Creatures/Necropolis/SleepingStrike_ProcFactor", 3)))
        {
            Emit(new AbilityTriggered(a.Id, Ab.SleepingStrike, t.Id));
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Sleep, Remaining = Stats.Get("combat/Creatures/Necropolis/SleepingStrike_Duration", 2), SourceSide = a.Side });
        }
        // Paw Strike (exe FUN_00a5d1b0 chance, FUN_00a5d520 push): one roll per tile walked, none when standing still or
        // on HOLD_GROUND targets; the push takes the free neighbour closest to "away from the attacker" in any direction
        // (FUN_00a5ceb0 with 2π) and the ability doesn't trigger when there is none.
        if (melee && !retaliation && HasAbilityNow(a, Ab.PawStrike) && !t.Has(Ab.HoldGround)
            && RollPercent(a, PawStrikePercent(a, t, hit.CellsWalked, primaryHpBefore), secondRoll: true)
            && PushDestination(a, t, PawStrikePushTolerance) is { } push)
        {
            Emit(new AbilityTriggered(a.Id, Ab.PawStrike, t.Id));
            SetAtb(t.Id, 0);
            if (push != t.Pos) Teleport(t, push);
        }
        if (HasAbilityNow(a, Ab.EntanglingRoots) && melee && !t.Flying && !a.HasEffect(H5.Combat.Fx.RageOfForest))
            AddEffect(t, new Effect { Id = H5.Combat.Fx.Entangled, SourceUnit = a.Id, SourceSide = a.Side });
        if (HasAbilityNow(a, Ab.Purger) && ProcRoll(a, t, Stats.Get("combat/Creatures/Haven/Purger_ProcFactor", 2)))
        {
            var pos = t.Effects.Where(e => e.Positive && e.Dispellable).ToList();
            if (pos.Count > 0) { Emit(new AbilityTriggered(a.Id, Ab.Purger, t.Id)); RemoveEffect(t, pos[S.Rng.Next(pos.Count)].Id); }
        }
        if (HasAbilityNow(a, Ab.StormStrike) && melee)
            StormStrikeChain(a, t, dmg);
        if (a.HasEffect(H5.Combat.Fx.RuneThunderclap) && RollProc(a, ProcChance(a, t, 1, primaryHpBefore)))
        {
            Emit(new AbilityTriggered(a.Id, "RUNE_THUNDERCLAP", t.Id));
            SetAtb(t.Id, 0);
            RemoveEffect(a, H5.Combat.Fx.RuneThunderclap);
        }
        if (a.HasEffect(H5.Combat.Fx.RuneMagicControl))
        {
            var bless = t.Effects.Where(e => e.Positive && e.School == "MAGIC_SCHOOL_LIGHT").ToList();
            RemoveEffect(a, H5.Combat.Fx.RuneMagicControl);
            if (bless.Count > 0)
            {
                var stolen = bless[S.Rng.Next(bless.Count)];
                RemoveEffect(t, stolen.Id);
                AddEffect(a, new Effect { Id = stolen.Id, Value = stolen.Value, Value2 = stolen.Value2, Remaining = stolen.Remaining,
                    Positive = true, SourceSide = a.Side, Mastery = stolen.Mastery, Power = stolen.Power, School = stolen.School, SpellLevel = stolen.SpellLevel });
            }
        }
        if (HasAbilityNow(a, Ab.BearRoar) && melee && !retaliation)
        {
            bool any = false;
            foreach (var victim in new[] { t }.Concat(Adjacent(t).Where(o => Controller(o) != Controller(a) && AreAdjacent(o, a))).Distinct().ToList())
            {
                if (MindImmune(victim)) continue;
                double mult = victim.IsLarge ? 1 : Stats.Get("combat/Creatures/Fortress/BearRoar_SmallCreaturesChanceMult", 2);
                if (ProcRoll(a, victim, Stats.Get("combat/Creatures/Fortress/BearRoar_ProcFactor", 0.9) * mult, soldiersLuckDoubles: true))
                {
                    any = true;
                    Emit(new AbilityTriggered(a.Id, Ab.BearRoar, victim.Id));
                    SetAtb(victim.Id, 0);
                }
            }
            if (any) AddEffect(a, new Effect { Id = H5.Combat.Fx.BearRoar, Remaining = Stats.Get("combat/Creatures/Fortress/BearRoar_EffectDuration", 2), Positive = true });
        }
        if (HasAbilityNow(a, Ab.LiquidFlameBreath) && melee)
        {
            var cells = t.Cells().ToList();
            AddField("LIQUID_FLAME", a.Side, cells, Stats.Get("combat/Creatures/Fortress/LiquidFlameBreath_MinDuration", 2),
                Stats.Get("combat/Creatures/Fortress/LiquidFlameBreath_DamagePerCreature", 10) * a.Count, a.Id);
        }
    }

    /// <summary>Thane Storm Strike: lightning chain from the target through adjacent enemies, same force as the attack (p.181).</summary>
    void StormStrikeChain(Unit a, Unit t, int dmg)
    {
        var hitIds = new HashSet<int> { t.Id };
        var cur = t;
        int force = dmg;
        Emit(new AbilityTriggered(a.Id, Ab.StormStrike, t.Id));
        for (int i = 0; i < 10 && force > 0; i++)
        {
            var next = S.Living.Where(o => Controller(o) != Controller(a) && !o.IsObject && !hitIds.Contains(o.Id) && AreAdjacent(o, cur))
                .OrderBy(o => o.Id).FirstOrDefault();
            if (next == null) break;
            hitIds.Add(next.Id);
            DealDamage(next, force, a.Id, AttackKind.Ability, Ab.StormStrike);
            cur = next;
        }
    }

    /// <summary>Fear: the target runs to the free cell farthest from the attacker within its speed.</summary>
    void Flee(Unit t, Unit from)
    {
        if (!CanMove(t)) return;
        var reach = Reachable(t);
        if (reach.Count == 0) return;
        var dest = reach.Keys.OrderByDescending(c => Octile(c, from.Pos)).ThenBy(c => c.X).ThenBy(c => c.Y).First();
        MoveUnit(t, dest);
        AddEffect(t, new Effect { Id = H5.Combat.Fx.Feared, Actions = 1 });
    }

    // ------------------------------------------------------------------ damage & healing

    /// <summary>Applies damage to a stack. Returns creatures killed.</summary>
    public int DealDamage(Unit t, int amount, int source, AttackKind kind, string? by)
    {
        if (!t.Alive || amount <= 0) return 0;
        // Arcane Armor absorbs a share of damage into its HP pool (p.194).
        if (t.GetEffect("SPELL_CELESTIAL_SHIELD") is { } armor && armor.Value2 > 0)
        {
            int absorbed = (int)Math.Min(armor.Value2, amount * armor.Value);
            armor.Value2 -= absorbed;
            amount -= absorbed;
            if (armor.Value2 <= 0) RemoveEffect(t, "SPELL_CELESTIAL_SHIELD");
        }
        int before = t.Count;
        int total = t.TotalHealth - amount;
        // Last Stand: a stack of 2+ survives a lethal blow with 1 HP, once (p.111).
        if (total <= 0 && before >= 2 && HeroOf(t).HasPerk(Sk.LastStand) && !t.AbilityUses.ContainsKey("LAST_STAND") && t.Kind == UnitKind.Creature)
        {
            t.AbilityUses["LAST_STAND"] = 1;
            total = 1;
            Emit(new AbilityTriggered(t.Id, "LAST_STAND"));
        }
        SetTotalHealth(t, Math.Max(0, total));
        int killed = before - t.Count;
        Emit(new Damaged(t.Id, amount, killed, t.Count, t.TopHealth, source, kind, by));
        CheckTreachery(t);
        // Mark of the Necromancer feeds mana (p.142).
        foreach (var h in S.Heroes)
            if (h.Present && h.Links.TryGetValue(Sk.MarkOfNecromancer, out var linked) && linked == t.Id)
            {
                int gain = (int)Math.Ceiling(amount / Stats.Get("combat/HeroSkills/Necromancer/SpiritLink_HPForOneMana", 75));
                h.Mana = Math.Min(h.MaxMana, h.Mana + gain);
                Emit(new ManaChanged(Actor.Hero(h.Side), h.Mana));
            }
        if (kind is AttackKind.Melee) RageEvent(t, "RecevieMeleeAttackRP");
        if (t.Count <= 0 && !TryRebirth(t)) OnDeath(t, source);
        return killed;
    }

    /// <summary>
    /// Specialisation summons when a (non-summoned) stack dies (exe FUN_00a61310):
    /// Master of Elements (the dead stack's hero, FUN_00a5a5a0) brings terrain elementals:
    /// max(1, round(count·HP·0.25 / elementalHP)) + ((level−1)/div)·per + base;
    /// Soulhunter (the opposing hero, FUN_00a5b440) raises min(count, round(level·perLevel + base)) Manes.
    /// </summary>
    void DeathSummons(Unit t)
    {
        var own = S.Heroes[t.Side];
        if (own.Present && own.Specialization == "HERO_SPEC_MASTER_OF_ELEMENTS")
        {
            string kind = TerrainElemental();
            double elemHp = Math.Max(1, Db.Creature(kind).Health);
            int n = Math.Max(1, (int)Math.Round(t.InitialCount * (double)t.BaseHealth * 0.25 / elemHp, MidpointRounding.ToEven));
            const string k = "combat/HeroSpecializations/Wizard/MasterOfElements_NumElementals/";
            n += (own.Level - 1) / Math.Max(1, (int)Stats.Get(k + "LevelDivisor", 3)) * (int)Stats.Get(k + "PerDividedLevel", 1) + (int)Stats.Get(k + "Base", 1);
            SummonStackNear(t, kind, n, "HERO_SPEC_MASTER_OF_ELEMENTS", 0);
        }
        var enemy = S.Heroes[1 - t.Side];
        if (enemy.Present && enemy.Specialization == "HERO_SPEC_SOULHUNTER")
        {
            int n = (int)Math.Round(enemy.Level * Stats.Get("combat/HeroSpecializations/Necromancer/Soulhunter_ManesSummonedPerLevel", 1)
                                    + Stats.Get("combat/HeroSpecializations/Necromancer/Soulhunter_ManesSummonedBase", 0), MidpointRounding.ToEven);
            n = Math.Min(t.InitialCount, n);
            if (n > 0)
            {
                var probe = new Unit { Size = Db.Creature("CREATURE_MANES").CombatSize };
                if (NearestFree(probe, t.Pos) is Cell pos)
                {
                    var m = BattleSetup.AddCreature(Db, S, 1 - t.Side, "CREATURE_MANES", n, pos, summoned: true);
                    Emit(new UnitSummoned(m.Id, m.CreatureId, n, m.Side, m.Pos, "HERO_SPEC_SOULHUNTER"));
                }
            }
        }
    }

    internal void Kill(Unit t, int source) => DealDamage(t, t.TotalHealth, source, AttackKind.Other, "KILL");

    void OnDeath(Unit t, int source)
    {
        t.Count = 0;
        t.TopHealth = 0;
        Emit(new UnitDied(t.Id));
        EndSeductionOf(t);
        foreach (var h in S.Heroes)
            foreach (var key in h.Links.Where(kv => kv.Value == t.Id).Select(kv => kv.Key).ToList())
                h.Links.Remove(key);
        if (t.Kind != UnitKind.Creature) return;
        if (!t.Summoned) DeathSummons(t);
        // Enraged: friendly Enraged stacks gain Attack (p.321).
        if (!t.Summoned)
        {
            double armyPower = S.Units.Where(u => u.Side == t.Side && u.Kind == UnitKind.Creature && !u.Summoned).Sum(u => (double)u.InitialCount * u.Power);
            double killedPower = (double)t.InitialCount * t.Power;
            foreach (var f in S.LivingOf(t.Side).Where(f => HasAbilityNow(f, Ab.Enraged)))
            {
                int bonus = Math.Max(1, (int)Math.Floor(t.BaseAttack * killedPower / Math.Max(1, armyPower)));
                var e = f.GetEffect(H5.Combat.Fx.Enraged);
                AddEffect(f, new Effect { Id = H5.Combat.Fx.Enraged, Value = (e?.Value ?? 0) + bonus, Positive = true, Dispellable = false });
                Emit(new AbilityTriggered(f.Id, Ab.Enraged));
            }
            // Enraged stacks gain FriendlyStackKilledRPEnraged instead (exe FUN_008a3b90).
            foreach (var f in S.LivingOf(t.Side)) RageEvent(f, f.HasEffect(H5.Combat.Fx.Enraged) ? "FriendlyStackKilledRPEnraged" : "FriendlyStackKilledRP");
            SpecOnEnemyStackKilled(1 - t.Side);
            foreach (var f in S.LivingOf(1 - t.Side)) RageEvent(f, "EnemyStackKilledRP");
        }
        // Guardian Angel: when all the knight's troops die, resurrect the strongest stack (p.120).
        var h2 = S.Heroes[t.Side];
        if (h2.HasPerk(Sk.GuardianAngel) && !h2.AbilityUses.ContainsKey("GUARDIAN_ANGEL") && !SideHasArmy(t.Side))
        {
            var best = S.Units.Where(u => u.Side == t.Side && u.Kind == UnitKind.Creature && !u.Summoned && !u.Has(Ab.Elemental) && !u.IsClone)
                .OrderByDescending(u => (double)u.InitialCount * u.Power).FirstOrDefault();
            if (best != null)
            {
                h2.AbilityUses["GUARDIAN_ANGEL"] = 1;
                Emit(new AbilityTriggered(best.Id, "GUARDIAN_ANGEL"));
                Resurrect(best, 240 + 30 * h2.Level);
            }
        }
    }

    /// <summary>Heals a stack. With resurrect, dead creatures come back up to the stack's initial size.</summary>
    public int Heal(Unit u, int amount, bool resurrect, string by)
    {
        if (amount <= 0 || u.Removed) return 0;
        int max = u.MaxHealthCache;
        int before = u.Count;
        if (resurrect)
        {
            int cap = u.InitialCount * max;
            int total = Math.Min(cap, u.TotalHealth + amount);
            int healed = total - u.TotalHealth;
            if (healed <= 0) return 0;
            SetTotalHealth(u, total);
            Emit(new Healed(u.Id, healed, u.Count - before, u.Count));
            return healed;
        }
        if (u.Count <= 0) return 0;
        int h = Math.Min(amount, max - u.TopHealth);
        if (h <= 0) return 0;
        u.TopHealth += h;
        Emit(new Healed(u.Id, h, 0, u.Count));
        return h;
    }

    void Resurrect(Unit u, int amount)
    {
        if (u.Count <= 0)
        {
            // A dead stack needs a free spot to come back.
            if (!Fits(u, u.Pos)) { var p = NearestFree(u, u.Pos); if (p == null) return; u.Pos = p.Value; }
            u.Effects.Clear();
        }
        Heal(u, amount, resurrect: true, "RESURRECT");
    }

    void DealElementalDamage(Unit t, int amount, string element, int source, string by)
    {
        if (!t.Alive || amount <= 0 || ElementImmune(t, element)) return;
        if (element == Elements.Fire && t.HasEffect(H5.Combat.Fx.MarkOfFire)) amount = (int)(amount * Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2));
        DealDamage(t, amount, source, AttackKind.Ability, by);
    }

    // ------------------------------------------------------------------ periodic effects

    void StartOfTurnDamage(Unit u)
    {
        if (u.GetEffect(H5.Combat.Fx.Poison) is { } poison)
            DealDamage(u, (int)poison.Value, -1, AttackKind.Other, "POISON");
        if (u.Alive && u.GetEffect("SPELL_PLAGUE") is { } decay)
            DealSpellDamageRaw(u, (int)decay.Value, "SPELL_PLAGUE");
        if (u.Alive && u.GetEffect(H5.Combat.Fx.Ignite) is { } ign)
            DealElementalDamage(u, (int)ign.Value, Elements.Fire, -1, "IGNITE");
        if (u.Alive)
            foreach (var o in Adjacent(u).Where(o => Controller(o) != Controller(u) && HasAbilityNow(o, Ab.SearingAura)).ToList())
                DealElementalDamage(u, (int)Stats.Get("combat/Creatures/Inferno/SearingAuraDamageHits", 10) * o.Count, Elements.Fire, o.Id, Ab.SearingAura);
        if (u.Alive && HasAbilityNow(u, Ab.Regeneration))
            Heal(u, S.Rng.Range((int)Stats.Get("combat/Creatures/Dungeon/HydraRegenerationMin", 30), (int)Stats.Get("combat/Creatures/Dungeon/HydraRegenerationMax", 50)), false, Ab.Regeneration);
        if (u.Alive && u.GetEffect("SPELL_REGENERATION") is { } regen)
            Heal(u, (int)regen.Value, false, "SPELL_REGENERATION");
        foreach (var f in S.Fields.Where(f => f.Kind == "STORM" && f.Cells.Any(u.Occupies)).ToList())
            if (u.Alive) DealElementalDamage(u, (int)f.Damage, Elements.Air, f.SourceUnit, "CALL_STORM");
    }

    void EndOfTurnFieldDamage(Unit u)
    {
        if (!u.Alive) return;
        foreach (var f in S.Fields.Where(f => (f.Kind is "FIREWALL" or "LIQUID_FLAME") && f.Cells.Any(u.Occupies)).ToList())
        {
            if (!u.Alive) break;
            if (f.Kind == "FIREWALL") DealSpellDamage(u, Db.Spell("SPELL_FIREWALL")!, f.Damage, f.Side, ignoreResist: false);
            else DealElementalDamage(u, (int)f.Damage, Elements.Fire, f.SourceUnit, Ab.LiquidFlameBreath);
        }
    }

    FieldObject AddField(string kind, int side, List<Cell> cells, double duration, double damage, int source = -1)
    {
        var f = new FieldObject { Id = S.NextFieldId++, Kind = kind, Side = side, Cells = cells, Remaining = duration, Damage = damage, SourceUnit = source };
        S.Fields.Add(f);
        Emit(new FieldChanged(f.Id, kind, cells, false));
        return f;
    }

    // ------------------------------------------------------------------ automatic actors

    /// <summary>War machine turn. Returns true if the player controls it (has the matching perk).</summary>
    bool WarMachineTurn(Unit u)
    {
        var h = HeroOf(u);
        switch (u.CreatureId)
        {
            case "WAR_MACHINE_BALLISTA":
                if (h.HasPerk(Sk.Ballista) && !S.AiControlled[u.Side] && h.Present) return true;
                {
                    // Automatic ballista: one action, all its shots at one target (FUN_00a65260 loop).
                    var target = S.Living.Where(o => o.Side != u.Side && o.Kind == UnitKind.Creature && !o.HasEffect(H5.Combat.Fx.Invisible))
                        .OrderByDescending(o => (double)o.Count * o.Power).ThenBy(o => o.Id).FirstOrDefault();
                    if (target != null) PerformRangedAttack(u, target);
                    if (CheckEnd()) return false;
                }
                FinishTurn(u, 0, allowMorale: false);
                return false;
            case "WAR_MACHINE_CATAPULT":
                return CatapultTurn(u);
            case "WAR_MACHINE_FIRST_AID_TENT":
                if (h.HasPerk(Sk.FirstAid) && !S.AiControlled[u.Side] && h.Present && u.Shots > 0) return true;
                var heal = BestTentTarget(u);
                if (heal != null && u.Shots > 0) TentHeal(u, heal);
                FinishTurn(u, 0, allowMorale: false);
                return false;
            default:
                FinishTurn(u, 0, allowMorale: false);
                return false;
        }
    }

    Unit? BestTentTarget(Unit tent) =>
        S.LivingOf(tent.Side).Where(o => o.Kind == UnitKind.Creature && IsLiving(o) && (o.TopHealth < o.MaxHealthCache || HeroOf(tent).HasPerk(Sk.FirstAid) && o.Count < o.InitialCount))
            .OrderByDescending(o => o.MaxHealthCache - o.TopHealth).ThenBy(o => o.Id).FirstOrDefault();

    /// <summary>First Aid Tent heal (exe FUN_00977190): 10/20/50/100 by War Machines mastery, +5 per level for
    /// HERO_SPEC_EMPIRIC, then doubled by Ring of Machine Affinity.</summary>
    int TentHealAmount(Unit tent)
    {
        var h = HeroOf(tent);
        int amount = h.SkillLevel(Sk.WarMachines) switch
        {
            Mastery.Basic => 20, Mastery.Advanced => 50, >= Mastery.Expert => 100, _ => 10,
        };
        if (h.Present && h.Specialization == "HERO_SPEC_EMPIRIC") amount += h.Level * 5;
        if (HasMachineAffinityRing(h)) amount *= 2;
        return amount;
    }

    void TentHeal(Unit tent, Unit target)
    {
        tent.Shots = Math.Max(0, tent.Shots - 1);
        Emit(new AbilityTriggered(tent.Id, "FIRST_AID", target.Id));
        Heal(target, TentHealAmount(tent), resurrect: HeroOf(tent).HasPerk(Sk.FirstAid), "FIRST_AID");
        var m = HeroOf(tent).SkillLevel(Sk.WarMachines);
        if (m >= Mastery.Advanced)
            foreach (var e in target.Effects.Where(e => !e.Positive && e.Dispellable && (e.SpellLevel <= (m >= Mastery.Expert ? 3 : 1))).Select(e => e.Id).ToList())
                RemoveEffect(target, e);
    }

    void ObjectTurn(Unit u)
    {
        if (IsSiegeTower(u)) { TowerTurn(u); return; }
        if (u.CreatureId == "SUMMON_HIVE")
        {
            var caster = S.Heroes[u.Side];
            var spell = Db.Spell("SPELL_WASP_SWARM");
            var target = S.Living.Where(o => o.Side != u.Side && o.Kind == UnitKind.Creature && IsLiving(o) && !o.HasEffect(H5.Combat.Fx.Invisible))
                .OrderBy(o => Distance(o, u)).ThenBy(o => o.Id).FirstOrDefault();
            if (spell != null && target != null && caster.Mana >= spell.ManaCost)
            {
                caster.Mana -= spell.ManaCost;
                Emit(new ManaChanged(Actor.Hero(u.Side), caster.Mana));
                ResolveSpell(u.Id, u.Side, spell, Mastery.Expert, u.Power, target, target.Pos, fromHero: false);
            }
        }
        FinishTurn(u, 0, allowMorale: false);
    }

    /// <summary>Frenzy / Berserker Rage: attack the nearest unit (any side when frenzied) without retaliation for berserker.</summary>
    void AutoBerserkAttack(Unit u, bool anySide)
    {
        var candidates = S.Living.Where(o => o.Id != u.Id && !o.IsObject && (anySide || Controller(o) != Controller(u)) && !o.IsWarMachine).ToList();
        if (candidates.Count == 0) return;
        var options = candidates.Select(t => (t, pos: AttackPositions(u, t))).Where(x => x.pos.Count > 0).ToList();
        if (options.Count == 0 && !anySide)
            options = S.Living.Where(o => o.Id != u.Id && o.Side == u.Side && !o.IsObject && !o.IsWarMachine)
                .Select(t => (t, pos: AttackPositions(u, t))).Where(x => x.pos.Count > 0).ToList();
        if (options.Count == 0)
        {
            var nearest = candidates.OrderBy(t => Distance(u, t)).ThenBy(t => t.Id).First();
            var reach = Reachable(u);
            if (reach.Count > 0) MoveUnit(u, reach.Keys.OrderBy(c => Distance(u, c, nearest, nearest.Pos)).ThenBy(c => c.X).ThenBy(c => c.Y).First());
            return;
        }
        var (target, positions) = options.OrderBy(x => x.pos.Min(p => Octile(u.Pos, p))).ThenBy(x => x.t.Id).First();
        var from = positions.OrderBy(p => Octile(u.Pos, p)).First();
        PerformMeleeAttack(u, target, from);
    }

    // ------------------------------------------------------------------ combat start

    void CombatStartEffects()
    {
        foreach (var u in S.Units.Where(u => u.Alive).ToList())
        {
            var mine = S.Heroes[u.Side]; var enemy = S.Heroes[1 - u.Side];
            // Mana Stealer / Mana Destroyer: 0.25 mana per creature (p.178).
            if (u.Has(Ab.ManaStealer) && enemy.Present)
            {
                int n = Math.Min(enemy.Mana, (int)Math.Round(u.Count * 0.25));
                enemy.Mana -= n; Emit(new ManaChanged(Actor.Hero(enemy.Side), enemy.Mana));
                if (mine.Present) { mine.Mana += n; Emit(new ManaChanged(Actor.Hero(mine.Side), mine.Mana)); }
                Emit(new AbilityTriggered(u.Id, Ab.ManaStealer));
            }
            if (u.Has(Ab.ManaDestroyer) && enemy.Present)
            {
                int n = Math.Min(enemy.Mana, (int)Math.Round(u.Count * 0.25));
                enemy.Mana -= n; Emit(new ManaChanged(Actor.Hero(enemy.Side), enemy.Mana));
                Emit(new AbilityTriggered(u.Id, Ab.ManaDestroyer));
            }
            if (u.Has(Ab.DemonRaged))
            {
                if (mine.HasPerk(Sk.MemoryOfBlood)) AddRage(u, 10 * S.Rng.Range(5, 10));
                if (mine.HasPerk(Sk.AbsoluteRage)) AddRage(u, (int)Stats.Get("combat/Creatures/Stronghold/DemonRage/AbsoluteRageRP", 500));
            }
        }
    }

    // ------------------------------------------------------------------ Blood Rage (p.139, DefaultStats DemonRage)

    void RageEvent(Unit u, string key)
    {
        if (!u.Alive || !u.Has(Ab.DemonRaged)) return;
        if (key == "BattleElation") { if (HeroOf(u).HasPerk(Sk.BattleElation)) AddRage(u, (int)Stats.Get("combat/Creatures/Stronghold/DemonRage/BattleElation_RPOnGoodMorale", 50)); return; }
        double rp = Stats.Get("combat/Creatures/Stronghold/DemonRage/" + key, 0);
        if (rp != 0) AddRage(u, (int)rp);
    }

    internal void AddRage(Unit u, int rp)
    {
        var h = HeroOf(u);
        // Rage gain (exe FUN_008a2c20): ×RPCoefUnderBerserk while berserk, ×RPCoefPowerOfBlood (both rounded), then
        // + DemonHunter_RPBonusPerHeroLevel for the Demon Hunter specialisation when demons are in the battle.
        if (u.HasEffect("SPELL_BERSERK")) rp = (int)Math.Round(rp * Stats.Get("combat/Creatures/Stronghold/DemonRage/RPCoefUnderBerserk", 1), MidpointRounding.ToEven);
        if (h.HasPerk(Sk.Bloodfire)) rp = (int)Math.Round(rp * Stats.Get("combat/Creatures/Stronghold/DemonRage/RPCoefPowerOfBlood", 1.5), MidpointRounding.ToEven);
        if (h.Present && h.Specialization == "HERO_SPEC_DEMON_HUNTER" && S.Living.Any(d => d.Kind == UnitKind.Creature && d.Town == Towns.Inferno))
        {
            const string k = "combat/Creatures/Stronghold/DemonRage/DemonHunter_RPBonusPerHeroLevel/";
            rp += (h.Level - 1) / Math.Max(1, (int)Stats.Get(k + "LevelDivisor", 1)) * (int)Stats.Get(k + "PerDividedLevel", 2) + (int)Stats.Get(k + "Base", 10);
        }
        u.RagePoints = Math.Max(0, u.RagePoints + rp);
        int maxLevel = (int)h.SkillLevel(Sk.BloodRage);
        if (maxLevel >= 4) maxLevel = 3;
        double coef = h.SkillLevel(Sk.BloodRage) == Mastery.Ultimate ? 1 - Stats.Get("combat/Creatures/Stronghold/DemonRage/UltimateBloodRage_RageForLevelDecrease", 0.25) : 1;
        int level = 0;
        for (int l = 1; l <= 3; l++)
            if (u.RagePoints >= Stats.Get($"combat/Creatures/Stronghold/DemonRage/RPForLevel{l}", l * 300) * coef) level = l;
        level = Math.Min(level, maxLevel);
        if (level != u.RageLevel)
        {
            u.RageLevel = level;
            Emit(new AbilityTriggered(u.Id, "BLOOD_RAGE_" + level));
            RefreshHealth(u);
        }
    }
}
