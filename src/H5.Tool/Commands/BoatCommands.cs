using H5.Adventure.Map;
using H5.Core.Data;

namespace H5.Tool.Commands;

/// <summary>
/// Survey of water on every map: water tiles, pre-placed ships, shipyards, towns with a slipway,
/// sea objects, and whether each slipway lands on water under the plain or rotated reading.
/// </summary>
sealed class AdvWaterCommand : ICommand
{
    public string Name => "adv-water";
    public string Help => "adv-water  - list maps with water, ships, shipyards and slipway checks";

    public int Run(ToolContext ctx, string[] args)
    {
        var db = GameDatabase.Load(ctx.Fs);
        var loader = new MapLoader(db, ctx.Fs);
        int plainOk = 0, rotOk = 0, slipways = 0;
        foreach (var r in MapCatalog.All(ctx.Fs, ctx.Install))
        {
            AdvMap m;
            try { m = loader.Load(r); } catch { continue; }
            int water = m.Floors.Where(f => f != null).Sum(f => f!.Terrain.Count(t => t == TerrainClass.Water));
            if (water == 0) continue;
            int ships = m.Objects.Count(o => o.Shared.Type == "BUILDING_SHIP_GALEON");
            int yards = m.Objects.Count(o => o.Kind == ObjectKind.Shipyard);
            int sea = m.Objects.Count(o => o.Shared.WaterBased && o.Shared.Type != "BUILDING_SHIP_GALEON");
            int waterHeroes = m.Objects.Count(o => o.Kind == ObjectKind.Hero && m.Floor(o.Pos.Floor) is { } f && f.InBounds(o.Pos.X, o.Pos.Y) && f.IsWater(o.Pos.X, o.Pos.Y));
            int townSlip = 0;
            foreach (var o in m.Objects.Where(o => o.ShipTile is { } st && (o.Kind == ObjectKind.Shipyard || (o.Kind == ObjectKind.Town && st != (0, 0)))))
            {
                var f = m.Floor(o.Pos.Floor)!;
                var st = o.ShipTile!.Value;
                var plain = o.Pos.Offset(st.X, st.Y);
                var rot = Footprint.Place(o.Pos, st, o.Rot);
                bool pOk = f.InBounds(plain.X, plain.Y) && f.IsWater(plain.X, plain.Y);
                bool rOk = f.InBounds(rot.X, rot.Y) && f.IsWater(rot.X, rot.Y);
                slipways++; if (pOk) plainOk++; if (rOk) rotOk++;
                if (o.Kind == ObjectKind.Town) townSlip++;
                if (!pOk || !rOk)
                    Console.WriteLine($"    {o} rot={Footprint.QuarterTurns(o.Rot)} slip={st} plainWater={pOk} rotatedWater={rOk}");
            }
            Console.WriteLine($"{r.Id,-40} water={water,6} ships={ships,2} shipyards={yards,2} townSlipways={townSlip,2} seaObjects={sea,3} heroesAtSea={waterHeroes}");
        }
        Console.WriteLine($"slipways: {slipways}, water under plain offset: {plainOk}, under rotated offset: {rotOk}");
        return 0;
    }
}

/// <summary>Temporary probe: terrain chunk values at slipways, sea objects and land objects.</summary>
sealed class AdvWaterProbeCommand : ICommand
{
    public string Name => "adv-water-probe";
    public string Help => "adv-water-probe <map>  - terrain chunk values at slipways / sea objects / land objects";

    public int Run(ToolContext ctx, string[] args)
    {
        var db = GameDatabase.Load(ctx.Fs);
        var m = new MapLoader(db, ctx.Fs).Load(MapCatalog.Find(ctx.Fs, ctx.Install, args[0])!);
        string Probe(Tile t)
        {
            var f = m.Floor(t.Floor)!; var d = f.Data!;
            if (!f.InBounds(t.X, t.Y)) return "oob";
            int v7(int x, int y) => d.VertexFlags?[Math.Clamp(x, 0, d.VertexFlags.Width - 1), Math.Clamp(y, 0, d.VertexFlags.Height - 1)] ?? -1;
            int c10 = d.HalfTileMap is { } h ? h[Math.Min(2 * t.X + 1, h.Width - 1), Math.Min(2 * t.Y + 1, h.Height - 1)] : -1;
            int v15(int x, int y) => d.VertexMap15?[Math.Clamp(x, 0, d.VertexMap15.Width - 1), Math.Clamp(y, 0, d.VertexMap15.Height - 1)] ?? -1;
            return $"cls={f.At(t.X, t.Y)} type={f.TileType[t.Y * f.Width + t.X]} v7=[{v7(t.X, t.Y)},{v7(t.X + 1, t.Y)},{v7(t.X, t.Y + 1)},{v7(t.X + 1, t.Y + 1)}] c10={c10} v15=[{v15(t.X, t.Y)},{v15(t.X + 1, t.Y + 1)}] h={d.SampleHeight(t.X + 0.5f, t.Y + 0.5f):F2} blk={f.IsBlocked(t.X, t.Y)}";
        }
        foreach (var o in m.Objects.Where(o => o.Kind == ObjectKind.Shipyard))
            Console.WriteLine($"slip {o} -> {Probe(Footprint.Place(o.Pos, o.ShipTile!.Value, o.Rot))}   plain {Probe(o.Pos.Offset(o.ShipTile!.Value.X, o.ShipTile!.Value.Y))}");
        foreach (var o in m.Objects.Where(o => o.Shared.WaterBased).Take(12)) Console.WriteLine($"sea  {o} -> {Probe(o.Pos)}");
        foreach (var o in m.Objects.Where(o => o.Kind is ObjectKind.Town or ObjectKind.Mine).Take(6)) Console.WriteLine($"land {o} -> {Probe(o.ActiveTiles.First())}");
        return 0;
    }
}

