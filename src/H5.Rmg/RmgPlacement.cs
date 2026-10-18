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
    public float Rot => Quarter * MathF.PI / 2;
}

public sealed partial class RmgGenerator
{
    SharedCatalog? _catalog;
    SharedCatalog Catalog => _catalog ??= new SharedCatalog(_db.Xdb);

    /// <summary>Treasure and chest multipliers (exe switch at 0x00905eac: 0.2, 0.5, 1, 2, 4). The stock
    /// generator's defaults match index 3 on the reference maps (see docs/rules/RMG.md).</summary>
    public int ResourceMultiplier { get; set; } = 3;
    public int ChestMultiplier { get; set; } = 3;
    static readonly float[] Multipliers = { 0.2f, 0.5f, 1f, 2f, 4f };

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
        var f = Floors[z.Floor];
        var active = new HashSet<(int, int)>(d.Active.Count > 0 ? new[] { ActiveTile(d, x, y, quarter) } : Array.Empty<(int, int)>());
        foreach (var (tx, ty) in Footprint(d, x, y, quarter, includeActive: false))
            if (f.In(tx, ty)) f.Occ[f.At(tx, ty)] = Occ.Object;
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

    /// <summary>Guard stack for a value: rand(30)+10 creatures whose Power fits a widening band (×1.1, ×1.2 …).</summary>
    (RmgCreature Creature, int Count)? PickGuard(int power)
    {
        int value = (int)MathF.Round(power * MonsterCoef[Math.Clamp(MonsterLevel, 0, 4)]);
        if (value < 100) return null;
        // 60% of guards use a two-stack army template in the original; we keep the single-stack
        // draw sequence for both (the template branch is documented as a gap).
        Rng.Float(0, 1);
        int r = Rng.Next(30);
        var cands = new List<RmgCreature>();
        float k = 1.1f;
        for (int iter = 0; iter < 60 && cands.Count < 10; iter++)
        {
            cands.Clear();
            foreach (var c in _db.Creatures)
            {
                if (c.Index == 0 || c.Index == 0x59 || c.Index == 0x72 || c.Power <= 0 || c.MonsterShared == null) continue;
                float lo = (r + 10) / k * c.Power, hi = (r + 10) * k * c.Power;
                if (lo <= value && value <= hi) cands.Add(c);
            }
            k *= 1.2f;
        }
        if (cands.Count == 0) return null;
        var pick = cands[Rng.Next(cands.Count)];
        return (pick, Math.Max(1, value / pick.Power));
    }

