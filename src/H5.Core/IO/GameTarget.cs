namespace H5.Core.IO;

/// <summary>Which game H5Reborn runs: stock Tribes of the East 3.1, or the Might &amp; Magic: Heroes 5.5 install.</summary>
public enum GameTarget { Stock, Mmh55 }

/// <summary>
/// The game folders named in game_dir.txt: <c>stock=&lt;path&gt;</c> and <c>mmh55=&lt;path&gt;</c> lines (keys ignore case;
/// <c>3.1=</c> / <c>5.5=</c> are accepted too), or the old single-path form (first non-comment line without a key),
/// whose target is decided by the folder's content.
/// </summary>
public sealed record GameDirConfig(string? Single, string? Stock, string? Mmh55)
{
    public static readonly GameDirConfig Empty = new(null, null, null);
}

/// <summary>
/// Picks the install H5Reborn runs at startup (H5Reborn launcher feature, not part of the original game: the user
/// asked to choose between 3.1 and 5.5 on each start). Pure logic so it can be tested; the game supplies the inputs.
/// </summary>
public static class GameTargets
{
    public const string TargetArg = "--target=";

    /// <summary>The startup inputs. Predicates default to the real folder checks.</summary>
    public sealed class Inputs
    {
        /// <summary>H5_GAME_DIR (or an explicit root): decides the install outright.</summary>
        public string? ExplicitRoot;
        /// <summary><c>--target=</c>, else H5_TARGET.</summary>
        public GameTarget? Forced;
        public IReadOnlyList<GameDirConfig> Configs = Array.Empty<GameDirConfig>();
        public IEnumerable<string> Defaults = Array.Empty<string>();
        /// <summary>The choice made at the previous start (user settings): the prompt's default selection.</summary>
        public GameTarget? Remembered;
        /// <summary>No prompt possible or wanted (dev/test command-line switches, headless runs, tools).</summary>
        public bool NonInteractive;
        public Func<string, bool> IsInstall = GameInstall.IsInstall;
        public Func<string, bool> IsMmh55 = PakLoadOrder.IsMmh55Install;
    }

    /// <summary>
    /// The installs found and whether the target is already decided. <see cref="Decided"/> null means: ask, with
    /// <see cref="DefaultChoice"/> preselected.
    /// </summary>
    public sealed record Selection(string? StockRoot, string? Mmh55Root, GameTarget? Decided, GameTarget DefaultChoice)
    {
        public bool NeedsPrompt => Decided == null;
        public bool Any => StockRoot != null || Mmh55Root != null;
        public string? RootFor(GameTarget t) => t == GameTarget.Stock ? StockRoot : Mmh55Root;
        /// <summary>The install to mount now: the decided one, else the preselected one.</summary>
        public GameTarget Initial => Decided ?? DefaultChoice;
        public string? InitialRoot => RootFor(Initial);
    }

    /// <summary>"stock" / "3.1" / "tote" → Stock; "mmh55" / "5.5" / "mmh" → Mmh55; anything else null.</summary>
    public static GameTarget? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "stock" or "3.1" or "tote" or "31" => GameTarget.Stock,
        "mmh55" or "5.5" or "mmh" or "55" or "mmh5.5" => GameTarget.Mmh55,
        _ => null,
    };

    public static string Name(GameTarget t) => t == GameTarget.Stock ? "stock" : "mmh55";

    /// <summary>The last <c>--target=</c> switch among the command-line user arguments.</summary>
    public static GameTarget? FromArgs(IEnumerable<string> args)
    {
        GameTarget? t = null;
        foreach (var a in args)
            if (a.StartsWith(TargetArg, StringComparison.OrdinalIgnoreCase) && Parse(a[TargetArg.Length..]) is { } p) t = p;
        return t;
    }

    /// <summary>Parses game_dir.txt (blank lines and '#' comments skipped; quotes and a leading ~/ handled).</summary>
    public static GameDirConfig ParseConfig(IEnumerable<string> lines)
    {
        string? single = null, stock = null, mmh = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq > 0 && Parse(line[..eq]) is { } key)
            {
                var path = Clean(line[(eq + 1)..]);
                if (path.Length == 0) continue;
                if (key == GameTarget.Stock) stock ??= path; else mmh ??= path;
                continue;
            }
            single ??= Clean(line);
        }
        return new GameDirConfig(single, stock, mmh);
    }

    static string Clean(string s)
    {
        s = s.Trim().Trim('"').Trim();
        if (s == "~" || s.StartsWith("~/"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            s = s.Length > 2 ? Path.Combine(home, s[2..]) : home;
        }
        return s;
    }

    public static Selection Resolve(Inputs inp)
    {
        bool Ok(string? p) => !string.IsNullOrWhiteSpace(p) && inp.IsInstall(p.Trim());
        string Full(string p) => Path.GetFullPath(p.Trim());

        if (Ok(inp.ExplicitRoot))
        {
            var root = Full(inp.ExplicitRoot!);
            var t = inp.IsMmh55(root) ? GameTarget.Mmh55 : GameTarget.Stock;
            return new Selection(t == GameTarget.Stock ? root : null, t == GameTarget.Mmh55 ? root : null, t, t);
        }

        string? stock = null, mmh = null;
        GameTarget? legacy = null; // the kind of an old single-path game_dir.txt: what H5Reborn used to run
        foreach (var c in inp.Configs)
        {
            if (stock == null && Ok(c.Stock)) stock = Full(c.Stock!);
            if (mmh == null && Ok(c.Mmh55)) mmh = Full(c.Mmh55!);
        }
        foreach (var c in inp.Configs)
        {
            if (!Ok(c.Single)) continue;
            var root = Full(c.Single!);
            bool isMmh = inp.IsMmh55(root);
            legacy ??= isMmh ? GameTarget.Mmh55 : GameTarget.Stock;
            if (isMmh) mmh ??= root; else stock ??= root;
        }
        if (stock == null || mmh == null)
            foreach (var d in inp.Defaults)
            {
                if (!Ok(d)) continue;
                var root = Full(d);
                if (inp.IsMmh55(root)) mmh ??= root; else stock ??= root;
                if (stock != null && mmh != null) break;
            }

        if (stock == null && mmh == null) return new Selection(null, null, null, GameTarget.Mmh55);
        if (stock == null) return new Selection(null, mmh, GameTarget.Mmh55, GameTarget.Mmh55);
        if (mmh == null) return new Selection(stock, null, GameTarget.Stock, GameTarget.Stock);
        if (inp.Forced is { } f) return new Selection(stock, mmh, f, f);
        // Without a prompt the previous behaviour holds: a single-path game_dir.txt, else the 5.5 install first.
        if (inp.NonInteractive) { var t = legacy ?? GameTarget.Mmh55; return new Selection(stock, mmh, t, t); }
        return new Selection(stock, mmh, null, inp.Remembered ?? GameTarget.Mmh55);
    }
}
