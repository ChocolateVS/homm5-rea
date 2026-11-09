namespace H5.Combat.AI;

// The approach generator (FUN_008d5b90 → FUN_008d4330 → FUN_008d3c40) and the AI's move planner
// (FUN_00c540c0 → FUN_00c53f60 → FUN_00c536f0, stop cell FUN_00c53640 / FUN_00c53310). Ledger: AUDIT_AI.md, "Combat AI".
public sealed partial class OriginalBattleAI
{
    /// <summary>
    /// Approach (FUN_008d5b90 → FUN_008d4330(whole-path map think +0xfc, think +0x1bc, 0)): for every enemy stack, the ring
    /// cells around it (FUN_00b3f2f0 = FUN_00b3d010 for the actor's size) in whole-path cost order (FUN_008d6dd0); per cell
    /// FUN_008d3c40 plans the move twice in full (FUN_00c540c0 with planner mode 2, command kinds 3 and 1) — both must
    /// succeed — then once for this turn (mode 1; a flyer whose plan fails tries again on map think +0xe4); the record takes
    /// that plan's turn count (record +0xc), the whole-path cost of the ring cell (FUN_00b5eb90 on the map, +0x10) and
    /// FUN_008d00d0(enemy, actor, 1) (+0xac); with ConsiderOnlyShortestPathToEnemy (row +0xb) the first record per enemy
    /// ends its scan. GAP-details: the enemy gates of FUN_008d4330 — the invisible-enemy test FUN_00a6af50, the
    /// FUN_00b3e1c0 filter (think +0x1bc), the skip of a stack whose vf 0x80 record has +0x1c == 2 (think +0x1b6) and the
    /// adjacency / think +0x1b7 / FUN_008d0440 test — are not applied; the duplicate "Move-no-attack" records (actor
    /// ability 0x37, not too weak) carry the same move and are left out.
    /// </summary>
    IEnumerable<Candidate> ApproachMoves(Battle b, Unit u, int side, LegalActions la, List<Unit> enemies, CombatDifficulty diff)
    {
        var map = WholePath(b, u);
        (Dictionary<Cell, int>, Dictionary<Cell, Cell>)? mapE4 = null;
        int Cost(Cell c) => map.Dist.TryGetValue(c, out var v) ? v : Unreachable;
        foreach (var e in enemies)
            foreach (var (cell, cost) in SortedRing(e, u, Cost))
            {
                if (PlanMove(b, u, cell, 2, map) == null) continue;
                var plan = PlanMove(b, u, cell, 1, map);
                if (plan == null && u.Flying) plan = PlanMove(b, u, cell, 1, mapE4 ??= b.AiPathMap(u, true, true, true));
                if (plan is not { } p || p.Stop == u.Pos) continue;
                yield return new Candidate
                {
                    Command = new MoveCmd(u.Id, p.Stop) { Side = side }, Turns = p.Turns, MovePrice = cost,
                    Priority = TargetPriority(b, e, u, true, p.Turns, cost), Label = $"approach {e.Id} via {cell.X},{cell.Y}",
                };
                if (diff.ConsiderOnlyShortestPathToEnemy) break;
            }
    }

    /// <summary>
    /// The whole-path map think +0xfc (FUN_008d0720(map, 0, 1, 0), built when its size fields are not set): the actor's
    /// search over the obstacles and the enemy stacks — its own stacks do not block — without a budget
    /// (<see cref="Battle.AiPathMap"/>); a flyer searches an empty board.
    /// </summary>
    internal static (Dictionary<Cell, int> Dist, Dictionary<Cell, Cell> Prev) WholePath(Battle b, Unit u) => b.AiPathMap(u, false, true, false);

    /// <summary>A planned move: where the unit stops this turn and the turns the plan still needs (FUN_00c536f0's count).</summary>
    public readonly record struct MovePlan(Cell Stop, int Turns);

