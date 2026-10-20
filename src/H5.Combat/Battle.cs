using H5.Core.Data;

namespace H5.Combat;

/// <summary>
/// The authoritative combat rules engine. Wraps a <see cref="BattleState"/> and mutates it in response to
/// commands; every mutation is reported as <see cref="BattleEvent"/>s. Deterministic given the state
/// (including its RNG) and the command sequence. Not thread-safe; one instance per battle.
/// </summary>
public sealed partial class Battle
{
    public BattleState S { get; }
    public BattleState State => S;
    public GameDatabase Db { get; }
    RpgStats Stats => Db.Stats;
    List<BattleEvent> _ev = new();
    const double Eps = 1e-9;

    public Battle(GameDatabase db, BattleState state)
    {
        Db = db;
        S = state;
        foreach (var u in S.Units) if (u.MaxHealthCache == 0) u.MaxHealthCache = u.BaseHealth;
    }

    void Emit(BattleEvent e) => _ev.Add(e);

    /// <summary>
    /// Begins the battle: if both sides already finished tactics (or tactics was skipped) combat starts
    /// immediately. Returns the opening events.
    /// </summary>
    public List<BattleEvent> Begin()
    {
        _ev = new List<BattleEvent>();
        if (S.Phase == BattlePhase.Tactics && S.TacticsDone[0] && S.TacticsDone[1]) StartCombat();
        else if (S.Phase == BattlePhase.Tactics) Emit(new PhaseChanged(BattlePhase.Tactics));
        return _ev;
    }

    public ApplyResult Apply(BattleCommand cmd)
    {
        if (S.Phase == BattlePhase.Finished) return ApplyResult.Reject("battle is over");
        _ev = new List<BattleEvent>();
        string? err = cmd switch
        {
            DeployCmd c => DoDeploy(c),
            EndTacticsCmd c => DoEndTactics(c),
            ReserveCmd c => DoReserve(c),
            SplitStackCmd c => DoSplitStack(c),
            AutoplaceCmd c => DoAutoplace(c),
            SurrenderCmd c => DoSurrender(c),
            _ when S.Phase != BattlePhase.Combat => "not in combat phase",
            MoveCmd c => DoMove(c),
            AttackCmd c => DoAttack(c),
            ShootCmd c => DoShoot(c),
            WaitCmd c => DoWait(c),
            DefendCmd c => DoDefend(c),
            CastSpellCmd c => DoCast(c),
            UseAbilityCmd c => DoAbility(c),
            HeroAttackCmd c => DoHeroAttack(c),
            _ => "unknown command",
        };
        if (err != null) return ApplyResult.Reject(err);
        return new ApplyResult { Ok = true, Events = _ev };
    }

    /// <summary>Which side currently controls a unit (Puppet Master / Seduction can change it).</summary>
    public int Controller(Unit u)
    {
        if (u.GetEffect("SPELL_HYPNOTIZE") is { } h) return h.SourceSide;
        if (u.GetEffect(H5.Combat.Fx.Seduced) is { } sd) return sd.SourceSide;
        return u.Side;
    }

    /// <summary>The side expected to issue the next command (null when waiting on tactics from both or finished).</summary>
    public int? SideToAct
    {
        get
        {
            if (S.Phase != BattlePhase.Combat || S.Active is not int a) return null;
            return Actor.IsHero(a) ? Actor.HeroSide(a) : Controller(S.Unit(a)!);
        }
    }

    // ------------------------------------------------------------------ tactics

    public (int MinX, int MaxX) DeployZone(int side) =>
        side == 0 ? (0, S.DeployColumns0 - 1) : (S.Width - S.DeployColumns1, S.Width - 1);

    string? DoDeploy(DeployCmd c)
    {
        if (S.Phase != BattlePhase.Tactics) return "not in tactics phase";
        if (S.TacticsDone[c.Side]) return "tactics already finished";
        var u = S.Unit(c.UnitId);
        if (u == null || u.Side != c.Side || !(u.Alive || u.Reserve) || u.IsWarMachine) return "invalid unit";
        var (minX, maxX) = DeployZone(c.Side);
        foreach (var cell in u.CellsAt(c.Cell))
        {
            if (!S.InBounds(cell) || cell.X < minX || cell.X > maxX) return "outside deployment zone";
            if (S.Obstacles.Contains(cell)) return "blocked by obstacle";
        }
        // Friendly stacks under the new footprint go back to the army bar (exe FUN_005779d0 → FUN_00576cd0).
        var others = S.Units.Where(o => o.Alive && o.Id != u.Id && !o.IsWarMachine && u.CellsAt(c.Cell).Any(o.Occupies)).ToList();
        if (others.Any(o => o.Side != u.Side || o.IsObject)) return "cell occupied";
        u.Reserve = false;
        u.Pos = u.StartPos = c.Cell;
        Emit(new Deployed(u.Id, u.Pos));
        foreach (var o in others) { o.Reserve = true; Emit(new Reserved(o.Id)); }
        return null;
    }

