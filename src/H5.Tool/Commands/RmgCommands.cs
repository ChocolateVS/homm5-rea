using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using H5.Adventure.Map;
using H5.Assets.Textures;
using H5.Rmg;

namespace H5.Tool.Commands;

static class RmgShared
{
    public static RmgOptions Parse(string[] args, out string? png, out string? outDir)
    {
        png = null; outDir = null;
        var o = new RmgOptions { Template = args[0] };
        if (args.Length > 1) o.Size = SizeOf(args[1]);
        if (args.Length > 2 && int.TryParse(args[2], out var seed)) o.Seed = seed;
        for (int i = 3; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--players=")) o.Players = int.Parse(a[10..]);
            else if (a == "--underground") o.Underground = true;
            else if (a == "--water") o.WaterAmount = 2;
            else if (a.StartsWith("--monsters=")) o.MonsterLevel = int.Parse(a[11..]);
            else if (a.StartsWith("--png=")) png = a[6..];
            else if (!a.StartsWith("--")) outDir = a;
        }
        return o;
    }

    public static int SizeOf(string s) => s.ToLowerInvariant() switch
    {
        "t" or "tiny" => 0, "s" or "small" => 1, "m" or "n" or "normal" or "medium" => 2, "l" or "large" => 3,
        "xl" => 4, "h" or "huge" => 5, "i" or "impossible" => 6, _ => int.TryParse(s, out var n) ? n : 1,
    };

    public static Dictionary<string, int> Counts(RmgResult r) =>
        r.Objects.GroupBy(o => o.Def.Tag.Replace("Shared", "")).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Top-down overview: terrain by zone (shaded by race colour), obstacles dark, objects as markers.</summary>
    public static void Render(RmgResult r, int floor, string path)
    {
        var g = r.Generator;
        var f = g.Floors[floor];
        int scale = Math.Max(2, 900 / g.W);
        int w = g.W * scale, h = g.H * scale;
        var px = new byte[w * h * 4];
        void Fill(int tx, int ty, (byte R, byte G, byte B) c, int inset = 0)
        {
            for (int y = ty * scale + inset; y < (ty + 1) * scale - inset; y++)
                for (int x = tx * scale + inset; x < (tx + 1) * scale - inset; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = c.R; px[i + 1] = c.G; px[i + 2] = c.B; px[i + 3] = 255;
                }
        }
        var palette = new (byte, byte, byte)[] { (120, 150, 90), (160, 140, 90), (110, 130, 150), (150, 110, 130), (130, 160, 150), (170, 150, 120), (100, 120, 100), (150, 150, 160), (140, 120, 90), (120, 140, 170), (170, 120, 100), (110, 160, 120), (160, 160, 110), (130, 110, 160), (150, 130, 110), (100, 150, 150) };
        for (int x = 0; x < g.W; x++)
            for (int y = 0; y < g.H; y++)
            {
                int id = f.Zone[f.At(x, y)];
                var c = id < 0 ? ((byte)40, (byte)40, (byte)40) : palette[id % palette.Length];
                if (r.TileLayers[floor][y * g.W + x].Contains("Road", StringComparison.OrdinalIgnoreCase)) c = (200, 190, 150);
                Fill(x, y, c);
            }
        foreach (var o in r.Objects.Where(o => o.Floor == floor && o.Def.Tag == "AdvMapStaticShared"))
            foreach (var b in o.Def.Blocked.DefaultIfEmpty((0, 0)))
            {
                var t = Footprint.Place(new Tile(o.X, o.Y), b, o.Rot);
                if (f.In(t.X, t.Y)) Fill(t.X, t.Y, (35, 55, 30));
            }
        foreach (var o in r.Objects.Where(o => o.Floor == floor && o.Def.Tag != "AdvMapStaticShared"))
        {
            (byte, byte, byte) c = o.Def.Tag switch
            {
                "AdvMapTownShared" => (255, 255, 255), "AdvMapMonsterShared" => (230, 30, 30), "AdvMapMineShared" => (250, 200, 0),
                "AdvMapTreasureShared" => (255, 140, 0), "AdvMapArtifactShared" => (200, 0, 255), "AdvMapDwellingShared" => (0, 200, 255),
                "AdvMapShrineShared" => (0, 90, 255), _ => (0, 255, 160),
            };
            foreach (var b in o.Def.Blocked.Concat(o.Def.Active).DefaultIfEmpty((0, 0)))
            {
                var t = Footprint.Place(new Tile(o.X, o.Y), b, o.Rot);
                if (f.In(t.X, t.Y)) Fill(t.X, t.Y, c, o.Def.Tag == "AdvMapMonsterShared" ? 0 : Math.Min(1, scale / 4));
            }
        }
        File.WriteAllBytes(path, PngWriter.Encode(px, w, h));
    }
}

