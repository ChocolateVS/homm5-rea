using H5.Adventure.Map;

namespace H5.Rmg;

/// <summary>An object placed by the generator, in generator coordinates.</summary>
public sealed class RmgObject
{
    public required string Shared;
    public required SharedDef Def;
    public int X, Y, Floor;
    public int Quarter;              // rotation in quarter turns (exe draws rand(4) · π/2)
    public int Owner = -1;
    public int Zone;
    public string? Creature;
    public int Amount;
    public string? Note;             // what placed it (mine, guard, shop...)
    /// <summary>Further guard stacks of a two-stack army template (written as AdditionalStacks, exe 0x00914cc0).</summary>
    public List<(string Creature, int Count)> AdditionalStacks = new();
    public float? Angle;             // rotation passed in radians (guards turned by their caller, treasure-block items)
    public float Rot => Angle ?? Quarter * MathF.PI / 2;
}

public sealed partial class RmgGenerator
{
    SharedCatalog? _catalog;
    SharedCatalog Catalog => _catalog ??= new SharedCatalog(_db.Xdb);

    /// <summary>
    /// The screen's Resources / Experience combos (InitialParams.ResourceMultiplier = generator +0xa8,
    /// ExpMultiplier = +0xb0; RESOURCE_/EXP_MISERABLE, LITTLE, NORMAL, LOTS, MUCH = 0..4) select these factors
    /// (switches at 0x00905eac / 0x00905fc6 and 0x00907150): +0xa8 scales TreasureDensity, +0xb0
    /// TreasureChestDensity and UpgBuildingsDensity.
    /// </summary>
    static readonly float[] Multipliers = { 0.2f, 0.5f, 1f, 2f, 4f };
    static float Multiplier(int level) => level is >= 0 and <= 4 ? Multipliers[level] : 1f;

    /// <summary>Monster strength coefficients by monster level (exe 0x00ba7690: 0.4, 0.9, 1.7, 4, 12).</summary>
    static readonly float[] MonsterCoef = { 0.4f, 0.9f, 1.7f, 4f, 12f };

    static readonly (int Dx, int Dy)[] Neighbours4 = { (0, 1), (1, 0), (0, -1), (-1, 0) };

    // ------------------------------------------------------------------ placement primitives

    IEnumerable<(int X, int Y)> Footprint(SharedDef d, int x, int y, int quarter, bool includeActive)
    {
        float rot = quarter * MathF.PI / 2;
        foreach (var b in d.Blocked)
        {
            var t = H5.Adventure.Map.Footprint.Place(new Tile(x, y), b, rot);
            yield return (t.X, t.Y);
        }
        if (includeActive)
            foreach (var a in d.Active)
            {
                var t = H5.Adventure.Map.Footprint.Place(new Tile(x, y), a, rot);
                yield return (t.X, t.Y);
            }
    }

    (int X, int Y) ActiveTile(SharedDef d, int x, int y, int quarter)
    {
        if (d.Active.Count == 0) return (x, y);
        var t = H5.Adventure.Map.Footprint.Place(new Tile(x, y), d.Active[0], quarter * MathF.PI / 2);
        return (t.X, t.Y);
    }

    /// <summary>
    /// Footprint test (exe 0x00919be0 + 0x009145b0): every blocked/active tile inside the zone,
    /// off the zone border and free, and the visit tile reachable from a free neighbour.
    /// </summary>
    bool CanPlace(RmgZone z, SharedDef d, int x, int y, int quarter, out (int X, int Y) access, HashSet<(int, int)>? reachable = null)
    {
        access = (-1, -1);
        var f = Floors[z.Floor];
        var tiles = Footprint(d, x, y, quarter, includeActive: true).ToList();
        if (tiles.Count == 0) tiles.Add((x, y));
        foreach (var (tx, ty) in tiles)
        {
            if (!f.In(tx, ty)) return false;
            int i = f.At(tx, ty);
            if (f.Zone[i] != z.Id || f.BorderDist[i] < 1 || f.Occ[i] != Occ.Free) return false;
        }
        if (d.Active.Count == 0) { access = (x, y); return true; }
        var set = new HashSet<(int, int)>(tiles);
        var (ax, ay) = ActiveTile(d, x, y, quarter);
        foreach (var (dx, dy) in Neighbours8)
        {
            int nx = ax + dx, ny = ay + dy;
            if (!f.In(nx, ny) || set.Contains((nx, ny))) continue;
            int i = f.At(nx, ny);
            if (f.Zone[i] == z.Id && f.Occ[i] is Occ.Free or Occ.Road && f.BorderDist[i] >= 1 && (reachable == null || reachable.Contains((nx, ny))))
            { access = (nx, ny); return true; }
        }
        return false;
    }

    RmgObject Commit(RmgResult r, RmgZone z, string shared, SharedDef d, int x, int y, int quarter, (int X, int Y) access, string note)
    {
        CreateObjectDraws();   // 0x009145b0 between the footprint test and 0x0091a420
        var f = Floors[z.Floor];
        var active = new HashSet<(int, int)>(d.Active.Count > 0 ? new[] { ActiveTile(d, x, y, quarter) } : Array.Empty<(int, int)>());
        foreach (var (tx, ty) in Footprint(d, x, y, quarter, includeActive: false))
            if (f.In(tx, ty)) { f.Occ[f.At(tx, ty)] = Occ.Object; f.Flags[f.At(tx, ty)] = TileFlags.Blocked; }
        // 0x0091a420: blockedTiles get flag 2, then every activeTiles entry flag 4 and a node on the zone list +0x68.
        foreach (var a in d.Active)
        {
            var t = H5.Adventure.Map.Footprint.Place(new Tile(x, y), a, quarter * MathF.PI / 2);
            if (!f.In(t.X, t.Y)) continue;
            f.Flags[f.At(t.X, t.Y)] = TileFlags.Active;
            z.RoadNodes.Add((t.X, t.Y));
        }
        foreach (var (tx, ty) in active)
            if (f.In(tx, ty)) f.Occ[f.At(tx, ty)] = Occ.Entrance;
        if (access.X >= 0 && f.In(access.X, access.Y) && f.Occ[f.At(access.X, access.Y)] == Occ.Free)
            f.Occ[f.At(access.X, access.Y)] = Occ.Road;
        var o = new RmgObject { Shared = shared, Def = d, X = x, Y = y, Floor = z.Floor, Quarter = quarter, Zone = z.Id, Note = note };
        r.Objects.Add(o);
        r.Access[o] = access;
        NoteObject(o);
        return o;
    }

