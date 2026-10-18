namespace H5.Combat;

public interface IBattleAI
{
    /// <summary>Chooses a command for <paramref name="side"/>; the battle must be waiting on that side.</summary>
    BattleCommand Decide(Battle battle, int side);
}

/// <summary>
/// One-ply search AI: enumerates sensible commands, simulates each on a cloned battle and picks the one
/// with the best resulting army value (plus small heuristics for buffs and positioning). Uses the same
/// rules engine, so it is always legal; deterministic for a given state.
/// </summary>
public sealed class GreedyBattleAI : IBattleAI
{
    public int MaxCandidates { get; init; } = 40;

    public BattleCommand Decide(Battle b, int side)
    {
        var s = b.State;
        if (s.Phase == BattlePhase.Tactics) return new EndTacticsCmd { Side = side };
        var la = b.GetLegalActions();
        if (la == null || b.SideToAct != side) return new WaitCmd(Actor.Hero(side)) { Side = side };

        var candidates = Candidates(b, la, side).Take(MaxCandidates).ToList();
        double baseline = Evaluate(b, side);
        BattleCommand? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var (cmd, heuristic) in candidates)
        {
            var sim = new Battle(b.Db, s.Clone());
            var r = sim.Apply(cmd);
            if (!r.Ok) continue;
            double score = Evaluate(sim, side) - baseline + heuristic;
            if (score > bestScore + 1e-9) { bestScore = score; best = cmd; }
        }
        return best ?? Fallback(la, side);
    }

    static BattleCommand Fallback(LegalActions la, int side) =>
        la.CanDefend && !la.IsHero ? new DefendCmd(la.Actor) { Side = side } : new WaitCmd(la.Actor) { Side = side };

    /// <summary>Army value: sum of remaining creature power, own minus enemy (enemy weighted slightly higher to favour trades).</summary>
    public static double Evaluate(Battle b, int side)
    {
        var s = b.State;
        if (s.Phase == BattlePhase.Finished) return s.Winner == side ? 1e9 : s.Winner == null ? 0 : -1e9;
        double mine = 0, theirs = 0;
        foreach (var u in s.Units)
        {
            if (!u.Alive || u.IsObject) continue;
            double perHp = u.Power / (double)Math.Max(1, u.MaxHealthCache);
            double v = u.TotalHealth * perHp;
            if (u.IsWarMachine) v = 300 * (u.TotalHealth / (double)Math.Max(1, u.MaxHealthCache));
            if (b.Controller(u) == side) mine += v; else theirs += v;
        }
        var me = s.Heroes[side]; var en = s.Heroes[1 - side];
        mine += me.Mana * 20; theirs += en.Mana * 20;
        return mine - 1.1 * theirs;
    }

    static double StackValue(Unit u) => (double)u.Count * u.Power;

    IEnumerable<(BattleCommand, double)> Candidates(Battle b, LegalActions la, int side)
    {
        var s = b.State;
        var list = new List<(BattleCommand, double)>();
        if (la.IsHero)
        {
            var h = s.Heroes[side];
            foreach (var t in la.HeroAttack.Select(s.Unit).OrderByDescending(u => StackValue(u!)).Take(6))
                list.Add((new HeroAttackCmd(t!.Id) { Side = side }, 0));
            foreach (var sp in la.Spells)
            {
                var def = b.Db.Spell(sp.Spell)!;
                bool hostile = def.Target == "TARGET_HOSTILE";
                double manaPenalty = -sp.ManaCost * 15;
                switch (sp.Targeting)
                {
                    case Battle.SpellTargeting.None:
                        list.Add((new CastSpellCmd(la.Actor, sp.Spell) { Side = side }, manaPenalty + BuffHeuristic(b, def, null, side)));
                        break;
                    case Battle.SpellTargeting.Cell:
                        foreach (var t in s.Living.Where(u => b.Controller(u) != side && !u.IsObject).OrderByDescending(StackValue).Take(4))
                            list.Add((new CastSpellCmd(la.Actor, sp.Spell, null, t.Pos) { Side = side }, manaPenalty + BuffHeuristic(b, def, t, side)));
                        break;
                    case Battle.SpellTargeting.Unit:
                        foreach (var t in sp.Targets.Select(s.Unit).Where(u => u!.Alive).OrderByDescending(u => StackValue(u!)).Take(4))
                            list.Add((new CastSpellCmd(la.Actor, sp.Spell, t!.Id) { Side = side }, manaPenalty + BuffHeuristic(b, def, t, side)));
                        break;
                }
            }
            foreach (var ab in la.Abilities)
            {
                if (ab.Targeting == Battle.AbilityTargeting.None)
                    list.Add((new UseAbilityCmd(la.Actor, ab.Ability) { Side = side }, 500));
                else
                    foreach (var t in ab.Targets.Select(s.Unit).OrderByDescending(u => StackValue(u!)).Take(2))
                        list.Add((new UseAbilityCmd(la.Actor, ab.Ability, t!.Id) { Side = side }, ab.Ability == "CONSUME_CORPSE" ? 50 : 300));
            }
            list.Add((new DefendCmd(la.Actor) { Side = side }, -1));
            return list;
        }

        var u0 = s.Unit(la.Actor)!;
        foreach (var t in la.Shoot) list.Add((new ShootCmd(u0.Id, t) { Side = side }, 0));
        foreach (var (tid, cells) in la.Melee)
        {
            // Strike from the cell that keeps the unit closest to where it is (fewest new threats).
            var from = cells.OrderBy(c => Battle.Octile(c, u0.Pos)).First();
            list.Add((new AttackCmd(u0.Id, tid, from == u0.Pos ? null : from) { Side = side }, 0));
        }
        foreach (var sp in la.Spells)
        {
            var def = b.Db.Spell(sp.Spell)!;
            if (sp.Targeting == Battle.SpellTargeting.Unit)
                foreach (var t in sp.Targets.Select(s.Unit).OrderByDescending(u => StackValue(u!)).Take(3))
                    list.Add((new CastSpellCmd(u0.Id, sp.Spell, t!.Id) { Side = side }, BuffHeuristic(b, def, t, side)));
            else if (sp.Targeting == Battle.SpellTargeting.Cell)
                foreach (var t in s.Living.Where(u => b.Controller(u) != side).OrderByDescending(StackValue).Take(2))
                    list.Add((new CastSpellCmd(u0.Id, sp.Spell, null, t.Pos) { Side = side }, 0));
            else list.Add((new CastSpellCmd(u0.Id, sp.Spell) { Side = side }, 0));
        }
        foreach (var ab in la.Abilities)
        {
            if (ab.Ability == Ab.Gating) { list.Add((new UseAbilityCmd(u0.Id, ab.Ability) { Side = side }, 0)); continue; }
            if (ab.Targeting == Battle.AbilityTargeting.None)
                list.Add((new UseAbilityCmd(u0.Id, ab.Ability) { Side = side }, ab.Ability is Ab.Dash or Ab.Invisibility or Ab.RageOfForest ? 50 : 0));
            else if (ab.Targeting == Battle.AbilityTargeting.Cell)
                foreach (var t in s.Living.Where(u => b.Controller(u) != side).OrderByDescending(StackValue).Take(2))
                    list.Add((new UseAbilityCmd(u0.Id, ab.Ability, null, t.Pos) { Side = side }, 0));
            else
                foreach (var t in ab.Targets.Select(s.Unit).OrderByDescending(u => StackValue(u!)).Take(2))
                    list.Add((new UseAbilityCmd(u0.Id, ab.Ability, t!.Id) { Side = side }, ab.Ability == "FIRST_AID" ? 20 : 0));
        }
        // Positional moves when nothing better: approach the most valuable reachable-soon enemy.
        if (la.Moves.Count > 0 && la.Melee.Count == 0 && la.Shoot.Count == 0)
        {
            var enemies = s.Living.Where(e => b.Controller(e) != side && !e.IsObject && !e.IsWarMachine).ToList();
            if (enemies.Count > 0)
            {
                bool shooter = u0.MaxShots > 0;
                var scored = la.Moves.Select(c =>
                {
                    double d = enemies.Min(e => Battle.Distance(u0, c, e, e.Pos));
                    return (c, score: shooter ? d : -d);
                }).OrderByDescending(x => x.score).ThenBy(x => x.c.X).ThenBy(x => x.c.Y).Take(3);
                foreach (var (c, score) in scored)
                    list.Add((new MoveCmd(u0.Id, c) { Side = side }, 5 + score));
            }
        }
        if (la.CanDefend) list.Add((new DefendCmd(u0.Id) { Side = side }, 1));
        list.Add((new WaitCmd(u0.Id) { Side = side }, la.Melee.Count == 0 && la.Shoot.Count == 0 && !u0.HasActed ? 2 : -5));
        return list;
    }

    /// <summary>Value of non-damage spell effects that simulation alone cannot see.</summary>
    static double BuffHeuristic(Battle b, H5.Core.Data.SpellDef def, Unit? t, int side)
    {
        if (def.Damage.Length > 0 && def.Target == "TARGET_HOSTILE" && def.Duration.Length == 0) return 0;  // pure damage: simulation scores it
        if (def.School is "MAGIC_SCHOOL_SUMMONING" && def.Target != "TARGET_FRIEND") return 600;
        if (t == null) return def.Target == "TARGET_FRIEND" ? 1500 : 800;
        double v = (double)t.Count * t.Power;
        bool friendly = b.Controller(t) == side;
        if (friendly && def.Target == "TARGET_HOSTILE") return -1e6;
        if (t.HasEffect(def.Id)) return -200;   // refreshing is rarely worth a turn
        return Math.Min(4000, v * 0.12);
    }
}

/// <summary>Drives a battle where both sides (or the given sides) are AI controlled, until it ends.</summary>
public static class BattleRunner
{
    public static List<BattleEvent> RunToEnd(Battle b, IBattleAI ai0, IBattleAI ai1, int maxCommands = 5000)
    {
        var log = new List<BattleEvent>(b.Begin());
        for (int i = 0; i < maxCommands && b.State.Phase != BattlePhase.Finished; i++)
        {
            int side;
            if (b.State.Phase == BattlePhase.Tactics) side = b.State.TacticsDone[0] ? 1 : 0;
            else side = b.SideToAct ?? 0;
            var cmd = (side == 0 ? ai0 : ai1).Decide(b, side);
            var r = b.Apply(cmd);
            if (!r.Ok)
            {
                // Should not happen; fall back to a safe action so the game always terminates.
                var la = b.GetLegalActions();
                r = b.Apply(la != null && !la.IsHero ? new DefendCmd(la.Actor) { Side = side } : new WaitCmd(la?.Actor ?? Actor.Hero(side)) { Side = side });
                if (!r.Ok) r = b.Apply(new SurrenderCmd { Side = side });
            }
            log.AddRange(r.Events);
        }
        return log;
    }
}
