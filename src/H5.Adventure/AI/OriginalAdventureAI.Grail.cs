using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.AI;

// The grail search (exe AI phase 0 "Solve puzzlemap": command 00428790 → the guess 00bc6530).
public sealed partial class OriginalAdventureAI
{
    /// <summary>The puzzle piece of each of the 6 × 6 template blocks, row-major (exe DAT_00f78250).</summary>
    public static readonly int[] PuzzlePieceOrder =
    {
        8, 20, 9, 2, 3, 15,
        5, 22, 31, 21, 14, 16,
        19, 30, 33, 34, 32, 6,
        26, 29, 35, 25, 17, 23,
        4, 28, 24, 13, 0, 18,
        27, 10, 12, 11, 1, 7,
    };

    /// <summary>Template size around the grail tile (00bc6530: 17 columns from x − 8, 13 rows from y − 6).</summary>
    public const int GrailTemplateW = 17, GrailTemplateH = 13;

    /// <summary>
    /// The AI's per-player grail state (CPlayerAIInformation): the per-floor candidate bitmaps kept from the last search
    /// (vf 0x28 → +0x54; one (width, height, bits) entry per floor) and the guess (vf 0x2c / 0x30 / 0x34: +0x60 tile,
    /// +0x64 valid, +0x6c probability).
    /// </summary>
    public sealed class GrailSearchState
    {
        public (int W, int H, bool[] Bits)?[] Floors = Array.Empty<(int, int, bool[])?>();
        public Tile? Guess;
        public double Probability;
        /// <summary>The grail value of the day (vf 0x4c / 0x50: +0x7c), from <see cref="GrailValue"/>.</summary>
        public double Value;
    }

    readonly Dictionary<int, GrailSearchState> _grail = new();

    /// <summary>The player's grail state (null before its first AI day).</summary>
    public GrailSearchState? GrailState(int player) => _grail.GetValueOrDefault(player);

    /// <summary>The map object standing for the grail guess in a hero's destinations (no map object: index −3).</summary>
    static readonly MapObject GrailSpot = new() { Index = -3, Kind = ObjectKind.Unknown };

    /// <summary>
    /// "Solve puzzlemap" (AI phase 0, command 00428790): nothing once the AI information says the grail is found
    /// (vf 0x40, taken as the game's <c>GrailFound</c>); else the day's grail value is stored (vf 0x50, <see cref="GrailValue"/>)
    /// and the guess of <see cref="GuessGrail"/> replaces the stored one (vf 0x2c with its probability; none when the
    /// guess finds nothing).
    /// </summary>
    public void SolvePuzzleMap(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, int player)
    {
        if (g.S.Grail == null || g.S.GrailFound) return;
        if (!_grail.TryGetValue(player, out var state)) _grail[player] = state = new GrailSearchState();
        state.Value = GrailValue(g, st, diff, player);
        var guess = GuessGrail(g, diff, player, state);
        state.Guess = guess?.Tile;
        state.Probability = guess?.Probability ?? 0.0;
    }

