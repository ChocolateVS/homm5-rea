using H5.Adventure.Map;

namespace H5.Rmg;

/// <summary>
/// Values of the exe's per-tile flag grid (floor record +0xd4, <see cref="RmgFloor.Flags"/>). Every placer writes it
/// the way H5_Game.exe does; the footprint tests 0x00919be0 / 0x00919a50 and the road builder read it.
/// </summary>
public static class TileFlags
{
    /// <summary>One-tile non-blocking static (0x00922f70); 0x00919be0 accepts it under active tiles when asked to.</summary>
    public const int Nonblocker = 1;
    /// <summary>Object footprint (blockedTiles, 0x0091a420), static, mine decoration, lake centre. The road builder treats exactly 2 as a wall.</summary>
    public const int Blocked = 2;
    /// <summary>Object visit tile (activeTiles / PossessionMarkerTile, 0x0091a420), mine and upgrade guards (0x0091c4a0, 0x00ba79f0).</summary>
    public const int Active = 4;
    /// <summary>Road from the town (or the first connection) to each connection, 0x0091bf90; painted with the preset's RoadTile.</summary>
    public const int MainRoad = 8;
    /// <summary>Road from each mine to the road network, 0x0091bf90; painted with SecondaryRoadTile.</summary>
    public const int MineRoad = 0x10;
    /// <summary>Path between the zone's object nodes, 0x0091bdb0; reserved, not painted.</summary>
    public const int ObjectPath = 0x20;
    /// <summary>Any road (0x009191a0 is called with this mask before the treasure blocks).</summary>
    public const int AnyRoad = MainRoad | MineRoad | ObjectPath;
    /// <summary>Lake tile grown around a lake centre (0x00924940).</summary>
    public const int Lake = 0x80;
    /// <summary>Mountain footprint while the mountains are placed (0x00922a10), rewritten to 2 at the end.</summary>
    public const int Mountain = 0x100;
}

/// <summary>Primitives of the map builder shared by every placer, ported from H5_Game.exe.</summary>
public sealed partial class RmgGenerator
{
    /// <summary>The angle of quarter turn q as the placers compute it: (float)q x (float)(π/2) (0x00e0d2d0).</summary>
    public static float QuarterAngle(int q) => q * 1.5707964f;

    /// <summary>
    /// exe 0x00b0bbd0: an angle's quarter turn. While the angle is below 0 (0x00e0be10) 2π (0x00e155a8) is added; then
    /// FISTP (round to nearest) of the float angle x 2/π (0x00e7ab2c) + 0.25 (0x00e11bb8), and 3.
    /// </summary>
    public static int ExeQuarter(float angle)
    {
        float a = angle;
        while (a < 0f) a += 6.2831855f;
        float t = a * 0.63661975f + 0.25f;
        return (int)Math.Round(t, MidpointRounding.ToEven) & 3;
    }

    /// <summary>exe 0x00b0bca0 with the matrix of 0x00b0bbd0: quarter 1 maps (x, y) to (−y, x), 2 to (−x, −y), 3 to (y, −x).</summary>
    public static (int X, int Y) Rotate((int X, int Y) o, int q) => q switch
    {
        1 => (-o.Y, o.X),
        2 => (-o.X, -o.Y),
        3 => (o.Y, -o.X),
        _ => (o.X, o.Y),
    };

    /// <summary>SAdvMapObjectBaseShared +0x84: the PossessionMarkerTile (one point; (0,0) when absent).</summary>
    internal static (int X, int Y) Marker(SharedDef d)
    {
        var m = d.Raw?.Element("PossessionMarkerTile");
        if (m == null) return (0, 0);
        int.TryParse((string?)m.Element("x"), out int x);
        int.TryParse((string?)m.Element("y"), out int y);
        return (x, y);
    }

    /// <summary>exe 0x009189f0 (underground floor only): x and y in [5, size − 5).</summary>
    bool Inner5(int x, int y) => y >= 5 && y < H - 5 && x >= 5 && x < W - 5;

