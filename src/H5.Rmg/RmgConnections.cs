namespace H5.Rmg;

/// <summary>
/// Zone connections (exe 0x00904920, "ZoneConnections"): every floor, every zone of the floor's zone table, 0x00926500
/// (0x009191a0(4) then the border junctions 0x00925a00); then every zone's vf 0x2c, the teleports (CGameZone 0x0091dbf0).
/// </summary>
public sealed partial class RmgGenerator
{
    const string MonolithTwoWay = "MapObjects/Monolith_Two_Way.(AdvMapBuildingShared).xdb";          // 0x00ff9aa4
    const string GateIn = "MapObjects/Subterranean_Gate_In.(AdvMapBuildingShared).xdb";              // 0x00ff9ac4
    const string GateOut = "MapObjects/Subterranean_Gate_Out.(AdvMapBuildingShared).xdb";            // 0x00ff9ae4

    void CreateConnections(RmgResult r)
    {
        foreach (var f in Floors)
            foreach (var z in f.Zones.Values)
            {
                DistanceField(z, 4);
                BorderJunctions(r, z);
            }
        foreach (var f in Floors)
            foreach (var z in f.Zones.Values)
                Teleports(r, z);
    }

    /// <summary>The first template connection between the two zones (destination checked first, as the exe does).</summary>
    RmgConnectionDef? ConnectionBetween(int own, int other) =>
        _t.Connections.FirstOrDefault(c => (c.Dest == own && c.Source == other) || (c.Source == own && c.Dest == other));