/// <summary>Temporary: scores water-tile rules against sea-object and land-object entrance tiles on all maps.</summary>
sealed class AdvWaterRuleCommand : ICommand
{
    public string Name => "adv-water-rule";
    public string Help => "adv-water-rule  - score water classification rules (vertex flag 0x10) on all maps";

    public int Run(ToolContext ctx, string[] args)
    {
        var db = GameDatabase.Load(ctx.Fs);
        var loader = new MapLoader(db, ctx.Fs);
        var rules = new (string Name, Func<MapFloor, int, int, bool> Water)[]
        {
            ("lvl0", (f, x, y) => Count(f, x, y) == 4),
            ("lvl0|c10>=1", (f, x, y) => Count(f, x, y) == 4 || C10(f, x, y) >= 1),
            ("lvl0|c10>=64", (f, x, y) => Count(f, x, y) == 4 || C10(f, x, y) >= 64),
            ("lvl0|c10>=128", (f, x, y) => Count(f, x, y) == 4 || C10(f, x, y) >= 128),
            ("lvl0|c10>=200", (f, x, y) => Count(f, x, y) == 4 || C10(f, x, y) >= 200),
            ("lvl0|c10avg>=128", (f, x, y) => Count(f, x, y) == 4 || C10Avg(f, x, y) >= 128),
        };
        int misses = 0; var missFloor = new int[2];
        var seaOk = new int[rules.Length]; var landOk = new int[rules.Length]; int seaN = 0, landN = 0;
        foreach (var r in MapCatalog.All(ctx.Fs, ctx.Install))
        {
            AdvMap m;
            try { m = loader.Load(r); } catch { continue; }
            foreach (var o in m.Objects)
            {
                if (o.Kind is ObjectKind.Static or ObjectKind.Unknown) continue;
                var f = m.Floor(o.Pos.Floor);
                if (f?.Data?.VertexFlags == null) continue;
                bool sea = o.Shared.WaterBased;
                foreach (var t in o.ActiveTiles.Take(1))
                {
                    if (!f.InBounds(t.X, t.Y)) continue;
                    if (sea) seaN++; else landN++;
                    bool w0 = rules[0].Water(f, t.X, t.Y);
                    if (sea != w0 && misses++ < 0)
                    {
                        var d = f.Data!; var g = d.VertexFlags!;
                        Console.WriteLine($"miss {r.Id} {o} floor={t.Floor} sea={sea} v7=[{g[t.X, t.Y]},{g[t.X + 1, t.Y]},{g[t.X, t.Y + 1]},{g[t.X + 1, t.Y + 1]}] h={d.SampleHeight(t.X + .5f, t.Y + .5f):F2} c10={d.HalfTileMap?[2 * t.X + 1, 2 * t.Y + 1]} v15={d.VertexMap15?[t.X, t.Y]} cls={f.At(t.X, t.Y)}");
                    }
                    if (sea != w0) missFloor[t.Floor]++;
                    for (int i = 0; i < rules.Length; i++)
                    {
                        bool w = rules[i].Water(f, t.X, t.Y);
                        if (sea && w) seaOk[i]++;
                        if (!sea && !w) landOk[i]++;
                    }
                }
            }
        }
        Console.WriteLine($"misses by floor: surface {missFloor[0]} underground {missFloor[1]}");
        for (int i = 0; i < rules.Length; i++)
            Console.WriteLine($"{rules[i].Name,-5} sea {seaOk[i]}/{seaN}  land {landOk[i]}/{landN}");
        return 0;
    }

    static int C10(MapFloor f, int x, int y) => f.Data!.HalfTileMap is { } h && 2 * x + 1 < h.Width && 2 * y + 1 < h.Height ? h[2 * x + 1, 2 * y + 1] : 0;
    static int C10Avg(MapFloor f, int x, int y)
    {
        if (f.Data!.HalfTileMap is not { } h) return 0;
        int s = 0, n = 0;
        for (int dy = 0; dy <= 2; dy++) for (int dx = 0; dx <= 2; dx++)
            if (2 * x + dx < h.Width && 2 * y + dy < h.Height) { s += h[2 * x + dx, 2 * y + dy]; n++; }
        return n == 0 ? 0 : s / n;
    }

    static int Count(MapFloor f, int x, int y)
    {
        var g = f.Data!.VertexFlags!;
        int n = 0;
        for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
                if (g[Math.Min(x + dx, g.Width - 1), Math.Min(y + dy, g.Height - 1)] == 0) n++;
        return n;
    }
}
