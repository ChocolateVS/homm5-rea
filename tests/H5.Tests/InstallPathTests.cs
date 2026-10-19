using H5.Core.IO;

namespace H5.Tests;

public sealed class InstallPathTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "h5-installpath-" + Guid.NewGuid().ToString("N"));

    public InstallPathTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Data"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "Music", "A1"));
        Directory.CreateDirectory(Path.Combine(_root, "usermods"));
        File.WriteAllText(Path.Combine(_root, "Data", "Data.PAK"), "x");
        File.WriteAllText(Path.Combine(_root, "Music", "A1", "Campaign-Dungeon.OGG"), "x");
        File.WriteAllText(Path.Combine(_root, "usermods", "B.H5U"), "x");
        File.WriteAllText(Path.Combine(_root, "usermods", "a.pak"), "x");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void ResolvesSegmentsIgnoringCase()
    {
        Assert.Equal(Path.Combine(_root, "Data", "Data.PAK"), InstallPath.ResolveFile(_root, "data", "data.pak"), ignoreCase: true);
        Assert.NotNull(InstallPath.ResolveFile(_root, "data/DATA.pak"));
        Assert.NotNull(InstallPath.ResolveDirectory(_root, "MUSIC"));
        Assert.Null(InstallPath.ResolveFile(_root, "data", "missing.pak"));
        Assert.Null(InstallPath.ResolveFile(_root, "Music")); // a directory, not a file
    }

    [Fact]
    public void HandlesWindowsStyleRelativePaths()
    {
        // Music FileName as stored in the paks, relative to bin/.
        var p = InstallPath.ResolveFile(_root, "bin", @"..\music\a1\campaign-dungeon.ogg");
        Assert.NotNull(p);
        Assert.True(File.Exists(p));
        Assert.StartsWith(_root, p!);
    }

    [Fact]
    public void ResolveOrCombineFallsBackToPlainPath()
    {
        var p = InstallPath.ResolveOrCombine(_root, @"profiles\nobody");
        Assert.Equal(Path.Combine(_root, "profiles", "nobody"), p);
    }

    [Fact]
    public void WildcardsIgnoreCase()
    {
        var mods = InstallPath.ResolveDirectory(_root, "UserMODs")!;
        Assert.Single(InstallPath.EnumerateFiles(mods, "*.h5u"));
        Assert.Single(InstallPath.EnumerateFiles(mods, "*.PAK"));
    }

    [Fact]
    public void InstallDetectionAndPakEnumerationIgnoreCase()
    {
        Assert.True(GameInstall.IsInstall(_root));
        var install = GameInstall.Locate(_root);
        var paks = install.EnumeratePaks().Select(p => Path.GetFileName(p).ToLowerInvariant()).ToList();
        Assert.Equal(new[] { "data.pak", "a.pak", "b.h5u" }, paks);
    }

    [Fact]
    public void GameDirFileSkipsCommentsAndQuotes()
    {
        Assert.Equal("/games/toe", GameInstall.ParseGameDirFile(new[] { "# where the game is", "", "  \"/games/toe\"  " }));
        Assert.Null(GameInstall.ParseGameDirFile(new[] { "# nothing" }));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Games/ToE"), GameInstall.ParseGameDirFile(new[] { "~/Games/ToE" }));
    }
}
