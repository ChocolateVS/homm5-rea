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
