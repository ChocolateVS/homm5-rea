using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>Dig for the Tear of Asha on the hero's tile. Exe: CDigCmd (vtable 0xe0dda8, execute 0x4277a0 → world slot +0x114 = 009920a0).</summary>
public sealed record DigCmd(int HeroId) : AdvCommand;

// Tear of Asha (grail), obelisks and the puzzle map.
public sealed partial class Adventure
{
    public const string GrailArtifact = "GRAAL";
    /// <summary>Pieces of every PuzzleDesc (UI/PuzzleMap/*.(PuzzleDesc).xdb; 0x24 in 00a7f8c0).</summary>
    public const int PuzzlePieces = 36;
    /// <summary>Chebyshev reach of a dig: 0, or 2 with Tear of Asha Vision (009920a0: skill 0x50 → 2).</summary>
    const int GrailVisionReach = 2;
    /// <summary>Direction hints are given within this many tiles (00a7f8f0: |dx|, |dy| ≤ 5, same floor).</summary>
    const int GrailHintRange = 5;

    /// <summary>
    /// The Tear of Asha (map init 009960e0): the map editor places Graal artifact objects; one of them, picked at
    /// random, is where it is buried, and all of them are taken off the map. A map without one has no Tear of Asha
    /// (and its obelisks uncover nothing).
    /// </summary>
    void PlaceGrail()
    {
        var spots = Map.Objects.Where(o => o.Kind == ObjectKind.Artifact && o.ArtifactId == GrailArtifact).ToList();
        if (spots.Count == 0) return;
        S.Grail = spots[S.Rng.Next(spots.Count)].Pos;
        foreach (var o in spots) S.Obj(o.Index).Removed = true;
    }

    /// <summary>
    /// Puzzle pieces the player has uncovered (00a7f8c0): with N obelisks on the map, each obelisk visited (up to N,
    /// 00a7f880) uncovers (36 − already open) / (obelisks still unvisited), integer division — the last one opens the
    /// rest. No pieces when the map has no Tear of Asha.
    /// </summary>
    public int PuzzlePiecesOpen(PlayerState p) => PuzzlePiecesFor(Math.Min(ObelisksVisited(p), ObeliskCount), ObeliskCount, S.Grail != null);

    public static int PuzzlePiecesFor(int visited, int obelisks, bool hasGrail = true)
    {
        if (!hasGrail || obelisks <= 0) return 0;
        int open = 0, left = obelisks;
        for (int i = 0; i < visited; i++, left--) open += (PuzzlePieces - open) / left;
        return open;
    }

    /// <summary>
    /// Direction hint for a dig at <paramref name="at"/> towards the Tear of Asha (00a7f710, map y grows northwards):
    /// mostly vertical when |dx|·26 ≤ |dy|·10, mostly horizontal when |dy|·26 ≤ |dx|·10, else diagonal.
    /// Returns the RPGRoot text id.
    /// </summary>
    public static string GrailHint(Tile at, Tile grail)
    {
        int dx = grail.X - at.X, dy = grail.Y - at.Y, ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (ax * 26 <= ay * 10) return dy >= 1 ? "GRAAL_HINT_NORTH" : "GRAAL_HINT_SOUTH";
        if (dx > 0) return ay * 26 <= ax * 10 ? "GRAAL_HINT_EAST" : dy >= 1 ? "GRAAL_HINT_NORTH_EAST" : "GRAAL_HINT_SOUTH_EAST";
        return ay * 26 <= ax * 10 ? "GRAAL_HINT_WEST" : dy >= 1 ? "GRAAL_HINT_NORTH_WEST" : "GRAAL_HINT_SOUTH_WEST";
    }

    /// <summary>
    /// Dig (009920a0 with the checks of 0098ec30): a tile already dug says GRAAL_HAS_HOLE (no cost); a tile that is
    /// not clear is refused with GRAAL_TRY_ON_CLEAR; too few movement points with GRAAL_NOT_ENOUGH_MOVEMENT.
    /// Otherwise the movement is spent, a hole is left, and the Tear of Asha is found within reach (GRAAL_NAME /
    /// GRAAL_CONGRATULATIONS) or a direction hint / GRAAL_NOTHING_HERE is given.
    /// TODO(exe): the cost (hero slots +0x104 → +0xf8) and the "clear tile" test (00bcc2b0 / 0098eb30) are not
    /// traced: the whole day's movement, on a free land tile, as the stock texts describe.
    /// </summary>
    string? DoDig(DigCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != p.Index) return "not your hero";
        if (h.InBoat) return "GRAAL_TRY_ON_CLEAR";
        if (S.Holes.Contains(h.Pos)) { Emit(new ObjectVisited(h.Id, -1, "dig:GRAAL_HAS_HOLE")); return null; }
        var f = Map.Floor(h.Pos.Floor);
        if (f == null || f.IsWater(h.Pos.X, h.Pos.Y) || ObjectsAt(h.Pos).Any(o => o.Kind != ObjectKind.Hero) || TownAt(h.Pos) != null)
            return "GRAAL_TRY_ON_CLEAR";
        if (h.MovePoints < h.MaxMovePoints || h.MaxMovePoints <= 0) return "GRAAL_NOT_ENOUGH_MOVEMENT";
        h.MovePoints = 0;
        S.Holes.Add(h.Pos);
        int reach = h.Perks.Contains("HERO_SKILL_GRAIL_VISION") ? GrailVisionReach : 0;
        if (!S.GrailFound && S.Grail is { } g && g.Floor == h.Pos.Floor)
        {
            if (Math.Max(Math.Abs(g.X - h.Pos.X), Math.Abs(g.Y - h.Pos.Y)) <= reach)
            {
                S.GrailFound = true;
                GiveArtifact(h, GrailArtifact);
                Emit(new ObjectVisited(h.Id, -1, "dig:grail"));
                return null;
            }
            if (Math.Abs(g.X - h.Pos.X) <= GrailHintRange && Math.Abs(g.Y - h.Pos.Y) <= GrailHintRange)
            {
                Emit(new ObjectVisited(h.Id, -1, "dig:" + GrailHint(h.Pos, g)));
                return null;
            }
        }
        Emit(new ObjectVisited(h.Id, -1, "dig:GRAAL_NOTHING_HERE"));
        return null;
    }
}