    /// <summary>
    /// exe 0x00919be0 (pos, angle, blockedTiles, activeTiles, PossessionMarkerTile, allowNonblocker): every rotated
    /// blocked tile and the rotated marker (skipped when it is (0,0)) must lie on the map (and, underground, inside
    /// 0x009189f0), in the zone, with flags 0 and border distance ≥ 1; every rotated active tile on the map, in the
    /// zone, with flags 0 (0 or 1 when <paramref name="allowNonblocker"/>), border distance ≥ 3 and, underground,
    /// inside 0x009189f0. Tile coordinates are chopped (0x00d11a60).
    /// </summary>
    internal bool FootprintFits(RmgZone z, int px, int py, float angle, SharedDef d, bool allowNonblocker = false)
    {
        var f = Floors[z.Floor];
        int q = ExeQuarter(angle);
        bool ug = z.Floor == 1;
        foreach (var b in d.Blocked)
        {
            var (ox, oy) = Rotate(b, q);
            int x = px + ox, y = py + oy;
            if (!f.In(x, y)) return false;
            if (ug && !Inner5(x, y)) return false;
            int i = f.At(x, y);
            if (f.Zone[i] != z.Id || f.Flags[i] != 0 || f.BorderDist[i] < 1) return false;
        }
        var mk = Rotate(Marker(d), q);
        if (mk != (0, 0))
        {
            int x = px + mk.X, y = py + mk.Y;
            if (!f.In(x, y)) return false;
            if (ug && !Inner5(x, y)) return false;
            int i = f.At(x, y);
            if (f.Zone[i] != z.Id || f.Flags[i] != 0 || f.BorderDist[i] < 1) return false;
        }
        foreach (var a in d.Active)
        {
            var (ox, oy) = Rotate(a, q);
            int x = px + ox, y = py + oy;
            if (!f.In(x, y)) return false;
            int i = f.At(x, y);
            if (f.Zone[i] != z.Id) return false;
            if (allowNonblocker ? f.Flags[i] is not (0 or 1) : f.Flags[i] != 0) return false;
            if (f.BorderDist[i] < 3) return false;
            if (ug && !Inner5(x, y)) return false;
        }
        return true;
    }

    /// <summary>
    /// exe 0x00919a50 (CGameZone vf 0x44, the statics' test): every rotated blocked tile on the map (underground inside
    /// 0x009189f0), with (flags &amp; 0x3e) == 0 and a distance field value ≥ 2. No zone test.
    /// </summary>
    internal bool StaticFits(RmgZone z, int px, int py, float angle, SharedDef d)
    {
        var f = Floors[z.Floor];
        int q = ExeQuarter(angle);
        foreach (var b in d.Blocked)
        {
            var (ox, oy) = Rotate(b, q);
            int x = px + ox, y = py + oy;
            if (!f.In(x, y)) return false;
            if (z.Floor == 1 && !Inner5(x, y)) return false;
            int i = f.At(x, y);
            if ((f.Flags[i] & 0x3e) != 0 || f.Dist[i] < 2) return false;
        }
        return true;
    }

    /// <summary>
    /// exe 0x0091a420 (pos, angle, blockedTiles, activeTiles, PossessionMarkerTile, list): every rotated blocked tile
    /// gets flag 2 and goes to the zone's blocked list +0x5c; every rotated active tile goes to <paramref name="list"/>
    /// (when given) and to the node list +0x68 and gets flag 4; a non-zero marker goes to +0x68 and gets flag 4.
    /// </summary>
    internal void Stamp(RmgZone z, int px, int py, float angle, SharedDef d, List<(float X, float Y)>? list)
    {
        var f = Floors[z.Floor];
        int q = ExeQuarter(angle);
        foreach (var b in d.Blocked)
        {
            var (ox, oy) = Rotate(b, q);
            int x = px + ox, y = py + oy;
            if (f.In(x, y)) f.Flags[f.At(x, y)] = TileFlags.Blocked;
            z.BlockedList.Add((x, y));
        }
        foreach (var a in d.Active)
        {
            var (ox, oy) = Rotate(a, q);
            int x = px + ox, y = py + oy;
            list?.Add((x, y));
            z.RoadNodes.Add((x, y));
            if (f.In(x, y)) f.Flags[f.At(x, y)] = TileFlags.Active;
        }
        var mk = Rotate(Marker(d), q);
        if (mk != (0, 0))
        {
            int x = px + mk.X, y = py + mk.Y;
            z.RoadNodes.Add((x, y));
            if (f.In(x, y)) f.Flags[f.At(x, y)] = TileFlags.Active;
        }
    }

    /// <summary>The first rotated active tile (the visit tile), or the position for objects without one.</summary>
    internal static (int X, int Y) VisitTile(SharedDef d, int px, int py, float angle)
    {
        if (d.Active.Count == 0) return (px, py);
        var (ox, oy) = Rotate(d.Active[0], ExeQuarter(angle));
        return (px + ox, py + oy);
    }

