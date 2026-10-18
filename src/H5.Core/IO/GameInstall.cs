namespace H5.Core.IO;

/// <summary>
/// Locates an installed copy of Heroes of Might and Magic V: Tribes of the East.
/// H5Reborn never ships the original assets; it reads them from the user's install.
/// </summary>
public sealed class GameInstall
{
    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");

    // Load order of the stock archives; later entries override earlier ones.
    static readonly string[] StockPaks =
    {
        "data.pak", "texts.pak", "sound.pak", "soundsfx.pak",
        "a2p1-data.pak", "a2p1-texts.pak",
    };

    GameInstall(string root) => Root = root;

    public static GameInstall Locate(string? explicitRoot = null)
    {
        var candidates = new List<string?>
        {
            explicitRoot,
            Environment.GetEnvironmentVariable("H5_GAME_DIR"),
            @"G:\HoMM 5 - Tribes of the East",
            @"C:\GOG Games\Heroes of Might and Magic V - Tribes of the East",
            @"C:\Program Files (x86)\GOG Galaxy\Games\Heroes of Might and Magic V - Tribes of the East",
        };
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c, "data", "data.pak")))
                return new GameInstall(c);
        throw new DirectoryNotFoundException(
            "Could not find a Heroes V: Tribes of the East install. Set H5_GAME_DIR to the game folder.");
    }

    public IEnumerable<string> EnumeratePaks()
    {
        foreach (var name in StockPaks)
        {
            var p = Path.Combine(DataDir, name);
            if (File.Exists(p)) yield return p;
        }
        // User mods are applied last, in name order, like the original engine.
        var mods = Path.Combine(Root, "UserMODs");
        if (Directory.Exists(mods))
            foreach (var p in Directory.EnumerateFiles(mods, "*.h5u").Concat(Directory.EnumerateFiles(mods, "*.pak")).Order())
                yield return p;
    }
}
