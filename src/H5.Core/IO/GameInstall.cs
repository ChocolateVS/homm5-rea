namespace H5.Core.IO;

/// <summary>
/// Locates an installed copy of Heroes of Might and Magic V: Tribes of the East.
/// H5Reborn never ships the original assets; it reads them from the user's install.
/// </summary>
public sealed class GameInstall
{
    public string Root { get; }
    public string DataDir => InstallPath.ResolveDirectory(Root, "data") ?? Path.Combine(Root, "data");

    /// <summary>Name of the optional text file (next to the executable) holding the game folder.</summary>
    public const string GameDirFile = "game_dir.txt";

    GameInstall(string root) => Root = root;

    /// <summary>
    /// Finds the install without asking (tools, server, tests): explicit root, then H5_GAME_DIR, then game_dir.txt next
    /// to the executable (or in the working directory), then well-known install locations. With both a stock 3.1 and an
    /// MMH55 install available, H5_TARGET (stock / mmh55) decides, else an old single-path game_dir.txt, else MMH55
    /// (see <see cref="GameTargets.Resolve"/>; the game asks the player instead).
    /// </summary>
    public static GameInstall Locate(string? explicitRoot = null)
    {
        var sel = GameTargets.Resolve(new GameTargets.Inputs
        {
            ExplicitRoot = !string.IsNullOrWhiteSpace(explicitRoot) ? explicitRoot : Environment.GetEnvironmentVariable("H5_GAME_DIR"),
            Forced = GameTargets.Parse(Environment.GetEnvironmentVariable("H5_TARGET")),
            Configs = ReadGameDirFiles(),
            Defaults = DefaultLocations(),
            NonInteractive = true,
        });
        return At(sel.InitialRoot ?? throw NotFound());
    }

    /// <summary>An install at a folder already checked with <see cref="IsInstall"/>.</summary>
    public static GameInstall At(string root) => new(Path.GetFullPath(root));

    public static DirectoryNotFoundException NotFound() => new(
        "Could not find a Heroes V: Tribes of the East install. Set H5_GAME_DIR to the game folder, " +
        $"or put its path in {GameDirFile} next to the H5Reborn executable (or stock=<path> and mmh55=<path> lines).");

    /// <summary>True when <paramref name="dir"/> holds data/data.pak (matched ignoring case).</summary>
    public static bool IsInstall(string dir) =>
        Directory.Exists(dir) && InstallPath.ResolveFile(dir, "data", "data.pak") != null;

    /// <summary>Each game_dir.txt found next to the executable, in the app folder and in the working directory.</summary>
    public static List<GameDirConfig> ReadGameDirFiles()
    {
        var dirs = new List<string>();
        try { if (Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } d) dirs.Add(d); } catch { }
        dirs.Add(AppContext.BaseDirectory);
        try { dirs.Add(Environment.CurrentDirectory); } catch { }
        var result = new List<GameDirConfig>();
        foreach (var dir in dirs.Distinct())
        {
            try
            {
                var f = Path.Combine(dir, GameDirFile);
                if (File.Exists(f)) result.Add(GameTargets.ParseConfig(File.ReadAllLines(f)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>The old single-path game_dir.txt form: first line that is neither blank, a '#' comment nor a <c>stock=</c> / <c>mmh55=</c> key.</summary>
    public static string? ParseGameDirFile(IEnumerable<string> lines) => GameTargets.ParseConfig(lines).Single;

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
            // The target is Tribes of the East 3.1 + Might & Magic: Heroes 5.5 when present (docs/mmh55/PLAN.md).
            yield return @"G:\HoMM 5 - Tribes of the East (5.5)";
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

    /// <summary>True when the install carries the Might &amp; Magic: Heroes 5.5 mod (data/MMH55-Data.pak).</summary>
    public bool IsMmh55 => PakLoadOrder.IsMmh55Install(Root);

    /// <summary>Archives in the original engine's mount order (see <see cref="PakLoadOrder"/>).</summary>
    public IEnumerable<string> EnumeratePaks() =>
        PakLoadOrder.Enumerate(DataDir, InstallPath.ResolveDirectory(Root, "UserMODs") ?? Path.Combine(Root, "UserMODs"));
}