    /// <summary>
    /// The grail value (exe FUN_00bc7340): ValueOfGrailMin (block +0x2d0); then, for each of the player's towns without
    /// TB_GRAIL (town vf 0x34(0xe) &lt; 1), the grail building is built on a snapshot of the world (vf 0x98 / quote vf
    /// 0x5c(…, 0xe, 1, 0), build vf 0x6c, rolled back with vf 8) and, when the build succeeds, a gain in the player value
    /// (004eb790, <see cref="PlayerValue"/>) at least the running value replaces it.
    /// GAP-details: the objectives part (world vf 0x130 → the player's objectives of kind 5: none of them active (vf 0x54
    /// = 2) with no towns → 0; vf 0x70 → ValueOfGrailForInstantVictory (+0x2d8); else ValueOfGrailByObjective (+0x2d4))
    /// is not ported (objective states are not modelled), so the start value is always the minimum; the build is our
    /// <see cref="Engine.BuildCmd"/> on a state copy (its checks, including the Tear of Asha in the town).
    /// </summary>
    public double GrailValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, int player)
    {
        double v = st.ValueOfGrailMin;
        long before = PlayerValue(g, st, diff, player);
        foreach (var t in g.S.Towns.Where(t => t.Owner == player))
        {
            if (t.Level("TB_GRAIL") >= 1) continue;
            var sim = Simulation(g);
            int was = sim.S.CurrentPlayer;
            sim.S.CurrentPlayer = player;
            bool ok = sim.Apply(new Engine.BuildCmd(t.Object, "TB_GRAIL") { Player = player }).Ok;
            sim.S.CurrentPlayer = was;
            if (!ok) continue;
            double gain = PlayerValue(sim, st, diff, player) - (double)before;
            if (v <= gain) v = gain;
        }
        return v;
    }

    /// <summary>
    /// The grail destination of a hero's search (exe FUN_00a7ae20 from 00a7af80): with a stored guess (vf 0x30) and a
    /// route to it (00c07360; the route's last cost w2 at most the limit of 0045dbe0, none when it has no entry), a
    /// record at the guess tile with value ftol(grail value · probability) (vf 0x4c, vf 0x34) and the route's cost.
    /// The hero standing on the guess has a one-cell route; 0086c760 then digs (<see cref="Engine.DigCmd"/>).
    /// GAP-details: the record's flag +0x30 = 1 is not interpreted; the limit map of 0045dbe0 is not identified (taken
    /// as no entry); the record is added after the map objects.
    /// </summary>
    (Tile Tile, int Cost, long Value)? GrailDestination(HeroState h, Dictionary<Tile, int> costs)
    {
        if (GrailState(h.Owner) is not { Guess: { } t } state) return null;
        if (t.Floor != h.Pos.Floor || !costs.TryGetValue(t, out int cost)) return null;
        return (t, cost, (long)(state.Value * state.Probability));
    }

    /// <summary>One template / map cell as 00bc6530 compares it: passability tile +8 (kind), +0 (ground), +4 (road) and the object key.</summary>
    public readonly record struct GrailCell(int Kind, int Ground, int Road, object? Key);

    /// <summary>
    /// The object key of each tile of a floor (passability tile +0x14 through 00bc62b0): the tile's object is the last one
    /// whose blocked tiles (object vf 0xb4, registered by 00991650) or active tiles (vf 0xb8, 00990bc0, after the blocked
    /// ones) cover it, in map order (00993ab0 per placed object); its key is the shared descriptor (vf 0x8c), none for the
    /// object kinds 00bc62b0 leaves out (<see cref="GrailKeyless"/>) and for a dug hole (the GroundHole static, compared
    /// by 0068d1a0 against DAT_01042b68). A removed object leaves no key.
    /// GAP-details: whether removing an object clears its tiles (00990dc0 clears +0x10 / +0x14 of a tile) and whether
    /// heroes overwrite the tiles they stand on (00990d40 / 00990e40) is taken as: removed objects leave nothing, heroes
    /// leave the tiles as they are.
    /// </summary>
    public static object?[] GrailCellKeys(Engine.Adventure g, int floor)
    {
        var f = g.Map.Floor(floor);
        if (f == null) return Array.Empty<object?>();
        var keys = new object?[f.Width * f.Height];
        foreach (var o in g.Map.Objects)
        {
            if (o.Kind == ObjectKind.Hero) continue;
            if (g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            object? key = GrailKeyless(o) || IsGroundHole(o.Shared) ? null : o.Shared;
            foreach (var t in o.BlockedTiles.Concat(o.ActiveTiles))
                if (t.Floor == floor && f.InBounds(t.X, t.Y)) keys[t.Y * f.Width + t.X] = key;
        }
        // A dig leaves a GroundHole static on the tile (009920a0), which has no key.
        foreach (var t in g.S.Holes)
            if (t.Floor == floor && f.InBounds(t.X, t.Y)) keys[t.Y * f.Width + t.X] = null;
        return keys;
    }

    static bool IsGroundHole(SharedDef s) => s.Path.Contains("/GroundHole/GroundHole.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The object kinds whose tiles 00bc62b0 gives no key: the IAdvMapObject casts vf 0x2c, 0x38, 0x30, 0x34, 0x20, 0x1c
    /// that answer — hero (vf 0x2c, CAdvMapHero interface vtable 00e54324), ship (vf 0x38, 00e5457c; ships are not map
    /// objects here), artifact (vf 0x20, 00e187d4), monster (vf 0x1c, 00e18f7c). Towns, garrisons, buildings, dwellings,
    /// mines, treasures, statics, prisons, seer huts, sphinxes, taverns, stands, hill forts, abandoned mines, dwarven
    /// warrens, cartographers and shipyards answer none of them (interface vtables 00e9567c, 00e92ea4, 00e64cd4, 00e62d14,
    /// 00e114ec, 00e715ec, 00e710f4, 00e10e7c, 00e19444, 00e648cc, 00e4cc24, 00e50654, 00e50a14, 00e50c6c, 00e1176c,
    /// 00e1581c, 00e8a3dc) and keep their key; no class answering vf 0x30 / 0x34 was found.
    /// </summary>
    public static bool GrailKeyless(MapObject o) => o.Kind is ObjectKind.Hero or ObjectKind.Artifact or ObjectKind.Monster;

    /// <summary>The cell record of a tile (passability tile defaults 00bcc5d0 = kind 1, ground 1, road 9, no object, off the map).</summary>
    static GrailCell CellOf(MapFloor? f, object?[] keys, int x, int y)
    {
        if (f == null || !f.InBounds(x, y) || f.Kind.Length != f.Width * f.Height) return new GrailCell(1, 1, 9, null);
        int i = y * f.Width + x;
        return new GrailCell((int)f.Kind[i], f.Ground[i], f.Road.Length > i ? f.Road[i] : 9, keys.Length > i ? keys[i] : null);
    }

    /// <summary>
    /// The grail guess (exe FUN_00bc6530): when the player's open puzzle pieces (00a7f8c0) reach
    /// GrailSearchOpenPartsMinimum (row +0x48 of the world difficulty, vf 0x120 → 00c02a90), or with <paramref name="force"/>: a 17 × 13 template around the true grail tile
    /// (x − 8 + dx, y − 6 + dy, its floor) records per cell whether its puzzle block is open (piece
    /// <see cref="PuzzlePieceOrder"/>[col + row · 6] &lt; open pieces, col = ⌊dx · 5 / 16 + 0.5⌋, row = ⌊dy · 5 / 12 + 0.5⌋)
    /// and the cell (<see cref="GrailCell"/>); a template cell of kind 5 limits the search to floor 1. Per floor, the
    /// candidate windows are the centres (cx, cy) in [0, W − 16) × [0, H − 12), kept from the previous search's bitmap
    /// when it has the floor's size and (on the grail's floor) still holds the true window, else all. Every map tile:
    /// unexplored → +1 to each window covering it (and to the unexplored count); explored → per covering window: its
    /// template cell closed → +1, open and any field different (or the window whose grail point is a dug hole) → the
    /// window is dropped. The survivors become the floor's bitmap. Limit = ((unexplored · 50 / (W · H) + open · 50 / 36)
    /// · GrailSearchPercentMatchNeed (row +0x4c) / 100) · 221 / 100 (integer steps); a window is a candidate when its count ≤ limit
    /// (or force), with n = the windows within ±5 of it. Over all floors in order: score = 221 − count; a score ≥ the best
    /// counts as met, a better one (or equal with a larger n) becomes the best (met restarts at 1 when the score is over
    /// twice the old best). Result: the best window's grail point (cx + 8, cy + 6) with probability n / met, or null.
    /// The block / row factors are the exe's floats (5.0 · 0.0625 and 5.0 · 0.083333336, + 0.5, floor).
    /// GAP-details: template cells off the map read as the tile defaults (the exe reads past the floor's rows); the
    /// explored grid (player vf 0x19c +0x1c) is taken per floor.
    /// </summary>
    public static (Tile Tile, double Probability)? GuessGrail(Engine.Adventure g, AdventureDifficulty diff, int player, GrailSearchState state, bool force = false)
    {
        if (g.S.Grail is not { } grail || g.Player(player) is not { } p) return null;
        int open = g.PuzzlePiecesOpen(p);
        if (open < diff.GrailSearchOpenPartsMinimum && !force) return null;
        int floors = g.Map.Floors.Length;
        while (floors > 0 && g.Map.Floors[floors - 1] == null) floors--;
        if (floors == 0) return null;

        // The template around the true grail tile.
        var gf = g.Map.Floor(grail.Floor);
        var gkeys = GrailCellKeys(g, grail.Floor);
        var tOpen = new bool[GrailTemplateW * GrailTemplateH];
        var tCell = new GrailCell[GrailTemplateW * GrailTemplateH];
        bool underOnly = false;
        for (int dy = 0; dy < GrailTemplateH; dy++)
        {
            int row = (int)Math.Floor((double)((float)dy * 5f * (1f / 12f) + 0.5f));
            for (int dx = 0; dx < GrailTemplateW; dx++)
            {
                int col = (int)Math.Floor((double)((float)dx * 5f * 0.0625f + 0.5f));
                int k = dy * GrailTemplateW + dx;
                tOpen[k] = PuzzlePieceOrder[col + row * 6] < open;
                tCell[k] = CellOf(gf, gkeys, grail.X - 8 + dx, grail.Y - 6 + dy);
                if (tCell[k].Kind == 5) underOnly = true;
            }
        }

        if (state.Floors.Length < floors) Array.Resize(ref state.Floors, floors);
        int best = 0, met = 0, bestN = 0;
        Tile? bestTile = null;
        for (int fl = 0; fl < floors; fl++)
        {
            if (underOnly && fl != 1) continue;
            var f = g.Map.Floor(fl);
            if (f == null) continue;
            int W = g.Map.TilesX, H = g.Map.TilesY, cw = W - 16, ch = H - 12;
            if (cw <= 0 || ch <= 0) continue;
            var alive = new bool[cw * ch];
            var count = new int[cw * ch];
            bool fromCache = false;
            if (state.Floors[fl] is { } c && c.W == cw && c.H == ch &&
                (fl != grail.Floor || c.Bits[Math.Max(0, grail.Y - 6) * cw + Math.Max(0, grail.X - 8)]))
            {
                for (int i = 0; i < alive.Length; i++) alive[i] = c.Bits[i];
                fromCache = Array.IndexOf(alive, true) >= 0;
            }
            if (!fromCache) Array.Fill(alive, true);

            var keys = fl == grail.Floor ? gkeys : GrailCellKeys(g, fl);
            var explored = fl < p.Explored.Count ? p.Explored[fl] : null;
            int unexplored = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int x0 = Math.Max(0, x - 16), x1 = x >= cw ? cw - 1 : x;
                    int y0 = Math.Max(0, y - 12), y1 = y >= ch ? ch - 1 : y;
                    bool seen = explored != null && y * W + x < explored.Length && explored[y * W + x];
                    if (!seen)
                    {
                        unexplored++;
                        for (int cy = y0; cy <= y1; cy++)
                            for (int cx = x0; cx <= x1; cx++)
                                if (alive[cy * cw + cx]) count[cy * cw + cx]++;
                        continue;
                    }
                    var cell = CellOf(f, keys, x, y);
                    // A dug hole (0098db40: an object on the tile whose shared is the GroundHole static) drops the window
                    // whose grail point it is.
                    int holeWin = g.S.Holes.Contains(new Tile(x, y, fl)) && x - 8 >= 0 && y - 6 >= 0 && x - 8 < cw && y - 6 < ch ? (y - 6) * cw + (x - 8) : -1;
                    for (int cy = y0; cy <= y1; cy++)
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            int w = cy * cw + cx;
                            if (!alive[w]) continue;
                            int k = (y - cy) * GrailTemplateW + (x - cx);
                            if (!tOpen[k]) { count[w]++; continue; }
                            if (tCell[k] != cell || w == holeWin) alive[w] = false;
                        }
                }
            state.Floors[fl] = (cw, ch, (bool[])alive.Clone());

            int limit = (unexplored * 50 / (W * H) + open * 50 / 36) * diff.GrailSearchPercentMatchNeed / 100 * 0xdd / 100;
            for (int cy = 0; cy < ch; cy++)
                for (int cx = 0; cx < cw; cx++)
                {
                    int w = cy * cw + cx;
                    if (!alive[w] || (count[w] > limit && !force)) continue;
                    int n = 0;
                    for (int yy = Math.Max(0, cy - 5); yy <= Math.Min(ch - 1, cy + 5); yy++)
                        for (int xx = Math.Max(0, cx - 5); xx <= Math.Min(cw - 1, cx + 5); xx++)
                            if (alive[yy * cw + xx]) n++;
                    int score = 0xdd - count[w];
                    if (score < best) continue;
                    met++;
                    if (score > best || n > bestN)
                    {
                        if (best * 2 < score) met = 1;
                        best = score;
                        bestN = n;
                        bestTile = new Tile(cx + 8, cy + 6, fl);
                    }
                }
        }
        if (best <= 0 || bestTile == null) return null;
        return (bestTile.Value, (double)bestN / met);
    }
}
