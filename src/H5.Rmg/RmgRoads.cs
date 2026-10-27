namespace H5.Rmg;

public sealed partial class RmgGenerator
{
    // Neighbour tables of the road builder 0x0091aee0 (pairs of floats: first index, second index).
    static readonly (int Dx, int Dy)[] RoadOrtho = { (0, -1), (1, 0), (0, 1), (-1, 0) };          // 0x00f467d8
    static readonly (int Dx, int Dy)[] RoadDiag = { (1, -1), (1, 1), (-1, 1), (-1, -1) };         // 0x00f467f8
    static readonly (int Dx, int Dy)[] RoadBack =                                                // 0x00f46798
        { (0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1) };

    float[]? _roadDist;   // zone +0xa8: the builder's W x H distance grid

    /// <summary>
    /// exe 0x0091aee0: one road from <paramref name="from"/> to <paramref name="to"/> inside zone <paramref name="z"/>.
    /// Distances start at 1000 (0x447a0000), the source at 0. Pass k relaxes every zone tile (first index 1..W-2
    /// outer, second 1..H-2 inner) whose truncated distance equals k: orthogonal neighbours cost
    /// (100 - border distance) x 0.01 + 1, diagonal ones (100 - bd) x 0.01 + 1.41, both + (5 - bd) when bd &lt; 5; a
    /// neighbour whose flag is exactly 2 is a wall (neighbours outside the zone are relaxed but never expanded).
    /// Passes stop when the target is reached or no zone tile is left at 1000 (pass 800 logs "pure road algo
    /// failed.", more than 2000 aborts). The source and target flags are cleared for the search and OR-ed back
    /// afterwards. The road is then walked back from the target: each tile is appended to <paramref name="output"/>
    /// and gets <paramref name="flag"/>, Next(2) picks the scan order of the 8 neighbours (0: backwards), the first
    /// strictly lower one is taken, until the walk is within 0.5 of the source. All float-to-int conversions chop
    /// (FLDCW | 0xc00). Costs are summed at double precision (x87 register), distances stored as floats.
    /// </summary>
    void BuildRoad(RmgZone z, (float X, float Y) from, (float X, float Y) to, int flag, List<(float X, float Y)> output)
    {
        var f = Floors[z.Floor];
        int n = W * H;
        if (_roadDist == null || _roadDist.Length != n) _roadDist = new float[n];
        var dist = _roadDist;
        Array.Fill(dist, 1000f);
        int fx = (int)from.X, fy = (int)from.Y, tx = (int)to.X, ty = (int)to.Y;
        dist[f.At(fx, fy)] = 0;
        int savedFrom = f.Flags[f.At(fx, fy)];
        int savedTo = f.Flags[f.At(tx, ty)];
        f.Flags[f.At(tx, ty)] = 0;
        f.Flags[f.At(fx, fy)] = 0;
        if (FullScanRoadSearch) RoadSearchFullScan(f, z, dist, f.At(tx, ty)); else RoadSearch(f, z, dist, f.At(tx, ty));
        float cx = to.X, cy = to.Y;
        if (Dist(cx, cy, from) > 0.5f)                                                   // 0x00e0ab70
        {
            do
            {
                output.Add((cx, cy));
                f.Flags[f.At((int)cx, (int)cy)] |= flag;
                if (cy < 1 || cy >= H - 1 || cx < 1 || cx >= W - 1) break;
                int best = (int)dist[f.At((int)cx, (int)cy)];
                int order = Rng.Next(2);
                int dir = 0;
                bool found = false;
                for (int k = 0; k < 8; k++)
                {
                    int d = order == 0 ? 7 - k : k;
                    int nx = (int)(cx + RoadBack[d].Dx), ny = (int)(cy + RoadBack[d].Dy);
                    if (ny < 1 || ny >= H - 1 || nx < 1 || nx >= W - 1) continue;
                    float nd = dist[f.At(nx, ny)];
                    if (nd < best) { best = (int)nd; dir = d; found = true; }
                }
                if (!found) break;
                cx += RoadBack[dir].Dx;
                cy += RoadBack[dir].Dy;
                // The exe returns here without restoring the end-point flags.
                if (cy < 1 || cy >= H - 1 || cx < 1 || cx >= W - 1) return;
            } while (Dist(cx, cy, from) > 0.5f);
        }
        f.Flags[f.At(tx, ty)] |= savedTo;
        f.Flags[f.At(fx, fy)] |= savedFrom;
    }

    static double Dist(float x, float y, (float X, float Y) p)
    {
        double dx = x - p.X, dy = y - p.Y;
        return Math.Sqrt(dy * dy + dx * dx);
    }

