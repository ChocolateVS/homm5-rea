namespace H5.Combat;

// Combat audit sweep 16 (docs/rules/audit/combat_sweep_16.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>The army vf 0xa4 start value (FUN_004d9310): no actor gives 0xffffff.</summary>
    internal const uint NoTurnTime = 0xffffff;

    /// <summary>
    /// FUN_004e4920 ≠ 0 (Blind 0x13, Frozen 0xc9, Sabotage 0x14a, Sleeping Strike 0x150, the army's Seduce 0x148 —
    /// the same set as FUN_004e4a20): the tick FUN_00a6a260 leaves such a unit's ATB alone, and FUN_00a6e440 fills its gauge
    /// at 0 until those effects end. A unit the engine gives no initiative (Frozen, a seducing succubus) counts as well.
    /// </summary>
    internal bool AtbFrozen(Unit u) => DisabledForHelperStrike(u) || InitiativeOf(u) <= 0;

    /// <summary>One full turn of an actor (vf 0x174 = FUN_00a6a060 → FUN_00a2d6c0) in exe game time.</summary>
    internal double FullTurnTimeOf(int actor)
    {
        if (!Actor.IsHero(actor)) return FullTurnTime(S.Unit(actor)!);
        float init = (float)HeroInitiative(Actor.HeroSide(actor));
        if (init <= 0f) init = 1f;
        double t = AtbBaseTime() / (double)init;
        return t == 0 ? 1 : t;
    }

    /// <summary>The time to an actor's next turn (vf 0x180): <see cref="TimeToNextTurn"/> for units; a hero has no timed
    /// effects, so chop((1.0f − ATB) · its full turn), 0 at ATB ≥ 1.</summary>
    internal uint TimeToNextTurnOf(int actor)
    {
        if (!Actor.IsHero(actor)) return TimeToNextTurn(S.Unit(actor)!);
        float atb = (float)GetAtb(actor);
        if (atb >= 1f) return 0;
        double t = Math.Floor((1.0 - atb) * FullTurnTimeOf(actor));
        return t >= uint.MaxValue ? uint.MaxValue : (uint)t;
    }

    /// <summary>
    /// The turn sequence (FUN_00a2e0c0, rebuilt by FUN_00a2e310 after each turn): the present stacks of the attacker
    /// (combat vf 0x84(1) = +0x8c) then of the defender in army order (FUN_00af4550 → army vf 0x68), the disabled ones
    /// (FUN_004e4a20) set aside, then the attacker's and the defender's hero (FUN_00a2d8f0), then the disabled stacks;
    /// paired with vf 0x180 (FUN_00a2dc10) and merge-sorted (FUN_00a2e8b0, stable) by comparator FUN_00a2d800: time
    /// ascending, then ATB (+0x1c) descending. The future-turn entries FUN_00a2dd70 appends never precede an actor's first
    /// entry, so only the first entries matter here.
    /// </summary>
    internal List<int> TurnSequence()
    {
        var present = Actors().ToList();
        var order = new List<int>();
        var disabled = new List<int>();
        for (int side = 0; side < 2; side++)
            foreach (var a in present)
                if (!Actor.IsHero(a) && S.Unit(a)!.Side == side)
                    (DisabledForHelperStrike(S.Unit(a)!) ? disabled : order).Add(a);
        for (int side = 0; side < 2; side++)
            if (present.Contains(Actor.Hero(side))) order.Add(Actor.Hero(side));
        order.AddRange(disabled);
        return order.Select((a, i) => (a, i, t: TimeToNextTurnOf(a), atb: (float)GetAtb(a)))
            .OrderBy(x => x.t).ThenByDescending(x => x.atb).ThenBy(x => x.i)
            .Select(x => x.a).ToList();
    }

    /// <summary>
    /// The combat step FUN_00af5930 (00af5a4f..00af5b48) on the exe's integer clock: when no stack of either army is ready
    /// (combat vf 0xe4 = FUN_00af32a0, the armies' ready flags +0x34), dt = combat vf 0xac (FUN_00af2690: the smaller of
    /// the armies' vf 0xa4 = FUN_004d9310, the least vf 0x180 over the hero and the present stacks, from 0xffffff), at
    /// least 1, and both armies tick by it (army vf 0x94 = FUN_004d90d0 on +0x8c, then +0x90). The turn then goes to the
    /// head of the turn sequence when its ATB is ≥ 1 (FUN_00af2c60 → FUN_00a2d890). Since the sequence is rebuilt only
    /// after a turn, it is the order of the times at the last rebuild; a truncated time leaves the head just under 1.0
    /// after the first tick and a second tick of 1 brings it over.
    /// </summary>
    internal int? NextActorOnTheClock()
    {
        var seq = TurnSequence();
        for (int guard = 0; guard < 100000; guard++)
        {
            if (seq.Count == 0) return null;
            int head = seq[0];
            if (Actors().Contains(head) && (float)GetAtb(head) >= 1f) return head;
            var present = Actors().ToList();
            if (!Actors().Contains(head) || present.Any(a => (float)GetAtb(a) >= 1f))
            {
                // A dead head, or a ready actor that is not the head: the exe waits for a sequence rebuild (not traced
                // which event triggers it); the engine rebuilds at once.
                seq = TurnSequence();
                if (seq.Count > 0 && Actors().Contains(seq[0]) && (float)GetAtb(seq[0]) >= 1f) return seq[0];
                if (present.Any(a => (float)GetAtb(a) >= 1f)) return present.First(a => (float)GetAtb(a) >= 1f);
            }
            uint dt = NoTurnTime;
            foreach (var a in present) dt = Math.Min(dt, TimeToNextTurnOf(a));
            if (dt == NoTurnTime && present.All(a => TimeToNextTurnOf(a) >= NoTurnTime)) return null;
            if (dt < 1) dt = 1;
            ClockTick(dt);
        }
        return null;
    }

    /// <summary>
    /// The tick (army vf 0x94 = FUN_004d90d0: the hero, then every stack, vf 0x178 = FUN_00a6a260 → FUN_00a6a0b0): a
    /// present actor not disabled (FUN_004e4920 = 0) gets ATB' = max(0, min(1.25, (float)dt / vf 0x174 + ATB)) in single
    /// precision. Then the engine's effect and field clocks run dt (one engine time unit = ATBBaseTime / 10 exe units);
    /// the order of the effect expiries against the tick is not traced — the ATB uses the initiative in force before them.
    /// </summary>
    internal void ClockTick(uint dt)
    {
        for (int side = 0; side < 2; side++)
        {
            var army = Actors().Where(a => Actor.IsHero(a) ? Actor.HeroSide(a) == side : S.Unit(a)!.Side == side)
                .OrderBy(a => Actor.IsHero(a) ? 0 : 1).ToList();
            foreach (var a in army)
            {
                if (!Actor.IsHero(a) && AtbFrozen(S.Unit(a)!)) continue;
                float atb = (float)GetAtb(a);
                float v = (float)((float)dt / FullTurnTimeOf(a) + atb);
                v = Math.Max(0f, Math.Min(1.25f, v));
                if (Actor.IsHero(a)) S.Heroes[Actor.HeroSide(a)].Atb = v; else S.Unit(a)!.Atb = v;
            }
        }
        double dtE = dt * 10.0 / AtbBaseTime();
        S.Time += dtE;
        foreach (var u in S.Units)
            foreach (var e in u.Effects)
                if (!double.IsPositiveInfinity(e.Remaining)) e.Remaining -= dtE;
        foreach (var f in S.Fields)
            if (!double.IsPositiveInfinity(f.Remaining)) f.Remaining -= dtE;
        ExpireEffects();
    }

    /// <summary>
    /// Enchanted Obsidian's damage-to-heal (the damage-queue flush FUN_008d7f40, 008d8023..008d80c0): the record's spell
    /// is its +0x20 — argument 11 of FUN_008d7da0, argument 10 of the queue's slot-0 add 008d7e70 — and the target with
    /// HasAbility(0x99) is healed when that spell is non-zero, passes FUN_00a349f0 (school Runic / Warcries, or any school
    /// but Special / None, Dark Ritual excluded) and is not FUN_009776e0 (base Magic Fist or Weakening Strike). The strikes
    /// (FUN_008cf270, FUN_00a608d0 / a60be0 / a60e20 / a61090, FUN_00a64ad0, FUN_00a673c0) queue with spell 0.
    /// </summary>
    internal bool EnchantedObsidianHeals(Unit t, string? spell) =>
        t.Has(Ab.EnchantedObsidian) && !string.IsNullOrEmpty(spell)
        && spell is not ("SPELL_MAGIC_FIST" or "SPELL_ABILITY_WEAKNING_STRIKE")
        && ExeCasterSpells(Db, new[] { spell });
}