    string? DoEndTactics(EndTacticsCmd c)
    {
        if (S.Phase != BattlePhase.Tactics) return "not in tactics phase";
        if (TacticsStacks(c.Side).Any() && !S.LivingOf(c.Side).Any(u => !u.IsWarMachine && !u.IsObject))
            return "no creatures placed";
        S.TacticsDone[c.Side] = true;
        if (S.TacticsDone[0] && S.TacticsDone[1]) StartCombat();
        return null;
    }

    void StartCombat()
    {
        S.Phase = BattlePhase.Combat;
        // Stacks left in the army bar sit the battle out (exe FUN_00541fa0 sends only the placed entries).
        foreach (var u in S.Units) if (u.Reserve) u.Removed = true;
        Emit(new PhaseChanged(BattlePhase.Combat));
        foreach (var u in S.Units)
        {
            u.StartPos = u.Pos;
            u.Atb = S.Rng.NextDouble() * 0.25;   // fan manual p.310
        }
        foreach (var h in S.Heroes)
        {
            if (!h.Present) continue;
            h.Atb = S.Rng.NextDouble() * 0.25;
            if (h.HasPerk(Sk.SwiftMind)) h.Atb += Stats.Get("combat/HeroSkills/Runemage/QuicknessOfMind_InitiativeBonus", 0.25);
        }
        CombatStartEffects();
        SpecCombatStart();
        RemoteControlAtStart();
        AdvanceUntilDecision();
    }

    string? DoSurrender(SurrenderCmd c)
    {
        // H5_Game.exe CCombatArmy::CanRetreat (FUN_004d9e20) gates both flee and surrender.
        if (RetreatRefusal(c.Side, c.PayRansom) is var refusal && refusal != 0) return $"cannot retreat (reason {refusal})";
        S.Heroes[c.Side].Fled = true;
        EndBattle(1 - c.Side, c.PayRansom ? "surrender" : "fled");
        return null;
    }

    // ------------------------------------------------------------------ ATB / turn flow

    IEnumerable<int> Actors()
    {
        foreach (var u in S.Units)
            if (u.Alive && (!u.IsObject || u.BaseInitiative > 0)) yield return u.Id;
        for (int side = 0; side < 2; side++)
            if (S.Heroes[side].Present && !S.Heroes[side].Fled) yield return Actor.Hero(side);
    }

    double Rate(int actor) => (Actor.IsHero(actor) ? HeroInitiative(Actor.HeroSide(actor)) : InitiativeOf(S.Unit(actor)!)) / 10.0;
    double GetAtb(int actor) => Actor.IsHero(actor) ? S.Heroes[Actor.HeroSide(actor)].Atb : S.Unit(actor)!.Atb;

    void SetAtb(int actor, double v, bool emit = true)
    {
        if (Actor.IsHero(actor)) S.Heroes[Actor.HeroSide(actor)].Atb = v; else S.Unit(actor)!.Atb = v;
        if (emit) Emit(new AtbChanged(actor, v));
    }

    void AddAtb(int actor, double delta, double cap = 1)
    {
        double v = Math.Min(cap, GetAtb(actor) + delta);
        SetAtb(actor, v);
    }

    /// <summary>Advance battle time to the next actor, resolving automatic turns, until a player decision is needed.</summary>
    void AdvanceUntilDecision()
    {
        for (int guard = 0; guard < 10000; guard++)
        {
            if (CheckEnd()) return;
            int? next = AdvanceTimeToNextActor();
            if (next == null) { EndBattle(null, "no actors"); return; }
            int actor = next.Value;
            S.Active = actor;
            S.TurnCounter++;
            Emit(new TurnStarted(actor, S.Time));
            if (BeginTurn(actor)) return;      // needs a command
            if (S.Phase == BattlePhase.Finished) return;
        }
        EndBattle(null, "turn limit");
    }