    /// <summary>Picks a random candidate tile and rotation until one fits (pattern shared by all placers).</summary>
    RmgObject? PlaceRandom(RmgResult r, RmgZone z, string shared, List<(int X, int Y)> candidates, string note, HashSet<(int, int)>? reachable = null)
    {
        var d = Catalog.Get(shared);
        var pool = candidates.ToList();
        while (pool.Count > 0)
        {
            int i = Rng.Next(pool.Count);
            var (x, y) = pool[i];
            int q = Rng.Next(4);
            if (CanPlace(z, d, x, y, q, out var access, reachable)) return Commit(r, z, shared, d, x, y, q, access, note);
            pool.RemoveAt(i);
        }
        r.Warnings.Add($"Can't place {note} {shared} at zone #{z.Id}, floor {z.Floor}");
        return null;
    }

    /// <summary>
    /// exe 0x009191a0 with flag 4: distance from each zone tile to the nearest object already
    /// placed in the zone (towns included); used to spread objects out. Kept incrementally:
    /// initialised once per zone, then lowered as objects are committed.
    /// </summary>
    readonly HashSet<(int Floor, int Zone)> _distReady = new();

    void ComputeObjectDistances(RmgResult r, RmgZone z)
    {
        if (!_distReady.Add((z.Floor, z.Id))) return;
        var f = Floors[z.Floor];
        foreach (var (x, y) in z.Tiles) f.Dist[f.At(x, y)] = 10000;
        foreach (var o in r.Objects.Where(o => o.Zone == z.Id && o.Floor == z.Floor && o.Def.Tag != "AdvMapStaticShared"))
            LowerDistances(z, o.X, o.Y);
        if (z.TownPos is { } tp) LowerDistances(z, tp.X, tp.Y);
    }

    void LowerDistances(RmgZone z, int px, int py)
    {
        var f = Floors[z.Floor];
        foreach (var (x, y) in z.Tiles)
        {
            float dx = x - px, dy = y - py;
            int d = (int)MathF.Round(MathF.Sqrt(dx * dx + dy * dy));
            int i = f.At(x, y);
            if (d < f.Dist[i]) f.Dist[i] = d;
        }
    }

    void NoteObject(RmgObject o)
    {
        if (o.Def.Tag == "AdvMapStaticShared" || !_distReady.Contains((o.Floor, o.Zone))) return;
        var z = Zones.FirstOrDefault(zz => zz.Id == o.Zone && zz.Floor == o.Floor);
        if (z != null) LowerDistances(z, o.X, o.Y);
    }

