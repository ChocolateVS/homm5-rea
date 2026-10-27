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
/// <param name="SummonBoat">settings +0x35 (pf +0x55): the hero knows SPELL_SUMMON_BOAT (0x31), may cast it and is not at sea.</param>
public readonly record struct DimensionDoorSettings(bool Enabled, int MoveCost, int Count, int StartCostLimit,
                                                    bool TownPortal = false, int LandDayMove = 0, bool SummonBoat = false);

/// <summary>One node of a flood route (exe 0x40-byte node: w0 position, w1 flags, w2 cost, w3 priority).</summary>
/// <param name="Cost">w2: movement cost from the start; an Instant Travel landing keeps its origin's cost.</param>
/// <param name="Priority">w3: the queue key (cost plus the 500 landing penalty and the spell's movement cost).</param>
/// <param name="CanStop">w1 bit 0x200 (P).</param>
/// <param name="Action">w1 bit 0: the node's tile holds an object (FUN_00c072d0).</param>
/// <param name="Landing">w1 bit 0x4000000: reached by Instant Travel.</param>
/// <param name="Casts">w1 bits 15..22: Instant Travel starts on the way (at most 10).</param>
/// <param name="Portal">w1 bit 0x20: the route starts with Town Portal (set on the portal start node, inherited by every step from it).</param>
/// <param name="Summon">w1 bit 0x2000000: the step onto water that needs Vessel of Shalassa.</param>
/// <param name="Late">w1 bit 0x800000: w2 exceeds the hero's movement points (00c09d90: pf +0x50 &lt; w2).</param>
public readonly record struct FloodNode(Tile Tile, int Cost, int Priority, bool CanStop, bool Action, bool Boat, bool Landing, int Casts,
                                        bool Portal = false, bool Summon = false, bool Late = false);

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
        public bool Layer, CanStop, Action, Landing, Portal, Root, Boat, Summon;
        public int W2, W3, W4, Casts;
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
    /// Days (w4, the movement left): the start has the hero's movement points; a step costing more than w4 is
    /// taken from a node that may end a move with a fresh day's movement (land +0x48 / sea +0x4c by the node's boat
    /// state; the step's cost is not taken from it) and is dropped otherwise; else w4 - cost. w1 0x800000 marks
    /// w2 over the hero's movement points. A blocked step onto a map-edge cell (cell type 5, our <c>TileKind.Edge</c>)
    /// adds 100 to the spell-layer priority.
    /// Sea (00c07f80 / 009d22f0, boat provider 00bcc020): a land node that may end a move stepping onto free water
    /// (no object) with Vessel of Shalassa (+0x55) and a free ship on the floor (provider vf 0xc = 00864920 over
    /// the free ships of 00865230) gives a sea node 0x2000002 (not P) at priority + 500, cost w4 (or land day + w4
    /// when w4 is below the step), w4 = sea day; it floods on as a ship. A popped land node on a free ship
    /// (009d22f0: the object's vf 0x38) boards it: w2 + w4, w4 = sea day, boat state, and expands at sea (settings
    /// +0x10 skips the cost, taken as Snatch). A sea node stepping onto land that may end a move lands: cost w4
    /// (or sea day + w4), w4 = land day, land state.
    /// GAP-details: the blocked test is our <c>Passable</c> (not the exe's cell types 009d1410 / 009d1700);
    /// step costs are ours (<see cref="StepCost"/>, not ca9a70 with its diagonal rule); no shipyard edge (provider
    /// vf 0, 0x1000002 +250); queue ties are taken in insertion order; the town's tile (town vf 0xc4) is taken as its
    /// entrance (<see cref="EntranceOf"/>); the main-grid replacement compares w3 only (00c09d90 also weighs the
    /// danger values of pf +0xc).
    /// </summary>
    public SpellFloodResult SpellFlood(HeroState h, int limit, DimensionDoorSettings dd)
    {
        var res = new SpellFloodResult { Start = h.Pos };
        var main = new Dictionary<Tile, FNode>();
        var layer = new Dictionary<Tile, FNode>();
        var pq = new PriorityQueue<FNode, (int, int)>();
        int seq = 0;
        var start = new FNode { T = h.Pos, Origin = h.Pos, CanStop = true, Root = true, Boat = h.InBoat, W4 = h.MovePoints };
        main[h.Pos] = start;
        pq.Enqueue(start, (0, seq++));
        var f = Map.Floor(h.Pos.Floor);
        int landDay = DayMove(h, false), seaDay = DayMove(h, true);
        bool snatch = h.HasPerk(SnatchPerk);
        bool freeShip = dd.SummonBoat && NearestEmptyBoat(h) != null;
        if (f == null) return Collect(res, main, h.MovePoints);

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
                                  W4 = dd.LandDayMove, Boat = start.Boat, CanStop = true, Portal = true, Root = true, Parent = h.Pos });
        }

        while (pq.TryDequeue(out var cur, out _))
        {
            if (cur.Layer)
            {
                if (!layer.TryGetValue(cur.T, out var lc) || lc != cur) continue;
                if (cur.T != cur.Origin && DimensionDoorLandingOk(h, cur.T, cur.Boat) && main.TryGetValue(cur.Origin, out var org))
                    Relax(new FNode { T = cur.T, Origin = cur.T, W2 = org.W2, W3 = cur.W3 + 500, W4 = cur.W4, Casts = cur.Casts, CanStop = true,
                                      Landing = true, Portal = org.Portal, Boat = cur.Boat, Parent = cur.Origin });
                foreach (var (dx, dy) in FloodDirs)
                {
                    var nx = cur.T.Offset(dx, dy);
                    if (!f.InBounds(nx.X, nx.Y) || DistSq(nx, cur.Origin) > DimensionDoorRadiusSq) continue;
                    EnterLayer(new FNode { T = nx, Origin = cur.Origin, Layer = true, W2 = cur.W2, W3 = cur.W3, W4 = cur.W4, Casts = cur.Casts,
                                           Boat = cur.Boat, Portal = cur.Portal });
                }
                continue;
            }
            if (!main.TryGetValue(cur.T, out var mc) || mc != cur) continue;
            bool wasBoat = h.InBoat;
            try { Expand(cur); }
            finally { h.InBoat = wasBoat; }
        }
        return Collect(res, main, h.MovePoints);

        void Expand(FNode cur)
        {
            // Passable / StepCost read the hero's boat state: the node's (w1 bit 2) while it expands.
            h.InBoat = cur.Boat;
            int w2 = cur.W2, w4 = cur.W4;
            bool boat = cur.Boat;
            if (!cur.Root)
            {
                if (GuardingMonster(cur.T) != null) return;
                // 009d22f0: a land node on a free ship boards it and floods on at sea.
                if (!boat && BoatAt(cur.T) is { HeroId: -1 })
                {
                    if (!snatch) { w2 += w4; w4 = seaDay; }
                    boat = true;
                    cur.Boat = true;   // 0059e980: the stored node gets w1 | 2 too
                    h.InBoat = true;
                }
                // As ReachableCosts: tiles a path may not cross end the walk.
                else if (!Passable(h, cur.T, new Tile(-1, -1, cur.T.Floor))) return;
            }
            bool canStart = dd.Enabled && cur.CanStop && cur.Casts < 10 && (dd.StartCostLimit <= 0 || w2 <= dd.StartCostLimit);
            foreach (var (dx, dy) in FloodDirs)
            {
                var nx = cur.T.Offset(dx, dy);
                if (!f.InBounds(nx.X, nx.Y)) continue;
                bool water = f.IsWater(nx.X, nx.Y);
                bool action = ObjectsAt(nx).Any() || HeroAt(nx, h.Id) != null || BoatAt(nx) != null;
                // 00c07f80: Vessel of Shalassa onto free water from a land node that may end a move.
                if (!boat && water && freeShip && cur.CanStop && !action && !f.IsBlocked(nx.X, nx.Y) && GuardingMonster(nx) == null)
                {
                    int c = StepCost(h, cur.T, nx);
                    c = w4 < c ? landDay + w4 : w4;
                    Relax(new FNode { T = nx, Origin = nx, W2 = w2 + c, W3 = cur.W3 + c + 500, W4 = seaDay, Casts = cur.Casts, Portal = cur.Portal,
                                      Boat = true, Summon = true, Parent = cur.T });
                    continue;
                }
                if (!Passable(h, nx, nx))
                {
                    if (canStart)
                    {
                        int edge = f.Kind.Length == f.Width * f.Height && f.Kind[nx.Y * f.Width + nx.X] == TileKind.Edge ? 100 : 0;
                        EnterLayer(new FNode { T = nx, Origin = cur.T, Layer = true, W2 = w2 + dd.MoveCost, W3 = cur.W3 + edge + dd.MoveCost, W4 = w4,
                                               Casts = cur.Casts + 1, Portal = cur.Portal, Boat = boat });
                    }
                    continue;
                }
                int step = StepCost(h, cur.T, nx);
                bool canStop = !action && GuardingMonster(nx) == null;
                // 00c07f80: from a ship only cell type 1 is a shore; another kind is blocked.
                if (boat && !water && f.Kind.Length == f.Width * f.Height && f.Kind[nx.Y * f.Width + nx.X] != TileKind.Land)
                {
                    if (canStart)
                        EnterLayer(new FNode { T = nx, Origin = cur.T, Layer = true, W2 = w2 + dd.MoveCost, W3 = cur.W3 + dd.MoveCost, W4 = w4,
                                               Casts = cur.Casts + 1, Portal = cur.Portal, Boat = boat });
                    continue;
                }
                int n4;
                if (w4 < step)
                {
                    if (!cur.CanStop) continue;
                    n4 = boat ? seaDay : landDay;
                }
                else n4 = w4 - step;
                bool nBoat = boat;
                // A sea node landing on a tile that may end a move: the rest of the day (or a day more).
                if (boat && !water && canStop)
                {
                    step = w4 < step ? seaDay + w4 : w4;
                    n4 = landDay;
                    nBoat = false;
                }
                Relax(new FNode { T = nx, Origin = nx, W2 = w2 + step, W3 = cur.W3 + step, W4 = n4, Casts = cur.Casts, Portal = cur.Portal,
                                  CanStop = canStop, Action = action, Boat = nBoat, Parent = cur.T });
            }
        }
    }

    static SpellFloodResult Collect(SpellFloodResult res, Dictionary<Tile, FNode> main, int movePoints)
    {
        foreach (var (t, n) in main)
        {
            res.Nodes[t] = new FloodNode(t, n.W2, n.W3, n.CanStop, n.Action, n.Boat, n.Landing, n.Casts, n.Portal, n.Summon, n.W2 > movePoints);
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

    /// <summary>The day's movement on land or at sea (settings +0x28 / +0x2c, hero vf 0xf8 by move type).</summary>
    int DayMove(HeroState h, bool sea)
    {
        bool was = h.InBoat;
        h.InBoat = sea;
        try { return ComputeMaxMovePoints(h); }
        finally { h.InBoat = was; }
    }

    static int DistSq(Tile a, Tile b) { int dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }

    /// <summary>
    /// Instant Travel landing tile (exe FUN_008649f0): cell +0x10 (object kind) 0 (our not blocked), cell type 1 / 4
    /// (<c>TileKind.Land</c> / <c>ObjectPassable</c>) for a walking hero or 2 (<c>Water</c>) for a sailing one, no object,
    /// hero or ship on it. No exploration test (was: the caster's player had to have explored the tile).
    /// </summary>
    public bool DimensionDoorLandingOk(HeroState h, Tile t, bool inBoat)
    {
        var f = Map.Floor(t.Floor);
        if (f == null || !f.InBounds(t.X, t.Y) || f.IsBlocked(t.X, t.Y)) return false;
        if (f.Kind.Length == f.Width * f.Height)
        {
            var k = f.Kind[t.Y * f.Width + t.X];
            if (inBoat ? k != TileKind.Water : k is not (TileKind.Land or TileKind.ObjectPassable)) return false;
        }
        else if (f.IsWater(t.X, t.Y) != inBoat) return false;
        return HeroAt(t) == null && !ObjectsAt(t).Any() && BoatAt(t) == null;
    }
}
