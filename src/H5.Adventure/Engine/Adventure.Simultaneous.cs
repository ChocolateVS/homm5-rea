using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>Two players' simultaneous-turn areas meet (CWorld slot 7, 009c2660).</summary>
public sealed record SimultaneousConflict(int AreaOwner, int Intruder, Tile At)
{
    /// <summary>The exe's debug log line (009c20a0).</summary>
    public override string ToString() =>
        $"Simultaneous Turns: area of player {AreaOwner} was intersected at {At.X} {At.Y} {At.Floor} by player {Intruder}";
}

// Simultaneous turns support (docs/rules/SIMULTANEOUS.md). The engine itself stays single-player-
// at-a-time; the network session switches CurrentPlayer between the humans of the phase.
public sealed partial class Adventure
{
    /// <summary>
    /// Optional turn order. Called with the player whose turn ends (or -1 after a new day began);
    /// returns the next player's index into <see cref="AdvState.Players"/>, or -1 to start a new day.
    /// Null keeps the map order.
    /// </summary>
    public Func<int, int>? TurnOrder { get; set; }

    /// <summary>
    /// Simultaneous turns' attack gate (00426b30, first test of the attack 00433440): returns true when an
    /// attack by <c>attacker</c> on a hero (<c>defenderHero</c>) or object/town (<c>defenderObject</c>) must not
    /// happen because the target's owner holds a combat or the target defends a running combat. A refused
    /// attack does nothing (the hero stays where its move stopped). Null: no gate.
    /// </summary>
    public Func<HeroState, int, int, bool>? AttackRefused { get; set; }

    bool Refused(HeroState attacker, int defenderHero, int defenderObject) =>
        AttackRefused?.Invoke(attacker, defenderHero, defenderObject) == true;

    /// <summary>
    /// The original's order (00998060 GiveTurnToNextPlayers): alive human players first, then the
    /// computer players, then a new day. <paramref name="isHuman"/> tells which map players humans control.
    /// </summary>
    public int HumansFirstOrder(int endingIndex, Func<int, bool> isHuman)
    {
        var order = S.Players.Select((p, i) => (p, i)).Where(x => x.p.Alive)
            .OrderBy(x => isHuman(x.p.Index) ? 0 : 1).ThenBy(x => x.i).Select(x => x.i).ToList();
        if (endingIndex < 0) return order.Count > 0 ? order[0] : -1;
        // The ending player may have just been eliminated: find its place in the full ordering.
        var full = S.Players.Select((p, i) => (p, i)).OrderBy(x => isHuman(x.p.Index) ? 0 : 1).ThenBy(x => x.i).Select(x => x.i).ToList();
        int pos = full.IndexOf(endingIndex);
        for (int k = pos + 1; k < full.Count; k++) if (S.Players[full[k]].Alive) return full[k];
        return -1;
    }

    /// <summary>The limit of a hero's area: max daily movement + the largest movement bonus (009c2660, RPGStats adventure +0x18..+0x2c).</summary>
    public int SimultaneousAreaLimit(HeroState h)
    {
        var adv = Db.Stats.Root.Element("adventure");
        int bonus = 0;
        foreach (var f in new[] { "VisitFountainOfYouthMovementBonus", "VisitRallyFlagMovementBonus", "VisitOasisMovementBonus", "VisitStablesMovementBonus", "LighthouseMaxSeaMovementBonus" })
            bonus = Math.Max(bonus, XmlHelpers.Int(adv, f, 0));
        return ComputeMaxMovePoints(h) + bonus;
    }

