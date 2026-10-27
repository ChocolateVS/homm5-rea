using H5.Adventure.Map;
using H5.Core.Data;

namespace H5.Tool.Commands;

/// <summary>
/// Terrain passability survey (exe 00990800, <see cref="TerrainPassability"/>) over every map: tile kinds per floor,
/// chunk 14 / BorderSize, object entrances and heroes on refused tiles, and the objects nobody can reach from the
/// players' heroes and towns (walking, sailing counted as open) under the exe rule and under the old rule
/// (TT_SMALL_WATER dominant tiles and object footprints only).
/// </summary>
sealed class AdvPassCommand : ICommand
{
    public string Name => "adv-pass";
    public string Help => "adv-pass [filter] [-v]  - terrain passability kinds, blocked entrances and unreachable objects per map";

    public int Run(ToolContext ctx, string[] args)
    {
        var db = GameDatabase.Load(ctx.Fs);
        var loader = new MapLoader(db, ctx.Fs);
        string? filter = args.FirstOrDefault(a => !a.StartsWith("-"));
        bool verbose = args.Contains("-v");
        int maps = 0, totEntr = 0, totHero = 0, totNew = 0, totOld = 0;
        foreach (var r in MapCatalog.All(ctx.Fs, ctx.Install))
        {
            if (filter != null && !r.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            AdvMap m;
            string border;
            try
            {
                m = loader.Load(r);
                var root = r.Open(ctx.Fs).LoadXml(r.Open(ctx.Fs).MapXdbPath).Root;
                border = root?.Element("BorderSize")?.Value.Trim() ?? "-";
            }
            catch (Exception e) { Console.WriteLine($"FAIL {r.Id}: {e.Message}"); continue; }
            maps++;
            var sb = new System.Text.StringBuilder($"{r.Id} border={border}");
            int entr = 0, heroes = 0;
            foreach (var f in m.Floors)
            {
                if (f?.Data == null) continue;
                var k = f.Kind;
                if (verbose)
                    for (int li = 0; li < f.Data.Layers.Count; li++)
                        Console.WriteLine($"  f{f.Index} layer {li}: type {f.LayerTypes.ElementAtOrDefault(li)} {f.Data.Layers[li].TilePath}");
                sb.Append($" | f{f.Index} c14={f.Data.Flags14} c15={(f.Data.VertexMap15 == null ? "-" : f.Data.VertexMap15.Data.Count(v => v == 0).ToString())}" +
                          $" land={k.Count(x => x == TileKind.Land)} water={k.Count(x => x == TileKind.Water)} imp={k.Count(x => x == TileKind.Impassable)}" +
                          $" obj={k.Count(x => x == TileKind.ObjectPassable)} edge={k.Count(x => x == TileKind.Edge)} none={f.Ground.Count(g => g == TerrainPassability.TypeNone)}");
            }
            foreach (var o in m.Objects)
            {
                var f = m.Floor(o.Pos.Floor);
                if (f == null) continue;
                if (o.Kind == ObjectKind.Hero)
                {
                    if (f.InBounds(o.Pos.X, o.Pos.Y) && f.TerrainBlocked(o.Pos.X, o.Pos.Y))
                    {
                        heroes++;
                        if (verbose) Console.WriteLine($"  hero on refused tile {o} {Probe(f, o.Pos)}");
                    }
                    continue;
                }
                if (o.Kind is ObjectKind.Static or ObjectKind.Unknown || o.Shared.Active.Count == 0) continue;
                if (o.ActiveTiles.All(t => !f.InBounds(t.X, t.Y) || f.TerrainBlocked(t.X, t.Y)))
                {
                    entr++;
                    if (verbose) Console.WriteLine($"  entrance refused {o} {Probe(f, o.ActiveTiles.First())}");
                }
            }
            int un = Unreachable(m, (f, x, y) => f.Blocked[y * f.Width + x], verbose ? "new" : null);
            int uo = Unreachable(m, (f, x, y) => OldBlocked(m, f, x, y), null);
            sb.Append($" | entrances refused {entr}, heroes refused {heroes}, unreachable {un} (old rule {uo})");
            Console.WriteLine(sb);
            totEntr += entr; totHero += heroes; totNew += un; totOld += uo;
        }
        Console.WriteLine($"{maps} maps: entrances refused {totEntr}, heroes refused {totHero}, unreachable objects {totNew} (old rule {totOld})");
        return 0;
    }

    static readonly Dictionary<MapFloor, bool[]> OldCache = new();

    static bool OldBlocked(AdvMap m, MapFloor f, int x, int y)
    {
        if (!OldCache.TryGetValue(f, out var b))
        {
            b = new bool[f.Width * f.Height];
            for (int i = 0; i < b.Length; i++) b[i] = f.TileType[i] == "TT_SMALL_WATER";
            foreach (var o in m.Objects)
            {
                if (o.Pos.Floor != f.Index) continue;
                var act = new HashSet<Tile>(o.ActiveTiles);
                foreach (var t in o.BlockedTiles)
                    if (f.InBounds(t.X, t.Y) && !act.Contains(t)) b[t.Y * f.Width + t.X] = true;
            }
            OldCache[f] = b;
        }
        return b[y * f.Width + x];
    }

    static string Probe(MapFloor f, Tile t)
    {
        var d = f.Data!;
        int L(int x, int y) => d.VertexFlags?[Math.Min(x, d.VertexFlags.Width - 1), Math.Min(y, d.VertexFlags.Height - 1)] ?? -1;
        int i = t.Y * f.Width + t.X;
        return $"kind={f.Kind[i]} ground={f.Ground[i]} lv=[{L(t.X, t.Y)},{L(t.X + 1, t.Y)},{L(t.X, t.Y + 1)},{L(t.X + 1, t.Y + 1)}] " +
               $"c15={d.VertexMap15?[t.X, t.Y]} c10={d.HalfTileMap?[Math.Min(2 * t.X + 1, d.HalfTileMap.Width - 1), Math.Min(2 * t.Y + 1, d.HalfTileMap.Height - 1)]}";
    }

    /// <summary>
    /// Objects none of whose entrances a flood from the players' heroes and town entrances reaches. The flood walks
    /// 8-connected over tiles the rule leaves open (sea included, guards and visits ignored); every teleporting object
    /// links to all the others.
    /// </summary>
    static int Unreachable(AdvMap m, Func<MapFloor, int, int, bool> blocked, string? tag)
    {
        var entrances = new HashSet<Tile>();
        foreach (var o in m.Objects)
            if (o.Kind is not (ObjectKind.Static or ObjectKind.Unknown or ObjectKind.Hero))
                foreach (var t in o.ActiveTiles) entrances.Add(t);
        // Every teleporting object (gates, monoliths, portals, whirlpools) is taken as linked to all the others.
        var portals = new HashSet<Tile>(m.Objects.Where(o => o.Shared.Type is var ty &&
                (ty.Contains("GATE") || ty.Contains("MONOLITH") || ty.Contains("PORTAL") || ty.Contains("WHIRLPOOL") || ty.Contains("TELEPORT")))
            .SelectMany(o => o.ActiveTiles));
        var seen = new HashSet<Tile>();
        var q = new Queue<Tile>();
        var seeds = new HashSet<Tile>();
        foreach (var o in m.Objects)
        {
            bool start = o.Kind == ObjectKind.Hero || (o.Kind == ObjectKind.Town && m.Players.Any(p => p.Active && p.MainTownUid == o.Uid));
            if (!start) continue;
            foreach (var t in o.Kind == ObjectKind.Hero ? new[] { o.Pos } : o.ActiveTiles)
                if (seen.Add(t)) { q.Enqueue(t); seeds.Add(t); }
        }
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            var f = m.Floor(c.Floor);
            if (f == null) continue;
            if (portals.Contains(c))
                foreach (var t in portals)
                    if (seen.Add(t)) q.Enqueue(t);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = c.Offset(dx, dy);
                    if ((dx == 0 && dy == 0) || !f.InBounds(n.X, n.Y) || blocked(f, n.X, n.Y) || !seen.Add(n)) continue;
                    q.Enqueue(n);
                }
        }
        int un = 0;
        foreach (var o in m.Objects)
        {
            if (o.Kind is ObjectKind.Static or ObjectKind.Unknown or ObjectKind.Hero || o.Shared.Active.Count == 0) continue;
            if (o.ActiveTiles.Any(seen.Contains)) continue;
            un++;
            if (tag != null && m.Floor(o.Pos.Floor) is { } uf && uf.InBounds(o.ActiveTiles.First().X, o.ActiveTiles.First().Y)) Console.WriteLine($"  unreachable ({tag}) {o} {Probe(uf, o.ActiveTiles.First())}");
        }
        return un;
    }
}

