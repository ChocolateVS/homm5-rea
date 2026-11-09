using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    // ------------------------------------------------------------------ occupancy

    /// <summary>Visitable objects indexed by their active (entrance) tiles; built lazily from the static map.</summary>
    Dictionary<Tile, List<int>>? _activeIndex;
    Dictionary<Tile, List<int>> ActiveIndex
    {
        get
        {
            if (_activeIndex != null) return _activeIndex;
            _activeIndex = new();
            foreach (var o in Map.Objects)
            {
                if (o.Kind == ObjectKind.Static) continue;
                foreach (var t in o.ActiveTiles)
                {
                    if (!_activeIndex.TryGetValue(t, out var l)) _activeIndex[t] = l = new List<int>();
                    l.Add(o.Index);
                }
            }
            return _activeIndex;
        }
    }

    /// <summary>
    /// A tile index as per-floor arrays holding the same lists (in-bounds tiles; others go to the dictionary), rebuilt
    /// whenever the index it was made from is replaced (the scripts drop the indexes when they add or move objects).
    /// </summary>
    sealed class TileGrid(Dictionary<Tile, List<int>> source, AdvMap map)
    {
        public readonly Dictionary<Tile, List<int>> Source = source;
        readonly List<int>?[]?[] _cells = Build(source, map);
        readonly int[] _width = map.Floors.Select(f => f?.Width ?? 0).ToArray(), _height = map.Floors.Select(f => f?.Height ?? 0).ToArray();

        static List<int>?[]?[] Build(Dictionary<Tile, List<int>> source, AdvMap map)
        {
            var cells = new List<int>?[]?[map.Floors.Length];
            for (int fl = 0; fl < cells.Length; fl++) if (map.Floors[fl] is { } f) cells[fl] = new List<int>?[f.Width * f.Height];
            foreach (var (t, l) in source)
                if (t.Floor >= 0 && t.Floor < cells.Length && map.Floors[t.Floor] is { } f && f.InBounds(t.X, t.Y)) cells[t.Floor]![t.Y * f.Width + t.X] = l;
            return cells;
        }

        public List<int>? At(Tile t)
        {
            if (t.Floor >= 0 && t.Floor < _cells.Length && _cells[t.Floor] is { } c && (uint)t.X < (uint)_width[t.Floor] && (uint)t.Y < (uint)_height[t.Floor])
                return c[t.Y * _width[t.Floor] + t.X];
            return Source.TryGetValue(t, out var l) ? l : null;
        }
    }

    TileGrid? _activeGrid, _actionGrid;

    /// <summary>The active-index list of a tile (null when none).</summary>
    List<int>? ActiveAt(Tile t)
    {
        var src = ActiveIndex;
        if (_activeGrid == null || !ReferenceEquals(_activeGrid.Source, src)) _activeGrid = new TileGrid(src, Map);
        return _activeGrid.At(t);
    }

    /// <summary>The action-index list of a tile (null when none).</summary>
    List<int>? ActionAt(Tile t)
    {
        var src = ActionIndex;
        if (_actionGrid == null || !ReferenceEquals(_actionGrid.Source, src)) _actionGrid = new TileGrid(src, Map);
        return _actionGrid.At(t);
    }

    IEnumerable<MapObject> ObjectsAt(Tile t) =>
        ActiveAt(t) is { } l ? l.Where(i => !(S.Objects.TryGetValue(i, out var os) && os.Removed)).Select(O) : Enumerable.Empty<MapObject>();

    /// <summary><c>ObjectsAt(t).Any()</c> without the enumerator.</summary>
    bool AnyObjectAt(Tile t)
    {
        if (ActiveAt(t) is not { } l) return false;
        foreach (int i in l) if (!(S.Objects.TryGetValue(i, out var os) && os.Removed)) return true;
        return false;
    }

    /// <summary>Objects indexed by the tiles the pathfinder's object map registers for them (<see cref="MapObject.ActionTiles"/>).</summary>
    Dictionary<Tile, List<int>>? _actionIndex;
    Dictionary<Tile, List<int>> ActionIndex
    {
        get
        {
            if (_actionIndex != null) return _actionIndex;
            _actionIndex = new();
            foreach (var o in Map.Objects)
            {
                // TODO(exe): which kinds the floor lists (vf 0x38) hold and what vf 0x100 / vf 0x28 exclude is not traced;
                // statics and heroes (moving, met through HeroAt) are left out.
                if (o.Kind is ObjectKind.Static or ObjectKind.Hero) continue;
                foreach (var t in o.ActionTiles)
                {
                    if (!_actionIndex.TryGetValue(t, out var l)) _actionIndex[t] = l = new List<int>();
                    l.Add(o.Index);
                }
            }
            return _actionIndex;
        }
    }

    /// <summary>
    /// H5_Game.exe 009d1410: a tile in the object map's blocked set (0045d670) is refused; else a tile holding a live
    /// object's action tile (0045d690) is enterable whatever the terrain kind or ground type; else the static rule.
    /// </summary>
    internal bool StaticallyEnterable(MapFloor f, Tile t)
    {
        int i = t.Y * f.Width + t.X;
        if (i < f.ObjectBlocked.Length && f.ObjectBlocked[i]) return false;
        if (ActionAt(t) is { } l)
            foreach (int o in l) if (!(S.Objects.TryGetValue(o, out var os) && os.Removed)) return true;
        return !f.IsBlocked(t.X, t.Y);
    }

    public TownState? TownAt(Tile t) => S.Towns.Find(tn => O(tn.Object).ActiveTiles.Contains(t) || tn.Pos == t);

    public Tile EntranceOf(TownState t) => O(t.Object).ActiveTiles.FirstOrDefault(t.Pos);

    HeroState? HeroAt(Tile t, int exceptId = -1) =>
        S.Heroes.Find(h => h.Alive && h.Id != exceptId && h.Pos == t && !(h.InTown >= 0 && h.Garrisoned));

    MapObject? MonsterAt(Tile t)
    {
        if (ActiveAt(t) is not { } l) return null;
        foreach (int i in l)
        {
            if (S.Objects.TryGetValue(i, out var os) && os.Removed) continue;
            var o = O(i);
            if (o.Kind == ObjectKind.Monster) return o;
        }
        return null;
    }

    /// <summary>
    /// Neutral monsters guard the eight tiles around them: stepping next to one starts a fight.
    /// TODO(exe): confirm H5's guard-zone rule (inherited from earlier Heroes games).
    /// </summary>
    public MapObject? GuardingMonster(Tile t)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (MonsterAt(t.Offset(dx, dy)) is { } m) return m;
        return null;
    }

    // ------------------------------------------------------------------ movement costs

    static readonly Dictionary<string, TerrainClass[]> Native = new()
    {
        ["TOWN_HEAVEN"] = new[] { TerrainClass.Grass },
        ["TOWN_PRESERVE"] = new[] { TerrainClass.Grass },
        ["TOWN_NECROMANCY"] = new[] { TerrainClass.Dirt },
        ["TOWN_INFERNO"] = new[] { TerrainClass.Lava },
        ["TOWN_ACADEMY"] = new[] { TerrainClass.Sand },
        ["TOWN_FORTRESS"] = new[] { TerrainClass.Snow, TerrainClass.DwarvenSubterranean },
        ["TOWN_STRONGHOLD"] = new[] { TerrainClass.Taiga },
        ["TOWN_DUNGEON"] = new[] { TerrainClass.Subterranean },
    };

    /// <summary>Terrain penalty (fan manual p.299): 0, 25% or 50%; roads are a 25% bonus.</summary>
    static double Penalty(TerrainClass c) => c switch
    {
        TerrainClass.Dirt or TerrainClass.Lava or TerrainClass.Taiga or TerrainClass.Subterranean => 0.25,
        TerrainClass.Sand or TerrainClass.Snow => 0.5,
        _ => 0,
    };

    /// <summary>
    /// Cost of stepping onto <paramref name="to"/>: 100 orthogonal / 141 diagonal, times the destination
    /// terrain factor; no penalty on the hero faction's native terrain, half penalty with Pathfinding,
    /// none with Boots of the Open Road; roads cost 75/105 (p.299-300). At sea it is always 100/141.
    /// TODO(exe): confirm the destination tile (not the source) sets the cost.
    /// </summary>
    public int StepCost(HeroState h, Tile from, Tile to)
    {
        bool diag = from.X != to.X && from.Y != to.Y;
        int baseCost = diag ? 141 : 100;
        if (h.InBoat) return baseCost;
        var f = Map.Floor(to.Floor);
        if (f == null) return int.MaxValue;
        var c = f.At(to.X, to.Y);
        if (c == TerrainClass.Road) return (int)(baseCost * 0.75);
        double pen = Penalty(c);
        if (Native.TryGetValue(h.Town, out var nat) && nat.Contains(c)) pen = 0;
        if (h.HasArtifact("WAYFARER_BOOTS")) pen = 0;
        else if (h.HasPerk("HERO_SKILL_PATHFINDING")) pen *= 0.5;
        return (int)(baseCost * (1 + pen));
    }

    bool Passable(HeroState h, Tile t, Tile target)
    {
        var f = Map.Floor(t.Floor);
        if (f == null || !f.InBounds(t.X, t.Y)) return false;
        if (!StaticallyEnterable(f, t)) return false;
        if (ScriptHooks != null && ScriptHooks.IsTileBlocked(h.Owner, t)) return false;
        bool water = f.IsWater(t.X, t.Y);
        if (water != h.InBoat && (t != target || !CanCrossShoreTo(h, t))) return false;
        if (t == target) return true;
        // Ships block the way like heroes do (boarding one is only possible as the final step).
        if (BoatAt(t) != null) return false;
        // Intermediate tiles must be free of heroes, monsters and visitable objects.
        if (HeroAt(t, h.Id) != null) return false;
        if (AnyObjectAt(t)) return false;
        return true;
    }

    static readonly (int dx, int dy)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    /// <summary>Cheapest path (Dijkstra over 8-connected tiles) from the hero to a target tile, or null.</summary>
    public List<Tile>? FindPath(HeroState h, Tile target)
    {
        if (target.Floor != h.Pos.Floor) return null;
        var f = Map.Floor(target.Floor);
        if (f == null || !f.InBounds(target.X, target.Y)) return null;
        if (!f.InBounds(h.Pos.X, h.Pos.Y)) return FindPathSlow(h, target);
        // The same Dijkstra on pooled per-tile arrays (FloodScratch): the queue sees the same operations in the same order,
        // so ties break as before; the tile tests are memoised (the state does not change during the search).
        var fs = FloodScratch.Rent(f.Width * f.Height);
        try
        {
            int w = f.Width, fl = target.Floor;
            var cost = new StepCoster(this, h);
            int start = h.Pos.Y * w + h.Pos.X, goal = target.Y * w + target.X;
            fs.Set(start, 0);
            var pq = fs.Queue;
            int seq = 0;
            pq.Enqueue(start, (0, seq++));
            while (pq.TryDequeue(out int ci, out var pri))
            {
                if (ci == goal) break;
                if (pri.Item1 > fs.Dist[ci]) continue;
                var cur = new Tile(ci % w, ci / w, fl);
                // Stepping into a guarded tile ends the move there: don't route through it
                // (except onto the target itself, e.g. the monster being attacked).
                bool guarded = ci != start && fs.Guarded(ci, this, cur);
                foreach (var (dx, dy) in Dirs)
                {
                    var nx = cur.Offset(dx, dy);
                    if (guarded && nx != target) continue;
                    if (!f.InBounds(nx.X, nx.Y)) continue;
                    int ni = nx.Y * w + nx.X;
                    if (!fs.Enter(ni, this, h, nx, target)) continue;
                    int nd = pri.Item1 + cost.Cost(cur, nx);
                    if (fs.Has(ni) && fs.Dist[ni] <= nd) continue;
                    fs.Set(ni, nd);
                    fs.Prev[ni] = ci;
                    pq.Enqueue(ni, (nd, seq++));
                }
            }
            if (!fs.Has(goal)) return null;
            var path = new List<Tile> { target };
            for (int k = goal; k != start;) { k = fs.Prev[k]; path.Add(new Tile(k % w, k / w, fl)); }
            path.Reverse();
            return path;
        }
        finally { FloodScratch.Return(fs); }
    }

    /// <summary>The dictionary form of <see cref="FindPath"/>, kept for a hero standing outside its floor.</summary>
    List<Tile>? FindPathSlow(HeroState h, Tile target)
    {
        if (target.Floor != h.Pos.Floor) return null;
        var f = Map.Floor(target.Floor);
        if (f == null || !f.InBounds(target.X, target.Y)) return null;
        int w = f.Width;
        var dist = new Dictionary<Tile, int> { [h.Pos] = 0 };
        var prev = new Dictionary<Tile, Tile>();
        var pq = new PriorityQueue<Tile, (int, int)>();
        int seq = 0;
        pq.Enqueue(h.Pos, (0, seq++));
        while (pq.TryDequeue(out var cur, out var pri))
        {
            if (cur == target) break;
            if (pri.Item1 > dist[cur]) continue;
            // Stepping into a guarded tile ends the move there: don't route through it
            // (except onto the target itself, e.g. the monster being attacked).
            bool guarded = cur != h.Pos && GuardingMonster(cur) != null;
            foreach (var (dx, dy) in Dirs)
            {
                var nx = cur.Offset(dx, dy);
                if (guarded && nx != target) continue;
                if (!Passable(h, nx, target)) continue;
                int nd = pri.Item1 + StepCost(h, cur, nx);
                if (dist.TryGetValue(nx, out var od) && od <= nd) continue;
                dist[nx] = nd;
                prev[nx] = cur;
                pq.Enqueue(nx, (nd, seq++));
            }
        }
        if (!dist.ContainsKey(target)) return null;
        var path = new List<Tile> { target };
        while (path[^1] != h.Pos) path.Add(prev[path[^1]]);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// <see cref="StepCost"/> with the hero's constant parts (boat, native terrain, Boots of the Open Road, Pathfinding)
    /// taken once; the same expression, so the same result.
    /// </summary>
    readonly struct StepCoster
    {
        readonly bool _boat, _noPenalty, _halfPenalty;
        readonly TerrainClass[]? _native;
        readonly AdvMap _map;

        public StepCoster(Adventure g, HeroState h)
        {
            _map = g.Map;
            _boat = h.InBoat;
            _native = Native.TryGetValue(h.Town, out var nat) ? nat : null;
            _noPenalty = h.HasArtifact("WAYFARER_BOOTS");
            _halfPenalty = !_noPenalty && h.HasPerk("HERO_SKILL_PATHFINDING");
        }

        public int Cost(Tile from, Tile to)
        {
            bool diag = from.X != to.X && from.Y != to.Y;
            int baseCost = diag ? 141 : 100;
            if (_boat) return baseCost;
            var f = _map.Floor(to.Floor);
            if (f == null) return int.MaxValue;
            var c = f.At(to.X, to.Y);
            if (c == TerrainClass.Road) return (int)(baseCost * 0.75);
            double pen = Penalty(c);
            if (_native != null && _native.Contains(c)) pen = 0;
            if (_noPenalty) pen = 0;
            else if (_halfPenalty) pen *= 0.5;
            return (int)(baseCost * (1 + pen));
        }
    }

    /// <summary>
    /// Pooled per-tile arrays of one flood: distance / parent (valid while the tile's stamp is the current generation) and
    /// the memoised tile tests (<see cref="Passable"/> as an end tile, the "may cross" test, the guard test). A flood never
    /// changes the game state, so a test answers the same every time the flood asks it.
    /// </summary>
    internal sealed class FloodScratch
    {
        public int[] Dist = Array.Empty<int>(), Prev = Array.Empty<int>();
        int[] _stamp = Array.Empty<int>(), _memoStamp = Array.Empty<int>();
        // Per tile: bits 0 / 1 the end-tile test known / value, bits 2 / 3 the cross test, bits 4 / 5 the guard test.
        byte[] _memo = Array.Empty<byte>();
        int _gen;
        public readonly PriorityQueue<int, (int, int)> Queue = new();

        [ThreadStatic] static Stack<FloodScratch>? _pool;

        public static FloodScratch Rent(int n)
        {
            var pool = _pool ??= new();
            var fs = pool.Count > 0 ? pool.Pop() : new FloodScratch();
            if (fs.Dist.Length < n)
            {
                fs.Dist = new int[n]; fs.Prev = new int[n]; fs._stamp = new int[n]; fs._memoStamp = new int[n]; fs._memo = new byte[n];
                fs._gen = 0;
            }
            if (++fs._gen == int.MaxValue) { Array.Clear(fs._stamp); Array.Clear(fs._memoStamp); fs._gen = 1; }
            fs.Queue.Clear();
            return fs;
        }

        public static void Return(FloodScratch fs) => (_pool ??= new()).Push(fs);

        public bool Has(int i) => _stamp[i] == _gen;
        public void Set(int i, int d) { _stamp[i] = _gen; Dist[i] = d; }

        byte Memo(int i)
        {
            if (_memoStamp[i] != _gen) { _memoStamp[i] = _gen; _memo[i] = 0; }
            return _memo[i];
        }

        /// <summary><c>Passable(h, t, target)</c>, memoised (the target is fixed for the flood).</summary>
        public bool Enter(int i, Adventure g, HeroState h, Tile t, Tile target)
        {
            byte m = Memo(i);
            if ((m & 1) != 0) return (m & 2) != 0;
            bool r = g.Passable(h, t, target);
            _memo[i] = (byte)(m | 1 | (r ? 2 : 0));
            return r;
        }

        /// <summary><c>Passable(h, t, (−1, −1))</c> (a tile a path may cross), memoised.</summary>
        public bool Cross(int i, Adventure g, HeroState h, Tile t)
        {
            byte m = Memo(i);
            if ((m & 4) != 0) return (m & 8) != 0;
            bool r = g.Passable(h, t, new Tile(-1, -1, t.Floor));
            _memo[i] = (byte)(m | 4 | (r ? 8 : 0));
            return r;
        }

        /// <summary><c>GuardingMonster(t) != null</c>, memoised.</summary>
        public bool Guarded(int i, Adventure g, Tile t)
        {
            byte m = Memo(i);
            if ((m & 16) != 0) return (m & 32) != 0;
            bool r = g.GuardingMonster(t) != null;
            _memo[i] = (byte)(m | 16 | (r ? 32 : 0));
            return r;
        }
    }

    string? DoMove(MoveHeroCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || h.Owner != Current.Index) return "not your hero";
        if (h.PendingLevelUps.Count > 0) return "resolve the level-up first";
        if (c.To == h.Pos) return VisitTile(h, h.Pos) ? null : "nothing to do here";
        var path = FindPath(h, c.To);
        if (path == null) return "no path";

        var walked = new List<Tile> { h.Pos };
        for (int i = 1; i < path.Count; i++)
        {
            var next = path[i];
            int cost = StepCost(h, h.Pos, next);
            if (h.MovePoints < cost) break;
            bool isTarget = i == path.Count - 1;

            // Blocking interactions on the target tile happen without moving onto it.
            if (isTarget && (HeroAt(next, h.Id) is { } other))
            {
                MoveStep(h, walked);
                return MeetHero(h, other);
            }
            if (isTarget && IsWaterTile(next) != h.InBoat && TryShoreStep(h, next, walked, cost))
                return null;
            if (isTarget && MonsterAt(next) is { } mon)
            {
                MoveStep(h, walked);
                StartNeutralEncounter(h, mon);
                return null;
            }

            h.MovePoints -= cost;
            h.Pos = next;
            SyncBoat(h);
            walked.Add(next);
            if (h.InTown >= 0 && TownAt(next)?.Object != h.InTown) h.InTown = -1;
            RevealAround(h);

            if (!isTarget && ScriptHooks != null && ScriptHooks.StopMovementAt(h, path[i - 1], next))
            {
                MoveStep(h, walked);
                return null;
            }
            if (GuardingMonster(next) is { } guard)
            {
                MoveStep(h, walked);
                StartNeutralEncounter(h, guard);
                return null;
            }
            if (isTarget)
            {
                MoveStep(h, walked);
                VisitTile(h, next);
                return null;
            }
        }
        if (walked.Count == 1) return "not enough movement points";
        MoveStep(h, walked);
        return null;
    }

    void MoveStep(HeroState h, List<Tile> walked)
    {
        if (walked.Count > 1) Emit(new HeroMoved(h.Id, walked.ToList(), h.MovePoints));
    }

    string? MeetHero(HeroState h, HeroState other)
    {
        if (Allied(h.Owner, other.Owner))
        {
            Emit(new ObjectVisited(h.Id, -1, $"meet:{other.Id}"));
            return null;
        }
        if (Refused(h, other.Id, -1)) return null;
        StartBattle(h, other.Id, -1, other.Pos, other.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList(), siege: false);
        return null;
    }

    // ------------------------------------------------------------------ vision

    /// <summary>Hero look range: 12 tiles (DefaultStats BaseHeroLookRange), +4 with Scouting (HeroSkills).</summary>
    public int LookRange(HeroState h)
    {
        var adv = Db.Stats.Root.Element("adventure");
        int r = H5.Core.Data.XmlHelpers.Int(adv, "BaseHeroLookRange", 12);
        if (h.HasPerk("HERO_SKILL_SCOUTING"))
            r += H5.Core.Data.XmlHelpers.Int(adv?.Element("HeroSkills"), "Scouting_VisibilityIncrease", 4);
        return r;
    }

    void RevealAround(HeroState h) => Reveal(PlayerByIndex(h.Owner), h.Pos, LookRange(h));

    void RevealOwned(PlayerState p)
    {
        foreach (var h in S.Heroes.Where(h => h.Alive && h.Owner == p.Index)) Reveal(p, h.Pos, LookRange(h), emit: false);
        // TODO(exe): exact town/mine sight radii; towns use the hero radius here.
        foreach (var t in S.Towns.Where(t => t.Owner == p.Index)) Reveal(p, t.Pos, 12, emit: false);
    }

    void Reveal(PlayerState? p, Tile c, int r, bool emit = true)
    {
        if (p == null) return;
        var f = Map.Floor(c.Floor);
        if (f == null || c.Floor >= p.Explored.Count) return;
        var ex = p.Explored[c.Floor];
        int n = 0;
        for (int y = Math.Max(0, c.Y - r); y <= Math.Min(f.Height - 1, c.Y + r); y++)
            for (int x = Math.Max(0, c.X - r); x <= Math.Min(f.Width - 1, c.X + r); x++)
            {
                int dx = x - c.X, dy = y - c.Y;
                if (dx * dx + dy * dy > r * r) continue;
                int i = y * f.Width + x;
                if (!ex[i]) { ex[i] = true; n++; }
            }
        if (emit && n > 0) Emit(new TilesRevealed(p.Index, c.Floor, n));
    }
}