    /// <summary>
    /// Tiles a hero could reach with at most <paramref name="limit"/> movement points, with their cost
    /// (the pathfinder flood of FUN_00c08be0 as used by 009c20a0). Every tile that could end a move
    /// counts; the flood continues only through tiles a path may cross, and stops at guarded tiles.
    /// TODO(exe): pathfinder mode 6 (FUN_00c076d0) is not decoded; flight/levitation are not modelled.
    /// </summary>
    public Dictionary<Tile, int> ReachableCosts(HeroState h, int limit)
    {
        var dist = new Dictionary<Tile, int> { [h.Pos] = 0 };
        var f = Map.Floor(h.Pos.Floor);
        if (f == null) return dist;
        var pq = new PriorityQueue<Tile, (int, int)>();
        int seq = 0;
        pq.Enqueue(h.Pos, (0, seq++));
        while (pq.TryDequeue(out var cur, out var pri))
        {
            if (pri.Item1 > dist[cur]) continue;
            if (cur != h.Pos && (GuardingMonster(cur) != null || !Passable(h, cur, new Tile(-1, -1, cur.Floor)))) continue;
            foreach (var (dx, dy) in Dirs)
            {
                var nx = cur.Offset(dx, dy);
                if (!Passable(h, nx, nx)) continue;
                int nd = pri.Item1 + StepCost(h, cur, nx);
                if (nd > limit) continue;
                if (dist.TryGetValue(nx, out var od) && od <= nd) continue;
                dist[nx] = nd;
                pq.Enqueue(nx, (nd, seq++));
            }
        }
        return dist;
    }

    /// <summary>
    /// The conflict check (CWorld slot 7, 009c2660): marks the areas of the given human players'
    /// heroes and towns; returns the first tile marked by two different players, or an owned object
    /// standing in another player's area (009c2380). Null when the areas are disjoint.
    /// Read-only: the game state is left unchanged.
    /// </summary>
    public SimultaneousConflict? FindSimultaneousConflict(IReadOnlyCollection<int> humanPlayers)
    {
        bool Playing(int owner) => humanPlayers.Contains(owner) && S.Players.Find(p => p.Index == owner) is { Alive: true };
        var marks = new Dictionary<Tile, int>();
        SimultaneousConflict? Mark(int owner, IEnumerable<Tile> tiles)
        {
            foreach (var t in tiles)
            {
                if (marks.TryGetValue(t, out var o)) { if (o != owner) return new SimultaneousConflict(o, owner, t); }
                else marks[t] = owner;
            }
            return null;
        }

        // 1. Heroes: max daily movement plus the largest movement bonus.
        foreach (var h in S.Heroes.Where(h => h.Alive && Playing(h.Owner)))
            if (Mark(h.Owner, ReachableCosts(h, SimultaneousAreaLimit(h)).Keys) is { } c) return c;

        // 2. Towns: a hero hired there could move as far as the fastest hero of the pool.
        // TODO(exe): FUN_00ce78c0 reads CWorld+0x40's hero list; taken to be the hero pool.
        int savedId = S.NextHeroId;
        try
        {
            foreach (var t in S.Towns.Where(t => Playing(t.Owner)))
            {
                var entrance = EntranceOf(t);
                int limit = 0;
                HeroState? probe = null;
                foreach (var defId in S.HeroPool)
                    if (Core.Heroes.TryGetValue(defId, out var def))
                    {
                        var hh = HeroFromDef(def, t.Owner, entrance);
                        int mp = ComputeMaxMovePoints(hh);
                        if (probe == null || mp > limit) { limit = mp; probe = hh; }
                    }
                probe ??= new HeroState { Owner = t.Owner, Pos = entrance, Town = t.Type };
                probe.Pos = entrance;
                if (Mark(t.Owner, ReachableCosts(probe, limit).Keys) is { } c) return c;
            }
        }
        finally { S.NextHeroId = savedId; }

        // 3. Owned objects (towns, mines, dwellings…) standing in another player's area (009c2380).
        IEnumerable<(int Owner, MapObject O)> owned =
            S.Towns.Where(t => Playing(t.Owner)).Select(t => (t.Owner, O(t.Object)))
            .Concat(S.Objects.Values.Where(o => !o.Removed && Playing(o.Owner) && S.Town(o.Object) == null).Select(o => (o.Owner, O(o.Object))));
        foreach (var (owner, o) in owned)
            foreach (var t in o.BlockedTiles.Concat(o.ActiveTiles).Append(o.Pos))
                if (marks.TryGetValue(t, out var m) && m != owner) return new SimultaneousConflict(m, owner, t);
        return null;
    }
}
