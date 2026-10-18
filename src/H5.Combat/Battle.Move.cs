namespace H5.Combat;

// Movement and geometry. Steps cost 100 orthogonally and 141 diagonally against a budget of
// Speed*100, as on the adventure map (fan manual p.299: "the system works the same in combat").
public sealed partial class Battle
{
    public const int OrthCost = 100, DiagCost = 141;

    public static int Octile(Cell a, Cell b)
    {
        int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        return OrthCost * Math.Max(dx, dy) + (DiagCost - OrthCost) * Math.Min(dx, dy);
    }

    /// <summary>Gap between two footprints in Chebyshev metric (0 = overlapping/adjacent-touching, 1 = adjacent).</summary>
    public static int Distance(Unit a, Cell aPos, Unit b, Cell bPos)
    {
        int dx = Gap(aPos.X, aPos.X + a.Size - 1, bPos.X, bPos.X + b.Size - 1);
        int dy = Gap(aPos.Y, aPos.Y + a.Size - 1, bPos.Y, bPos.Y + b.Size - 1);
        return Math.Max(dx, dy);
    }

    static int Gap(int a0, int a1, int b0, int b1) => b0 > a1 ? b0 - a1 : a0 > b1 ? a0 - b1 : 0;

    public static int Distance(Unit a, Unit b) => Distance(a, a.Pos, b, b.Pos);
    public static bool AreAdjacent(Unit a, Unit b) => Distance(a, b) <= 1;
    public static bool AdjacentAt(Unit a, Cell aPos, Unit b) => Distance(a, aPos, b, b.Pos) <= 1;

    /// <summary>Euclidean centre distance, used for the shooting range penalty.</summary>
    public static double CenterDistance(Unit a, Unit b)
    {
        var (ax, ay) = a.Center; var (bx, by) = b.Center;
        return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }

    bool CellBlocked(Cell c, Unit mover)
    {
        if (!S.InBounds(c)) return true;
        if (S.Obstacles.Contains(c)) return true;
        foreach (var o in S.Units)
            if (o.Id != mover.Id && o.Alive && !o.IsWarMachine && o.Occupies(c)) return true;
        return false;
    }

    public bool Fits(Unit u, Cell p)
    {
        foreach (var c in u.CellsAt(p)) if (CellBlocked(c, u)) return false;
        return true;
    }

    public bool CanMove(Unit u) =>
        !u.IsWarMachine && !u.IsObject && !IsEntangled(u) && !u.HasEffect("SPELL_BLIND") && !u.HasEffect(H5.Combat.Fx.Sleep);

    bool IsEntangled(Unit u) =>
        u.GetEffect(H5.Combat.Fx.Entangled) is { } e && S.Unit(e.SourceUnit) is { Alive: true } src && AreAdjacent(u, src);

    /// <summary>All top-left positions the unit can end a move on this turn, with path costs.</summary>
    public Dictionary<Cell, int> Reachable(Unit u) => ReachableWithPaths(u).ToDictionary(kv => kv.Key, kv => kv.Value.Cost);

    Dictionary<Cell, (int Cost, Cell Prev)> ReachableWithPaths(Unit u)
    {
        var result = new Dictionary<Cell, (int Cost, Cell Prev)>();
        if (!CanMove(u)) return result;
        int budget = SpeedOf(u) * 100;
        if (u.Flying)
        {
            for (int x = 0; x <= S.Width - u.Size; x++)
            for (int y = 0; y <= S.Height - u.Size; y++)
            {
                var p = new Cell(x, y);
                if (p == u.Pos) continue;
                int cost = Octile(u.Pos, p);
                if (cost <= budget && Fits(u, p)) result[p] = (cost, u.Pos);
            }
            return result;
        }
        // Dijkstra on the 8-connected grid (small: at most 120 nodes).
        var dist = new Dictionary<Cell, int> { [u.Pos] = 0 };
        var prev = new Dictionary<Cell, Cell>();
        var open = new SortedSet<(int Cost, int X, int Y)> { (0, u.Pos.X, u.Pos.Y) };
        while (open.Count > 0)
        {
            var (cost, cx, cy) = open.Min;
            open.Remove(open.Min);
            var cur = new Cell(cx, cy);
            if (cost > dist[cur]) continue;
            foreach (var d in Cell.Neighbours8)
            {
                var n = cur + d;
                bool diag = d.X != 0 && d.Y != 0;
                int nc = cost + (diag ? DiagCost : OrthCost);
                if (nc > budget) continue;
                if (!Fits(u, n)) continue;
                // No squeezing diagonally between two blocked orthogonal steps.
                if (diag && !Fits(u, new Cell(cur.X + d.X, cur.Y)) && !Fits(u, new Cell(cur.X, cur.Y + d.Y))) continue;
                if (dist.TryGetValue(n, out var old) && old <= nc) continue;
                dist[n] = nc;
                prev[n] = cur;
                open.Add((nc, n.X, n.Y));
            }
        }
        foreach (var (c, cost) in dist)
            if (c != u.Pos) result[c] = (cost, prev[c]);
        return result;
    }