/// <summary>Prints the level grid (chunk 7) and the tile kinds around a tile.</summary>
sealed class AdvPassProbeCommand : ICommand
{
    public string Name => "adv-pass-probe";
    public string Help => "adv-pass-probe <map> <x> <y> [floor] [radius=4]  - level vertices and tile kinds around a tile";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 3) { Console.WriteLine(Help); return 1; }
        var m = new MapLoader(GameDatabase.Load(ctx.Fs), ctx.Fs).Load(MapCatalog.Find(ctx.Fs, ctx.Install, args[0])!);
        int cx = int.Parse(args[1]), cy = int.Parse(args[2]), fl = args.Length > 3 ? int.Parse(args[3]) : 0, r = args.Length > 4 ? int.Parse(args[4]) : 4;
        var f = m.Floor(fl)!; var d = f.Data!;
        Console.WriteLine("levels (vertex rows y, columns x):");
        for (int y = cy - r; y <= cy + r + 1; y++)
            Console.WriteLine($"{y,4}: " + string.Join(" ", Enumerable.Range(cx - r, 2 * r + 2).Select(x => x < 0 || y < 0 || x >= d.VertexFlags!.Width || y >= d.VertexFlags.Height ? "  ." : $"{d.VertexFlags[x, y],3}")));
        Console.WriteLine("kinds (L land, W water, X impassable, O object-passable, E edge; lower case = object-blocked; 9 = TT_NONE):");
        for (int y = cy - r; y <= cy + r; y++)
            Console.WriteLine($"{y,4}: " + string.Join(" ", Enumerable.Range(cx - r, 2 * r + 1).Select(x =>
            {
                if (!f.InBounds(x, y)) return ".";
                int i = y * f.Width + x;
                string c = f.Kind[i] switch { TileKind.Land => "L", TileKind.Water => "W", TileKind.Impassable => "X", TileKind.ObjectPassable => "O", TileKind.Edge => "E", _ => "?" };
                if (f.Ground[i] == TerrainPassability.TypeNone) c = "9";
                return f.Blocked[i] && !f.TerrainBlocked(x, y) ? c.ToLowerInvariant() : c;
            })));
        return 0;
    }
}