    /// <summary>
    /// FUN_00c53f60 / FUN_00c536f0 without a target (the approach): plan the actor's move to <paramref name="cell"/> on
    /// <paramref name="map"/> in planner mode 0 (only within this turn), 1 (this turn's part of the path) or 2 (the whole
    /// path). Null when the planner fails.
    /// <list type="number">
    /// <item>the actor's own cell fails; a flyer (vf 0x190) fails on a goal its full grid (FUN_00b3f120(0, actor): obstacles
    /// and every other stack) blocks (FUN_0045c100); the path is the map's path to the goal (FUN_00b5ee10) and needs ≥ 2 cells;</item>
    /// <item>cost = combat vf 0x78 = FUN_00af2c20: Σ steps, 2 when x or y stays, else 3;</item>
    /// <item>siege (combat vf 0x50 = +0xac ≠ 2), a walker of the attacking army (army vf 0xac), mode ≠ 0: +1 turn per inner
    /// path cell in the moat (FUN_00b3e040: the anchor in the cells of the combat's type-5 object), +1 more for a size-2
    /// stack when that count is above 0;</item>
    /// <item>B = 2 × speed (vf 0x48): cost &gt; B → turns += (cost + B − 1) / B − 1 (10 at speed 0); mode 0 fails; mode 1
    /// cuts the path: B below the first step → only the start (no move); otherwise with k = the farthest cell within B and
    /// e = where the first of the path's B-sized pieces counted from its end begins, the stop is the cell of
    /// path[e..k] with the lowest danger (<see cref="StopCell"/>), the path ends there (FUN_0085a500, FUN_008d6d90);</item>
    /// <item>a walker in mode ≠ 0: the path ends before its first cell the full grid blocks, then mode 1 has turns ≥ 1;
    /// a path of ≥ 2 cells cut below 2 fails; a flyer drops end cells out of the field or blocked, with the same failure.</item>
    /// </list>
    /// Combat vf 0xb8 (FUN_00af1b70: an army vf 0xbc, the tactics phase) takes another branch; the AI ends its tactics at
    /// once (<see cref="Decide"/>), so it is not ported.
    /// </summary>
    internal static MovePlan? PlanMove(Battle b, Unit u, Cell cell, int mode, (Dictionary<Cell, int> Dist, Dictionary<Cell, Cell> Prev) map, bool target = false)
    {
        // FUN_00c536f0 with a target (the attack generator): the start as goal succeeds at once (a strike from here) and
        // a path shorter than 2 cells does not fail.
        if (cell == u.Pos) return target ? new MovePlan(u.Pos, 0) : null;
        bool flyer = u.Flying;
        if (flyer && !b.Fits(u, cell)) return null;
        if (!map.Dist.ContainsKey(cell)) return null;
        var path = new List<Cell> { cell };
        for (var c = cell; c != u.Pos; ) { c = map.Prev[c]; path.Add(c); }
        path.Reverse();
        if (path.Count < 2 && !target) return null;
        int cost = 0;
        for (int i = 0; i + 1 < path.Count; i++) cost += Step(path[i], path[i + 1]);
        int turns = 0;
        if (b.State.Siege is { Moat.Count: > 0 } sg && !flyer && u.Side == 0 && mode != 0)
        {
            for (int i = 1; i < path.Count - 1; i++) if (sg.Moat.Contains(path[i])) turns++;
            if (u.Size > 1 && turns > 0) turns++;
        }
        int speed = b.SpeedOf(u), budget = 2 * speed;
        if (cost > budget)
        {
            turns += speed < 1 ? 10 : (budget + cost - 1) / budget - 1;
            if (mode == 0) return null;
            if (mode == 1)
            {
                if (budget < Step(path[0], path[1])) path.RemoveRange(1, path.Count - 1);
                else
                {
                    var steps = new int[path.Count - 1];
                    for (int i = 0; i < steps.Length; i++) steps[i] = path[i].X == path[i + 1].X || path[i].Y == path[i + 1].Y ? 2 : 3;
                    int k = 0, sum = 0;
                    while (k < steps.Length && sum + steps[k] <= budget) sum += steps[k++];
                    int e = steps.Length, run = 0;
                    for (int i = steps.Length - 1; i >= 0; i--)
                    {
                        run += steps[i];
                        if (run > budget) { run = steps[i]; e = i + 1; }
                    }
                    var stop = StopCell(b, u, path.GetRange(e, k - e + 1));
                    path.RemoveRange(path.IndexOf(stop) + 1, path.Count - path.IndexOf(stop) - 1);
                }
            }
        }
        int n0 = path.Count;
        if (!flyer)
        {
            if (mode != 0)
            {
                int i = path.FindIndex(c => !b.Fits(u, c) && c != u.Pos);
                if (i >= 0)
                {
                    path.RemoveRange(i, path.Count - i);
                    if (mode == 1) turns = Math.Max(turns, 1);
                }
            }
        }
        else
            while (path.Count > 0 && !b.Fits(u, path[^1]) && path[^1] != u.Pos) path.RemoveAt(path.Count - 1);
        if (n0 >= 2 && path.Count < 2) return null;
        return new MovePlan(path.Count == 0 ? u.Pos : path[^1], turns);
    }