    /// <summary>
    /// exe 0x009145b0 with its record (object list +0x40 of the builder): the object's name number
    /// (Next(0xffff) &lt;&lt; 16) + Next(0xffff), then the object.
    /// </summary>
    internal RmgObject CreateObject(RmgResult r, RmgZone z, string shared, int x, int y, float angle, string note)
    {
        CreateObjectDraws();
        var d = Catalog.Get(shared);
        int q = ExeQuarter(angle);
        var o = new RmgObject { Shared = shared, Def = d, X = x, Y = y, Floor = z.Floor, Quarter = q, Angle = angle, Zone = z.Id, Note = note };
        r.Objects.Add(o);
        if (d.Active.Count > 0) r.Access[o] = VisitTile(d, x, y, angle);
        return o;
    }

    // ------------------------------------------------------------------ distance field 0x009191a0

    /// <summary>Per zone: the minimum distance (float) from each zone tile to the nodes +0x68 seen so far.</summary>
    readonly Dictionary<RmgZone, (float[] Min, int Seen)> _nodeMin = new();

    /// <summary>
    /// exe 0x009191a0 (mask, allZones): every tile of the zone's floor outside every zone gets 1000; every tile of the
    /// zone (every zoned tile when <paramref name="allZones"/>) the chopped minimum distance to the points of the
    /// lists the mask selects: 4 the node list +0x68, 2 the blocked list +0x5c, 8 the main road +0x74, 0x10 the mine
    /// roads +0x80, 0x20 the object paths +0x8c (0x40 / 0x400: zone tiles with those flags); 10000 (0x00e57d84) when
    /// none. Distances are float square roots (x major, y minor).
    /// </summary>
    internal void DistanceField(RmgZone z, int mask)
    {
        var f = Floors[z.Floor];
        // Nodes are only ever appended, so the minimum over the node list is kept incrementally.
        float[]? nodeMin = null;
        if ((mask & 4) != 0)
        {
            if (!_nodeMin.TryGetValue(z, out var nm)) nm = (Enumerable.Repeat(10000f, z.Tiles.Count).ToArray(), 0);
            for (int k = nm.Seen; k < z.RoadNodes.Count; k++)
            {
                var p = z.RoadNodes[k];
                for (int t = 0; t < z.Tiles.Count; t++)
                {
                    var (x, y) = z.Tiles[t];
                    float dy = y - p.Y, dx = x - p.X;
                    float d = MathF.Sqrt(dy * dy + dx * dx);
                    if (d < nm.Min[t]) nm.Min[t] = d;
                }
            }
            nm.Seen = z.RoadNodes.Count;
            _nodeMin[z] = nm;
            nodeMin = nm.Min;
        }
        var lists = new List<List<(float X, float Y)>>();
        if ((mask & 2) != 0) lists.Add(z.BlockedList);
        if ((mask & 8) != 0) lists.Add(z.Network);
        if ((mask & 0x10) != 0) lists.Add(z.MineNetwork);
        if ((mask & 0x20) != 0) lists.Add(z.ObjectPaths);
        for (int i = 0; i < f.Zone.Length; i++) if (f.Zone[i] < 0) f.Dist[i] = 1000;
        for (int t = 0; t < z.Tiles.Count; t++)
        {
            var (x, y) = z.Tiles[t];
            float best = nodeMin?[t] ?? 10000f;
            foreach (var list in lists)
                foreach (var p in list)
                {
                    float dy = y - p.Y, dx = x - p.X;
                    float d = MathF.Sqrt(dy * dy + dx * dx);
                    if (d < best) best = d;
                }
            f.Dist[f.At(x, y)] = (int)best;
        }
    }

    /// <summary>exe 0x00919710: the largest distance field value over the list's tiles in the zone with border distance &gt; 2 and flags != 2 (0 when none).</summary>
    internal int MaxDist(RmgZone z, List<(int X, int Y)> list)
    {
        var f = Floors[z.Floor];
        int max = 0;
        foreach (var (x, y) in list)
        {
            int i = f.At(x, y);
            if (f.Zone[i] == z.Id && f.BorderDist[i] > 2 && f.Flags[i] != TileFlags.Blocked && f.Dist[i] > max) max = f.Dist[i];
        }
        return max;
    }

    /// <summary>The free-for-a-guard test of 0x0091c4a0 / 0x00ba79f0 / 0x0091dbf0: flags carry a road bit (0x38) or are 0.</summary>
    internal static bool GuardTileFree(int flags) => (flags & 0x38) != 0 || flags == 0;
}
