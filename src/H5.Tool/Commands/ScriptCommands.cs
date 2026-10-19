using H5.Adventure.Map;
using H5.Scripting.Lua;
using H5.Scripting.Runtime;

namespace H5.Tool.Commands;

sealed class LuaCompileCommand : ICommand
{
    public string Name => "lua-compile";
    public string Help => "lua-compile [-v]  - compile every .lua in the game data and every map's script with the Lua 4.0 compiler";

    public int Run(ToolContext ctx, string[] args)
    {
        bool verbose = args.Contains("-v");
        int ok = 0, failed = 0;
        void Try(string name, string src)
        {
            try
            {
                var p = Compiler.Compile(src, name);
                ok++;
                if (verbose) Console.WriteLine($"ok   {name} ({p.Code.Count} instrs, {p.Protos.Count} functions)");
            }
            catch (LuaSyntaxException e)
            {
                failed++;
                Console.WriteLine($"FAIL {e.Message}");
            }
        }

        foreach (var path in ctx.Fs.Paths.Where(p => p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            Try(path, ScriptText.FromBytes(ctx.Fs.ReadAllBytes(path)));

        var loader = new MapLoader(H5.Core.Data.GameDatabase.Load(ctx.Fs), ctx.Fs);
        foreach (var m in MapCatalog.All(ctx.Fs, ctx.Install).Where(m => m.IsUserMap))
        {
            using var files = m.Open(ctx.Fs);
            var map = loader.Load(files);
            if (map.ScriptPath != null && files.TryReadAllBytes(map.ScriptPath, out var b))
                Try($"{m.Id}:{map.ScriptPath}", ScriptText.FromBytes(b));
        }
        Console.WriteLine($"compiled {ok}, failed {failed}");
        return failed == 0 ? 0 : 1;
    }
}

sealed class ScriptRunCommand : ICommand
{
    public string Name => "script-run";
    public string Help => "script-run <map|all|campaign> [days=14] [seed=1] [-v] [--no-map-script-check] [--include-user]  - play maps with their Lua scripts (bots for every player) and report script errors";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        bool verbose = args.Contains("-v");
        var pos = args.Where(a => !a.StartsWith('-')).ToArray();
        int days = pos.Length > 1 ? int.Parse(pos[1]) : 14;
        ulong seed = pos.Length > 2 ? ulong.Parse(pos[2]) : 1;
        var core = H5.Core.Data.GameDatabase.Load(ctx.Fs);
        var db = H5.Adventure.Data.AdventureDb.Load(core, ctx.Fs);
        var loader = new MapLoader(core, ctx.Fs);
        var ids = new EnumTables(ctx.Fs);
        bool includeUser = args.Contains("--include-user");
        var maps = pos[0] == "all"
            ? MapCatalog.All(ctx.Fs, ctx.Install).Where(m => includeUser || !m.IsUserMap).ToList()
            : new List<MapCatalog.MapRef> { MapCatalog.Find(ctx.Fs, ctx.Install, pos[0]) ?? throw new ArgumentException("map not found") };
        int totalErrors = 0, crashed = 0, clean = 0;
        foreach (var mref in maps)
        {
          try
          {
            using var files = mref.Open(ctx.Fs);
            var map = loader.Load(files);
            if (map.Players.Count(p => p.Active) == 0) { Console.WriteLine($"{mref.Id,-40} (no active players: not a playable map)"); continue; }
            var (g, _) = H5.Adventure.Engine.Adventure.NewGame(db, map, H5.Adventure.Engine.GameSetup.AllPlayers(map, humans: 1, seed));
            if (args.Contains("--heroes"))
            {
                foreach (var h in g.S.Heroes) Console.WriteLine($"  hero {h.DefId} ({h.Name}) owner {h.Owner} at {h.Pos} town {h.InTown} garrison {h.Garrisoned}");
                foreach (var h in g.S.OffMapHeroes) Console.WriteLine($"  reserve {h.DefId} owner {h.Owner}");
                foreach (var o in map.OfKind(ObjectKind.Town)) Console.WriteLine($"  town #{o.Index} {o.Name} owner {o.Owner} garrisonHero {o.GarrisonHero?.Shared?.Id}");
            }
            var host = AdventureScripting.Create(g, files, ids, loader.Catalog);
            host.AutoAnswerUi = true;
            host.RunWithoutMapScript = args.Contains("--no-map-script-check");
            host.SegmentsPerCommand = 2;
            string? src = null;
            if (map.ScriptPath != null && files.TryReadAllBytes(map.ScriptPath, out var b)) src = ScriptText.FromBytes(b);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            host.Start(map.ScriptPath, src);
            if (args.Contains("--trace"))
            {
                var inner = g.CommandSink!;
                g.CommandSink = cmd => { Console.Error.WriteLine($"[day {g.S.Day} p{g.S.CurrentPlayer}] {cmd}"); var r = inner(cmd); Console.Error.WriteLine($"   -> {(r.Ok ? "ok" : r.Error)} threads={host.L.Threads.Count}"); return r; };
            }
            var bot = new H5.Adventure.Engine.SimpleAdventureBot();
            int turns = 0;
            while (!g.S.Finished && g.S.Day <= days && turns < days * 10)
            {
                // A blocked game (BlockGame) only advances script time, like the original.
                for (int guard = 0; host.World.GameBlocked && guard < 3000; guard++) host.Tick();
                bot.PlayTurn(g, seed * 1000003UL + (ulong)turns);
                host.Tick(20);
                turns++;
            }
            totalErrors += host.Errors.Count;
            if (host.Errors.Count == 0) clean++;
            Console.WriteLine($"{mref.Id,-40} script={(src != null ? "yes" : "no "),-3} day {g.S.Day,3} threads {host.L.Threads.Count,3} ui {host.World.PendingUi.Count} events {host.Events.Count,5} errors {host.Errors.Count} ({sw.ElapsedMilliseconds} ms){(g.S.Finished ? " finished" : "")}");
            foreach (var e in host.Errors.Distinct().Take(verbose ? 50 : 5)) Console.WriteLine("    " + e);
            if (verbose) foreach (var l in host.Log.Take(40)) Console.WriteLine("    | " + l);
          }
          catch (Exception e)
          {
              // An engine (non-script) failure ends this map's run; report it and go on.
              crashed++;
              Console.WriteLine($"{mref.Id,-40} ENGINE EXCEPTION {e.GetType().Name}: {e.Message}");
              Console.WriteLine("    " + e.StackTrace?.Split((char)10).FirstOrDefault()?.Trim());
          }
        }
        Console.WriteLine($"maps {maps.Count}, clean {clean}, engine exceptions {crashed}, total script errors: {totalErrors}");
        return totalErrors == 0 ? 0 : 1;
    }
}