    RmgObject? PlaceGuard(RmgResult r, RmgZone z, int x, int y, int power, string note)
    {
        var f = Floors[z.Floor];
        if (!f.In(x, y)) return null;
        var g = PickGuard(power);
        if (g == null) return null;
        var (c, count) = g.Value;
        var d = Catalog.Get(c.MonsterShared!);
        f.Occ[f.At(x, y)] = Occ.Guard;
        var o = new RmgObject
        {
            Shared = c.MonsterShared!, Def = d, X = x, Y = y, Floor = z.Floor, Quarter = Rng.Next(4), Zone = z.Id,
            Creature = c.Id, Amount = count, Note = note,
        };
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
                // Post-placement (0x0092a400 / 0x00babbf0): the town's point light draws twice.
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
            if (guard > 0 && r.Access.TryGetValue(o, out var acc)) PlaceGuard(r, z, acc.X, acc.Y, guard, "teleport guard");
        }
    }

    // ------------------------------------------------------------------ main objects (exe 0x00907150)

    void PlaceMainObjects(RmgResult r)
    {
        foreach (var z in Zones.OrderBy(z => z.Def.Index))
        {
            var race = _db.Race(z.Race);
            PlaceMines(r, z, race);
            for (int i = 0; i < z.Def.AbandonedMines && race.AbandonedMine != null; i++)
            {
                var o = PlaceRandom(r, z, race.AbandonedMine, SpreadCandidates(r, z, z.Tiles, 2, 5), "abandoned mine");
                if (o != null && r.Access.TryGetValue(o, out var acc)) PlaceGuard(r, z, acc.X, acc.Y, GuardPower(_db.Params.Mine2LevelGuardLevel), "mine guard");
            }
            PlaceDwellings(r, z, race);
            // Upgrade buildings scale with the 0x00907150 switch on +0xb0 (0.2 .. 4); calibrated to x1
            // against the stock RMG maps in the user's Maps folder (see docs/rules/RMG.md).
            PlaceByDensity(r, z, race.Upgrades, z.Def.UpgBuildingsDensity, "upgrade building");
            for (int i = 0; i < z.Def.Prisons; i++)
                PlaceRandom(r, z, "MapObjects/Prison.(AdvMapPrisonShared).xdb", SpreadCandidates(r, z, z.Tiles, 1, 2), "prison");
            PlaceByPoints(r, z, race.Shrines, z.Def.ShrinePoints, "shrine");
            PlaceByDensity(r, z, race.ResourceGivers, z.Def.ResourceBuildingsDensity, "resource building");
            PlaceByPoints(r, z, race.Treasuries, z.Def.TreasureBuildingPoints, "treasury building");
            PlaceByDensity(r, z, race.LuckMorale, z.Def.LuckMoralBuildingsDensity, "luck/morale building");
            PlaceByPoints(r, z, race.Shops, z.Def.ShopPoints, "shop");
            PlaceByPoints(r, z, race.Buffs, z.Def.BuffPoints, "buff building");
            PlaceTreasures(r, z, (int)MathF.Round(z.Def.TreasureDensity * Multipliers[ResourceMultiplier]), chests: false);
            PlaceTreasures(r, z, (int)MathF.Round(z.Def.TreasureChestDensity * Multipliers[ChestMultiplier]), chests: true);
        }
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
                    if (f.In(nx, ny) && f.Occ[f.At(nx, ny)] == Occ.Free && Rng.Float(0, 1) < 0.8f) f.Occ[f.At(nx, ny)] = Occ.Near;
                }
    }

    void PlaceDwellings(RmgResult r, RmgZone z, RmgRacePreset race)
    {
        for (int tier = 0; tier < z.Def.Dwellings.Length; tier++)
            for (int n = 0; n < z.Def.Dwellings[tier]; n++)
            {
                if (tier >= race.NewDwellings.Count) break;
                var dw = race.NewDwellings[tier];
                var o = PlaceRandom(r, z, dw.Shared, SpreadCandidates(r, z, z.Tiles, 1, 3), "dwelling");
                if (o != null && dw.GuardStrength > 0 && r.Access.TryGetValue(o, out var acc))
                    PlaceGuard(r, z, acc.X, acc.Y, GuardPower(dw.GuardStrength), "dwelling guard");
            }
    }

    /// <summary>
    /// exe 0x009221d0 (shops, shrines, treasuries, buffs): spend a point budget, each pick drawn
    /// among the leading pool entries still affordable.
    /// </summary>
    void PlaceByPoints(RmgResult r, RmgZone z, List<RmgValuedObject> pool, int budget, string note) =>
        SpendBudget(r, z, pool, budget, note);

    /// <summary>exe 0x00921d70 (luck/morale, resource givers, upgrades): budget = zone tiles · (density + 40) / 10000.</summary>
    void PlaceByDensity(RmgResult r, RmgZone z, List<RmgValuedObject> pool, int density, string note)
    {
        if (density <= 0) return;
        SpendBudget(r, z, pool, z.Tiles.Count * (density + 40) / 10000, note);
    }

    void SpendBudget(RmgResult r, RmgZone z, List<RmgValuedObject> pool, int budget, string note)
    {
        if (pool.Count == 0 || budget <= 0) return;
        var sorted = pool.OrderBy(p => p.Value).ToList();
        int spent = 0;
        for (int guardRail = 0; guardRail < 200 && sorted[0].Value + spent <= budget; guardRail++)
        {
            int affordable = sorted.TakeWhile(p => p.Value + spent <= budget).Count();
            var pick = sorted[Rng.Next(Math.Max(1, affordable))];
            if (pick.Value + spent > budget) break;
            var o = PlaceRandom(r, z, pick.Shared, SpreadCandidates(r, z, z.Tiles, 1, 3), note);
            if (o == null) break;
            spent += Math.Max(1, pick.Value);
            if (pick.GuardStrength > 0 && r.Access.TryGetValue(o, out var acc))
                PlaceGuard(r, z, acc.X, acc.Y, GuardPower(pick.GuardStrength), note + " guard");
        }
    }

    static readonly string[] ResourcePiles =
    {
        "MapObjects/Wood.(AdvMapTreasureShared).xdb", "MapObjects/Ore.(AdvMapTreasureShared).xdb",
        "MapObjects/Mercury.(AdvMapTreasureShared).xdb", "MapObjects/Crystal.(AdvMapTreasureShared).xdb",
        "MapObjects/Sulfur.(AdvMapTreasureShared).xdb", "MapObjects/Gems.(AdvMapTreasureShared).xdb",
        "MapObjects/Gold.(AdvMapTreasureShared).xdb", "MapObjects/Campfire.(AdvMapTreasureShared).xdb",
    };

    /// <summary>exe 0x0091f8a0: count = zone tiles · density / 10000, placed far from other objects (2/3 of max).</summary>
    void PlaceTreasures(RmgResult r, RmgZone z, int density, bool chests)
    {
        int count = z.Tiles.Count * density / 10000;
        var minors = _db.Artifacts.Where(a => a.Class == "ARTF_CLASS_MINOR" && a.Cost > 0).ToList();
        for (int i = 0; i < count; i++)
        {
            // The exe draws a type from its treasure table (0x00ff9efc); about a quarter of loose
            // treasures on the stock RMG maps are minor artifacts (calibration, see docs/rules/RMG.md).
            string shared = chests ? "MapObjects/Chest.(AdvMapTreasureShared).xdb"
                : Rng.Next(4) == 0 && minors.Count > 0 ? minors[Rng.Next(minors.Count)].Shared
                : ResourcePiles[Rng.Next(ResourcePiles.Length)];
            if (PlaceRandom(r, z, shared, SpreadCandidates(r, z, z.Tiles, 2, 3), chests ? "chest" : "treasure") == null) break;
        }
    }

    /// <summary>Tiles a hero can reach from the zone hub (fighting through guards).</summary>
    HashSet<(int, int)> Reachable(RmgResult r, RmgZone z)
    {
        var f = Floors[z.Floor];
        var hub = z.TownPos is { } && !z.AnchorOnly && r.StartTownAccess(z, out var acc) ? acc : HubOf(z);
        var seen = new HashSet<(int, int)>();
        if (hub == null) return seen;
        var q = new Queue<(int X, int Y)>();
        seen.Add(hub.Value);
        q.Enqueue(hub.Value);
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            foreach (var (dx, dy) in Neighbours8)
            {
                int nx = x + dx, ny = y + dy;
                if (!f.In(nx, ny) || seen.Contains((nx, ny))) continue;
                int i = f.At(nx, ny);
                if (f.Zone[i] != z.Id || f.Occ[i] is Occ.Object or Occ.Obstacle) continue;
                seen.Add((nx, ny));
                if (f.Occ[i] != Occ.Entrance) q.Enqueue((nx, ny));
            }
        }
        return seen;
    }

    // ------------------------------------------------------------------ treasure blocks (exe 0x009198a0 → 0x00ba8050)

    void PlaceTreasureBlocks(RmgResult r)
    {
        var arts = _db.Artifacts.Where(a => a.Cost > 0 && a.Class is "ARTF_CLASS_MINOR" or "ARTF_CLASS_MAJOR" or "ARTF_CLASS_RELIC")
            .OrderBy(a => a.Cost).ToList();
        foreach (var z in Zones.OrderBy(z => z.Def.Index))
        {
            int remaining = z.Def.TreasureBlocksTotalValue;
            var f = Floors[z.Floor];
            for (int guardRail = 0; guardRail < 40 && remaining >= 3000 && arts.Count > 0; guardRail++)
            {
                int value = Math.Min(remaining, 5000 + Rng.Next(10001));
                var fit = arts.Where(a => a.Cost <= value).ToList();
                if (fit.Count == 0) break;
                var art = fit[Rng.Next(fit.Count)];
                var cands = SpreadCandidates(r, z, z.Tiles, 1, 2);
                var reach = Reachable(r, z);
                var o = PlaceRandom(r, z, art.Shared, cands, "treasure block artifact", reach);
                if (o == null) break;
                remaining -= value;
                // A couple of resource piles next to the artifact, then a guard worth the block.
                for (int k = 0; k < 2; k++)
                {
                    var near = z.Tiles.Where(t => Math.Abs(t.X - o.X) <= 2 && Math.Abs(t.Y - o.Y) <= 2).ToList();
                    PlaceRandom(r, z, ResourcePiles[Rng.Next(ResourcePiles.Length - 1)], near, "treasure block pile", Reachable(r, z));
                }
                if (r.Access.TryGetValue(o, out var acc)) PlaceGuard(r, z, acc.X, acc.Y, value, "treasure block guard");
            }
        }
    }

    // ------------------------------------------------------------------ roads (exe 0x00904cf0)

    void CreateRoads(RmgResult r)
    {
        foreach (var z in Zones)
        {
            var f = Floors[z.Floor];
            (int X, int Y)? hub = z.TownPos is { } tp && r.StartTownAccess(z, out var acc) ? acc : HubOf(z);
            if (hub == null) continue;
            var race = _db.Race(z.Race);
            string roadTile = race.RoadTile.Length > 0 ? race.RoadTile : race.SecondaryRoadTile;
            foreach (var p in r.Passages.Where(p => p.Floor == z.Floor && p.Zone == z.Id))
            {
                var path = FindPath(f, z, hub.Value, (p.X, p.Y), allowZoneExit: false);
                if (path == null) continue;
                foreach (var (x, y) in path)
                {
                    int i = f.At(x, y);
                    if (f.Occ[i] == Occ.Free || f.Occ[i] == Occ.Near) f.Occ[i] = Occ.Road;
                    if (z.TownPos != null && roadTile.Length > 0) r.TileLayers[z.Floor][y * W + x] = roadTile;
                }
            }
        }
    }

    (int X, int Y)? HubOf(RmgZone z)
    {
        var f = Floors[z.Floor];
        var best = z.Tiles.Where(t => f.Occ[f.At(t.X, t.Y)] is Occ.Free or Occ.Road)
            .OrderBy(t => (t.X - z.CenterX) * (t.X - z.CenterX) + (t.Y - z.CenterY) * (t.Y - z.CenterY)).FirstOrDefault((-1, -1));
        return best.Item1 < 0 ? null : best;
    }

    /// <summary>A* over walkable tiles of a zone (objects, guards and obstacles block; roads are cheap).</summary>
    List<(int X, int Y)>? FindPath(RmgFloor f, RmgZone z, (int X, int Y) from, (int X, int Y) to, bool allowZoneExit)
    {
        var open = new PriorityQueue<(int X, int Y), float>();
        var cost = new Dictionary<(int, int), float> { [from] = 0 };
        var prev = new Dictionary<(int, int), (int, int)>();
        open.Enqueue(from, 0);
        while (open.TryDequeue(out var cur, out _))
        {
            if (cur == to) break;
            foreach (var (dx, dy) in Neighbours8)
            {
                var n = (X: cur.X + dx, Y: cur.Y + dy);
                if (!f.In(n.X, n.Y)) continue;
                int i = f.At(n.X, n.Y);
                if (n != to)
                {
                    if (!allowZoneExit && f.Zone[i] != z.Id) continue;
                    if (f.Occ[i] is Occ.Object or Occ.Obstacle or Occ.Guard or Occ.Entrance) continue;
                }
                float step = (dx != 0 && dy != 0 ? 1.41f : 1f) * (f.Occ[i] == Occ.Road ? 0.5f : 1f);
                float c = cost[cur] + step;
                if (cost.TryGetValue(n, out var old) && old <= c) continue;
                cost[n] = c;
                prev[n] = cur;
                float h = MathF.Sqrt((n.X - to.X) * (n.X - to.X) + (n.Y - to.Y) * (n.Y - to.Y));
                open.Enqueue(n, c + h);
            }
        }
        if (!prev.ContainsKey(to) && from != to) return null;
        var path = new List<(int, int)>();
        for (var p = to; p != from; p = prev[p]) path.Add(p);
        path.Add(from);
        path.Reverse();
        return path;
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
                    foreach (var (tx, ty) in tiles) f.Occ[f.At(tx, ty)] = Occ.Obstacle;
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