    /// <summary>
    /// exe 0x00925a00. Over the zone's tiles with JunctionMinBorderDistance ≤ x &lt; W − it (y likewise), x major: the 8
    /// neighbours (0x00f46798) outside the zone are counted per zone id; a tile with exactly one such id seen 3 to 5
    /// times, and a template connection between the two zones, becomes a junction candidate of that neighbour. Then
    /// per neighbour (hash order) not yet joined (+0xd8) with more than 7 candidates: Next(n) picks the junction, which
    /// and its 4 orthogonal neighbours in the zone get border distance 1; a guard of BasicLevelGuardPower x
    /// ConnectionGuardLevel x the connection's GuardStrenght, angle 0 (0x00ba7690, no tile flag); the junction joins the
    /// connection points +0xc0, the guard points +0x98 and the nodes +0x68; the neighbour takes its side
    /// (0x00904bc0 → 0x0091a950) and is marked joined.
    /// </summary>
    void BorderJunctions(RmgResult r, RmgZone z)
    {
        var f = Floors[z.Floor];
        int p = _db.Params.JunctionMinBorderDistance;
        var cands = new NivalHashMap<List<(int X, int Y)>>();
        for (int x = p; x < W - p; x++)
            for (int y = p; y < H - p; y++)
            {
                if (f.Zone[f.At(x, y)] != z.Id) continue;
                var counts = new NivalHashMap<int>();
                foreach (var (dx, dy) in Around8)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!f.In(nx, ny)) continue;
                    int id = f.Zone[f.At(nx, ny)];
                    if (id == z.Id) continue;
                    counts[id] = (counts.TryGetValue(id, out var c) ? c : 0) + 1;
                }
                if (counts.Count != 1) continue;
                var (key, n) = counts.Items().First();
                if (n <= 2 || n >= 6 || ConnectionBetween(z.Id, key) == null) continue;
                if (!cands.TryGetValue(key, out var list)) { list = new List<(int, int)>(); cands.Add(key, list); }
                list.Add((x, y));
            }
        foreach (var (key, list) in cands.Items().ToList())
        {
            if (z.ConnectedZones.Contains(key) || list.Count <= 7) continue;
            var (jx, jy) = list[Rng.Next(list.Count)];
            f.BorderDist[f.At(jx, jy)] = 1;
            foreach (var (dx, dy) in Ortho4)
            {
                int nx = jx + dx, ny = jy + dy;
                if (f.In(nx, ny) && f.Zone[f.At(nx, ny)] == z.Id) f.BorderDist[f.At(nx, ny)] = 1;
            }
            int strength = ConnectionBetween(z.Id, key)?.GuardStrength ?? 0;
            PlaceGuard(r, z, jx, jy, _db.Params.BasicLevelGuardPower * _db.Params.ConnectionGuardLevel * strength, "connection guard", 0f);
            z.ConnectionPoints.Add((jx, jy));
            z.GuardPoints.Add((jx, jy));
            z.RoadNodes.Add((jx, jy));
            r.Passages.Add((z.Floor, z.Id, key, jx, jy));
            if (f.Zones.TryGetValue(key, out var other)) JunctionOtherSide(r, other, jx, jy, z.Id);
            z.ConnectedZones.Add(key);
        }
    }

    /// <summary>
    /// exe 0x0091a950 on the neighbour: its first tile among the junction's orthogonal (0x00f467d8) then diagonal
    /// (0x00f467f8) neighbours; the other zone is marked joined (+0xd8); the tile joins +0xc0, +0x98 and +0x68 and it and
    /// its 4 orthogonal neighbours in the zone get border distance 1.
    /// </summary>
    void JunctionOtherSide(RmgResult r, RmgZone z, int jx, int jy, int otherId)
    {
        var f = Floors[z.Floor];
        (int X, int Y)? q = null;
        foreach (var table in new[] { Ortho4, Diag4 })
        {
            foreach (var (dx, dy) in table)
            {
                int nx = jx + dx, ny = jy + dy;
                if (f.In(nx, ny) && f.Zone[f.At(nx, ny)] == z.Id) { q = (nx, ny); break; }
            }
            if (q != null) break;
        }
        z.ConnectedZones.Add(otherId);
        if (q is not { } t) return;
        z.ConnectionPoints.Add(t);
        z.GuardPoints.Add(t);
        z.RoadNodes.Add(t);
        r.Passages.Add((z.Floor, z.Id, otherId, t.X, t.Y));
        f.BorderDist[f.At(t.X, t.Y)] = 1;
        foreach (var (dx, dy) in Ortho4)
        {
            int nx = t.X + dx, ny = t.Y + dy;
            if (f.In(nx, ny) && f.Zone[f.At(nx, ny)] == z.Id) f.BorderDist[f.At(nx, ny)] = 1;
        }
    }

    /// <summary>exe 0x00904190: the floor of a zone id (0 when on the surface table, 1 when found underground, else −1).</summary>
    int FloorOfZone(int id)
    {
        if (Floors[0].Zones.ContainsKey(id)) return 0;
        return Floors.Count == 2 && Floors[1].Zones.ContainsKey(id) ? 1 : -1;
    }

    /// <summary>exe 0x00918a90: a teleport faces away from the zone's point +0x0c: |dx| ≤ |dy| → π (dy &lt; 0) or 0, else 3π/2 (dx &gt; 0) or π/2.</summary>
    float TeleportFacing(RmgZone z, int x, int y)
    {
        var (tx, ty) = z.TownPos ?? (0, 0);
        float dx = x - tx, dy = y - ty;
        if (MathF.Abs(dx) <= MathF.Abs(dy)) return dy < 0f ? 3.1415927f : 0f;
        return dx > 0f ? 4.712389f : 1.5707964f;
    }

    /// <summary>
    /// exe 0x0091dbf0 (CGameZone vf 0x2c): for each template connection of the zone whose other zone is not joined yet
    /// (+0xd8): candidates are the zone tiles with 2 &lt; border distance &lt; 10, then 0x009191a0(4) and those with distance
    /// &gt; 2M/3 (either list empty ends all of the zone's teleports). The object: Monolith_Two_Way when the other zone is
    /// on the same floor, else Subterranean_Gate_In (surface) / _Out. Next(n) picks a tile, the angle faces away from the
    /// zone point (0x00918a90), 0x00919be0 tests it (a failure erases the tile; exhausted: "cant find empty tiles to set
    /// teleport" and the end); 0x00914980 creates it (GroupID = min·100 + max) and 0x0091a420 stamps it with the
    /// connection list +0xc0. Its guard (power chop(BasicLevelGuardPower x ConnectionGuardLevel x GuardStrenght x
    /// 0.7071 + 0.5), turned by the angle, no tile flag, joins +0x98): from k = chop(angle x 4/π + 5.2124) the first of
    /// the last connection point's orthogonal neighbours (k &amp; 3 …), else diagonal ones, whose flags carry a road bit or are 0.
    /// </summary>
    void Teleports(RmgResult r, RmgZone z)
    {
        var f = Floors[z.Floor];
        foreach (var c in _t.Connections)
        {
            int other;
            if (c.Dest == z.Id) other = c.Source;
            else if (c.Source == z.Id) other = c.Dest;
            else continue;
            if (z.ConnectedZones.Contains(other)) continue;
            var cands = z.Tiles.Where(t => f.BorderDist[f.At(t.X, t.Y)] is > 2 and < 10).ToList();
            DistanceField(z, 4);
            int max = MaxDist(z, cands);
            if (cands.Count == 0) return;
            var far = cands.Where(t => f.Dist[f.At(t.X, t.Y)] > max * 2 / 3).ToList();
            if (far.Count == 0) return;
            int otherFloor = FloorOfZone(other);
            string shared = otherFloor == z.Floor ? MonolithTwoWay : z.Floor == 0 ? GateIn : GateOut;
            if (!_db.Xdb.FileSystem.Exists(shared)) return;
            var d = Catalog.Get(shared);
            RmgObject? o = null;
            float angle = 0;
            while (o == null)
            {
                if (far.Count == 0)
                {
                    r.Warnings.Add($"cant find empty tiles to set teleport, zone {z.Id}, connection to {other}");
                    return;
                }
                int i = Rng.Next(far.Count);
                var (x, y) = far[i];
                angle = TeleportFacing(z, x, y);
                if (!FootprintFits(z, x, y, angle, d)) { far.RemoveAt(i); continue; }
                o = CreateObject(r, z, shared, x, y, angle, "teleport");
                Stamp(z, x, y, angle, d, z.ConnectionPoints);
            }
            r.Passages.Add((z.Floor, z.Id, other, o.X, o.Y));
            var v = z.ConnectionPoints[^1];
            int vx = (int)v.X, vy = (int)v.Y;
            int k = (int)(angle * 1.2732395f + 5.212389f);                                     // 0x00e57dd0, 0x00e57e20
            int power = (int)((float)(_db.Params.BasicLevelGuardPower * _db.Params.ConnectionGuardLevel * c.GuardStrength) * 0.70710677f + 0.5f);
            bool found = false;
            foreach (var table in new[] { Ortho4, Diag4 })
            {
                for (int u = k; u < k + 4 && !found; u++)
                {
                    var (dx, dy) = table[u & 3];
                    int gx = vx + dx, gy = vy + dy;
                    if (!f.In(gx, gy) || !GuardTileFree(f.Flags[f.At(gx, gy)])) continue;
                    PlaceGuard(r, z, gx, gy, power, "teleport guard", angle);
                    z.GuardPoints.Add((gx, gy));
                    found = true;
                }
                if (found) break;
            }
        }
    }
}