    List<Cell> PathTo(Unit u, Cell to)
    {
        var r = ReachableWithPaths(u);
        if (!r.ContainsKey(to)) return new List<Cell>();
        if (u.Flying) return new List<Cell> { u.Pos, to };
        var path = new List<Cell> { to };
        var cur = to;
        while (cur != u.Pos) { cur = r[cur].Prev; path.Add(cur); }
        path.Reverse();
        return path;
    }

    static int Steps(List<Cell> path, bool flying) =>
        path.Count < 2 ? 0 : flying ? Math.Max(Math.Abs(path[0].X - path[^1].X), Math.Abs(path[0].Y - path[^1].Y)) : path.Count - 1;

    /// <summary>Moves a unit along a validated path, applying mines/firewalls, and records walked cells.</summary>
    List<Cell> MoveUnit(Unit u, Cell to)
    {
        var path = PathTo(u, to);
        if (path.Count < 2) return path;
        int steps = Steps(path, u.Flying);
        var from = u.Pos;
        u.Pos = to;
        u.CellsWalked = steps;
        u.HasMovedFromStart = true;
        Emit(new Moved(u.Id, path, u.Flying));
        RageEvent(u, "MoveRP");
        var eh = Enemy(u.Side);
        // Corrupted Soil: damage each time an enemy stack moves (p.109).
        if (eh.Present && (eh.HasPerk(Sk.CorruptedSoil) || eh.HasPerk(Sk.BarbCorruptedSoil)))
            DealDamage(u, (int)(Stats.Get("combat/HeroSkills/Ranger/SoilBurnDamagePerHeroLevel", 3) * eh.Level), Actor.Hero(1 - u.Side), AttackKind.Spell, "SOIL_BURN");
        // Fields entered along the way (flyers only touch the landing cells).
        var touched = u.Flying ? u.CellsAt(to).ToList() : path.Skip(1).SelectMany(p => u.CellsAt(p)).Distinct().ToList();
        foreach (var f in S.Fields.ToList())
        {
            if (!u.Alive) break;
            if (!f.Cells.Any(touched.Contains)) continue;
            if (f.Kind == "MINE" && f.Side != u.Side)
            {
                S.Fields.Remove(f);
                Emit(new FieldChanged(f.Id, f.Kind, f.Cells, true));
                DealSpellDamage(u, Db.Spell("SPELL_LAND_MINE")!, f.Damage, f.Side, ignoreResist: false);
            }
            else if (f.Kind == "FIREWALL")
                DealSpellDamage(u, Db.Spell("SPELL_FIREWALL")!, f.Damage, f.Side, ignoreResist: false);
        }
        if (u.HasEffect(H5.Combat.Fx.Invisible) && S.LivingOf(1 - u.Side).Any(e => AreAdjacent(u, e)))
            RemoveEffect(u, H5.Combat.Fx.Invisible);
        return path;
    }

    string? DoMove(MoveCmd c)
    {
        if (CheckActive(c, c.UnitId, out var u) is { } err) return err;
        if (!CanMove(u)) return "unit cannot move";
        if (!Reachable(u).ContainsKey(c.To)) return "destination not reachable";
        MoveUnit(u, c.To);
        EndAction(u.Id, 0);
        return null;
    }

    /// <summary>Positions from which <paramref name="u"/> can melee <paramref name="t"/> this turn (including staying put).</summary>
    public List<Cell> AttackPositions(Unit u, Unit t)
    {
        var list = new List<Cell>();
        if (AdjacentAt(u, u.Pos, t)) list.Add(u.Pos);
        foreach (var p in Reachable(u).Keys)
            if (AdjacentAt(u, p, t)) list.Add(p);
        return list;
    }

    void Teleport(Unit u, Cell to)
    {
        var from = u.Pos;
        u.Pos = to;
        u.HasMovedFromStart = true;
        Emit(new Teleported(u.Id, from, to));
    }

    /// <summary>Nearest free position for a unit footprint, searching outward from a cell.</summary>
    Cell? NearestFree(Unit u, Cell around)
    {
        Cell? best = null; int bestD = int.MaxValue;
        for (int x = 0; x <= S.Width - u.Size; x++)
        for (int y = 0; y <= S.Height - u.Size; y++)
        {
            var p = new Cell(x, y);
            if (!Fits(u, p)) continue;
            int d = Octile(p, around);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