    /// <summary>
    /// exe 0x0091bb10 (prisons, cartographers, shrines, resource/treasury/luck-morale buildings, shops, obelisks,
    /// Redwood Observatory, Den of Thieves): the object distance field (0x009191a0 flag 4), its maximum M over
    /// the zone's tiles with border distance &gt; 2 and occupancy != 2 (0x00919710), then every zone tile with
    /// distance &gt; 2M/3 and border distance &gt; 0. No fallback: an empty list places nothing.
    /// </summary>
    List<(int X, int Y)> FarCandidates(RmgResult r, RmgZone z)
    {
        ComputeObjectDistances(r, z);
        var f = Floors[z.Floor];
        int max = 0;
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Zone[i] == z.Id && f.BorderDist[i] > 2 && f.Occ[i] != Occ.Near && f.Dist[i] > max) max = f.Dist[i];
        }
        return z.Tiles.Where(t => f.Dist[f.At(t.X, t.Y)] > max * 2 / 3 && f.BorderDist[f.At(t.X, t.Y)] > 0).ToList();
    }

    /// <summary>Candidates far from other objects: distance above <paramref name="fraction"/> of the zone's maximum (exe 0x00919710).</summary>
    List<(int X, int Y)> SpreadCandidates(RmgResult r, RmgZone z, IEnumerable<(int X, int Y)> tiles, int num, int den)
    {
        ComputeObjectDistances(r, z);
        var f = Floors[z.Floor];
        var list = tiles.Where(t => f.BorderDist[f.At(t.X, t.Y)] > 2 && f.Occ[f.At(t.X, t.Y)] != Occ.Near).ToList();
        int max = 0;
        foreach (var (x, y) in list) max = Math.Max(max, f.Dist[f.At(x, y)]);
        int threshold = max * num / den;
        var far = list.Where(t => f.Dist[f.At(t.X, t.Y)] > threshold && f.BorderDist[f.At(t.X, t.Y)] > 0).ToList();
        return far.Count > 0 ? far : list;
    }

    // ------------------------------------------------------------------ guards (exe 0x00ba7690)

    /// <summary>
    /// Guard army for a guard power (exe 0x00ba7690). Power below 100 places nothing (no draws). The value is
    /// round(power x MonsterCoef[level]); then Float(0,1) &lt; 0.6 tries a two-stack army template with
    /// round(value x 0.9) (0x00ba71b0), and otherwise, or when no template fits, one stack (0x00ba6910).
    /// Returns the stacks (main stack first) or null.
    /// </summary>
    List<(RmgCreature Creature, int Count)>? PickGuard(int power)
    {
        if (power < 100) return null;
        int value = (int)Math.Round((double)power * MonsterCoef[Math.Clamp(MonsterLevel, 0, 4)], MidpointRounding.ToEven);
        if (Rng.Float(0, 1) < 0.6f)
        {
            var two = PickArmyTemplate((int)Math.Round((double)value * 0.9f, MidpointRounding.ToEven));
            if (two != null) return two;
        }
        var one = PickSingleStack(value);
        return one == null ? null : new List<(RmgCreature, int)> { one.Value };
    }

    RmgCreature? CreatureAt(int index) => index >= 0 && index < _db.Creatures.Count && _db.Creatures[index].Index == index
        ? _db.Creatures[index] : _db.Creatures.FirstOrDefault(c => c.Index == index);

    /// <summary>
    /// exe 0x00ba71b0: templates of the group whose [MinPower, MaxPower] contains the value, one picked with
    /// rand(n); each stack gets coef x (value / sum(coef x power)), the last one also the remainder / its power.
    /// Accepted (0x00ba7690) when the first stack has a creature and a count.
    /// </summary>
    List<(RmgCreature Creature, int Count)>? PickArmyTemplate(int value)
    {
        if (value < 100) return null;
        var fits = new List<int>();
        for (int i = 0; i < _db.ArmyTemplates.Count; i++)
            if (value >= _db.ArmyTemplates[i].MinPower && value <= _db.ArmyTemplates[i].MaxPower) fits.Add(i);
        if (fits.Count == 0) return null;
        var t = _db.ArmyTemplates[fits[Rng.Next(fits.Count)]];
        var stacks = new List<(RmgCreature? C, int Count)>();
        int sum = 0;
        foreach (var (cr, coef) in t.Stacks)
        {
            var c = CreatureAt(cr);
            stacks.Add((c, coef));
            sum += coef * (c?.Power ?? 0);
        }
        if (stacks.Count == 0 || sum <= 0) return null;
        int mult = value / sum, left = value;
        for (int i = 0; i < stacks.Count; i++)
        {
            int pw = stacks[i].C?.Power ?? 0;
            int count = stacks[i].Count * mult;
            left -= count * pw;
            if (i == stacks.Count - 1 && left > 0 && pw > 0) { count += left / pw; left -= left / pw * pw; }
            stacks[i] = (stacks[i].C, count);
        }
        if (stacks[0].C is not { Index: not 0 } || stacks[0].Count == 0) return null;
        return stacks.Where(st => st.C != null).Select(st => (st.C!, st.Count)).ToList();
    }

    /// <summary>
    /// exe 0x00ba6910: r = rand(30); every creature except ids 0, 0x59, 0x72 whose Power x (r+10) lies within
    /// a factor k of the value is appended (k = 1.1, x1.2 per pass; the list is not cleared between passes)
    /// until it holds at least 10 entries; one is picked with rand(n); count = trunc(value / Power).
    /// </summary>
    (RmgCreature Creature, int Count)? PickSingleStack(int value)
    {
        if (value < 100) return null;
        int r = Rng.Next(30);
        var cands = new List<RmgCreature>();
        float k = 1.1f;
        for (int pass = 0; cands.Count < 10 && pass < 200; pass++)
        {
            foreach (var c in _db.Creatures)
            {
                if (c.Index == 0 || c.Index == 0x59 || c.Index == 0x72) continue;
                // x87 at double precision; k is stored as a float after each pass (FSTP at 0x00ba6a7d).
                double lo = (double)(r + 10) / k * c.Power, hi = (double)(r + 10) * k * c.Power;
                if (lo <= value && value <= hi) cands.Add(c);
            }
            k *= 1.2f;
        }
        if (cands.Count == 0) return null;
        var pick = cands[Rng.Next(cands.Count)];
        return (pick, (int)((float)value / pick.Power));
    }

    /// <summary>
    /// exe 0x009145b0, the map-object creator every placer calls once its footprint test passed (and the guard
    /// creators 0x00914b40 / 0x00914cc0, the treasure blocks' 0x00915010): the object's name gets the number
    /// (Next(0xffff) &lt;&lt; 16) + Next(0xffff), two draws.
    /// </summary>
    void CreateObjectDraws()
    {
        Rng.Next(0xffff);
        Rng.Next(0xffff);
    }

    /// <summary>
    /// A guard (0x00ba7690 → 0x00ba6f00 → 0x00914b40 / 0x00914cc0). The caller passes the facing in the exe; ours
    /// is <paramref name="angle"/> when given, else none (TODO(exe): the mine, town and connection callers' angles).
    /// </summary>
    RmgObject? PlaceGuard(RmgResult r, RmgZone z, int x, int y, int power, string note, float? angle = null)
    {
        var f = Floors[z.Floor];
        if (!f.In(x, y)) return null;
        var g = PickGuard(power);
        if (g == null || g[0].Creature.MonsterShared == null) return null;
        var (c, count) = g[0];
        var d = Catalog.Get(c.MonsterShared!);
        CreateObjectDraws();
        f.Occ[f.At(x, y)] = Occ.Guard;
        var o = new RmgObject
        {
            Shared = c.MonsterShared!, Def = d, X = x, Y = y, Floor = z.Floor, Zone = z.Id, Angle = angle,
            Creature = c.Id, Amount = count, Note = note,
        };
        o.AdditionalStacks.AddRange(g.Skip(1).Select(st => (st.Creature.Id, st.Count)));
        r.Objects.Add(o);
        NoteObject(o);
        return o;
    }

    int GuardPower(int level) => _db.Params.BasicLevelGuardPower * level;

    // ------------------------------------------------------------------ terrain

    void PaintTerrain(RmgResult r)
    {
        foreach (var f in Floors)
        {
            var tiles = new string[W * H];
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    int id = f.Zone[f.At(x, y)];
                    string tile = f.Index == 0 ? _db.Params.DefaultSurfaceTile : _db.Params.DefaultSubterraTile;
                    if (id != -1 && f.Zones.TryGetValue(id, out var z) && _db.Race(z.Race).DefaultTile is { Length: > 0 } t) tile = t;
                    tiles[y * W + x] = tile;
                }
            r.TileLayers.Add(tiles);
        }
    }

    // ------------------------------------------------------------------ towns (exe 0x009058e0)

    void PlaceTowns(RmgResult r)
    {
        foreach (var z in Zones.OrderBy(z => z.Def.Index))
        {
            var f = Floors[z.Floor];
            if (!z.Def.Town)
            {
                // exe 0x00919810: zones without a town anchor their objects at the tile centroid.
                if (z.Tiles.Count > 0)
                    z.TownPos = ((int)MathF.Round(z.Tiles.Sum(t => (float)t.X) / z.Tiles.Count), (int)MathF.Round(z.Tiles.Sum(t => (float)t.Y) / z.Tiles.Count));
                z.AnchorOnly = true;
                continue;
            }
            var race = _db.Race(z.Race);
            if (race.TownProto == null) continue;
            var d = Catalog.Get(race.TownProto);
            // exe 0x00923e50: candidates are zone tiles deeper than R/2; each try draws a tile and a
            // quarter turn; the town's anchor (its first active tile) must be at least 2R/3 from the
            // border (relaxed after 100 tries); footprint failures drop the candidate.
            var cands = z.Tiles.Where(t => f.BorderDist[f.At(t.X, t.Y)] > z.Radius / 2).ToList();
            RmgObject? town = null;
            for (int tries = 0; cands.Count > 0 && town == null; tries++)
            {
                int i = Rng.Next(cands.Count);
                var (cx, cy) = cands[i];
                int q = Rng.Next(4);
                var (ax, ay) = ActiveTile(d, cx, cy, q);
                if (ay < 1 || ay >= H - 1 || ax < 1 || ax >= W - 1) continue;
                if (f.BorderDist[f.At(ax, ay)] < z.Radius * 2 / 3 && tries < 100) continue;
                if (!CanPlace(z, d, cx, cy, q, out var access)) { cands.RemoveAt(i); continue; }
                town = Commit(r, z, race.TownProto, d, cx, cy, q, access, "town");
                z.TownPos = (ax, ay);
                z.TownPlaced = true;
                // After 0x009145b0's two draws (in Commit), the town's point light draws twice (0x0092a400 / 0x00babbf0).
                Rng.Next(2);
                Rng.Next(2);
            }
            if (town == null) { r.Warnings.Add($"no room for town in zone {z.Id}"); continue; }
            if (z.Player > 0)
            {
                town.Owner = z.Player - 1;
                r.StartTowns[z.Player - 1] = town;
            }
            else if (z.Def.TownGuardStrength > 0 && r.Access.TryGetValue(town, out var acc))
                PlaceGuard(r, z, acc.X, acc.Y, GuardPower(z.Def.TownGuardStrength), "town guard");
            // Decorative objects over the town centre (OverTownCenterObjects) are effects only.
        }
    }

    // ------------------------------------------------------------------ connections (exe 0x00904920)

    void CreateConnections(RmgResult r)
    {
        foreach (var c in _t.Connections)
        {
            var a = Zones.FirstOrDefault(z => z.Id == c.Source);
            var b = Zones.FirstOrDefault(z => z.Id == c.Dest);
            if (a == null || b == null) continue;
            int guard = c.Guarded ? GuardPower(c.GuardStrength) : 0;
            if (a.Floor == b.Floor && TryBorderPassage(r, a, b, c.Wide, guard)) continue;
            // Zones that do not touch (or lie on different floors) are linked by a teleport pair.
            PlaceTeleportPair(r, a, b, guard);
        }
    }

    bool TryBorderPassage(RmgResult r, RmgZone a, RmgZone b, bool wide, int guard)
    {
        var f = Floors[a.Floor];
        var pairs = new List<(int X, int Y, int Bx, int By)>();
        foreach (var (x, y) in a.Tiles)
        {
            if (f.BorderDist[f.At(x, y)] != 0) continue;
            if (x < _db.Params.JunctionMinBorderDistance || y < _db.Params.JunctionMinBorderDistance ||
                x >= W - _db.Params.JunctionMinBorderDistance || y >= H - _db.Params.JunctionMinBorderDistance) continue;
            foreach (var (dx, dy) in Neighbours4)
            {
                int nx = x + dx, ny = y + dy;
                if (f.In(nx, ny) && f.Zone[f.At(nx, ny)] == b.Id) { pairs.Add((x, y, nx, ny)); break; }
            }
        }
        if (pairs.Count == 0) return false;
        // Prefer border points near the line between the two zone centres.
        float mx = (a.CenterX + b.CenterX) / 2, my = (a.CenterY + b.CenterY) / 2;
        var near = pairs.OrderBy(p => (p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my)).Take(Math.Max(1, pairs.Count / 3)).ToList();
        var pick = near[Rng.Next(near.Count)];
        int width = wide ? 3 : 1;
        // Carve the passage: the border tiles on both sides plus a step inward, `width` wide.
        int pdx = pick.Bx - pick.X, pdy = pick.By - pick.Y;
        for (int w = -(width / 2); w <= width / 2; w++)
            for (int s = -2; s <= 3; s++)
            {
                int tx = pick.X + pdx * s + pdy * w, ty = pick.Y + pdy * s + pdx * w;
                if (!f.In(tx, ty)) continue;
                int i = f.At(tx, ty);
                if (f.Occ[i] == Occ.Free) f.Occ[i] = Occ.Passage;
            }
        r.Passages.Add((a.Floor, a.Id, b.Id, pick.X, pick.Y));
        r.Passages.Add((a.Floor, b.Id, a.Id, pick.Bx, pick.By));
        // 0x00925a00: each side's connection point goes to the zone's connection list +0xc0 and its node list +0x68.
        a.ConnectionPoints.Add((pick.X, pick.Y)); a.RoadNodes.Add((pick.X, pick.Y)); a.GuardPoints.Add((pick.X, pick.Y));
        b.ConnectionPoints.Add((pick.Bx, pick.By)); b.RoadNodes.Add((pick.Bx, pick.By)); b.GuardPoints.Add((pick.Bx, pick.By));
        if (guard > 0) PlaceGuard(r, a, pick.X, pick.Y, guard, "connection guard");
        return true;
    }

    void PlaceTeleportPair(RmgResult r, RmgZone a, RmgZone b, int guard)
    {
        const string monolith = "MapObjects/Monolith_Two_Way.(AdvMapBuildingShared).xdb";
        if (!_db.Xdb.FileSystem.Exists(monolith)) return;
        foreach (var z in new[] { a, b })
        {
            var f = Floors[z.Floor];
            var cands = z.Tiles.Where(t =>
            {
                int d = f.BorderDist[f.At(t.X, t.Y)];
                return d >= _db.Params.TeleportMinBorderDistance && d <= _db.Params.TeleportMaxBorderDistance;
            }).ToList();
            var o = PlaceRandom(r, z, monolith, cands, "teleport");
            if (o == null) continue;
            r.Passages.Add((z.Floor, z.Id, z == a ? b.Id : a.Id, o.X, o.Y));
            // TODO(exe): which point a teleport adds to +0xc0 is not traced (0x0091a950 writes the list); ours: its visit tile.
            var (vx, vy) = ActiveTile(o.Def, o.X, o.Y, o.Quarter);
            z.ConnectionPoints.Add((vx, vy));
            if (guard > 0 && r.Access.TryGetValue(o, out var acc)) PlaceGuard(r, z, acc.X, acc.Y, guard, "teleport guard");
        }
    }

    // ------------------------------------------------------------------ main objects (exe 0x00907150)

    void PlaceMainObjects(RmgResult r)
    {
        // exe 0x00907150: with the Graal option one position in the template's zone list is drawn up front
        // (Next(template zones)), otherwise one draw is spent. Zones are then visited in template list order.
        int grailZone = -1;
        if (_o.Grail) grailZone = Rng.Next(_t.Zones.Count); else Rng.Advance();
        for (int zi = 0; zi < _t.Zones.Count; zi++)
        {
            var z = Zones.FirstOrDefault(zz => zz.Def == _t.Zones[zi]);
            if (z == null) continue;
            var race = _db.Race(z.Race);
            PlaceMines(r, z, race);
            for (int i = 0; i < z.Def.AbandonedMines && race.AbandonedMine != null; i++)
            {
                var o = PlaceRandom(r, z, race.AbandonedMine, SpreadCandidates(r, z, z.Tiles, 4, 5), "abandoned mine");
                if (o != null && r.Access.TryGetValue(o, out var acc)) PlaceGuard(r, z, acc.X, acc.Y, GuardPower(_db.Params.Mine2LevelGuardLevel), "mine guard");
            }
            PlaceDwellings(r, z, race);
            if (_o.Grail) PlaceGrailAndObelisks(r, z, zi == grailZone);
            // Upgrade buildings: density x the +0xb0 factor, rounded to nearest (0x00907150 -> 0x0091f140).
            SpendBudget(r, z, race.Upgrades,
                z.Tiles.Count * (int)MathF.Round(z.Def.UpgBuildingsDensity * Multiplier(_o.ExpLevel), MidpointRounding.ToEven) / 10000,
                "upgrade building", upgrades: true);
            for (int i = 0; i < z.Def.Prisons; i++)
                PlaceRandom(r, z, "MapObjects/Prison.(AdvMapPrisonShared).xdb", FarCandidates(r, z), "prison");
            // 0x00920c50: LandCartographer cartographers; stops at the first empty candidate list.
            for (int i = 0; i < z.Def.LandCartographer; i++)
            {
                var c = FarCandidates(r, z);
                if (c.Count == 0) break;
                PlaceRandom(r, z, "MapObjects/Cartographer.(AdvMapCartographerShared).xdb", c, "cartographer");
            }
            PlaceShrines(r, z, z.Def.ShrinePoints);
            // Resource buildings: tiles x density / 10000; luck/morale: tiles x (density + 40) / 10000; treasuries
            // and shops: the template's points.
            SpendBudget(r, z, race.ResourceGivers, z.Tiles.Count * z.Def.ResourceBuildingsDensity / 10000, "resource building");
            SpendBudget(r, z, race.Treasuries, z.Def.TreasureBuildingPoints, "treasury building");
            SpendBudget(r, z, race.LuckMorale, z.Tiles.Count * (z.Def.LuckMoralBuildingsDensity + 40) / 10000, "luck/morale building");
            SpendBudget(r, z, race.Shops, z.Def.ShopPoints, "shop");
            // BuffPoints (+0x70) go to 0x00918b30, an empty function: the stock engine places no buff buildings.
            // 0x009219a0: tiles / 4000 + 1 Redwood Observatories, and in zones without a player one Den of
            // Thieves when rand(10) < 2 (the template's DenOfThieves / RedwoodObservatoryDensity are not read).
            for (int i = z.Tiles.Count / 4000; i >= 0; i--)
                PlaceBuilding(r, z, "MapObjects/Redwood_Observatory.(AdvMapBuildingShared).xdb", "redwood observatory");
            if (z.Player == 0 && Rng.Next(10) < 2)
                PlaceBuilding(r, z, "MapObjects/Den_Of_Thieves.(AdvMapBuildingShared).xdb", "den of thieves");
            // 0x00905e60 here for surface zones only (zone floor +0xf4 == 0); underground zones get theirs after
            // the statics (0x00906380, PlaceUndergroundTreasures).
            if (z.Floor == 0) PlaceZoneTreasures(r, z);
            ConnectObjectNodes(z);
            ReserveRoadTiles();
        }
    }

    /// <summary>exe 0x00905e60: treasures (TreasureDensity x the +0xa8 factor), then chests (TreasureChestDensity x the +0xb0 factor), truncated.</summary>
    void PlaceZoneTreasures(RmgResult r, RmgZone z)
    {
        PlaceTreasures(r, z, (int)(z.Def.TreasureDensity * Multiplier(_o.ResourceLevel)), chests: false);
        PlaceTreasures(r, z, (int)(z.Def.TreasureChestDensity * Multiplier(_o.ExpLevel)), chests: true);
    }

    /// <summary>
    /// exe 0x00906380 ("additional objects set", after the statics 0x00906130): every template zone, in template
    /// list order, whose generated zone lies underground (+0xf4 != 0) gets its treasures and chests (0x00905e60).
    /// </summary>
    void PlaceUndergroundTreasures(RmgResult r)
    {
        for (int zi = 0; zi < _t.Zones.Count; zi++)
        {
            var z = Zones.FirstOrDefault(zz => zz.Def == _t.Zones[zi]);
            if (z != null && z.Floor != 0) PlaceZoneTreasures(r, z);
        }
    }

    /// <summary>
    /// Graal option, per zone after the dwellings (exe 0x00907150): the drawn zone gets the Graal artifact
    /// (RMG/Params Grail) by 0x00921a50 — at most 100 attempts of a random candidate tile and a random quarter turn;
    /// then every zone gets (zone tiles · k) / (W · H) + 2 "obelisks" (RMG/Params Obelisk, which the stock data
    /// points at the Learning Stone) through 0x00921a10 → 0x009216f0, k = 10 / 18 / 26 for size index 0 / 1 / larger.
    /// TODO(exe): the candidate filters of 0x0091bb10 and 0x009216f0, and the second test 0x00914a60 (radius 5),
    /// are not ported; candidates are the zone's tiles.
    /// </summary>
    void PlaceGrailAndObelisks(RmgResult r, RmgZone z, bool grail)
    {
        if (grail && _db.Params.Grail is { } g)
        {
            var d = Catalog.Get(g);
            var pool = z.Tiles.ToList();
            bool placed = false;
            for (int tries = 0; tries < 100 && pool.Count > 0 && !placed; tries++)
            {
                int i = Rng.Next(pool.Count);
                var (x, y) = pool[i];
                int q = Rng.Next(4);
                if (CanPlace(z, d, x, y, q, out var access, null)) { Commit(r, z, g, d, x, y, q, access, "graal"); placed = true; }
                else pool.RemoveAt(i);
            }
            if (!placed) r.Warnings.Add($"Can't place building {g} at zone #{z.Id}, floor {z.Floor}");
        }
        if (_db.Params.Obelisk is not { } ob) return;
        int k = SizeIndex == 0 ? 10 : SizeIndex == 1 ? 18 : 26;
        int n = z.Tiles.Count * k / (W * H) + 1;
        for (int i = 0; i <= n; i++) PlaceBuilding(r, z, ob, "obelisk");
    }

    /// <summary>
    /// exe 0x009216f0: candidates of 0x0091bb10, then at most 100 attempts of a random candidate (rand(n)) and a
    /// quarter turn (rand(4)); a footprint failure drops the candidate.
    /// </summary>
    RmgObject? PlaceBuilding(RmgResult r, RmgZone z, string shared, string note)
    {
        if (!_db.Xdb.FileSystem.Exists(shared)) return null;
        var pool = FarCandidates(r, z);
        if (pool.Count == 0) return null;
        var d = Catalog.Get(shared);
        for (int tries = 0; tries < 100; tries++)
        {
            if (pool.Count == 0) break;
            int i = Rng.Next(pool.Count);
            var (x, y) = pool[i];
            int q = Rng.Next(4);
            if (CanPlace(z, d, x, y, q, out var access)) return Commit(r, z, shared, d, x, y, q, access, note);
            pool.RemoveAt(i);
        }
        r.Warnings.Add($"Can't place building {shared} at zone #{z.Id}, floor {z.Floor}");
        return null;
    }

    /// <summary>exe 0x0091c4a0: wood/ore near the town (Mine1 radii), the rest further out (Mine2 radii), each guarded.</summary>
    void PlaceMines(RmgResult r, RmgZone z, RmgRacePreset race)
    {
        var f = Floors[z.Floor];
        var p = _db.Params;
        var near = new List<(int, int)>();
        var far = new List<(int, int)>();
        foreach (var (x, y) in z.Tiles)
        {
            if (f.BorderDist[f.At(x, y)] <= 1) continue;
            if (z.TownPos is not { } tp || z.AnchorOnly) { near.Add((x, y)); far.Add((x, y)); continue; }
            float d = MathF.Sqrt((tp.X - x) * (tp.X - x) + (tp.Y - y) * (tp.Y - y));
            if (d < p.Mine2LevelMaxRadius && d > p.Mine2LevelMinRadius) far.Add((x, y));
            if (d < p.Mine1LevelMaxRadius && d > p.Mine1LevelMinRadius) near.Add((x, y));
        }
        for (int type = 0; type < 7 && type < race.Mines.Count; type++)
        {
            for (int n = 0; n < z.Def.Mines[type]; n++)
            {
                var cands = SpreadCandidates(r, z, type < 2 ? near : far, 2, 5);
                var o = PlaceRandom(r, z, race.Mines[type], cands, "mine");
                if (o == null || !r.Access.TryGetValue(o, out var acc)) continue;
                // 0x0091c4a0 → 0x0091a420 with the mine list +0x11c: the mine's visit tiles.
                foreach (var a in o.Def.Active)
                {
                    var t = H5.Adventure.Map.Footprint.Place(new Tile(o.X, o.Y), a, o.Rot);
                    if (f.In(t.X, t.Y)) z.MinePoints.Add((t.X, t.Y));
                }
                int level = type < 2 ? p.Mine1LevelGuardLevel : type == 6 ? p.MineGoldGuardLevel : p.Mine2LevelGuardLevel;
                PlaceGuard(r, z, acc.X, acc.Y, GuardPower(level), "mine guard");
                MarkNear(f, o, 2);
            }
        }
    }

    /// <summary>Keeps a ring around an object clear of other objects (the exe marks such tiles 2).</summary>
    void MarkNear(RmgFloor f, RmgObject o, int radius)
    {
        foreach (var (tx, ty) in Footprint(o.Def, o.X, o.Y, o.Quarter, includeActive: true))
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int nx = tx + dx, ny = ty + dy;
                    if (f.In(nx, ny) && f.Occ[f.At(nx, ny)] == Occ.Free && Rng.Float(0, 1) < 0.8f)
                    {
                        f.Occ[f.At(nx, ny)] = Occ.Near;
                        // TODO(exe): 0x0091c4a0 writes flag 2 around the mine under its own rule; this ring is not that rule.
                    }
                }
    }

    void PlaceDwellings(RmgResult r, RmgZone z, RmgRacePreset race)
    {
        for (int tier = 0; tier < z.Def.Dwellings.Length; tier++)
            for (int n = 0; n < z.Def.Dwellings[tier]; n++)
            {
                if (tier >= race.NewDwellings.Count) break;
                var dw = race.NewDwellings[tier];
                var o = PlaceRandom(r, z, dw.Shared, SpreadCandidates(r, z, z.Tiles, 2, 3), "dwelling");
                if (o != null && dw.GuardStrength > 0 && r.Access.TryGetValue(o, out var acc))
                    PlaceGuard(r, z, acc.X, acc.Y, GuardPower(dw.GuardStrength), "dwelling guard");
            }
    }

    /// <summary>
    /// exe 0x009221d0 (shops, shrines, treasuries, buffs): spend a point budget, each pick drawn
    /// among the leading pool entries still affordable.
    /// </summary>
    /// <summary>
    /// The budget loop shared by 0x00920ec0 (resource buildings), 0x00921d70 (luck/morale), 0x00921360 (treasuries),
    /// 0x009221d0 (shops) and 0x0091f140 (upgrades): while the pool's first entry still fits, n = the number of
    /// leading entries (preset order) that fit, pick rand(n), stop if it does not fit or there are no candidates,
    /// place it (random candidate + quarter turn; an exhausted list ends the loop) and add its value.
    /// Only upgrade buildings get a guard (0x00ba79f0 with the entry's GuardStrenght).
    /// </summary>
    void SpendBudget(RmgResult r, RmgZone z, List<RmgValuedObject> pool, int budget, string note, bool upgrades = false)
    {
        if (pool.Count == 0) return;
        int spent = 0;
        while (pool[0].Value + spent <= budget)
        {
            int n = 0;
            while (n < pool.Count && pool[n].Value + spent <= budget) n++;
            var pick = pool[Rng.Next(n)];
            if (pick.Value + spent > budget) break;
            var cands = upgrades ? UpgradeCandidates(r, z) : FarCandidates(r, z);
            if (!upgrades && cands.Count == 0) break;
            var o = PlaceRandom(r, z, pick.Shared, cands, note);
            if (o == null) break;
            spent += pick.Value;
            if (upgrades && pick.GuardStrength > 0 && r.Access.TryGetValue(o, out var acc))
                PlaceGuard(r, z, acc.X, acc.Y, GuardPower(pick.GuardStrength), note + " guard");
        }
    }

    /// <summary>0x0091f140: zone tiles whose object distance exceeds 2/3 of the maximum (0x00919710), no border test.</summary>
    List<(int X, int Y)> UpgradeCandidates(RmgResult r, RmgZone z)
    {
        ComputeObjectDistances(r, z);
        var f = Floors[z.Floor];
        int max = 0;
        foreach (var (x, y) in z.Tiles)
        {
            int i = f.At(x, y);
            if (f.Zone[i] == z.Id && f.BorderDist[i] > 2 && f.Occ[i] != Occ.Near && f.Dist[i] > max) max = f.Dist[i];
        }
        return z.Tiles.Where(t => f.Dist[f.At(t.X, t.Y)] > max * 2 / 3).ToList();
    }

    /// <summary>The shrine table (exe 0x00ffa068, filled at 0x00dc8bd0) and its point costs (0x00e57d74).</summary>
    static readonly (string Shared, int Cost)[] ShrineTable =
    {
        ("MapObjects/Shrine_Of_Magic_1.(AdvMapShrineShared).xdb", 6),
        ("MapObjects/Shrine_Of_Magic_2.(AdvMapShrineShared).xdb", 10),
        ("MapObjects/Shrine_Of_Magic_3.(AdvMapShrineShared).xdb", 12),
    };

    /// <summary>
    /// exe 0x00920000: ShrinePoints buy Shrines of Magic 1/2/3 (6/10/12 points) from the fixed table, not the race
    /// preset's NewShrines. Needs more than 5 points; n = leading costs that fit, rand(n); stops without candidates;
    /// continues while 6 more points fit.
    /// </summary>
    void PlaceShrines(RmgResult r, RmgZone z, int points)
    {
        if (points <= 5) return;
        int spent = 0;
        do
        {
            int n = 0;
            while (n < ShrineTable.Length && ShrineTable[n].Cost + spent <= points) n++;
            var pick = ShrineTable[Rng.Next(n)];
            var cands = FarCandidates(r, z);
            if (cands.Count == 0) break;
            if (PlaceRandom(r, z, pick.Shared, cands, "shrine") == null) break;
            spent += pick.Cost;
        } while (spent + 6 <= points);
    }

    /// <summary>Resource piles (exe 0x00ff9e08, filled at 0x00dc8a50: Wood, Ore, Mercury, Crystal, Sulfur, Gems, Gold), used
    /// by the mine decoration; Campfire appended for the (not yet ported) treasure blocks.</summary>
    static readonly string[] ResourcePiles =
    {
        "MapObjects/Wood.(AdvMapTreasureShared).xdb", "MapObjects/Ore.(AdvMapTreasureShared).xdb",
        "MapObjects/Mercury.(AdvMapTreasureShared).xdb", "MapObjects/Crystal.(AdvMapTreasureShared).xdb",
        "MapObjects/Sulfur.(AdvMapTreasureShared).xdb", "MapObjects/Gems.(AdvMapTreasureShared).xdb",
        "MapObjects/Gold.(AdvMapTreasureShared).xdb", "MapObjects/Campfire.(AdvMapTreasureShared).xdb",
    };

    /// <summary>The loose treasure table (exe 0x00ff9ee8, 32-byte records filled at 0x00dc8ad0): rand(9) picks one
    /// for a treasure, index 1 (Chest) is used for chests.</summary>
    static readonly string[] TreasureTable =
    {
        "MapObjects/Campfire.(AdvMapTreasureShared).xdb", "MapObjects/Chest.(AdvMapTreasureShared).xdb",
        "MapObjects/Crystal.(AdvMapTreasureShared).xdb", "MapObjects/Gems.(AdvMapTreasureShared).xdb",
        "MapObjects/Gold.(AdvMapTreasureShared).xdb", "MapObjects/Mercury.(AdvMapTreasureShared).xdb",
        "MapObjects/Ore.(AdvMapTreasureShared).xdb", "MapObjects/Sulfur.(AdvMapTreasureShared).xdb",
        "MapObjects/Wood.(AdvMapTreasureShared).xdb",
    };

    /// <summary>
    /// exe 0x0091f8a0: count = tiles x density / 10000; per treasure the type is drawn first (rand(9), chests fixed),
    /// candidates are the zone's tiles with border distance &gt; 0 whose object distance exceeds 2/3 of the maximum
    /// (0x009191a0 flag 4 / 0x00919710) and occupancy != 2; random candidate + quarter turn until the footprint
    /// fits (failures drop the candidate). An exhausted list logs "Can't place treasure" and ends the zone's treasures.
    /// </summary>
    void PlaceTreasures(RmgResult r, RmgZone z, int density, bool chests)
    {
        var f = Floors[z.Floor];
        int count = z.Tiles.Count * density / 10000;
        var inner = z.Tiles.Where(t => f.BorderDist[f.At(t.X, t.Y)] > 0).ToList();
        for (int i = 0; i < count; i++)
        {
            ComputeObjectDistances(r, z);
            int max = 0;
            foreach (var (x, y) in inner)
            {
                int k = f.At(x, y);
                if (f.Zone[k] == z.Id && f.BorderDist[k] > 2 && f.Occ[k] != Occ.Near && f.Dist[k] > max) max = f.Dist[k];
            }
            var cands = inner.Where(t => { int k = f.At(t.X, t.Y); return f.Dist[k] > max * 2 / 3 && f.BorderDist[k] > 0 && f.Occ[k] != Occ.Near; }).ToList();
            string shared = TreasureTable[chests ? 1 : Rng.Next(TreasureTable.Length)];
            if (PlaceRandom(r, z, shared, cands, chests ? "chest" : "treasure") == null) break;
        }
    }


    (int X, int Y)? HubOf(RmgZone z)
    {
        var f = Floors[z.Floor];
        var best = z.Tiles.Where(t => f.Occ[f.At(t.X, t.Y)] is Occ.Free or Occ.Road)
            .OrderBy(t => (t.X - z.CenterX) * (t.X - z.CenterX) + (t.Y - z.CenterY) * (t.Y - z.CenterY)).FirstOrDefault((-1, -1));
        return best.Item1 < 0 ? null : best;
    }

    // ------------------------------------------------------------------ statics (exe 0x00906130)

    void PlaceStatics(RmgResult r)
    {
        foreach (var f in Floors)
        {
            // Keep a walkable route from each zone hub to every object entrance and passage.
            foreach (var z in f.Zones.Values)
            {
                var hub = z.TownPos is { } && r.StartTownAccess(z, out var acc) ? acc : HubOf(z);
                if (hub == null) continue;
                var targets = r.Access.Where(kv => kv.Key.Zone == z.Id && kv.Key.Floor == f.Index && kv.Value.X >= 0).Select(kv => kv.Value)
                    .Concat(r.Passages.Where(p => p.Floor == f.Index && p.Zone == z.Id).Select(p => (p.X, p.Y))).ToList();
                // One BFS tree from the hub; each target's route is traced back along it.
                var parent = new Dictionary<(int, int), (int, int)> { [hub.Value] = hub.Value };
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue(hub.Value);
                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    foreach (var (dx, dy) in Neighbours8)
                    {
                        var n = (X: cur.X + dx, Y: cur.Y + dy);
                        if (!f.In(n.X, n.Y) || parent.ContainsKey(n)) continue;
                        int i = f.At(n.X, n.Y);
                        if (f.Zone[i] != z.Id || f.Occ[i] is Occ.Object or Occ.Obstacle or Occ.Entrance) continue;
                        parent[n] = cur;
                        if (f.Occ[i] != Occ.Guard) queue.Enqueue(n);
                    }
                }
                foreach (var t in targets)
                {
                    if (!parent.ContainsKey(t)) continue;
                    for (var p = t; ; p = parent[p])
                    {
                        int i = f.At(p.Item1, p.Item2);
                        if (f.Occ[i] is Occ.Free or Occ.Near) f.Occ[i] = Occ.Road;
                        if (p == hub.Value) break;
                    }
                }
            }

            // Obstacle tiles: zone borders (except passages), unassigned tiles and the map edge,
            // plus clusters inside zones grown from random seeds.
            var obstacle = new bool[W * H];
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    int i = f.At(x, y);
                    if (f.Occ[i] != Occ.Free && f.Occ[i] != Occ.Near) continue;
                    bool edge = x < 2 || y < 2 || x >= W - 2 || y >= H - 2;
                    if (edge || f.Zone[i] == -1 || f.BorderDist[i] <= 1) obstacle[i] = true;
                }
            var free = new List<(int X, int Y)>();
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                    if (f.Occ[f.At(x, y)] is Occ.Free or Occ.Near && !obstacle[f.At(x, y)]) free.Add((x, y));
            int seeds = free.Count / 45;
            for (int s = 0; s < seeds && free.Count > 0; s++)
            {
                var (sx, sy) = free[Rng.Next(free.Count)];
                int size = 3 + Rng.Next(10);
                var frontier = new List<(int X, int Y)> { (sx, sy) };
                for (int k = 0; k < size && frontier.Count > 0; k++)
                {
                    var (cx, cy) = frontier[Rng.Next(frontier.Count)];
                    int ci = f.At(cx, cy);
                    if (f.Occ[ci] is not (Occ.Free or Occ.Near)) continue;
                    obstacle[ci] = true;
                    foreach (var (dx, dy) in Neighbours4)
                        if (f.In(cx + dx, cy + dy)) frontier.Add((cx + dx, cy + dy));
                }
            }

            // Fill obstacle tiles with the zone race's statics: big ones first, then one-tile blockers.
            var order = new List<(int X, int Y)>();
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                    if (obstacle[f.At(x, y)]) order.Add((x, y));
            Rng.Shuffle(order);
            foreach (var pass in new[] { 0, 1, 2 })
                foreach (var (x, y) in order)
                {
                    int i = f.At(x, y);
                    if (!obstacle[i] || f.Occ[i] == Occ.Obstacle) continue;
                    var race = RaceAt(f, x, y);
                    var pool = pass switch { 0 => race.BigStatics.Concat(race.Mountains).ToList(), 1 => race.OneTileBigObjects, _ => race.OneTileSmallBlockers };
                    if (pool.Count == 0) continue;
                    var shared = pool[Rng.Next(pool.Count)];
                    var d = Catalog.Get(shared);
                    int q = Rng.Next(4);
                    var tiles = Footprint(d, x, y, q, includeActive: false).ToList();
                    if (tiles.Count == 0) tiles.Add((x, y));
                    if (!tiles.All(t => f.In(t.X, t.Y) && obstacle[f.At(t.X, t.Y)] && f.Occ[f.At(t.X, t.Y)] != Occ.Obstacle)) continue;
                    // The exe's statics write flag 2 too (0x00924940); our statics are not the exe's (GAP).
                    foreach (var (tx, ty) in tiles) { f.Occ[f.At(tx, ty)] = Occ.Obstacle; f.Flags[f.At(tx, ty)] = TileFlags.Blocked; }
                    r.Objects.Add(new RmgObject { Shared = shared, Def = d, X = x, Y = y, Floor = f.Index, Quarter = q, Zone = f.Zone[i], Note = "static" });
                }

            // Non-blocking decoration on a few free tiles.
            foreach (var (x, y) in free)
            {
                int i = f.At(x, y);
                if (f.Occ[i] != Occ.Free || Rng.Next(100) >= 3) continue;
                var race = RaceAt(f, x, y);
                if (race.OneTileSmallNonblockers.Count == 0) continue;
                var shared = race.OneTileSmallNonblockers[Rng.Next(race.OneTileSmallNonblockers.Count)];
                r.Objects.Add(new RmgObject { Shared = shared, Def = Catalog.Get(shared), X = x, Y = y, Floor = f.Index, Quarter = Rng.Next(4), Zone = f.Zone[i], Note = "decoration" });
            }
        }
    }

    RmgRacePreset RaceAt(RmgFloor f, int x, int y)
    {
        int id = f.Zone[f.At(x, y)];
        if (id != -1 && f.Zones.TryGetValue(id, out var z)) return _db.Race(z.Race);
        // Unassigned tiles: take the nearest zone's race.
        foreach (var (dx, dy) in Neighbours8)
        {
            int nx = x + dx, ny = y + dy;
            if (f.In(nx, ny) && f.Zone[f.At(nx, ny)] is var n and not -1 && f.Zones.TryGetValue(n, out var zz)) return _db.Race(zz.Race);
        }
        return _db.Race(f.Index == 0 ? 3 : 6);
    }

    // ------------------------------------------------------------------ finish (exe 0x00904ad0)

    void Finish(RmgResult r)
    {
        // Every object should be reachable from its zone hub (fighting through guards); report the ones that are not.
        foreach (var f in Floors)
            foreach (var z in f.Zones.Values)
            {
                var hub = z.TownPos is { } && r.StartTownAccess(z, out var acc) ? acc : HubOf(z);
                if (hub == null) continue;
                var seen = new HashSet<(int, int)> { hub.Value };
                var q = new Queue<(int X, int Y)>();
                q.Enqueue(hub.Value);
                while (q.Count > 0)
                {
                    var (x, y) = q.Dequeue();
                    foreach (var (dx, dy) in Neighbours8)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (!f.In(nx, ny) || seen.Contains((nx, ny))) continue;
                        int i = f.At(nx, ny);
                        if (f.Zone[i] != z.Id) continue;
                        if (f.Occ[i] is Occ.Object or Occ.Obstacle) continue;
                        // Guards are fought through, so they do not cut anything off.
                        seen.Add((nx, ny));
                        q.Enqueue((nx, ny));
                    }
                }
                foreach (var kv in r.Access.Where(kv => kv.Key.Zone == z.Id && kv.Key.Floor == f.Index))
                    if (kv.Value.X >= 0 && !seen.Contains(kv.Value))
                    {
                        r.Unreachable++;
                        r.Warnings.Add($"unreachable {kv.Key.Note} {kv.Key.Shared} at ({kv.Key.X},{kv.Key.Y}) floor {f.Index}, visit tile {kv.Value}, zone {z.Id}");
                    }
            }
    }
}
