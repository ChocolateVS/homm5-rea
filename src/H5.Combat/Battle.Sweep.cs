namespace H5.Combat;

// Combat audit sweep (docs/rules/audit/combat_sweep_*.md): mechanics re-checked against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Rune of Magic Control (exe FUN_00a5ca80, called once by the attack routine FUN_00a65260 @ 00a65646 — after the
    /// approach, before the strike count vf 0x1c0 and the strike loop; not on retaliations): when the attacker holds the
    /// rune (effect 0xfb) and the target is alive, one of the target's effects accepted by FUN_004e4000 is chosen with the
    /// battle RNG, added to the attacker (vf 0x264) and removed from the target (vf 0x270); the rune is used up. The stolen
    /// effect therefore already counts for this attack's strikes. The engine stole after the first hit, on any hit.
    /// </summary>
    void RuneOfMagicControlSteal(Unit a, Unit t)
    {
        if (!a.HasEffect(H5.Combat.Fx.RuneMagicControl) || !t.Alive) return;
        var candidates = t.Effects.Where(MagicControlCandidate).ToList();   // any school (exe FUN_004e4000)
        RemoveEffect(a, H5.Combat.Fx.RuneMagicControl);
        if (candidates.Count == 0) return;
        var stolen = candidates[S.Rng.Next(candidates.Count)];
        RemoveEffect(t, stolen.Id);
        AddEffect(a, new Effect { Id = stolen.Id, Value = stolen.Value, Value2 = stolen.Value2, Remaining = stolen.Remaining,
            Positive = true, SourceSide = a.Side, Mastery = stolen.Mastery, Power = stolen.Power, School = stolen.School, SpellLevel = stolen.SpellLevel });
    }

    /// <summary>
    /// Squared cell distance of the combat (vf 0x118 = FUN_00af2d60): the minimum of dx² + dy² (FUN_00b5f550) over every
    /// pair of cells the two units cover (vf 0xc4).
    /// </summary>
    internal static int SquaredCellDistance(Unit a, Unit b)
    {
        int best = int.MaxValue;
        foreach (var p in a.Cells())
            foreach (var q in b.Cells())
                best = Math.Min(best, (p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        return best;
    }

    /// <summary>
    /// Summon Hive's target (exe FUN_00bbc310 → FUN_00bbcec0 with "pick first" 0): among the valid targets the ones at the
    /// smallest SquaredCellDistance (truncated, chop) from the hive; ties are broken with the battle RNG Random(n). The
    /// engine took the smallest Chebyshev distance and broke ties by unit id.
    /// </summary>
    internal Unit? HiveTarget(Unit hive, IEnumerable<Unit> valid)
    {
        var list = valid.ToList();
        if (list.Count == 0) return null;
        int best = list.Min(o => SquaredCellDistance(o, hive));
        var ties = list.Where(o => SquaredCellDistance(o, hive) == best).ToList();
        return ties[S.Rng.Next(ties.Count)];
    }

    const string LinkSummonElementals = "SPELL_SUMMON_ELEMENTALS";

    /// <summary>
    /// Summon Elementals' stack (exe FUN_00861500, called by FUN_00861630 with the "counter-summon" flag 0): the summon
    /// position is found first (FUN_00860f20 — nothing happens without one); then the elemental stack still linked to the
    /// caster for spell 0x2b (FUN_00a6e310) is destroyed — FUN_00a61530 with its creature count, i.e. count × health through
    /// the stack damage path — and unlinked (vf 0x2a0); the new stack is placed and linked (vf 0x298(0x2b, stack)) unless it
    /// died on arrival. The engine kept the previous stack and added another.
    /// </summary>
    Unit? SummonLinkedElementals(HeroState? hero, int side, string kind, int count, string by, double atb)
    {
        if (!Db.Creatures.ContainsKey(kind)) return null;
        var probe = new Unit { Size = Db.Creature(kind).CombatSize };
        Cell around = side == 0 ? new Cell(1, S.Height / 2) : new Cell(S.Width - 2, S.Height / 2);
        if (NearestFree(probe, around) is not Cell pos) return null;
        if (hero != null && hero.Links.TryGetValue(LinkSummonElementals, out var oldId))
        {
            if (S.Unit(oldId) is { Alive: true } old)
                DealDamage(old, old.MaxHealthCache * old.Count, -1, AttackKind.Other, by);
            hero.Links.Remove(LinkSummonElementals);
        }
        var u = BattleSetup.AddCreature(Db, S, side, kind, count, pos, summoned: true);
        u.Atb = atb;
        u.HasMovedFromStart = true;
        Emit(new UnitSummoned(u.Id, kind, count, side, u.Pos, by));
        if (hero != null && u.Alive) hero.Links[LinkSummonElementals] = u.Id;
        return u;
    }
}
