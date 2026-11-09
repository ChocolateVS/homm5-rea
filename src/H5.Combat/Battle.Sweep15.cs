namespace H5.Combat;

// Combat audit sweep 15 (docs/rules/audit/combat_sweep_15.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>ATBBaseTime (RPGStats +0x50, read by FUN_00a2d6c0): 100000 when below 1.</summary>
    internal int AtbBaseTime()
    {
        int v = (int)Stats.Get("ATBBaseTime", 100000);
        return v < 1 ? 100000 : v;
    }

    /// <summary>
    /// One full turn in the exe's integer game time (unit vf 0x174 = FUN_00a6a060 → FUN_00a2d6c0): ATBBaseTime divided
    /// by the current initiative (vf 0x1bc as a float; ≤ 0 → 1.0), on the x87 stack; a result of 0 gives 1.0. The engine's
    /// time unit is one turn at initiative 10, i.e. ATBBaseTime / 10 exe units.
    /// </summary>
    internal double FullTurnTime(Unit u)
    {
        float init = (float)InitiativeOf(u);
        if (init <= 0f) init = 1f;
        double t = AtbBaseTime() / (double)init;
        return t == 0 ? 1 : t;
    }

    /// <summary>
    /// The time to the unit's next turn in exe game time (vf 0x180 = FUN_00a6a210 → vf 0x17c = FUN_00a6e440 with the ATB
    /// +0x1c): 0 once the ATB is ≥ 1.0; without timed initiative changes chop((1.0f − ATB) · ATBBaseTime / initiative)
    /// (FISTP under OR AH,0x0C at 00a6e675..00a6e691); with them FUN_00a6e440 walks the unit's effects in expiry order,
    /// each segment filling the gauge at the initiative in force (frozen segments — ids 0x13, 0xc9, 0x14a, 0x150 — at 0).
    /// The engine's own initiative (<see cref="InitiativeOf"/>) is evaluated per segment by taking the expired effects
    /// off for the computation.
    /// </summary>
    internal uint TimeToNextTurn(Unit u)
    {
        float atb = (float)u.Atb;
        if (atb >= 1f) return 0;
        var saved = u.Effects.ToList();
        double scale = AtbBaseTime() / 10.0, t = 0, a = atb;   // t in exe units; engine expiries × ATBBaseTime / 10
        try
        {
            foreach (var e in saved.Where(e => !double.IsPositiveInfinity(e.Remaining) && e.Remaining > 0).OrderBy(e => e.Remaining))
            {
                // Sweep 16: a disabled stack (FUN_004e4920 ≠ 0) fills its gauge at 0 while the effect lasts.
                double full = AtbFrozen(u) ? double.PositiveInfinity : FullTurnTime(u), seg = e.Remaining * scale - t;
                if (!double.IsPositiveInfinity(full) && a + seg / full >= 1) break;
                if (!double.IsPositiveInfinity(full)) a += seg / full;
                t = e.Remaining * scale;
                u.Effects.Remove(e);
            }
            if (AtbFrozen(u)) return uint.MaxValue;
            t += (1.0 - a) * FullTurnTime(u);
        }
        finally
        {
            u.Effects.Clear();
            u.Effects.AddRange(saved);
        }
        double exe = Math.Floor(t);
        return exe >= uint.MaxValue ? uint.MaxValue : (uint)exe;
    }

    /// <summary>
    /// Battle Dive's take-off timing (FUN_00984a00, 0x9a branch, disassembly 00985474..00985527): with a target — the one
    /// given or the unit at the cell (FUN_00b3d6d0) — T = its vf 0x180 (time to its next turn) and C = chop(the griffins'
    /// vf 0x174, one full turn); C &lt; T (unsigned, JC at 009854b9) or the target a shooter (vf 0x1e0) casts the finish
    /// 0x9b at once. Otherwise the griffins stay up until their own next turn start.
    /// </summary>
    internal bool BattleDiveFinishesAtOnce(Unit griffins, Unit? target)
    {
        if (target == null) return false;
        uint c = (uint)Math.Truncate(FullTurnTime(griffins));
        return c < TimeToNextTurn(target) || target.MaxShots > 0;   // vf 0x1e0 taken as the shooter test
    }

    /// <summary>
    /// The finish, case 0x9b of FUN_00984a00 (009877df..00987963): the landing (<see cref="BattleDiveLanding"/>); none →
    /// vf 0x200, vf 0x184 → the ATB setter FUN_00a2d6a0(0.5) and nothing else, the griffins staying up (false). Else 0x9a is
    /// removed (vf 0x270), vf 0xe8(0) puts the griffins down and FUN_00a65d50(…, 0x9b, target cell) strikes the unit at
    /// the target cell (FUN_00a65260 → FUN_00a5fde0) ×2, friend or foe (true).
    /// </summary>
    internal bool BattleDiveFinish(Unit u, FieldObject f)
    {
        var target = f.Cells[0];
        var v = S.At(target) is { } occ && occ.Id != u.Id ? occ : null;
        if (BattleDiveLanding(u, target) is not { } land)
        {
            SetAtb(u.Id, 0.5);
            return false;
        }
        S.Fields.Remove(f);
        Emit(new FieldChanged(f.Id, f.Kind, f.Cells, true));
        RemoveEffect(u, FxDiving);
        RemoveEffect(u, H5.Combat.Fx.Invisible);
        if (land != u.Pos) Teleport(u, land);
        if (v is { Alive: true } && u.Alive)
            Strike(u, v, new Hit { Kind = AttackKind.Melee, Multiplier = 2, BattleDiveFinish = true }, pattern: false);
        return true;
    }

    /// <summary>
    /// The delayed finish: the turn start FUN_00bbc8e0 (after the Defile Magic release) calls FUN_00bbc510, which for a
    /// stack with ABILITY_BATTLE_DIVE (0x1e) whose vf 0x50 timeline byte (FUN_008a3320 → FUN_008ace60 on [this − 0x94],
    /// written by vf 0xd4 = FUN_008a6330) is clear at the current time issues the 0x9b command (FUN_0054e5e0). The engine
    /// finishes a pending dive at the griffins' turn start; the turn is then over (no landing: ATB 0.5). True when the
    /// turn was taken.
    /// </summary>
    internal bool BattleDiveTurnStart(Unit u)
    {
        if (!u.HasEffect(FxDiving) || S.Fields.Find(f => f.Kind == "BATTLE_DIVE" && f.SourceUnit == u.Id) is not { } f) return false;
        Emit(new AbilityTriggered(u.Id, Ab.BattleDive));
        if (!BattleDiveFinish(u, f)) { FinishTurn(u, 0.5, allowMorale: false); return true; }
        if (u.Alive) FinishTurn(u, 0);
        else AdvanceUntilDecisionTail();
        return true;
    }

    /// <summary>
    /// Defile Magic's outcome, rolled at every cast of the defiled stack (FUN_00983790 → FUN_00c50a30 at 00983d14, before
    /// FUN_0097efd0 applies it; FUN_00c50da0 removes 0x146 at the end of the cast). N(g) = Σ over the defilers linked to
    /// the caster (FUN_00c50920: units with ability 0x6d whose 0x146 list holds it) of max(1, count / max(1, weekly growth
    /// (record +0xa8, FUN_004babb0))); no roll when their counts sum to 0. N(t) for a creature caster = [record +0x100 ≠ 0
    /// (FUN_004babd0) + vf 0x1c of the stack interface ≠ 0] + max(1, count / max(1, growth)) (00c50b5b..00c50bba; the first
    /// term is taken as 1, the stack's vf 0x1c not identified). FUN_00c506f0(N(t), N(g), {10, 40, 40, 10}, battle RNG):
    /// |N(t) − N(g)| steps of 5 points — to outcome 1 from the highest of 4, 3, 2 holding ≥ 10 when N(t) &lt; N(g), else to
    /// outcome 4 from the lowest of 1, 2, 3 holding ≥ 10, stopping when none does — then Random(0, 100) inclusive picks the
    /// first outcome whose running sum reaches it; the result goes to the effect (vf 0x26c(0x146, outcome), +0x28).
    /// </summary>
    internal int DefileRoll(Unit caster, IReadOnlyList<Unit> defilers)
    {
        int ng = 0, total = 0;
        foreach (var d in defilers)
        {
            ng += Math.Max(1, d.Count / Math.Max(1, d.WeeklyGrowth));
            total += d.Count;
        }
        if (total <= 0) return 0;
        int nt = DefileCasterTerm(caster) + Math.Max(1, caster.Count / Math.Max(1, caster.WeeklyGrowth));
        var p = new[] { 10, 40, 40, 10 };
        int diff = Math.Abs(nt - ng);
        for (int k = 0; k < diff; k++)
        {
            int i;
            if (nt < ng)
            {
                for (i = 3; i >= 1 && p[i] < 10; i--) { }
                if (i < 1) break;
                p[i] -= 5; p[0] += 5;
            }
            else
            {
                for (i = 0; i <= 2 && p[i] < 10; i++) { }
                if (i > 2) break;
                p[i] -= 5; p[3] += 5;
            }
        }
        int roll = S.Rng.Range(0, 100), acc = 0;
        for (int i = 0; i < 4; i++)
        {
            acc += p[i];
            if (roll <= acc) return i + 1;
        }
        return 4;
    }
}
