namespace H5.Rmg;

/// <summary>
/// Treasure blocks (exe "at %g treasure blocks set", 0x00905d00): every template zone, in template order, whose zone
/// was generated runs 0x009198a0 with the template's TreasureBlocksTotalValue (zone record +0x64): the road
/// distance field (0x009191a0 with mask 0x38), then the zone's CTreasureBlockDistributor (zone +0x12c, built by the
/// zone constructor 0x0091beb0 via 0x00ba8100) set up by 0x00ba8050 and run by 0x00ba94c0 (site search 0x00ba82c0).
/// </summary>
public sealed partial class RmgGenerator
{
    // 0x00f76c28: the distributor's 8 neighbour offsets (first index, second index).
    static readonly (int Dx, int Dy)[] BlockNeighbours =
        { (0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1) };

    // 0x01040e10 (init 0x00dd7320): the block resource table, Next(7).
    static readonly string[] BlockResources =
    {
        "MapObjects/Wood.(AdvMapTreasureShared).xdb", "MapObjects/Ore.(AdvMapTreasureShared).xdb",
        "MapObjects/Mercury.(AdvMapTreasureShared).xdb", "MapObjects/Crystal.(AdvMapTreasureShared).xdb",
        "MapObjects/Sulfur.(AdvMapTreasureShared).xdb", "MapObjects/Gems.(AdvMapTreasureShared).xdb",
        "MapObjects/Gold.(AdvMapTreasureShared).xdb",
    };
    // 0x01040f10 (init 0x00dd73a0, entry 1 of that table).
    const string BlockChest = "MapObjects/Chest.(AdvMapTreasureShared).xdb";

    sealed class TreasureBlock
    {
        public float X, Y;                       // +0x00: the tile next to the road (the guard's tile)
        public List<(float X, float Y)> Tiles = new(); // +0x08: the item tiles
        public int Value;                        // +0x14
        public int Weight;                       // +0x18: distance to the town, chopped
    }

    void PlaceTreasureBlocks(RmgResult r)
    {
        for (int zi = 0; zi < _t.Zones.Count; zi++)
        {
            var z = Zones.FirstOrDefault(zz => zz.Def == _t.Zones[zi]);
            if (z != null) DistributeTreasureBlocks(r, z, _t.Zones[zi].TreasureBlocksTotalValue);
        }
    }

    /// <summary>
    /// exe 0x009191a0(0x38, 0): every tile of the zone's floor gets the distance to the nearest point of the zone's
    /// road lists +0x74, +0x80 and +0x8c (10000 when they are empty), tiles outside every zone 1000; tiles of other
    /// zones keep their value.
    /// </summary>
    void RoadDistanceField(RmgZone z)
    {
        var f = Floors[z.Floor];
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
            {
                int i = f.At(x, y);
                if (f.Zone[i] < 0) { f.Dist[i] = 1000; continue; }
                if (f.Zone[i] != z.Id) continue;
                float best = 10000f;                                                    // 0x00e57d84
                foreach (var list in new[] { z.Network, z.MineNetwork, z.ObjectPaths })
                    foreach (var p in list)
                    {
                        float d = MathF.Sqrt((y - p.Y) * (y - p.Y) + (x - p.X) * (x - p.X));
                        if (d < best) best = d;
                    }
                f.Dist[i] = (int)MathF.Round(best);
            }
        _distReady.Remove((z.Floor, z.Id));
    }

    /// <summary>"Free" for the distributor: flags 0 or bit 1 set. Tiles our other grid already uses are kept out too (our guards and statics do not write the exe flags).</summary>
    bool BlockFree(RmgFloor f, int i) => (f.Flags[i] == 0 || (f.Flags[i] & 1) != 0) && f.Occ[i] is Occ.Free or Occ.Near or Occ.Road;

    static bool Inner(RmgFloor f, float x, float y) => y >= 1 && y < f.H - 1 && x >= 1 && x < f.W - 1;   // 0x009157b0

    void DistributeTreasureBlocks(RmgResult r, RmgZone z, int total)
    {
        RoadDistanceField(z);
        var f = Floors[z.Floor];
        // 0x00ba8050: the town point is the zone's +0x0c when its town was placed, else the zero vector 0x010408cc.
        bool town = z.TownPlaced && z.TownPos != null;
        float townX = town ? z.TownPos!.Value.X : 0, townY = town ? z.TownPos!.Value.Y : 0;
        var blocks = FindBlockSites(z, f, town, townX, townY);
        // End of 0x00ba82c0: the value is split evenly, or with a town by distance weight.
        int weights = blocks.Sum(b => b.Weight);
        foreach (var b in blocks)
            b.Value = !town ? total / blocks.Count : weights == 0 ? 0 : b.Weight * total / weights;
        Log.Add($"in zone with town at {(int)townX}:{(int)townY} we got {blocks.Count} treasure blocks");
        foreach (var b in blocks) PlaceBlock(r, z, f, b);
    }

