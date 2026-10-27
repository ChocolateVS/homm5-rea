using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>Instant Travel inputs of the pathfinder settings (exe FUN_00ceb190, settings at pathfinder +0x20).</summary>
/// <param name="Enabled">settings +0x36: the hero knows SPELL_DIMENSION_DOOR (0x32, hero vf 0xdc) and may cast it (vf 0x154).</param>
/// <param name="MoveCost">settings +0x38: hero vf 0xfc / 2 (half the day's movement).</param>
/// <param name="Count">settings +0x3c: (mana − 20) / spell cost (FUN_00a34740), at least 0.</param>
/// <param name="StartCostLimit">settings +0x68: AI/Adventure DimDoorStartCostLimit (block +0x228); ≤ 0 means no limit.</param>
/// <param name="TownPortal">settings +0x34 (pf +0x54): the hero knows SPELL_TOWN_PORTAL (0x33), may cast it and is not at sea.</param>
/// <param name="LandDayMove">settings +0x28 (pf +0x48): the day's land movement (hero vf 0xf8 of the move type), the Town Portal start's w4.</param>
public readonly record struct DimensionDoorSettings(bool Enabled, int MoveCost, int Count, int StartCostLimit,
                                                    bool TownPortal = false, int LandDayMove = 0);

/// <summary>One node of a flood route (exe 0x40-byte node: w0 position, w1 flags, w2 cost, w3 priority).</summary>
/// <param name="Cost">w2: movement cost from the start; an Instant Travel landing keeps its origin's cost.</param>
/// <param name="Priority">w3: the queue key (cost plus the 500 landing penalty and the spell's movement cost).</param>
/// <param name="CanStop">w1 bit 0x200 (P).</param>
/// <param name="Action">w1 bit 0: the node's tile holds an object (FUN_00c072d0).</param>
/// <param name="Landing">w1 bit 0x4000000: reached by Instant Travel.</param>
/// <param name="Casts">w1 bits 15..22: Instant Travel starts on the way (at most 10).</param>
/// <param name="Portal">w1 bit 0x20: the route starts with Town Portal (set on the portal start node, inherited by every step from it).</param>
public readonly record struct FloodNode(Tile Tile, int Cost, int Priority, bool CanStop, bool Action, bool Boat, bool Landing, int Casts,
                                        bool Portal = false);

/// <summary>Result of <see cref="Adventure.SpellFlood"/>: the best node of every reached tile and its parent tile.</summary>
public sealed class SpellFloodResult
{
    public required Tile Start { get; init; }
    public Dictionary<Tile, FloodNode> Nodes { get; } = new();
    public Dictionary<Tile, Tile> Parent { get; } = new();

    public Dictionary<Tile, int> Costs() => Nodes.ToDictionary(kv => kv.Key, kv => kv.Value.Cost);

    /// <summary>Route from the start to <paramref name="target"/> (exe FUN_00c07360 walks the parents), or null.</summary>
    public List<FloodNode>? Route(Tile target)
    {
        if (!Nodes.ContainsKey(target)) return null;
        var r = new List<FloodNode>();
        var t = target;
        for (int guard = 0; guard <= Nodes.Count; guard++)
        {
            r.Add(Nodes[t]);
            if (t == Start) { r.Reverse(); return r; }
            if (!Parent.TryGetValue(t, out t)) return null;
        }
        return null;
    }
}

public sealed partial class Adventure
{
    /// <summary>Instant Travel radius: dx² + dy² ≤ 64 on one floor (exe 008652f0 case 0x32 "&lt; 0x41"; flood 00c09d90 "0x40 &lt;").</summary>
    public const int DimensionDoorRadiusSq = 64;

    /// <summary>Exe pathfinder neighbour order (DAT_00f757c8): N, S, W, E, NE, SE, NW, SW; index &gt; 3 is diagonal.</summary>
    static readonly (int dx, int dy)[] FloodDirs = { (0, -1), (0, 1), (-1, 0), (1, 0), (1, -1), (1, 1), (-1, -1), (-1, 1) };

    sealed class FNode
    {
        public Tile T, Origin;
        public bool Layer, CanStop, Action, Landing, Portal, Root;
        public int W2, W3, Casts;
        public Tile? Parent;
    }

