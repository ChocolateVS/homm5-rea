using H5.Core.Data;

namespace H5.Tool.Commands;

static class Db
{
    static GameDatabase? _db;
    public static GameDatabase Get(ToolContext ctx) => _db ??= GameDatabase.Load(ctx.Fs);
}

sealed class CreatureCommand : ICommand
{
    public string Name => "creature";
    public string Help => "creature <CREATURE_ID|substring>  - dump resolved creature stats";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        var db = Db.Get(ctx);
        var matches = db.Creatures.Values.Where(c => c.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = db.Creatures.Values.Where(c => c.Id.Contains(args[0], StringComparison.OrdinalIgnoreCase)
                                                    || c.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var c in matches)
        {
            Console.WriteLine($"{c.Id}  \"{c.Name}\"  tier {c.Tier}{(c.Upgrade ? "+" : "")}  {c.Town}  ({c.SourcePath})");
            Console.WriteLine($"  Att {c.Attack}  Def {c.Defence}  Dmg {c.MinDamage}-{c.MaxDamage}  HP {c.Health}  Spd {c.Speed}  Init {c.Initiative}" +
                              $"  Shots {c.Shots}  Mana {c.SpellPoints}  Size {c.CombatSize}{(c.Flying ? "  flying" : "")}");
            Console.WriteLine($"  Growth {c.WeeklyGrowth}  Gold {c.Cost.Gold}  Exp {c.Exp}  Power {c.Power}  Elements {c.Element1}/{c.Element2}");
            Console.WriteLine($"  Abilities: {string.Join(", ", c.Abilities)}");
            if (c.KnownSpells.Count > 0)
                Console.WriteLine($"  Spells: {string.Join(", ", c.KnownSpells.Select(s => $"{s.Spell}({s.Mastery})"))}");
            if (c.Upgrades.Count > 0) Console.WriteLine($"  Upgrades: {string.Join(", ", c.Upgrades)}");
        }
        if (matches.Count == 0) Console.WriteLine("no match");
        return matches.Count > 0 ? 0 : 1;
    }
}

sealed class DuelSimCommand : ICommand
{
    public string Name => "duel-sim";
    public string Help => "duel-sim <presetA> <presetB> [seed] [--games N] [--quiet]  - AI vs AI duel from duel presets";

    public int Run(ToolContext ctx, string[] args)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToList();
        if (pos.Count < 2) { Console.WriteLine(Help); return 1; }
        int a = int.Parse(pos[0]), b = int.Parse(pos[1]);
        ulong seed = pos.Count > 2 ? ulong.Parse(pos[2]) : 1;
        int games = 1;
        int gi = Array.IndexOf(args, "--games");
        if (gi >= 0 && gi + 1 < args.Length) games = int.Parse(args[gi + 1]);
        bool quiet = args.Contains("--quiet") || games > 1;
        var db = Db.Get(ctx);
        var pa = db.DuelPresets[a]; var pb = db.DuelPresets[b];
        Console.WriteLine($"{pa.Name} ({pa.Hero.Shared?.Id}) vs {pb.Name} ({pb.Hero.Shared?.Id}), {games} game(s) from seed {seed}");
        int[] wins = new int[3];
        long totalCmds = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int g = 0; g < games; g++)
        {
            var state = H5.Combat.BattleSetup.CreateDuel(db, a, b, seed + (ulong)g);
            var battle = new H5.Combat.Battle(db, state);
            var ai = new H5.Combat.GreedyBattleAI();
            var events = H5.Combat.BattleRunner.RunToEnd(battle, ai, ai);
            totalCmds += events.Count;
            wins[state.Winner is int w ? w : 2]++;
            if (!quiet) foreach (var e in events) Console.WriteLine(Describe(battle, e));
            var survivors = string.Join(", ", state.Units.Where(u => u.Alive && u.Kind == H5.Combat.UnitKind.Creature).Select(u => $"s{u.Side}:{u.Count} {u.Name}"));
            Console.WriteLine($"game {g + 1}: winner side {state.Winner?.ToString() ?? "none"} ({state.EndReason}), time {state.Time:F1}, turns {state.TurnCounter}, hash {state.ComputeHash()}  survivors: {survivors}");
        }
        Console.WriteLine($"wins: {pa.Name} {wins[0]}, {pb.Name} {wins[1]}, none {wins[2]}  ({sw.ElapsedMilliseconds} ms, {totalCmds} events)");
        return 0;
    }

    static string Describe(H5.Combat.Battle b, H5.Combat.BattleEvent e)
    {
        string N(int id) => id < 0 ? $"Hero{-1 - id}" : b.State.Unit(id) is { } u ? $"{u.Name}#{u.Id}(s{u.Side})" : $"#{id}";
        return e switch
        {
            H5.Combat.TurnStarted t => $"[{t.Time,6:F2}] turn: {N(t.Actor)}",
            H5.Combat.Moved m => $"    {N(m.UnitId)} moves to {m.Path[^1]}",
            H5.Combat.AttackStarted at => $"    {N(at.Attacker)} {at.Kind} -> {N(at.Target)}",
            H5.Combat.Damaged d => $"      {N(d.Target)} takes {d.Damage}{(d.By != null ? $" ({d.By})" : "")}, {d.Killed} killed, {d.CountLeft} left",
            H5.Combat.SpellCast sc => $"    {N(sc.Caster)} casts {sc.Spell}{(sc.Target is int t2 ? " on " + N(t2) : "")}{(sc.Cell is { } c ? " at " + c : "")}",
            H5.Combat.EffectAdded ea => $"      +{ea.Effect} on {N(ea.UnitId)} ({ea.Duration:F1})",
            H5.Combat.MoraleProc mp => $"    {(mp.Good ? "good" : "bad")} morale: {N(mp.UnitId)}",
            H5.Combat.LuckProc lp => $"    {(lp.Good ? "lucky" : "unlucky")} strike: {N(lp.UnitId)}",
            H5.Combat.AbilityTriggered ab => $"    {N(ab.UnitId)} uses {ab.Ability}{(ab.Target is int t3 ? " on " + N(t3) : "")}",
            H5.Combat.UnitDied ud => $"      {N(ud.UnitId)} dies",
            H5.Combat.Waited w => $"    {N(w.Actor)} waits",
            H5.Combat.Defended df => $"    {N(df.Actor)} defends",
            H5.Combat.UnitSummoned su => $"    summoned {su.Count} {su.CreatureId} for side {su.Side}",
            H5.Combat.Healed h => $"      {N(h.Target)} healed {h.Amount} (+{h.Resurrected})",
            H5.Combat.SpellResisted r => $"      {N(r.Target)} resists {r.Spell}",
            H5.Combat.TurnSkipped ts => $"    {N(ts.Actor)} skips turn ({ts.Reason})",
            H5.Combat.BattleEnded be => $"== battle ended: winner {be.Winner?.ToString() ?? "none"} ({be.Reason})",
            _ => $"    {e.GetType().Name}",
        };
    }
}