    /// <summary>
    /// exe 0x00ba82c0: over the zone tiles (x-major), a site is a free tile at road distance 1, more than √3 from the
    /// town point, none of whose 8 neighbours (scanned from Next(8)) is an active tile (flag 4) and at least two of
    /// which are free with road distance &gt; 1; it must be at least DistBetweenTreasureBlocks (Params +0x84) from the
    /// earlier sites and from the zone's connection guard points (+0x98). Its item tiles are the free neighbours off
    /// the road (distance &gt; 0) with at least two blocked (flag 2) and no active neighbours; two are needed.
    /// </summary>
    List<TreasureBlock> FindBlockSites(RmgZone z, RmgFloor f, bool town, float townX, float townY)
    {
        var blocks = new List<TreasureBlock>();
        int minDist = _db.Params.DistBetweenTreasureBlocks;
        foreach (var (tx, ty) in z.Tiles)
        {
            int ti = f.At(tx, ty);
            if (!BlockFree(f, ti) || f.Dist[ti] != 1) continue;
            float ddx = tx - townX, ddy = ty - townY;
            if (!(ddy * ddy + ddx * ddx > 3f)) continue;                               // 0x00e13280
            bool active = false;
            int open = 0;
            int start = Rng.Next(8);
            for (int k = start; k < start + 8 && !active; k++)
            {
                var (dx, dy) = BlockNeighbours[k & 7];
                int nx = tx + dx, ny = ty + dy;
                if (!Inner(f, nx, ny)) continue;
                int ni = f.At(nx, ny);
                if ((f.Flags[ni] & TileFlags.Active) != 0) active = true;
                if (BlockFree(f, ni) && f.Dist[ni] > 1) open++;
            }
            if (active || open < 2) continue;
            bool near = blocks.Any(b => Math.Sqrt((ty - b.Y) * (ty - b.Y) + (tx - b.X) * (tx - b.X)) < minDist)
                     || z.GuardPoints.Any(p => Math.Sqrt((ty - p.Y) * (ty - p.Y) + (tx - p.X) * (tx - p.X)) < minDist);
            if (near) continue;
            var block = new TreasureBlock { X = tx, Y = ty };
            block.Weight = (int)MathF.Sqrt((ty - townY) * (ty - townY) + (tx - townX) * (tx - townX));
            foreach (var (dx, dy) in BlockNeighbours)
            {
                int nx = tx + dx, ny = ty + dy;
                if (!Inner(f, nx, ny)) continue;
                int ni = f.At(nx, ny);
                if (!BlockFree(f, ni) || f.Dist[ni] <= 0) continue;
                int walls = 0;
                bool act = false;
                foreach (var (ex, ey) in BlockNeighbours)
                {
                    int mx = nx + ex, my = ny + ey;
                    if (!Inner(f, mx, my)) continue;
                    int mi = f.At(mx, my);
                    if ((f.Flags[mi] & TileFlags.Blocked) != 0) walls++;
                    if ((f.Flags[mi] & TileFlags.Active) != 0) act = true;
                }
                if (walls > 1 && !act) block.Tiles.Add((nx, ny));
            }
            if (block.Tiles.Count >= 2) blocks.Add(block);
        }
        return blocks;
    }

