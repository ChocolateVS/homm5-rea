using System.Diagnostics;
using H5.Adventure.Map;
using H5.Core.Data;

namespace H5.Tool.Commands;

sealed class AdvLoadCommand : ICommand
{
    public string Name => "adv-load";
    public string Help => "adv-load <map id|short name|file.h5m>  - summarise an adventure map";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        var mapRef = MapCatalog.Find(ctx.Fs, ctx.Install, args[0]);
        if (mapRef == null) { Console.WriteLine($"no map matches '{args[0]}'"); return 1; }
        var db = GameDatabase.Load(ctx.Fs);
        var sw = Stopwatch.StartNew();
        var m = new MapLoader(db, ctx.Fs).Load(mapRef);
        Console.WriteLine($"{m.Id}  \"{m.Name}\"  {m.TilesX}x{m.TilesY}{(m.HasUnderground ? " +underground" : "")}  v{m.Version}  loaded in {sw.ElapsedMilliseconds} ms");
        if (m.Description.Length > 0) Console.WriteLine($"  {m.Description.ReplaceLineEndings(" ")[..Math.Min(160, m.Description.Length)]}");
        foreach (var p in m.Players.Where(p => p.Active))
        {
            var town = m.Objects.FirstOrDefault(o => o.Uid == p.MainTownUid);
            Console.WriteLine($"  player {p.Index + 1} {p.Colour} team {p.Team} {p.Race} human={p.CanBeHuman} ai={p.CanBeComputer} main town {town?.Pos.ToString() ?? "-"}");
        }
        foreach (var g in m.Objects.GroupBy(o => o.Kind).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-14} {g.Count(),6}");
        foreach (var o in m.Objects.Where(o => o.Kind is ObjectKind.Town or ObjectKind.Hero))
            Console.WriteLine($"    {o.Kind} {o.Pos} owner {o.Owner + 1} {o.TownType ?? o.Hero?.DisplayName} {(o.Hero != null ? $"lvl {o.Hero.Level} army {string.Join(", ", o.Hero.Army.Select(a => $"{a.Count} {a.Creature}"))}" : "")}");
        foreach (var f in m.Floors.Where(f => f != null))
        {
            var counts = f!.Terrain.GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {100.0 * g.Count() / f.Terrain.Length:0.#}%");
            Console.WriteLine($"  floor {f.Index}: {f.Width}x{f.Height}  blocked {100.0 * f.Blocked.Count(b => b) / f.Blocked.Length:0.#}%  {string.Join(", ", counts)}");
        }
        foreach (var o in m.Objectives.Where(o => o.Kind != "OBJECTIVE_KIND_SCENARIO_INFO"))
            Console.WriteLine($"  objective {(o.Primary ? "primary" : "secondary")} p{o.Player + 1} {o.Kind} {o.Name} {string.Join(",", o.Parameters)}");
        if (m.ScriptPath != null) Console.WriteLine($"  script {m.ScriptPath} ({m.ScriptSource?.Length ?? 0} chars)");
        foreach (var w in m.Warnings.Take(20)) Console.WriteLine($"  warning: {w}");
        return 0;
    }
}

sealed class AdvStatsCommand : ICommand
{
    public string Name => "adv-stats";
    public string Help => "adv-stats  - load every stock and user adventure map and report failures";

