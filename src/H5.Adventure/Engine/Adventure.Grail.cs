using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>Dig for the Tear of Asha on the hero's tile (takes the whole day). Exe: CDigCmd (vtable 0xe0dda8, execute 0x4277a0).</summary>
public sealed record DigCmd(int HeroId) : AdvCommand;

// Tear of Asha (grail), obelisks and the puzzle map.
public sealed partial class Adventure
{
    public const string GrailArtifact = "GRAAL";
    /// <summary>Pieces of every PuzzleDesc (UI/PuzzleMap/*.(PuzzleDesc).xdb).</summary>
    public const int PuzzlePieces = 36;
    const int GrailEdgeMargin = 4;

    /// <summary>
    /// Places the Tear of Asha on a random open land tile of the surface, away from the map edge and from objects.
    /// TODO(exe): the original placement rule (and whether it can go underground) is not traced.
    /// </summary>
    void PlaceGrail()
    {
        var f = Map.Floor(0);
        if (f == null) return;
        var occupied = new HashSet<Tile>(Map.Objects.Where(o => o.Pos.Floor == 0).SelectMany(o => o.BlockedTiles.Append(o.Pos)));
        var candidates = new List<Tile>();
        for (int y = GrailEdgeMargin; y < f.Height - GrailEdgeMargin; y++)
            for (int x = GrailEdgeMargin; x < f.Width - GrailEdgeMargin; x++)
            {
                var c = f.At(x, y);
                if (f.IsBlocked(x, y) || c is TerrainClass.Water or TerrainClass.River or TerrainClass.Road) continue;
                var t = new Tile(x, y, 0);
                if (!occupied.Contains(t)) candidates.Add(t);
            }
        if (candidates.Count > 0) S.Grail = candidates[S.Rng.Next(candidates.Count)];
    }

    /// <summary>Puzzle pieces the player has uncovered: a share of the 36 per obelisk visited (all of them once every obelisk is visited).</summary>
    public int PuzzlePiecesOpen(PlayerState p)
    {
        int total = ObeliskCount;
        return total == 0 ? 0 : Math.Min(PuzzlePieces, PuzzlePieces * ObelisksVisited(p) / total);
    }

    /// <summary>
    /// Digging needs a hero on land who has not moved today (all movement points left) on a free tile; it uses
    /// up the day. The Tear of Asha is found on its tile, or next to it with the Tear of Asha Vision perk.
    /// TODO(exe): CDigCmd defers to the world (slot +0x114); conditions follow the manual / stock texts.
    /// </summary>
    string? DoDig(DigCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != p.Index) return "not your hero";
        if (h.InBoat) return "cannot dig at sea";
        if (h.MovePoints < h.MaxMovePoints || h.MaxMovePoints <= 0) return "the hero must not have moved today";
        var f = Map.Floor(h.Pos.Floor);
        if (f == null || f.IsWater(h.Pos.X, h.Pos.Y)) return "cannot dig here";
        if (ObjectsAt(h.Pos).Any(o => o.Kind != ObjectKind.Hero) || TownAt(h.Pos) != null) return "cannot dig here";
        h.MovePoints = 0;
        int reach = h.Perks.Contains("HERO_SKILL_GRAIL_VISION") ? 1 : 0;
        if (!S.GrailFound && S.Grail is { } g && g.Floor == h.Pos.Floor && Math.Max(Math.Abs(g.X - h.Pos.X), Math.Abs(g.Y - h.Pos.Y)) <= reach)
        {
            S.GrailFound = true;
            GiveArtifact(h, GrailArtifact);
            Emit(new ObjectVisited(h.Id, -1, "dig:grail"));
        }
        else Emit(new ObjectVisited(h.Id, -1, "dig:nothing"));
        return null;
    }
}