    /// <summary>
    /// exe 0x00ba94c0, per block worth more than 599: a guard of power trunc(value x 2.5 + 0.5) on the site
    /// (0x00ba7690), turned by -atan2(Σdx + 0.01, Σdy + 0.01) over the item tiles and the site's neighbours with flag
    /// 2 or 4; the artifacts that can be generated (CanBeGeneratedToSell with a shared, ids 1..96; id 10 only with
    /// generator +0x8d, list +0x70 of 0x00ba8100) with Cost/5 + 500 &lt; value &lt; Cost/5 x 7, one drawn with
    /// Next(n) (Next(1) when none fit), whose Cost/5 is taken off the value; then per item tile, with
    /// p = value / tiles / 100: the artifact on tile 1, a chest (amount p/6 + 1) when p &lt; 2, otherwise (p &gt; 10 →
    /// Next(6) + 7) with fewer than 3 tiles or Next(2) == 0 a resource Next(7) of amount p, else a chest. Items are
    /// created without footprint tests (0x009145b0 / 0x00915010), each turned by Float(0, 1).
    /// </summary>
    void PlaceBlock(RmgResult r, RmgZone z, RmgFloor f, TreasureBlock b)
    {
        if (b.Value <= 599) return;
        double sx = 0, sy = 0;                                                         // 0x010408cc (zero)
        foreach (var t in b.Tiles) { sx += t.X - b.X; sy += t.Y - b.Y; }
        foreach (var (dx, dy) in BlockNeighbours)
        {
            int nx = (int)(b.X + dx), ny = (int)(b.Y + dy);
            if (!Inner(f, nx, ny) || (f.Flags[f.At(nx, ny)] & (TileFlags.Blocked | TileFlags.Active)) == 0) continue;
            sx += nx - b.X;
            sy += ny - b.Y;
        }
        float angle = -(float)Math.Atan2(sx + (double)0.01f, sy + (double)0.01f);
        int power = (int)((float)b.Value * 2.5f + 0.5f);                               // 0x00e57d48, 0x00e0ab70
        PlaceGuard(r, z, (int)b.X, (int)b.Y, power, "treasure block guard", angle);

        var kept = new List<RmgArtifact>();
        foreach (var a in BlockArtifacts())
            if (a.Cost / 5 + 500 < b.Value && b.Value < a.Cost / 5 * 7) kept.Add(a);
        int pick = Rng.Next(Math.Max(kept.Count, 1));
        bool art = kept.Count > 0;
        if (art) b.Value += -(kept[pick].Cost / 5);
        for (int i = 0; i < b.Tiles.Count; i++)
        {
            var (x, y) = b.Tiles[i];
            int per = b.Value / b.Tiles.Count / 100;
            if (art && i == 1)
            {
                float rot = Rng.Float(0, 1);
                BlockItem(r, z, b, kept[pick].Shared, x, y, rot, 0, "treasure block artifact");
            }
            else if (per < 2)
            {
                float rot = Rng.Float(0, 1);
                BlockItem(r, z, b, BlockChest, x, y, rot, per / 6 + 1, "treasure block chest");
            }
            else
            {
                if (per > 10) per = Rng.Next(6) + 7;
                if (b.Tiles.Count < 3 || Rng.Next(2) == 0)
                {
                    string res = BlockResources[Rng.Next(7)];
                    float rot = Rng.Float(0, 1);
                    BlockItem(r, z, b, res, x, y, rot, per, "treasure block pile");
                }
                else
                {
                    float rot = Rng.Float(0, 1);
                    BlockItem(r, z, b, BlockChest, x, y, rot, per / 6 + 1, "treasure block chest");
                }
            }
        }
    }

    List<RmgArtifact>? _blockArtifacts;

    /// <summary>exe 0x00ba8100: artifact ids 1..0x60 in order with CanBeGeneratedToSell (+0x3c) and an ArtifactShared (+0x5c); id 10 needs generator +0x8d.</summary>
    List<RmgArtifact> BlockArtifacts() => _blockArtifacts ??= _db.Artifacts
        .Where(a => a.Index >= 1 && a.Index < 0x61 && a.Index != 10 && a.Shared.Length > 0)   // the list holds CanBeGeneratedToSell artifacts with a shared only
        .OrderBy(a => a.Index).ToList();

    void BlockItem(RmgResult r, RmgZone z, TreasureBlock b, string shared, float x, float y, float rot, int amount, string note)
    {
        var f = Floors[z.Floor];
        int ix = (int)x, iy = (int)y;
        if (!f.In(ix, iy)) return;
        CreateObjectDraws();
        var d = Catalog.Get(shared);
        var o = new RmgObject { Shared = shared, Def = d, X = ix, Y = iy, Floor = z.Floor, Zone = z.Id, Amount = amount, Note = note, Angle = rot };
        // Like the loose treasures (visit tile only), an item is collected, not walked around.
        if (f.Occ[f.At(ix, iy)] is Occ.Free or Occ.Near or Occ.Road) f.Occ[f.At(ix, iy)] = Occ.Entrance;
        r.Objects.Add(o);
        r.Access[o] = ((int)b.X, (int)b.Y);   // every item tile neighbours the site; the hero reaches it past the guard
    }
}