    public int Run(ToolContext ctx, string[] args)
    {
        var db = GameDatabase.Load(ctx.Fs);
        var loader = new MapLoader(db, ctx.Fs);
        var maps = MapCatalog.All(ctx.Fs, ctx.Install);
        int ok = 0, objects = 0, warnings = 0;
        var missingShared = new SortedSet<string>();
        var unknownKinds = new SortedSet<string>();
        var sw = Stopwatch.StartNew();
        foreach (var r in maps)
        {
            try
            {
                var m = loader.Load(r);
                ok++;
                objects += m.Objects.Count;
                warnings += m.Warnings.Count;
                foreach (var o in m.Objects)
                {
                    if (o.Shared.Tag == "missing") missingShared.Add(o.SharedPath);
                    if (o.Kind == ObjectKind.Unknown) unknownKinds.Add(o.Tag);
                }
                foreach (var w in m.Warnings.Take(3)) Console.WriteLine($"  {r.Id}: {w}");
            }
            catch (Exception e) { Console.WriteLine($"FAIL {r.Id}: {e.GetType().Name}: {e.Message}"); }
        }
        Console.WriteLine($"{ok}/{maps.Count} maps loaded, {objects} objects, {warnings} warnings, {sw.ElapsedMilliseconds} ms");
        if (unknownKinds.Count > 0) Console.WriteLine("unknown object kinds: " + string.Join(", ", unknownKinds));
        if (missingShared.Count > 0) Console.WriteLine($"{missingShared.Count} missing shared descriptors, e.g. {string.Join(", ", missingShared.Take(5))}");
        return ok == maps.Count ? 0 : 2;
    }
}

/// <summary>
/// Evidence for the footprint rotation convention: under the correct convention the
/// entrance (active) tiles of rotated objects are rarely covered by other objects'
/// blocked tiles or by water.
/// </summary>
sealed class AdvFootprintCheckCommand : ICommand
{
    public string Name => "adv-footprint-check";
    public string Help => "adv-footprint-check  - compare rotation conventions against entrance obstruction";

    static (int, int) Rotate((int X, int Y) o, int q, bool ccw)
    {
        if (!ccw) q = (4 - q) % 4;
        return q switch { 1 => (-o.Y, o.X), 2 => (-o.X, -o.Y), 3 => (o.Y, -o.X), _ => (o.X, o.Y) };
    }

    public int Run(ToolContext ctx, string[] args)
    {
        var db = GameDatabase.Load(ctx.Fs);
        var loader = new MapLoader(db, ctx.Fs);
        var tally = new Dictionary<string, (int Checked, int Obstructed)>();
        foreach (var r in MapCatalog.All(ctx.Fs, ctx.Install).Where(m => !m.IsUserMap))
        {
            var m = loader.Load(r);
            foreach (var ccw in new[] { true, false })
                foreach (var flipY in new[] { false, true })
                {
                    var key = $"{(ccw ? "ccw" : "cw")}{(flipY ? "+flipY" : "")}";
                    var blocked = new HashSet<Tile>();
                    foreach (var o in m.Objects)
                    {
                        int q = Footprint.QuarterTurns(o.Rot);
                        var act = o.Shared.Active.Select(a => Rotate(F(a, flipY), q, ccw)).ToHashSet();
                        foreach (var b in o.Shared.Blocked)
                        {
                            var rb = Rotate(F(b, flipY), q, ccw);
                            if (!act.Contains(rb)) blocked.Add(o.Pos.Offset(rb.Item1, rb.Item2));
                        }
                    }
                    var (c, ob) = tally.GetValueOrDefault(key);
                    foreach (var o in m.Objects.Where(o => o.Kind != ObjectKind.Static && Footprint.QuarterTurns(o.Rot) != 0 && o.Shared.Active.Count > 0))
                    {
                        int q = Footprint.QuarterTurns(o.Rot);
                        foreach (var a in o.Shared.Active)
                        {
                            var ra = Rotate(F(a, flipY), q, ccw);
                            var t = o.Pos.Offset(ra.Item1, ra.Item2);
                            var f = m.Floor(t.Floor);
                            c++;
                            if (blocked.Contains(t) || f == null || !f.InBounds(t.X, t.Y) || f.At(t.X, t.Y) is TerrainClass.Water or TerrainClass.River && !o.Shared.WaterBased) ob++;
                        }
                    }
                    tally[key] = (c, ob);
                }
        }
        foreach (var (k, (c, ob)) in tally.OrderBy(k => k.Value.Obstructed))
            Console.WriteLine($"{k,-10} entrances checked {c}, obstructed {ob} ({100.0 * ob / Math.Max(1, c):0.0}%)");
        return 0;
    }

    static (int X, int Y) F((int X, int Y) o, bool flipY) => flipY ? (o.X, -o.Y) : o;
}