    int? AdvanceTimeToNextActor()
    {
        for (int guard = 0; guard < 1000; guard++)
        {
            int? best = null; double bestT = double.PositiveInfinity, bestRate = 0;
            foreach (var a in Actors())
            {
                double r = Rate(a);
                if (r <= 0) continue;
                double t = Math.Max(0, (1 - GetAtb(a)) / r);
                bool better = t < bestT - Eps
                              || (Math.Abs(t - bestT) <= Eps && (r > bestRate + Eps
                                  || (Math.Abs(r - bestRate) <= Eps && best != null && TieBreak(a, best.Value))));
                if (best == null || better) { best = a; bestT = t; bestRate = r; }
            }
            if (best == null) return null;
            double expiry = NextExpiry();
            if (expiry < bestT - Eps)
            {
                AdvanceTime(expiry);
                continue;     // initiative may have changed; re-evaluate
            }
            AdvanceTime(bestT);
            SetAtb(best.Value, Math.Max(1, GetAtb(best.Value)), emit: false);
            return best;
        }
        return null;
    }

    // Lower side first (attacker), then lower id; heroes before their units on exact ties.
    static bool TieBreak(int a, int b)
    {
        int sa = Actor.IsHero(a) ? Actor.HeroSide(a) : int.MaxValue, sb = Actor.IsHero(b) ? Actor.HeroSide(b) : int.MaxValue;
        if (Actor.IsHero(a) != Actor.IsHero(b)) return Actor.IsHero(a);
        if (Actor.IsHero(a)) return sa < sb;
        return a < b;
    }

    double NextExpiry()
    {
        double m = double.PositiveInfinity;
        foreach (var u in S.Units)
            if (u.Alive)
                foreach (var e in u.Effects)
                    if (e.Remaining < m) m = e.Remaining;
        foreach (var f in S.Fields) if (f.Remaining < m) m = f.Remaining;
        return Math.Max(0, m);
    }

    void AdvanceTime(double dt)
    {
        if (dt <= 0) { ExpireEffects(); return; }
        foreach (var a in Actors().ToList())
        {
            double r = Rate(a);
            if (Actor.IsHero(a)) S.Heroes[Actor.HeroSide(a)].Atb += r * dt;
            else S.Unit(a)!.Atb += r * dt;
        }
        S.Time += dt;
        foreach (var u in S.Units)
            foreach (var e in u.Effects)
                if (!double.IsPositiveInfinity(e.Remaining)) e.Remaining -= dt;
        foreach (var f in S.Fields)
            if (!double.IsPositiveInfinity(f.Remaining)) f.Remaining -= dt;
        ExpireEffects();
    }

    void ExpireEffects()
    {
        foreach (var u in S.Units)
        {
            for (int i = u.Effects.Count - 1; i >= 0; i--)
                if (u.Effects[i].Remaining <= Eps || u.Effects[i].Actions == 0)
                    RemoveEffectAt(u, i);
        }
        for (int i = S.Fields.Count - 1; i >= 0; i--)
            if (S.Fields[i].Remaining <= Eps)
            {
                var f = S.Fields[i];
                S.Fields.RemoveAt(i);
                Emit(new FieldChanged(f.Id, f.Kind, f.Cells, true));
                if (f.Kind is "PACK_DIVE" or "BATTLE_DIVE") ResolveDive(f);
            }
    }

    void RemoveEffectAt(Unit u, int index)
    {
        var e = u.Effects[index];
        u.Effects.RemoveAt(index);
        Emit(new EffectRemoved(u.Id, e.Id));
        if (e.Id is H5.Combat.Fx.BearRoar or H5.Combat.Fx.ResurrectionPenalty) RefreshHealth(u);
    }

    public void RemoveEffect(Unit u, string id)
    {
        int i = u.Effects.FindIndex(e => e.Id == id);
        if (i >= 0) RemoveEffectAt(u, i);
    }

    /// <summary>Adds or refreshes an effect. Re-casting refreshes duration/value instead of stacking (p.304).</summary>
    public Effect AddEffect(Unit u, Effect e)
    {
        int i = u.Effects.FindIndex(x => x.Id == e.Id);
        if (e.Duration <= 0) e.Duration = e.Remaining;
        if (i >= 0) u.Effects[i] = e; else u.Effects.Add(e);
        Emit(new EffectAdded(u.Id, e.Id, double.IsPositiveInfinity(e.Remaining) ? -1 : e.Remaining, e.Value));
        BondOfLightCopy(u, e);
        if (e.Id is H5.Combat.Fx.BearRoar or H5.Combat.Fx.ResurrectionPenalty) RefreshHealth(u);
        return e;
    }

    /// <summary>Morale roll (exe 00a6b8f0 bad / 00a6ba00 good): |morale| clamped to 5; succeeds iff the battle RNG's
    /// inclusive Range(1,100) is below 10·|morale|, i.e. (10·|m| − 1) % (9 % at ±1, 49 % at ±5).</summary>
    internal bool MoraleRoll(int morale) => S.Rng.Range(1, 100) < 10 * Math.Min(5, Math.Abs(morale));