    static int Step(Cell a, Cell c) => a.X == c.X || a.Y == c.Y ? Battle.MoveOrth : Battle.MoveDiag;

    /// <summary>
    /// FUN_00c53640: the cell of <paramref name="cells"/> with the lowest danger FUN_00c53310 (ties: the later one).
    /// </summary>
    internal static Cell StopCell(Battle b, Unit u, List<Cell> cells)
    {
        var score = Danger(b, u, cells);
        int best = 0;
        for (int i = 1; i < cells.Count; i++) if (score[i] <= score[best]) best = i;
        return cells[best];
    }

    /// <summary>
    /// FUN_00c53310(cells, combat, actor): per cell, over the stacks of the actor's enemy army (combat vf 0xe0(actor vf 0xc),
    /// army vf 0x64) with a creature or war-machine interface (vf 0x6c / 0x7c), d = trunc((float)count (vf 0x1d8) ·
    /// max damage (vf 0x1f0)): a stack that shoots (vf 0x1e0) adds trunc(score + FUN_00a5c5a0(stack, cell, actor size) · d)
    /// (x87 chop, single precision); any other adds d when its move search this turn (FUN_00c52cc0(stack, 0, 0): budget
    /// 2 × speed, FUN_00b5f0d0 from its cell) reaches a cell of the ring around the actor standing there (FUN_00b3d010,
    /// FUN_00b5ee10). GAP-detail: the last term, +1 when a combat object the actor would trigger at the cell does damage
    /// (FUN_0085be20 on combat vf 0x18 = +0x190 with the start cell (−1, −1)), is not ported.
    /// </summary>
    internal static int[] Danger(Battle b, Unit u, List<Cell> cells)
    {
        var score = new int[cells.Count];
        foreach (var t in b.State.Living.Where(t => t.Side != u.Side && !t.IsObject))
        {
            var (_, max) = b.DamageRangeOf(t);
            int d = (int)((float)t.Count * (float)max);
            if (b.CanShoot(t))
            {
                for (int i = 0; i < cells.Count; i++)
                    score[i] = (int)((float)score[i] + (float)b.AiRangeFactorAt(t, u, cells[i]) * (float)d);
                continue;
            }
            int budget = 2 * b.SpeedOf(t);
            var reach = t.Flying ? null : b.MoveSearch(t, budget).Dist;
            for (int i = 0; i < cells.Count; i++)
            {
                bool hit = Battle.ExeRing(cells[i], t.Size, u.Size).Any(a => reach != null
                    ? reach.ContainsKey(a)
                    : a.X >= 0 && a.Y >= 0 && a.X <= b.State.Width - t.Size && a.Y <= b.State.Height - t.Size && Battle.MoveDistance(t.Pos, a) <= budget);
                if (hit) score[i] += d;
            }
        }
        return score;
    }
}