sealed class RmgCommand : ICommand
{
    public string Name => "rmg";
    public string Help => "rmg <template> <size t|s|m|l|xl|h|i> <seed> [--players=N] [--underground] [--monsters=0..4] [--png=out.png]  - generate a random map";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        var o = RmgShared.Parse(args, out var png, out _);
        var db = RmgDatabase.Load(new H5.Core.Xdb.XdbLoader(ctx.Fs));
        if (!db.Templates.ContainsKey(o.Template))
        {
            Console.WriteLine("templates: " + string.Join(", ", db.Templates.Keys));
            return 1;
        }
        var sw = Stopwatch.StartNew();
        var gen = new RmgGenerator(db, o);
        var res = gen.Generate();
        Console.WriteLine($"generated in {sw.ElapsedMilliseconds} ms: {res.Describe()}");
        foreach (var l in gen.Log.Take(40)) Console.WriteLine("  " + l);
        Console.WriteLine("  rnd counters: " + string.Join(", ", gen.Counters.Select(c => $"{c.Stage}={c.Counter}")));
        foreach (var z in gen.Zones) Console.WriteLine($"  {z} tiles {z.Tiles.Count} town {z.TownPos}");
        foreach (var kv in RmgShared.Counts(res).OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Key,-16} {kv.Value,6}");
        Console.WriteLine($"  passages {res.Passages.Count / 2}, unreachable objects {res.Unreachable}, warnings {res.Warnings.Count}");
        foreach (var w in res.Warnings.Take(10)) Console.WriteLine("    " + w);
        var map = res.ToAdvMap(new SharedCatalog(new H5.Core.Xdb.XdbLoader(ctx.Fs)));
        Console.WriteLine($"  AdvMap: {map.Objects.Count} objects, floors {map.Floors.Count(f => f != null)}, blocked {map.Floors[0]!.Blocked.Count(b => b)} tiles");
        if (png != null)
        {
            for (int f = 0; f < gen.Floors.Count; f++)
                RmgShared.Render(res, f, f == 0 ? png : Path.ChangeExtension(png, null) + $"_u{f}.png");
            Console.WriteLine($"  wrote {png}");
        }
        return 0;
    }

    static int RaceOf(string town) => town switch
    {
        "Heaven" => 3, "Preserve" => 4, "Academy" => 5, "Dungeon" => 6, "Necromancy" => 7, "Inferno" => 8,
        "Fortress" => 9, "Orc_Stronghold" => 10, _ => 1,
    };
}

sealed class RmgStatsCommand : ICommand
{
    public string Name => "rmg-stats";
    public string Help => "rmg-stats [seeds=1] [--threads=N] [--filter=substr] [--max-players]  - generate every template at every size/underground/player count the RMG screen lists it for (exe 0x0090c880), report failures";