    /// <summary>Start-of-turn processing. Returns true if the actor needs a command.</summary>
    bool BeginTurn(int actor)
    {
        if (Actor.IsHero(actor)) return true;
        var u = S.Unit(actor)!;

        u.RetaliationsLeft = HasAbilityNow(u, Ab.UnlimitedRetaliation) ? int.MaxValue : 1;
        u.Defending = false;
        RemoveEffect(u, PreparationEffect);   // exe 008a65e0: duration 1, i.e. until the stack's next turn
        Sweep7TurnStart(u);   // exe FUN_00bbc8e0: Battle Frenzy removed, Defile Magic released
        u.AgilityCells = 0;   // exe FUN_008a6c40: SPELL_ABILITY_AGILITY is removed when the creature's turn begins
        RemoveEffect(u, H5.Combat.Fx.TakeRoots);
        RemoveEffect(u, NecromancersDebuff);   // exe 008a6c40: lasts until the stack's next turn start
        // Mountain King: the hero's creatures get MountainKing_ActiveRune (Rune of Thunderclap) refreshed at the
        // start of each of their turns (exe FUN_008a6c40).
        if (u.Kind == UnitKind.Creature && HeroOf(u) is { Present: true, Specialization: "HERO_SPEC_MOUNTAIN_KING" })
            Rune(Stats.Str("combat/HeroSpecializations/Runemage/MountainKing_ActiveRune", "SPELL_RUNE_OF_STUNNING"), u);

        StartOfTurnDamage(u);
        if (!u.Alive) { CheckEnd(); return false; }

        if (u.HasEffect("SPELL_BLIND") || u.HasEffect(H5.Combat.Fx.Sleep) || u.HasEffect(H5.Combat.Fx.Stunned) || u.HasEffect(H5.Combat.Fx.Sabotaged) || u.HasEffect(FxDiving))
        {
            Emit(new TurnSkipped(u.Id, u.HasEffect("SPELL_BLIND") ? "blind" : u.HasEffect(H5.Combat.Fx.Sleep) ? "asleep" : u.HasEffect(FxDiving) ? "diving" : "stunned"));
            RemoveEffect(u, H5.Combat.Fx.Stunned);
            RemoveEffect(u, H5.Combat.Fx.Sabotaged);
            FinishTurn(u, 0, allowMorale: false);
            return false;
        }

        // Bad morale: the turn is lost and ATB reset to 0.5 (p.311).
        int morale = MoraleOf(u);
        if (morale < 0 && MoraleRoll(morale))
        {
            Emit(new MoraleProc(u.Id, false));
            RageEvent(u, "BadMoraleRP");
            // exe 008a8140: the opposing hero's Necromancers set (4+ pieces) cripples a stack that fails its morale.
            if (Specials.Pieces(Enemy(u.Side), Specials.Necromancers) > 3)
                AddEffect(u, new Effect { Id = NecromancersDebuff, SourceSide = 1 - u.Side, Dispellable = false });
            FinishTurn(u, Stats.Get("combat/LuckAndMorale/BadMorale_ATBValue", 0.5), allowMorale: false);
            return false;
        }

        if (u.IsWarMachine) return WarMachineTurn(u);
        if (u.IsObject) { ObjectTurn(u); return false; }

        if (u.GetEffect("SPELL_BERSERK") is { } frenzy)
        {
            AutoBerserkAttack(u, anySide: true);
            frenzy.Actions--;
            if (u.Alive) FinishTurn(u, 0);
            else AdvanceUntilDecisionTail();
            return false;
        }
        if (u.HasEffect(H5.Combat.Fx.BerserkerRage))
        {
            AutoBerserkAttack(u, anySide: false);
            if (u.Alive) FinishTurn(u, 0); else AdvanceUntilDecisionTail();
            return false;
        }
        if (u.HasEffect(H5.Combat.Fx.Feared))
        {
            RemoveEffect(u, H5.Combat.Fx.Feared);
        }
        return true;
    }

    void AdvanceUntilDecisionTail() { S.Active = null; }

