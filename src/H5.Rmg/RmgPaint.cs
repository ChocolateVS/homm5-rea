namespace H5.Rmg;

/// <summary>
/// One texture layer of a floor's terrain object (the 0x18-byte entries of terrain +8 .. +0xc, created by 0x00c0f7a0):
/// the vertex weight grid ((W + 1) x (H + 1) bytes, [row y][column x], terrain chunk 4), the AdvMapTile (+0x10) and the
/// tile's Priority (+0x5c) and Type (+0x60, ESpecialTerrainType id). The list stays sorted by priority (draw order:
/// layer 0 first).
/// </summary>
public sealed class RmgTerrainLayer
{
    public required string Tile;
    public int Priority, Type;
    public required byte[] Weights;
}

public sealed partial class RmgGenerator
{
    /// <summary>exe 0x00c0e320: the blend class of a tile type (dirt/gravel/cobblestone roads 1, small water 4, big water 3, else 0).</summary>
    static int BlendClass(int type) => type switch { 6 or 7 or 8 => 1, 10 => 4, 11 => 3, _ => 0 };

    /// <summary>
    /// exe 0x00913510 (ECX = vertex (row, column) as floats, EDX = tile, strength, terrain): paints one vertex.
    /// 0x00c0f140 refuses a null tile, or a tile that is not yet a layer while a layer of a type other than 0 (TT_DIRT)
    /// has the same blend class and the same priority. 0x00c0f7a0 finds the tile's layer, or inserts a zeroed one before
    /// the first layer whose priority is not below the tile's. Strength &lt; 1: the weight moves by the strength.
    /// Otherwise v = strength + the largest weight at the vertex among the layers of the same blend class whose
    /// priority is not below the tile's (the tile's own layer included); v &gt; 255 lowers the same-class layers of
    /// another priority by 2·v − 0x1fe and writes 255. Weights clamp to 0..255; indices are chopped (FISTP with
    /// rounding mode 0xc00).
    /// </summary>
    internal void Paint(RmgFloor f, int row, int col, string tile, int strength)
    {
        if (_db.Tile(tile) is not { } t) return;
        var layers = f.Layers;
        int own = layers.FindIndex(l => string.Equals(l.Tile, tile, StringComparison.OrdinalIgnoreCase));
        if (own < 0)
        {
            foreach (var l in layers)
                if (l.Type != 0 && BlendClass(l.Type) == BlendClass(t.Type) && l.Priority == t.Priority) return;
            own = layers.FindIndex(l => t.Priority <= l.Priority);
            if (own < 0) own = layers.Count;
            layers.Insert(own, new RmgTerrainLayer { Tile = tile, Priority = t.Priority, Type = t.Type, Weights = new byte[(f.W + 1) * (f.H + 1)] });
        }
        var layer = layers[own];
        int i = f.Lv(row, col);
        if (row < 0 || col < 0 || row > f.H || col > f.W) return;   // never reached by the callers (the exe has no test)
        int v;
        if (strength < 1) v = layer.Weights[i] + strength;
        else
        {
            int max = 0;
            int cls = BlendClass(t.Type);
            foreach (var l in layers)
                if (BlendClass(l.Type) == cls && t.Priority <= l.Priority && max < l.Weights[i]) max = l.Weights[i];
            v = max + strength;
            if (v > 0xff)
            {
                int d = v * 2 - 0x1fe;
                foreach (var l in layers)
                    if (BlendClass(l.Type) == cls && l.Priority != t.Priority) l.Weights[i] = (byte)Math.Clamp(l.Weights[i] - d, 0, 0xff);
                v = 0xff;
            }
        }
        layer.Weights[i] = (byte)Math.Clamp(v, 0, 0xff);
    }

    /// <summary>A tile's four vertices (row y / y + 1, column x / x + 1), as every caller of 0x00913510 but 0x00918270 paints them.</summary>
    internal void PaintTile(RmgFloor f, int x, int y, string tile, int strength)
    {
        Paint(f, y, x, tile, strength);
        Paint(f, y, x + 1, tile, strength);
        Paint(f, y + 1, x, tile, strength);
        Paint(f, y + 1, x + 1, tile, strength);
    }

    static readonly (int Dx, int Dy)[] TransitionDiagonals = { (1, -1), (1, 1), (-1, 1), (-1, -1) };    // 0x00f46738 (x, y pairs)
    static readonly (int Dx, int Dy)[] TransitionSides = { (0, -1), (1, 0), (0, 1), (-1, 0) };          // 0x00f46718

    /// <summary>
    /// exe 0x00918270 (after 0x00915dc0, stage "FillTerrain"): per floor, per vertex (row y 0 .. H, column x 0 .. W), the
    /// zone of tile (min(x, W − 1), min(y, H − 1)) — none: "no zone found", the vertex stays unpainted — paints its
    /// +0x20 preset's DefaultTile (+0x44), or OtherTiles (+0x7c) [seed % n] when that list is not empty and the zone seed
    /// (+0x13c) is even, at 0xff. Surface only: the first diagonal neighbour tile (x ± 1, y ± 1 in the order of
    /// 0x00f46738; 1 ≤ x', y' &lt; size − 1) in another zone ends the search when the two races (+0x18) are equal or
    /// Haven / Preserve (3 / 4), else paints Params DefaultTransitiveTile (+0x160) at 0x80 (a missing zone or tile goes
    /// on to the next neighbour); then the first side neighbour (0x00f46718) likewise at 0xff. On the underground the
    /// first neighbour in another zone ends both searches.
    /// </summary>
    void PaintTerrain()
    {
        string trans = _db.Params.DefaultTransitiveTile;
        foreach (var f in Floors)
            for (int y = 0; y <= H; y++)
                for (int x = 0; x <= W; x++)
                {
                    int id = f.Zone[f.At(Math.Min(x, W - 1), Math.Min(y, H - 1))];
                    if (!f.Zones.TryGetValue(id, out var z)) continue;
                    var race = _db.Race(z.Preset);
                    string tile = race.DefaultTile;
                    if (race.OtherTiles.Count > 0 && z.ZoneSeed % 2 == 0) tile = race.OtherTiles[z.ZoneSeed % race.OtherTiles.Count];
                    Paint(f, y, x, tile, 0xff);
                    if (f.Index != 0)
                    {
                        // 0x009184cb: the underground leaves at the first neighbour in another zone, painting nothing.
                        continue;
                    }
                    Transition(f, z, x, y, TransitionDiagonals, trans, 0x80);
                    Transition(f, z, x, y, TransitionSides, trans, 0xff);
                }
    }

    void Transition(RmgFloor f, RmgZone z, int x, int y, (int Dx, int Dy)[] around, string trans, int strength)
    {
        foreach (var (dx, dy) in around)
        {
            int nx = x + dx, ny = y + dy;                                   // 0x00e0a7e4 = 1.0
            if (ny < 1 || ny >= H - 1 || nx < 1 || nx >= W - 1) continue;
            int nid = f.Zone[f.At(nx, ny)];
            if (nid == z.Id || !f.Zones.TryGetValue(nid, out var other)) continue;
            if (z.Race == other.Race || (z.Race == 3 && other.Race == 4) || (z.Race == 4 && other.Race == 3)) return;
            if (_db.Tile(trans) == null) continue;
            Paint(f, y, x, trans, strength);
            return;
        }
    }
}
