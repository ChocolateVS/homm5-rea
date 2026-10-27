using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.AI;

// Sea travel for the computer players, used only when no land destination is reachable.
// Reconstructed (the exe's AI boat logic is not traced): a land hero walks to the nearest empty
// ship it can reach; a sailing hero picks the most valuable object it cannot reach on foot and
// sails to the free shore tile closest to it (landing there ends the move, as for humans).
public sealed partial class OriginalAdventureAI
{
    const int MaxSeaCandidates = 8;

    Destination? SeaTravel(Engine.Adventure g, HeroState h, double myPower, AdventureDifficulty diff, AdventureAiStats st, HashSet<int> claimed)
    {
        if (!h.InBoat)
        {
            var boat = g.S.Boats.Where(b => b.HeroId < 0 && b.Pos.Floor == h.Pos.Floor)
                .OrderBy(b => Math.Max(Math.Abs(b.Pos.X - h.Pos.X), Math.Abs(b.Pos.Y - h.Pos.Y))).ThenBy(b => b.Id)
                .FirstOrDefault(b => g.FindPath(h, b.Pos) != null);
            return boat == null ? null : new Destination(-1, boat.Pos, 1);
        }
        var floor = g.Map.Floor(h.Pos.Floor);
        if (floor == null) return null;
        var candidates = new List<(MapObject O, double Score)>();
        foreach (var o in g.Map.Objects)
        {
            if (o.Pos.Floor != h.Pos.Floor || o.Kind == ObjectKind.Static || claimed.Contains(o.Index)) continue;
            if (g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            var tile0 = o.ActiveTiles.FirstOrDefault(o.Pos);
            var (state0, v0) = ValueOf(g, h, o, os, tile0, int.MaxValue, diff, st);
            double value = state0 < 0 ? 0 : v0;
            if (value <= 0) continue;
            int d = Math.Max(Math.Abs(o.Pos.X - h.Pos.X), Math.Abs(o.Pos.Y - h.Pos.Y));
            candidates.Add((o, value / (1 + d * st.SearchAverageTerrainCost / (double)Math.Max(100, h.MaxMovePoints))));
        }
        foreach (var (o, score) in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.O.Index).Take(MaxSeaCandidates))
        {
            var target = o.ActiveTiles.FirstOrDefault(o.Pos);
            var landing = NearestLanding(g, floor, target);
            if (landing is { } l && g.FindPath(h, l) != null) return new Destination(o.Index, l, score);
        }
        return null;
    }

    /// <summary>
    /// The free land tile next to the sea that is closest to <paramref name="t"/>. Tiles are tested with the
    /// pathfinder's passability rule 009d1410 (<c>StaticallyEnterable</c>: the object map's blocked set, then live
    /// objects' action tiles, then the terrain), as the move itself (<c>FindPath</c>) does.
    /// </summary>
    static Tile? NearestLanding(Engine.Adventure g, MapFloor f, Tile t)
    {
        for (int r = 0; r <= 12; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var c = t.Offset(dx, dy);
                    if (!f.InBounds(c.X, c.Y) || f.IsWater(c.X, c.Y) || !g.StaticallyEnterable(f, c)) continue;
                    if (g.S.Heroes.Any(x => x.Alive && x.Pos == c) || g.GuardingMonster(c) != null) continue;
                    for (int ny = -1; ny <= 1; ny++)
                        for (int nx = -1; nx <= 1; nx++)
                        {
                            var n = c.Offset(nx, ny);
                            if (f.InBounds(n.X, n.Y) && f.IsWater(n.X, n.Y) && g.StaticallyEnterable(f, n)) return c;
                        }
                }
        }
        return null;
    }
}
