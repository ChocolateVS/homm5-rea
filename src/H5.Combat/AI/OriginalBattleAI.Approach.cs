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
        // Think +0x1b8 (settings +0xa4): FUN_00af3cc0 is constant false, so the siege attacker always takes FUN_008d4610.
        if (SiegeAttacker(b, u.Side))
        {
            if (SiegeApproach(b, u, side) is { } rec) yield return rec;
            yield break;
        }
        bool stop = !IgnoresMoat(b, u);
        var map = WholePath(b, u, stop);
        (Dictionary<Cell, int>, Dictionary<Cell, Cell>)? mapE4 = null;
        int Cost(Cell c) => map.Dist.TryGetValue(c, out var v) ? v : Unreachable;
        foreach (var e in enemies)
            foreach (var (cell, cost) in SortedRing(e, u, Cost))
            {
                if (PlanMove(b, u, cell, 2, map) == null) continue;
                var plan = PlanMove(b, u, cell, 1, map);
                if (plan == null && u.Flying) plan = PlanMove(b, u, cell, 1, mapE4 ??= b.AiPathMap(u, true, true, true, stop));
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
    internal static (Dictionary<Cell, int> Dist, Dictionary<Cell, Cell> Prev) WholePath(Battle b, Unit u, bool moatStop = false) => b.AiPathMap(u, false, true, false, moatStop);

    /// <summary>
    /// Think +0x1b8 = settings +0xa4 (FUN_00a30460): combat vf 0x58 (00af7ea0: the town type +0xac) is not 2 (TOWN_NO_TYPE)
    /// and the actor's army answers vf 0xac (004d9380: combat vf 0x84(1), the attacker, is this army). With it FUN_008d5b90
    /// runs FUN_008d4610 instead of the approach unless FUN_00af3cc0 holds, and FUN_00af3cc0 never holds: it scans the
    /// defender's (combat vf 0x84(0) = combat +0x90) town buildings (army vf 0x78 = 004da7d0: the units' building interfaces,
    /// unit vf 0x84 = 00bff090, kept by FUN_00a6d490 only while the unit interface's vf 0x5c is false) for one of unit type 5
    /// (00bff800: building kind 0 / 1 = BUILDING_ANY / WALL) whose unit vf 0x5c (00bff070: current health +0xac == 0, i.e.
    /// destroyed) holds; the filter has already dropped every such building.
    /// </summary>
    internal static bool SiegeAttacker(Battle b, int side) => b.State.Siege is { } sg && sg.Town != "TOWN_NO_TYPE" && side == 0;

    /// <summary>
    /// Think +0x1c8 = settings +0xb4 (FUN_00a30460, once per think): 1; for an actor that walks (vf 0x190) in a siege (combat
    /// vf 0x50 = 00af7e90: town type != 2) it becomes 0 unless its army is the attacker (vf 0xac) and the defender's gate
    /// (army vf 0x7c = 004d9660: the first unit of type 6 = GATE, its building interface) is standing (unit vf 0x5c: health != 0)
    /// and closed (building vf 0x14 = 0071c640: +0x14, set by vf 4 = 00bfd270 for a gate); then with m = FUN_0085b6d0 (the moat
    /// damage) and h the actor's total health (vf 0x1dc(vf 0x20)): h &lt; 4m gives 0, and a 0 becomes 1 when h &gt; 40m. Set, the
    /// maps built through FUN_008d0720 do not stop at the moat (FUN_00c52e20(+0x1c8, ...) gives map +0x10 = 0, else 2) and
    /// FUN_008d64e0 adds the siege attacker's second pass. GAP-details: our gate has no open state (closed until destroyed);
    /// m is <see cref="SiegeState.MoatDamage"/>, 0 when the fort has no moat (the exe reads the town's moat record regardless);
    /// a siege without a gate (army vf 0x7c null, a null call in the exe) counts as a fallen gate.
    /// </summary>
    internal static bool IgnoresMoat(Battle b, Unit u)
    {
        if (u.Flying || b.State.Siege is not { } sg || sg.Town == "TOWN_NO_TYPE") return true;
        bool flag = u.Side == 0 && sg.Gate is { Destroyed: false };
        long m = sg.MoatDamage, h = u.TotalHealth;
        if (h < 4 * m) flag = false;
        if (!flag && h > 40 * m) flag = true;
        return flag;
    }

    /// <summary>
    /// FUN_008d4610, the siege attacker's approach ("Move-no-attack"): the map FUN_00c52e20(1, 1, 0, 1) (own stacks block, the
    /// enemy's do not, a flyer as a walker, no moat stop) from the actor; the goal is (combat vf 0x44 = 00af1450 field width - 4,
    /// actor y) in exe cells, ours (Width - 2, y) with the siege offsets 2 / 1; FUN_00c536f0 mode 2 plans the path; the danger
    /// FUN_00c53310 over its cells; i = n - 1, or the index before the first cell (from 1) whose danger exceeds danger[0] +
    /// h / 20 (h the actor's total health vf 0x1dc, integer division) or whose map cell carries the moat flag (+7 bit 0,
    /// <see cref="Battle.AiMoatFlag"/>); then, over 1 &lt;= j &lt; i, the index before the first cell whose map cost exceeds
    /// 0xffffff. i &gt;= 1 gives one record (defaults FUN_008d7550: Priority 2, compare type 0): FUN_00c540c0(cell i, kind 1,
    /// mode 2) must succeed, then the command of FUN_00c540c0(cell i, kind 0, mode 1); Turns = that plan's count, MovePrice =
    /// the map cost of cell i (FUN_00b5eb90), name "Move-no-attack".
    /// </summary>
    internal static Candidate? SiegeApproach(Battle b, Unit u, int side)
    {
        var map = b.AiPathMap(u, true, false, true);
        var goal = new Cell(b.State.Width - 2, u.Pos.Y);
        var path = new List<Cell>();
        if (PlanMove(b, u, goal, 2, map, false, path) == null) return null;
        var danger = Danger(b, u, path);
        int n = path.Count, i = n - 1, h = (int)Math.Min(int.MaxValue, u.TotalHealth);
        for (int j = 1; j < n; j++)
            if (danger[j] > h / 20 + danger[0] || b.AiMoatFlag(u, path[j])) { i = j - 1; break; }
        for (int j = 1; j < i; j++)
            if (!map.Dist.ContainsKey(path[j])) { i = j - 1; break; }
        if (i < 1) return null;
        var cell = path[i];
        if (PlanMove(b, u, cell, 2, map) == null) return null;
        if (PlanMove(b, u, cell, 1, map) is not { } p || p.Stop == u.Pos) return null;
        return new Candidate
        {
            Command = new MoveCmd(u.Id, p.Stop) { Side = side }, Turns = p.Turns,
            MovePrice = map.Dist.TryGetValue(cell, out var c) ? c : Unreachable, Label = $"Move-no-attack via {cell.X},{cell.Y}",
        };
    }

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
    internal static MovePlan? PlanMove(Battle b, Unit u, Cell cell, int mode, (Dictionary<Cell, int> Dist, Dictionary<Cell, Cell> Prev) map, bool target = false, List<Cell>? pathOut = null)
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
        pathOut?.AddRange(path);
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
    /// FUN_00b5ee10; a walker of the attacking army stops at the moat: <see cref="Battle.AiDangerReach"/>). GAP-detail: the last term, +1 when a combat object the actor would trigger at the cell does damage
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
            var reach = t.Flying ? null : b.AiDangerReach(t, budget);
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
