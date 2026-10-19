namespace H5.Core.IO;

/// <summary>
/// Locates an installed copy of Heroes of Might and Magic V: Tribes of the East.
/// H5Reborn never ships the original assets; it reads them from the user's install.
/// </summary>
public sealed class GameInstall
{
    public string Root { get; }
    public string DataDir => InstallPath.ResolveDirectory(Root, "data") ?? Path.Combine(Root, "data");

    // Load order of the stock archives; later entries override earlier ones.
    static readonly string[] StockPaks =
    {
        "data.pak", "texts.pak", "sound.pak", "soundsfx.pak",
        "a2p1-data.pak", "a2p1-texts.pak",
    };

    /// <summary>Name of the optional text file (next to the executable) holding the game folder.</summary>
    public const string GameDirFile = "game_dir.txt";

    GameInstall(string root) => Root = root;

    /// <summary>
    /// Finds the install: explicit root, then H5_GAME_DIR, then game_dir.txt next to the
    /// executable (or in the working directory), then well-known install locations.
    /// </summary>
    public static GameInstall Locate(string? explicitRoot = null)
    {
        var candidates = new List<string?> { explicitRoot, Environment.GetEnvironmentVariable("H5_GAME_DIR") };
        candidates.AddRange(GameDirFileCandidates());
        foreach (var c in candidates.Concat(DefaultLocations()))
            if (!string.IsNullOrWhiteSpace(c) && IsInstall(c.Trim()))
                return new GameInstall(Path.GetFullPath(c.Trim()));
        throw new DirectoryNotFoundException(
            "Could not find a Heroes V: Tribes of the East install. Set H5_GAME_DIR to the game folder, " +
            $"or put its path in {GameDirFile} next to the H5Reborn executable.");
    }

    /// <summary>True when <paramref name="dir"/> holds data/data.pak (matched ignoring case).</summary>
    public static bool IsInstall(string dir) =>
        Directory.Exists(dir) && InstallPath.ResolveFile(dir, "data", "data.pak") != null;