    /// <summary>
    /// The pass loop of 0x0091aee0. The exe rescans the whole map every pass and expands the zone tiles whose chopped
    /// distance equals the pass number; here each pass visits only those tiles, in the same x-major order (our tile
    /// index x·H + y is that order): a tile lowered to the current pass ahead of the scan is still visited, one behind
    /// it is not, as in the exe. The "every zone tile reached" test counts the scanned tiles (first index 1..W-2,
    /// second 1..H-2) that are still at 1000 when the scan reaches them.
    /// </summary>
    void RoadSearch(RmgFloor f, RmgZone z, float[] dist, int target)
    {
        var buckets = _roadBuckets;
        foreach (var bl in buckets.Values) bl.Clear();
        bool Scanned(int i) { int x = i / H, y = i % H; return x >= 1 && x <= W - 2 && y >= 1 && y <= H - 2 && f.Zone[i] == z.Id; }
        int unreached = 0;
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (!Scanned(i)) continue;
            if (Math.Abs(dist[i] - 1000f) < 1e-4f) unreached++;                    // 0x00e3dee4
            else Bucket((int)dist[i]).Add(i);
        }
        var current = new SortedSet<int>();
        for (int pass = 0; ; )
        {
            int stillUnreached = unreached;   // tiles at 1000 when the scan reaches them
            current.Clear();
            if (buckets.TryGetValue(pass, out var bl)) { foreach (var i in bl) current.Add(i); bl.Clear(); }
            int pos = -1;
            while (current.Count > 0)
            {
                int i = current.Min;
                current.Remove(i);
                pos = i;
                if ((int)dist[i] != pass) continue;
                int x = i / H, y = i % H;
                int bd = f.BorderDist[i];
                double extra = bd < 5 ? 5 - bd : 0;
                double ortho = (100 - bd) * (double)0.01f + 1.0 + extra;             // 0x00e15a80, 0x00e0a7e4
                double diag = (100 - bd) * (double)0.01f + (double)1.41f + extra;    // 0x00e57da0
                for (int t = 0; t < 2; t++)
                {
                    var table = t == 0 ? RoadOrtho : RoadDiag;
                    double cost = t == 0 ? ortho : diag;
                    foreach (var (dx, dy) in table)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= W || ny < 0 || ny >= H) continue;
                        int j = f.At(nx, ny);
                        if (f.Flags[j] == TileFlags.Blocked) continue;
                        double c = cost + dist[i];
                        if (!(c < dist[j])) continue;
                        bool wasUnreached = Math.Abs(dist[j] - 1000f) < 1e-4f;
                        dist[j] = (float)c;
                        if (!Scanned(j)) continue;
                        if (wasUnreached)
                        {
                            unreached--;
                            if (j > pos) stillUnreached--;   // the scan meets it already reached
                        }
                        int k = (int)dist[j];
                        if (k == pass) { if (j > pos) current.Add(j); }
                        else if (k > pass) Bucket(k).Add(j);
                    }
                }
            }
            bool done = stillUnreached == 0;
            if (pass == 800) Log.Add("pure road algo failed.");
            else if (pass > 2000) break;
            if (dist[target] != 1000f) done = true;
            pass++;
            if (done) break;
        }
    }

    /// <summary>Test switch: run the literal transcription of the exe's pass loop instead of <see cref="RoadSearch"/>.</summary>
    [ThreadStatic] public static bool FullScanRoadSearch;

    /// <summary>The pass loop of 0x0091aee0 as written in the exe: a full scan per pass (reference for <see cref="RoadSearch"/>).</summary>
    void RoadSearchFullScan(RmgFloor f, RmgZone z, float[] dist, int target)
    {
        for (int pass = 0; ; )
        {
            bool done = true;
            for (int x = 1; x <= W - 2; x++)
                for (int y = 1; y <= H - 2; y++)
                {
                    int i = f.At(x, y);
                    if (f.Zone[i] != z.Id) continue;
                    if ((int)dist[i] == pass)
                    {
                        int bd = f.BorderDist[i];
                        double extra = bd < 5 ? 5 - bd : 0;
                        double ortho = (100 - bd) * (double)0.01f + 1.0 + extra;
                        double diag = (100 - bd) * (double)0.01f + (double)1.41f + extra;
                        for (int t = 0; t < 2; t++)
                            foreach (var (dx, dy) in t == 0 ? RoadOrtho : RoadDiag)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= W || ny < 0 || ny >= H) continue;
                                int j = f.At(nx, ny);
                                if (f.Flags[j] == TileFlags.Blocked) continue;
                                double c = (t == 0 ? ortho : diag) + dist[i];
                                if (c < dist[j]) dist[j] = (float)c;
                            }
                    }
                    if (Math.Abs(dist[i] - 1000f) < 1e-4f) done = false;
                }
            if (pass == 800) Log.Add("pure road algo failed.");
            else if (pass > 2000) break;
            if (dist[target] != 1000f) done = true;
            pass++;
            if (done) break;
        }
    }

    readonly Dictionary<int, List<int>> _roadBuckets = new();
    List<int> Bucket(int k)
    {
        if (!_roadBuckets.TryGetValue(k, out var l)) _roadBuckets[k] = l = new List<int>();
        return l;
    }

    /// <summary>
    /// exe 0x0091bdb0 (per zone, right after its treasures in 0x00907150): for every node i of the road-node list
    /// +0x68 a path (flag 0x20, output +0x8c) from node i to the nearest later node (strictly nearer than 1000, first
    /// one on ties), or to node 0 when there is none.
    /// </summary>
    void ConnectObjectNodes(RmgZone z)
    {
        var nodes = z.RoadNodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            int target = 0;
            double best = 1000f;                                                    // 0x00e156d8
            for (int j = i + 1; j < nodes.Count; j++)
            {
                double d = Dist(nodes[i].X, nodes[i].Y, nodes[j]);
                if (d < best) { best = d; target = j; }
            }
            BuildRoad(z, nodes[i], nodes[target], TileFlags.ObjectPath, z.ObjectPaths);
        }
    }

    /// <summary>
    /// exe 0x00904cf0: every floor, every zone of the floor's zone table in table order, 0x0091bf90:
    /// the network +0x74 starts at the town (+0x0c, when the town was placed: +0xf8) or else at the first connection
    /// point; each connection point (+0xc0, in order) is joined to its nearest network tile by a main road (flag 8,
    /// appended to +0x74). Then each mine point (+0x11c) is joined (flag 0x10, appended to +0x80) to the nearest of
    /// the network tiles with index % 13 == 5, or, when one is nearer, of the mine-road tiles with index % 11 == 7.
    /// </summary>
    void CreateRoads(RmgResult r)
    {
        foreach (var f in Floors)
            foreach (var z in f.Zones.Values)
                BuildZoneRoads(z);
    }

    void BuildZoneRoads(RmgZone z)
    {
        var net = z.Network;
        if (z.TownPlaced && z.TownPos is { } tp) net.Add((tp.X, tp.Y));
        else if (z.ConnectionPoints.Count > 0) net.Add(z.ConnectionPoints[0]);
        foreach (var c in z.ConnectionPoints)
        {
            int k = 0;
            double best = 1000f;
            for (int j = 0; j < net.Count; j++)
            {
                double d = Dist(c.X, c.Y, net[j]);
                if (d < best) { best = d; k = j; }
            }
            if (k >= net.Count) continue;
            var s = net[k];
            if (InMap(s) && InMap(c)) BuildRoad(z, s, c, TileFlags.MainRoad, net);
        }
        var mine = z.MineNetwork;
        foreach (var m in z.MinePoints)
        {
            int k = 0;
            double best = 1000f;
            for (int j = 0; j < net.Count; j++)
                if (j % 13 == 5)
                {
                    double d = Dist(m.X, m.Y, net[j]);
                    if (d < best) { best = d; k = j; }
                }
            bool onMine = false;
            for (int j = 0; j < mine.Count; j++)
                if (j % 11 == 7)
                {
                    double d = Dist(m.X, m.Y, mine[j]);
                    if (d < best) { best = d; k = j; onMine = true; }
                }
            if (onMine) BuildRoad(z, m, mine[k], TileFlags.MineRoad, mine);
            else if (k < net.Count) BuildRoad(z, m, net[k], TileFlags.MineRoad, mine);
        }
    }

    bool InMap((float X, float Y) p) => (int)p.X >= 0 && (int)p.X < W && (int)p.Y >= 0 && (int)p.Y < H;

    /// <summary>
    /// exe 0x00916570 (from 0x00904ad0, after the treasure blocks): every tile whose flags carry 8 or 0x10 is painted
    /// with the road tile of the race preset of its zone (preset +0x20 of the zone, Tiles +0x4c RoadTile when the flag
    /// 8 is set, else +0x58 SecondaryRoadTile). The exe paints the tile's four corners at strength 0xff; our layer has
    /// one entry per tile.
    /// </summary>
    void PaintRoads(RmgResult r)
    {
        foreach (var f in Floors)
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    int i = f.At(x, y);
                    int fl = f.Flags[i];
                    if ((fl & (TileFlags.MainRoad | TileFlags.MineRoad)) == 0) continue;
                    if (!f.Zones.TryGetValue(f.Zone[i], out var z)) continue;
                    var race = _db.Race(z.Race);
                    string tile = (fl & TileFlags.MainRoad) != 0 ? race.RoadTile : race.SecondaryRoadTile;
                    if (tile.Length > 0) r.TileLayers[f.Index][y * W + x] = tile;
                }
    }

}