    public int Run(ToolContext ctx, string[] args)
    {
        int seeds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 1;
        int threads = Environment.ProcessorCount / 2;
        string? filter = null;
        bool maxOnly = false;
        foreach (var a in args)
        {
            if (a.StartsWith("--threads=")) threads = int.Parse(a[10..]);
            else if (a.StartsWith("--filter=")) filter = a[9..];
            else if (a == "--max-players") maxOnly = true;
        }
        var db0 = RmgDatabase.Load(new H5.Core.Xdb.XdbLoader(ctx.Fs));
        var jobs = new List<(string T, int Size, bool Ug, int Players, int Seed)>();
        foreach (var t in db0.Templates.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (filter != null && !t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            for (int size = 0; size <= 6; size++)
                foreach (bool ug in new[] { false, true })
                    for (int p = maxOnly ? t.MaxPlayers : t.MinPlayers; p <= t.MaxPlayers; p++)
                        if (t.ListedFor(size, ug, false, p))
                            for (int seed = 1; seed <= seeds; seed++) jobs.Add((t.Name, size, ug, p, seed * 7919 + size * 31 + p));
        }
        Console.WriteLine($"{db0.Templates.Count} templates, {jobs.Count} runs on {threads} threads");
        var lines = new string[jobs.Count];
        int ok = 0, fail = 0, unreachable = 0, next = -1;
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, Math.Max(1, threads)).Select(_ => new Thread(() =>
        {
            using var fs = H5.Core.IO.PakFileSystem.OpenInstall(ctx.Install);
            var xdb = new H5.Core.Xdb.XdbLoader(fs);
            var db = RmgDatabase.Load(xdb);
            var catalog = new SharedCatalog(xdb);
            int i;
            while ((i = Interlocked.Increment(ref next)) < jobs.Count)
            {
                var j = jobs[i];
                string head = $"{j.T,-24} size {j.Size} ug {(j.Ug ? 1 : 0)} players {j.Players} seed {j.Seed,6}";
                try
                {
                    var gen = new RmgGenerator(db, new RmgOptions { Template = j.T, Size = j.Size, Underground = j.Ug, Players = j.Players, Seed = j.Seed });
                    var r = gen.Generate();
                    var map = r.ToAdvMap(catalog);
                    var t = db.Templates[j.T];
                    int startZones = t.Zones.Count(z => z.CanBePlayerStart);
                    int expected = Math.Min(gen.Players, startZones);
                    int towns = r.StartTowns.Count;
                    Interlocked.Add(ref unreachable, r.Unreachable);
                    if (towns < expected) throw new Exception($"only {towns} start towns for {expected} (players {gen.Players}, start zones {startZones})");
                    if (gen.Players != j.Players) throw new Exception($"players resolved to {gen.Players}");
                    Interlocked.Increment(ref ok);
                    lines[i] = $"ok   {head}: {RmgSizes.Names[gen.SizeIndex]}{(gen.HasUnderground ? "+ug" : "")} {map.Objects.Count,5} objects, start towns {towns}, monsters {r.OfKind("AdvMapMonsterShared").Count()}, mines {r.OfKind("AdvMapMineShared").Count()}, unreachable {r.Unreachable}, warnings {r.Warnings.Count}";
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref fail);
                    lines[i] = $"FAIL {head}: {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split((char)10).FirstOrDefault()?.Trim()}";
                }
            }
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        foreach (var l in lines) Console.WriteLine(l);
        Console.WriteLine($"{ok} ok, {fail} failed, {unreachable} unreachable objects total, {sw.Elapsed.TotalSeconds:0.0}s");
        return fail == 0 ? 0 : 2;
    }
}

sealed class RmgRefCommand : ICommand
{
    public string Name => "rmg-ref";
    public string Help => "rmg-ref  - statistics of the RMG maps in the install's Maps folder, next to our generator on the same template/size";

    public int Run(ToolContext ctx, string[] args)
    {
        var db = RmgDatabase.Load(new H5.Core.Xdb.XdbLoader(ctx.Fs));
        var mapsDir = Path.Combine(ctx.Install.Root, "Maps");
        foreach (var file in Directory.EnumerateFiles(mapsDir, "*RMG*.h5m").Order())
        {
            using var z = ZipFile.OpenRead(file);
            string Read(string suffix)
            {
                var e = z.Entries.FirstOrDefault(x => x.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (e == null) return "";
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return H5.Core.Xdb.TextDecoding.Decode(ms.ToArray());
            }
            var desc = Read("mapdesc-text-0.txt");
            var xml = Read("/map.xdb");
            var tmpl = Regex.Match(desc, @"template (\S+)").Groups[1].Value;
            var seed = Regex.Match(desc, @"seed is (\d+)").Groups[1].Value;
            var size = int.Parse(Regex.Match(xml, @"<TileX>(\d+)").Groups[1].Value);
            var refCounts = Regex.Matches(xml, @"<Item href=""#n:inline\((\w+)\)").GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Count());
            Console.WriteLine($"{Path.GetFileName(file)}  {tmpl} {size}x{size} seed {seed}");
            int sizeIndex = Array.IndexOf(RmgSizes.Tiles, size);
            if (!db.Templates.ContainsKey(tmpl) || sizeIndex < 0) continue;
            int players = int.Parse(Regex.Match(desc, @"for (\d+) players").Groups[1].Value);
            int monsters = desc.Contains("weak") ? 0 : desc.Contains("impossible") ? 4 : desc.Contains("very strong") ? 3 : desc.Contains("strong") ? 2 : 1;
            // Player races as on the reference (the setup screen may have fixed them; random and fixed
            // races consume the same draws, and matching races keep town footprints identical).
            var refTowns = Regex.Matches(xml, @"<AdvMapTown[^>]*>(.*?)</AdvMapTown>", RegexOptions.Singleline)
                .Where(m => Regex.IsMatch(m.Groups[1].Value, @"<PlayerID>PLAYER_\d"))
                .Select(m => (Player: int.Parse(Regex.Match(m.Groups[1].Value, @"<PlayerID>PLAYER_(\d)").Groups[1].Value),
                              Race: RaceOf(Regex.Match(m.Groups[1].Value, @"MapObjects/([^.]+)\.").Groups[1].Value),
                              X: int.Parse(Regex.Match(m.Groups[1].Value, @"<x>(\d+)</x>").Groups[1].Value),
                              Y: int.Parse(Regex.Match(m.Groups[1].Value, @"<y>(\d+)</y>").Groups[1].Value)))
                .OrderBy(t => t.Player).ToList();
            int Level(string key, string prefix) => Array.IndexOf(new[] { "MISERABLE", "LITTLE", "NORMAL", "LOTS", "MUCH" },
                Regex.Match(xml, "<" + key + ">" + prefix + "([A-Z]+)<").Groups[1].Value) is var l and >= 0 ? l : 2;
            var opts = new RmgOptions
            {
                Template = tmpl, Size = sizeIndex, Players = players, MonsterLevel = monsters, Seed = (int)uint.Parse(seed),
                ResourceLevel = Level("ResourceMultiplier", "RESOURCE_"), ExpLevel = Level("ExpMultiplier", "EXP_"),
            };
            opts.PlayerRaces.AddRange(refTowns.Select(t => t.Race));
            var gen = new RmgGenerator(db, opts);
            var r = gen.Generate();
            var ours = RmgShared.Counts(r);
            foreach (var t in refTowns)
            {
                var tz = gen.Zones.FirstOrDefault(zz => zz.Player == t.Player);
                var mine = r.StartTowns.TryGetValue(t.Player - 1, out var o) ? $"({o.X},{o.Y}) q{o.Quarter} anchor {tz?.TownPos} zone {tz?.Id} c=({tz?.CenterX},{tz?.CenterY}) R={tz?.Radius}" : "-";
                Console.WriteLine($"    town P{t.Player} race {t.Race}: ref ({t.X},{t.Y})  ours {mine}");
            }
            foreach (var key in refCounts.Keys.Union(ours.Keys).Order())
                Console.WriteLine($"    {key,-16} ref {refCounts.GetValueOrDefault(key),6}   ours {ours.GetValueOrDefault(key),6}");
            if (args.Contains("--detail"))
            {
                // Per shared object (buildings, treasures, artifacts, shrines): Shared href of each inline object.
                var refShared = Regex.Matches(xml, @"<Item href=""#n:inline\((AdvMap(?:Building|Treasure|Artifact|Shrine|Dwelling))\)[^>]*>.*?<Shared href=""([^""#]+)", RegexOptions.Singleline)
                    .GroupBy(m => m.Groups[2].Value.TrimStart('/')).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var ourShared = r.Objects.Where(o => o.Def.Tag is "AdvMapBuildingShared" or "AdvMapTreasureShared" or "AdvMapArtifactShared" or "AdvMapShrineShared" or "AdvMapDwellingShared")
                    .GroupBy(o => o.Shared.TrimStart('/')).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                foreach (var key in refShared.Keys.Union(ourShared.Keys, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).Order())
                    Console.WriteLine($"      {key,-70} ref {refShared.GetValueOrDefault(key),4}   ours {ourShared.GetValueOrDefault(key),4}");
            }
        }
        return 0;
    }

    static int RaceOf(string town) => town switch
    {
        "Heaven" => 3, "Preserve" => 4, "Academy" => 5, "Dungeon" => 6, "Necromancy" => 7, "Inferno" => 8,
        "Fortress" => 9, "Orc_Stronghold" => 10, _ => 1,
    };
}