    /// <summary>The first non-empty, non-comment line of each game_dir.txt found.</summary>
    static List<string> GameDirFileCandidates()
    {
        var dirs = new List<string>();
        try { if (Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } d) dirs.Add(d); } catch { }
        dirs.Add(AppContext.BaseDirectory);
        try { dirs.Add(Environment.CurrentDirectory); } catch { }
        var result = new List<string>();
        foreach (var dir in dirs.Distinct())
        {
            try
            {
                var f = Path.Combine(dir, GameDirFile);
                if (!File.Exists(f)) continue;
                var line = ParseGameDirFile(File.ReadAllLines(f));
                if (line != null) result.Add(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>game_dir.txt format: first line that is neither blank nor a '#' comment; quotes and a leading ~/ are handled.</summary>
    public static string? ParseGameDirFile(IEnumerable<string> lines)
    {
        var line = lines.Select(l => l.Trim().TrimStart('\uFEFF').Trim().Trim('"')).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (line == null) return null;
        if (line == "~" || line.StartsWith("~/"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            line = line.Length > 2 ? Path.Combine(home, line[2..]) : home;
        }
        return line;
    }

    const string GameFolderHint = "tribes of the east";

    /// <summary>
    /// Well-known places the game is installed: GOG/Ubisoft/Steam on Windows; on Linux the
    /// same layouts inside Wine, Proton (Steam compatdata), Heroic, Lutris and Bottles
    /// prefixes, plus ~/Games. Any folder one or two levels below those bases whose name
    /// mentions "Tribes of the East" (or a Heroes/HoMM folder) is a candidate.
    /// </summary>
    public static IEnumerable<string> DefaultLocations()
    {
        var bases = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            yield return @"G:\HoMM 5 - Tribes of the East";
            foreach (var drive in new[] { "C:", "D:", "E:", "F:", "G:" })
            {
                bases.Add(drive + @"\GOG Games");
                bases.Add(drive + @"\Games");
                bases.Add(drive + @"\Program Files (x86)\GOG Galaxy\Games");
                bases.Add(drive + @"\Program Files (x86)\Ubisoft");
                bases.Add(drive + @"\Program Files (x86)\Steam\steamapps\common");
                bases.Add(drive + @"\Program Files\Steam\steamapps\common");
                bases.Add(drive + @"\SteamLibrary\steamapps\common");
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (home.Length > 0) bases.AddRange(LinuxBases(home));
        }
        foreach (var b in bases.Distinct())
            foreach (var d in SubDirs(b))
            {
                var name = Path.GetFileName(d);
                if (name.Contains(GameFolderHint, StringComparison.OrdinalIgnoreCase)) { yield return d; continue; }
                if (!name.Contains("heroes", StringComparison.OrdinalIgnoreCase) && !name.Contains("homm", StringComparison.OrdinalIgnoreCase)) continue;
                yield return d;
                foreach (var dd in SubDirs(d))
                    if (Path.GetFileName(dd).Contains(GameFolderHint, StringComparison.OrdinalIgnoreCase)) yield return dd;
            }
    }

    static List<string> LinuxBases(string home)
    {
        var prefixes = new List<string> { Path.Combine(home, ".wine") };
        // Heroic / Lutris / Bottles keep one Wine prefix per game.
        prefixes.AddRange(SubDirs(Path.Combine(home, "Games", "Heroic", "Prefixes")));
        prefixes.AddRange(SubDirs(Path.Combine(home, "Games", "Heroic", "Prefixes", "default")));
        prefixes.AddRange(SubDirs(Path.Combine(home, "Games")));
        prefixes.AddRange(SubDirs(Path.Combine(home, ".local", "share", "bottles", "bottles")));
        prefixes.AddRange(SubDirs(Path.Combine(home, ".var", "app", "com.usebottles.bottles", "data", "bottles", "bottles")));
        var bases = new List<string>();
        foreach (var steam in new[]
                 {
                     Path.Combine(home, ".steam", "steam"),
                     Path.Combine(home, ".steam", "root"),
                     Path.Combine(home, ".local", "share", "Steam"),
                     Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                 })
        {
            if (!Directory.Exists(steam)) continue;
            bases.Add(Path.Combine(steam, "steamapps", "common"));
            foreach (var pfx in SubDirs(Path.Combine(steam, "steamapps", "compatdata")))
                prefixes.Add(Path.Combine(pfx, "pfx"));
        }
        foreach (var pfx in prefixes)
        {
            var c = Path.Combine(pfx, "drive_c");
            if (!Directory.Exists(c)) continue;
            bases.Add(Path.Combine(c, "GOG Games"));
            bases.Add(Path.Combine(c, "Games"));
            bases.Add(Path.Combine(c, "Program Files (x86)", "GOG Galaxy", "Games"));
            bases.Add(Path.Combine(c, "Program Files (x86)", "Ubisoft"));
            bases.Add(Path.Combine(c, "Program Files (x86)", "Steam", "steamapps", "common"));
        }
        bases.Add(Path.Combine(home, "Games"));
        bases.Add(Path.Combine(home, "GOG Games"));
        bases.Add(Path.Combine(home, "Games", "Heroic"));
        bases.Add(Path.Combine(home, "Games", "GOG"));
        return bases;
    }

    static string[] SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Resolves a path below the install root, ignoring case (see <see cref="InstallPath"/>).</summary>
    public string? Resolve(params string[] parts) => InstallPath.Resolve(Root, parts);

    /// <summary>
    /// Identifies the exact game data in use (stock archives + mods) so multiplayer
    /// peers can refuse to play with mismatched rules. Based on names and sizes,
    /// which is cheap and catches patches and mod differences.
    /// </summary>
    public string DataFingerprint()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in EnumeratePaks())
            sb.Append(Path.GetFileName(p).ToLowerInvariant()).Append(':').Append(new FileInfo(p).Length).Append(';');
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8);
    }

    public IEnumerable<string> EnumeratePaks()
    {
        var data = DataDir;
        foreach (var name in StockPaks)
            if (InstallPath.ResolveFile(data, name) is { } p) yield return p;
        // User mods are applied last, in name order, like the original engine.
        // (Extension match ignores case so "*.H5U" mods load on Linux too.)
        if (InstallPath.ResolveDirectory(Root, "UserMODs") is { } mods)
            foreach (var p in InstallPath.EnumerateFiles(mods, "*.h5u").Concat(InstallPath.EnumerateFiles(mods, "*.pak")).Order())
                yield return p;
    }
}
