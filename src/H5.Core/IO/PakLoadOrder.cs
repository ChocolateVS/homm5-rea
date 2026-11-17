namespace H5.Core.IO;

/// <summary>
/// Which archives the original engine mounts, and in which order (H5_Game.exe 3.1, unchanged by MMH55):
/// FUN_00c8bd00 → FUN_009e0b40 → FUN_009e0560 registers <c>data\*.pak</c> (priority 0), then
/// <c>UserMODs\*.h5u</c> and <c>UserMODs\*.zip</c> (priority −1); FUN_009e01b0 enumerates each pattern with
/// FindFirstFile/FindNextFile (alphabetical on NTFS). Every pak in data/ is mounted, MMH55-*.pak included.
/// The order only breaks ties: a path present in several archives resolves to the newest zip entry
/// (FUN_009def50, implemented in <see cref="PakFileSystem"/>). FUN_00c8bd00 registers the user patterns, all at
/// priority −1, in the order <c>Maps\*.h5m</c>, <c>DuelPresets\*.h5p</c>, <c>UserCampaigns\*.h5c</c>,
/// <c>UserMODs\*.h5u</c>, <c>UserMODs\*.zip</c>. User campaigns are mounted with the mods (their
/// <c>UserCampaigns/*.xdb</c> campaigns are read by 009d2ad0); maps and duel presets are opened by their own loaders here.
/// </summary>
public static class PakLoadOrder
{
    /// <summary>True when an install folder carries Might &amp; Magic: Heroes 5.5 (data/MMH55-Data.pak, any case).</summary>
    public static bool IsMmh55Install(string? root) =>
        !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) && InstallPath.ResolveFile(root, "data", "MMH55-Data.pak") != null;

    /// <summary>H5_TARGET=stock asks for the unmodded 3.1 install among the default locations.</summary>
    public static bool StockOnlyRequested =>
        string.Equals(Environment.GetEnvironmentVariable("H5_TARGET"), "stock", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Enumerate(string dataDir, string userModsDir, string? userCampaignsDir = null)
    {
        foreach (var p in Files(dataDir, "*.pak")) yield return p;
        if (userCampaignsDir != null) foreach (var p in Files(userCampaignsDir, "*.h5c")) yield return p;
        foreach (var p in Files(userModsDir, "*.h5u")) yield return p;
        foreach (var p in Files(userModsDir, "*.zip")) yield return p;
    }

    static IEnumerable<string> Files(string dir, string pattern)
    {
        var ext = pattern[1..];
        // The extension check guards against 8.3 short-name matches (e.g. "*.pak" matching "x.pak2").
        return InstallPath.EnumerateFiles(dir, pattern)
            .Where(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
    }
}