    /// <summary>
    /// The pathfinder flood with Instant Travel edges (exe FUN_00c08890 loop, expansion FUN_00c07f80, insert FUN_00c09d90),
    /// for a computer hero (00ceb670 → 00ceb190 with the spell flag 1).
    /// Walking steps add their cost to both cost (w2) and priority (w3); the queue pops the lowest priority.
    /// When a step from a node P (w1 0x200, not 4) to a neighbour is blocked and the settings allow the spell
    /// (+0x56) and the node's cost is within DimDoorStartCostLimit (+0x88, when &gt; 0), the neighbour enters the
    /// spell layer (w1 0x40, its own grid +0xb4) with priority + the spell's movement cost (+0x58) and one more cast
    /// (bits 15..22, refused past 10); the origin is the node it started from (w5). Layer nodes spread to all 8
    /// neighbours for free while dx² + dy² to the origin is ≤ 64; a layer tile already reached keeps its node when
    /// that has fewer casts or an origin at most as far. A popped layer node that is not the origin and passes the
    /// landing check (FUN_008649f0) yields a normal node: the origin's cost, its own priority + 500, flags
    /// 0x4000200 (P, landing) and the origin as parent. A main-grid tile keeps the node with the lower priority and
    /// nodes whose cost exceeds the limit are dropped (00c09d90: +0x40 &lt; cost, when &gt; 0).
    /// Town Portal (00c08be0, pf +0x54): each entry of the start list pf +0x98 (filled by 0086c010 from AI +0xcc via
    /// 00868560: the town 00865170 → 00ccf6e0 picks, i.e. <see cref="PortalTown"/>, when its hero slot is free, 008648b0)
    /// becomes a second start node: a copy of the hero's node at the town's tile with flag 0x20 (inherited by every
    /// step from it), parent the hero's tile, cost w2 = the hero's movement points (start w4), priority w3 = that +
    /// land day move (+0x48) + 200 + (entries − 1) · 500, w4 = land day move; it expands like the hero's node.
    /// GAP-details: the blocked test is our <c>Passable</c> (not the exe's cell types 009d1410 / 009d1700 / type 5,
    /// whose +100 penalty is not applied); single-day costs as in <see cref="ReachableCosts"/>; no boat edges
    /// (settings +0x35); queue ties are taken in insertion order; the town's tile (town vf 0xc4) is taken as its
    /// entrance (<see cref="EntranceOf"/>).
    /// </summary>
    public SpellFloodResult SpellFlood(HeroState h, int limit, DimensionDoorSettings dd)
    {
        var res = new SpellFloodResult { Start = h.Pos };
        var main = new Dictionary<Tile, FNode>();
        var layer = new Dictionary<Tile, FNode>();
        var pq = new PriorityQueue<FNode, (int, int)>();
        int seq = 0;
        var start = new FNode { T = h.Pos, Origin = h.Pos, CanStop = true, Root = true };
        main[h.Pos] = start;
        pq.Enqueue(start, (0, seq++));
        var f = Map.Floor(h.Pos.Floor);
        if (f == null) return Collect(res, main, h.InBoat);

        void Relax(FNode n)
        {
            if (limit > 0 && n.W2 > limit) return;
            if (main.TryGetValue(n.T, out var old) && old.W3 <= n.W3) return;
            main[n.T] = n;
            pq.Enqueue(n, (n.W3, seq++));
        }

        void EnterLayer(FNode n)
        {
            if (layer.TryGetValue(n.T, out var old))
            {
                if (old.Casts < n.Casts) return;
                if (DistSq(n.T, old.Origin) <= DistSq(n.T, n.Origin)) return;
            }
            layer[n.T] = n;
            pq.Enqueue(n, (n.W3, seq++));
        }

        if (dd.TownPortal)
        {
            var portal = PortalStartTiles(h);
            foreach (var t in portal)
                Relax(new FNode { T = t, Origin = t, W2 = h.MovePoints, W3 = dd.LandDayMove + 200 + (portal.Count - 1) * 500 + h.MovePoints,
                                  CanStop = true, Portal = true, Root = true, Parent = h.Pos });
        }

        while (pq.TryDequeue(out var cur, out _))
        {
            if (cur.Layer)
            {
                if (!layer.TryGetValue(cur.T, out var lc) || lc != cur) continue;
                if (cur.T != cur.Origin && DimensionDoorLandingOk(h, cur.T, h.InBoat) && main.TryGetValue(cur.Origin, out var org))
                    Relax(new FNode { T = cur.T, Origin = cur.T, W2 = org.W2, W3 = cur.W3 + 500, Casts = cur.Casts, CanStop = true, Landing = true,
                                      Portal = org.Portal, Parent = cur.Origin });
                foreach (var (dx, dy) in FloodDirs)
                {
                    var nx = cur.T.Offset(dx, dy);
                    if (!f.InBounds(nx.X, nx.Y) || DistSq(nx, cur.Origin) > DimensionDoorRadiusSq) continue;
                    EnterLayer(new FNode { T = nx, Origin = cur.Origin, Layer = true, W2 = cur.W2, W3 = cur.W3, Casts = cur.Casts });
                }
                continue;
            }
            if (!main.TryGetValue(cur.T, out var mc) || mc != cur) continue;
            // As ReachableCosts: guarded tiles and tiles a path may not cross end the walk.
            bool expand = cur.Root || (GuardingMonster(cur.T) == null && Passable(h, cur.T, new Tile(-1, -1, cur.T.Floor)));
            if (!expand) continue;
            bool canStart = dd.Enabled && cur.CanStop && cur.Casts < 10 && (dd.StartCostLimit <= 0 || cur.W2 <= dd.StartCostLimit);
            foreach (var (dx, dy) in FloodDirs)
            {
                var nx = cur.T.Offset(dx, dy);
                if (!f.InBounds(nx.X, nx.Y)) continue;
                if (!Passable(h, nx, nx))
                {
                    if (canStart)
                        EnterLayer(new FNode { T = nx, Origin = cur.T, Layer = true, W2 = cur.W2 + dd.MoveCost, W3 = cur.W3 + dd.MoveCost, Casts = cur.Casts + 1,
                                               Portal = cur.Portal });
                    continue;
                }
                int step = StepCost(h, cur.T, nx);
                bool action = ObjectsAt(nx).Any() || HeroAt(nx, h.Id) != null || BoatAt(nx) != null;
                Relax(new FNode { T = nx, Origin = nx, W2 = cur.W2 + step, W3 = cur.W3 + step, Casts = cur.Casts, Portal = cur.Portal,
                                  CanStop = !action && GuardingMonster(nx) == null, Action = action, Parent = cur.T });
            }
        }
        return Collect(res, main, h.InBoat);
    }