    /// <summary>Ends the current unit's turn: action-limited effects tick, ATB resets, good morale may trigger.</summary>
    void FinishTurn(Unit u, double atbReset, bool allowMorale = true, bool continueFlow = false)
    {
        u.HasActed = true;
        for (int i = u.Effects.Count - 1; i >= 0; i--)
        {
            var e = u.Effects[i];
            if (e.Actions > 0) e.Actions--;
            if (e.Actions == 0) RemoveEffectAt(u, i);
        }
        EndOfTurnFieldDamage(u);
        double reset = atbReset;
        if (u.Alive && allowMorale && reset < 0.5)
        {
            int morale = MoraleOf(u);
            if (morale > 0 && MoraleRoll(morale))
            {
                reset = Stats.Get("combat/LuckAndMorale/GoodMorale_ATBValue", 0.5);
                Emit(new MoraleProc(u.Id, true));
                RageEvent(u, "BattleElation");
                var h = HeroOf(u);
                if (h.HasPerk(Sk.Empathy)) AddAtb(Actor.Hero(u.Side), Stats.Get("combat/HeroSkills/Runemage/Empathy_ATBAffectionOnMorale", 0.1));
            }
        }
        if (u.Alive) SetAtb(u.Id, reset);
        S.Active = null;
    }

    /// <summary>Called by every command handler once the actor's action is done.</summary>
    void EndAction(int actor, double atbReset, bool allowMorale = true)
    {
        if (Actor.IsHero(actor))
        {
            SetAtb(actor, atbReset);
            S.Active = null;
        }
        else
        {
            var u = S.Unit(actor)!;
            if (u.Alive) FinishTurn(u, atbReset, allowMorale);
            else S.Active = null;
        }
        if (S.Phase == BattlePhase.Combat) AdvanceUntilDecision();
    }

    bool CheckEnd()
    {
        if (S.Phase == BattlePhase.Finished) return true;
        bool alive0 = SideHasArmy(0), alive1 = SideHasArmy(1);
        if (alive0 && alive1) return false;
        int? winner = alive0 ? 0 : alive1 ? 1 : 1;   // mutual destruction: defender holds the field
        EndBattle(winner, "army destroyed");
        return true;
    }

    // Units under enemy mind control still count for their owner (control ends with the effect).
    bool SideHasArmy(int side) => S.Units.Any(u => u.Alive && u.Kind == UnitKind.Creature && u.Side == side && !u.IsClone);

    void EndBattle(int? winner, string reason)
    {
        if (S.Phase == BattlePhase.Finished) return;
        S.Phase = BattlePhase.Finished;
        S.Winner = winner;
        S.EndReason = reason;
        S.Active = null;
        Emit(new BattleEnded(winner, reason));
    }

    // ------------------------------------------------------------------ validation helpers

    string? CheckActive(BattleCommand c, int unitId, out Unit u)
    {
        u = null!;
        if (S.Active != unitId) return "not this unit's turn";
        var unit = S.Unit(unitId);
        if (unit == null || !unit.Alive) return "invalid unit";
        if (Controller(unit) != c.Side) return "not your unit";
        u = unit;
        return null;
    }

    string? CheckHeroActive(BattleCommand c, out HeroState h)
    {
        h = S.Heroes[Math.Clamp(c.Side, 0, 1)];
        if (S.Active != Actor.Hero(c.Side)) return "not your hero's turn";
        if (!h.Present || h.Fled) return "no hero";
        return null;
    }

    // ------------------------------------------------------------------ simple actions

    string? DoWait(WaitCmd c)
    {
        if (Actor.IsHero(c.ActorId))
        {
            if (CheckHeroActive(c, out _) is { } e) return e;
            Emit(new Waited(c.ActorId));
            EndAction(c.ActorId, 0.5, false);
            return null;
        }
        if (CheckActive(c, c.ActorId, out var u) is { } err) return err;
        Emit(new Waited(u.Id));
        RageEvent(u, "WaitRP");
        // Take Roots also triggers on Wait (p.181).
        if (u.Has(Ab.TakeRoots)) AddEffect(u, new Effect { Id = H5.Combat.Fx.TakeRoots, Positive = true, Dispellable = false });
        EndAction(u.Id, 0.5, allowMorale: false);
        return null;
    }

    string? DoDefend(DefendCmd c)
    {
        if (Actor.IsHero(c.ActorId))
        {
            if (CheckHeroActive(c, out _) is { } e) return e;
            Emit(new Defended(c.ActorId));
            EndAction(c.ActorId, 0, false);
            return null;
        }
        if (CheckActive(c, c.ActorId, out var u) is { } err) return err;
        u.Defending = true;
        if (HeroOf(u).HasPerk(Sk.Preparation)) AddEffect(u, new Effect { Id = PreparationEffect, Positive = true, Dispellable = false });   // exe 008a65e0
        Emit(new Defended(u.Id));
        RageEvent(u, "DefendRP");
        if (u.Has(Ab.TakeRoots)) AddEffect(u, new Effect { Id = H5.Combat.Fx.TakeRoots, Positive = true, Dispellable = false });
        EndAction(u.Id, 0);
        return null;
    }
}