sealed class DuelMatrixCommand : ICommand
{
    public string Name => "duel-matrix";
    public string Help => "duel-matrix [seed] [--stride N]  - AI vs AI for every preset pair (robustness + win rates)";

    public int Run(ToolContext ctx, string[] args)
    {
        var db = Db.Get(ctx);
        ulong seed = args.Length > 0 && !args[0].StartsWith("--") ? ulong.Parse(args[0]) : 1;
        int stride = 1;
        int si = Array.IndexOf(args, "--stride");
        if (si >= 0 && si + 1 < args.Length) stride = int.Parse(args[si + 1]);
        int n = db.DuelPresets.Count, games = 0, errors = 0;
        var wins = new int[n];
        var played = new int[n];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int a = 0; a < n; a++)
        for (int b = 0; b < n; b += stride)
        {
            if (a == b) continue;
            try
            {
                var s = H5.Combat.BattleSetup.CreateDuel(db, a, b, seed + (ulong)(a * n + b));
                var battle = new H5.Combat.Battle(db, s);
                var ai = new H5.Combat.GreedyBattleAI();
                H5.Combat.BattleRunner.RunToEnd(battle, ai, ai);
                games++;
                played[a]++; played[b]++;
                if (s.Winner == 0) wins[a]++; else if (s.Winner == 1) wins[b]++;
                if (s.EndReason != "army destroyed") Console.WriteLine($"  {a} vs {b}: ended by {s.EndReason}");
            }
            catch (Exception e)
            {
                errors++;
                Console.WriteLine($"  {a} vs {b}: {e.GetType().Name}: {e.Message}\n{e.StackTrace?.Split('\n').FirstOrDefault()}");
            }
        }
        Console.WriteLine($"{games} games, {errors} errors, {sw.Elapsed.TotalSeconds:F0}s");
        for (int i = 0; i < n; i++)
            Console.WriteLine($"  [{i,2}] {db.DuelPresets[i].Name,-26} {db.DuelPresets[i].Hero.Class,-24} win {100.0 * wins[i] / Math.Max(1, played[i]),5:F1}%");
        return errors == 0 ? 0 : 1;
    }
}

sealed class DbCheckCommand : ICommand
{
    public string Name => "db-check";
    public string Help => "db-check  - load the whole rules database and print counts/warnings";

    public int Run(ToolContext ctx, string[] args)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var db = Db.Get(ctx);
        Console.WriteLine($"loaded in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"creatures {db.Creatures.Count}, abilities {db.Abilities.Count}, spells {db.Spells.Count}, skills {db.Skills.Count}, " +
                          $"artifacts {db.Artifacts.Count}, classes {db.HeroClasses.Count}, war machines {db.WarMachines.Count}, heroes {db.Heroes.Count}, duel presets {db.DuelPresets.Count}");
        foreach (var p in db.DuelPresets)
            Console.WriteLine($"  [{p.Index,2}] {p.Name,-28} {p.Hero.Shared?.Id,-12} {p.Hero.Class,-22} L{p.Hero.Level}  " +
                              $"A{p.Hero.Attack} D{p.Hero.Defence} SP{p.Hero.SpellPower} K{p.Hero.Knowledge}  army {p.Hero.Army.Count}  spells {p.Hero.Spells.Count}");
        foreach (var w in db.Warnings.Take(30)) Console.WriteLine("warn: " + w);
        if (db.Warnings.Count > 30) Console.WriteLine($"... {db.Warnings.Count - 30} more warnings");
        return 0;
    }
}