    static SpellFloodResult Collect(SpellFloodResult res, Dictionary<Tile, FNode> main, bool boat)
    {
        // No boarding edges: every node keeps the hero's boat state (w1 bit 1).
        foreach (var (t, n) in main)
        {
            res.Nodes[t] = new FloodNode(t, n.W2, n.W3, n.CanStop, n.Action, boat, n.Landing, n.Casts, n.Portal);
            if (n.Parent is { } p) res.Parent[t] = p;
        }
        return res;
    }

    /// <summary>
    /// The Town Portal start list (exe 00868560 into AI +0xcc, copied to pf +0x98 by 00c076c0): the portal town
    /// (00865170 → 00ccf6e0, <see cref="PortalTown"/>) when its hero slot is free (008648b0: town vf 0x18(0) = 0,
    /// taken as no other hero on its entrance, as the cast). At most one entry.
    /// </summary>
    public List<Tile> PortalStartTiles(HeroState h)
    {
        var l = new List<Tile>();
        if (PortalTown(h) is { } town && EntranceOf(town) is var t && HeroAt(t, h.Id) == null) l.Add(t);
        return l;
    }

    static int DistSq(Tile a, Tile b) { int dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }

    /// <summary>
    /// Instant Travel landing tile (exe FUN_008649f0): land for a walking hero, water for a sailing one, no object,
    /// hero or ship on it, not blocked. Ours also wants the tile explored by the caster's player (not in the exe check).
    /// </summary>
    public bool DimensionDoorLandingOk(HeroState h, Tile t, bool inBoat)
    {
        var f = Map.Floor(t.Floor);
        if (f == null || !f.InBounds(t.X, t.Y) || f.IsBlocked(t.X, t.Y)) return false;
        if (f.IsWater(t.X, t.Y) != inBoat) return false;
        if (HeroAt(t) != null || ObjectsAt(t).Any() || BoatAt(t) != null) return false;
        var p = PlayerByIndex(h.Owner);
        return p != null && t.Floor < p.Explored.Count && p.Explored[t.Floor][t.Y * f.Width + t.X];
    }
}
