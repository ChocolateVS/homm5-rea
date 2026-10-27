using H5.Core.IO;
using Xunit;

namespace H5.Tests;

/// <summary>Startup target selection (stock 3.1 vs MMH 5.5): game_dir.txt keys, --target / H5_TARGET, prompt rules.</summary>
public class GameTargetTests
{
    static readonly string Stock = Path.GetFullPath("/games/stock");
    static readonly string Mmh = Path.GetFullPath("/games/mmh55");
    static readonly string Other = Path.GetFullPath("/games/other-stock");

    static GameTargets.Inputs In(params string[] installs)
    {
        var set = new HashSet<string>(installs.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return new GameTargets.Inputs
        {
            IsInstall = p => set.Contains(Path.GetFullPath(p)),
            IsMmh55 = p => Path.GetFullPath(p).Contains("mmh55", StringComparison.OrdinalIgnoreCase),
        };
    }

    [Fact]
    public void ConfigKeysAndLegacySinglePath()
    {
        var c = GameTargets.ParseConfig(new[] { "# both installs", "stock = \"/games/stock\"", "MMH55=/games/mmh55", "" });
        Assert.Null(c.Single);
        Assert.Equal("/games/stock", c.Stock);
        Assert.Equal("/games/mmh55", c.Mmh55);
        var alt = GameTargets.ParseConfig(new[] { "3.1=/a", "5.5=/b" });
        Assert.Equal(("/a", "/b"), (alt.Stock, alt.Mmh55));
        // Old files: one path, no key. A path containing '=' is not mistaken for a key.
        Assert.Equal("/games/toe", GameTargets.ParseConfig(new[] { "  \"/games/toe\"  " }).Single);
        Assert.Equal("/x=y/toe", GameTargets.ParseConfig(new[] { "/x=y/toe" }).Single);
        Assert.Null(GameInstall.ParseGameDirFile(new[] { "stock=/a" }));
    }

    [Fact]
    public void ParseTargetNamesAndArgs()
    {
        Assert.Equal(GameTarget.Stock, GameTargets.Parse("STOCK"));
        Assert.Equal(GameTarget.Stock, GameTargets.Parse("3.1"));
        Assert.Equal(GameTarget.Mmh55, GameTargets.Parse("mmh55"));
        Assert.Equal(GameTarget.Mmh55, GameTargets.Parse("5.5"));
        Assert.Null(GameTargets.Parse("hd"));
        Assert.Null(GameTargets.Parse(null));
        Assert.Equal(GameTarget.Stock, GameTargets.FromArgs(new[] { "--skip", "--target=mmh55", "--target=stock" }));
        Assert.Null(GameTargets.FromArgs(new[] { "--target=bogus", "--adventure=x" }));
    }

    [Fact]
    public void BothInstallsPromptWithRememberedDefault()
    {
        var i = In(Stock, Mmh);
        i.Defaults = new[] { Mmh, Stock };
        var s = GameTargets.Resolve(i);
        Assert.True(s.NeedsPrompt);
        Assert.Equal((Stock, Mmh), (s.StockRoot, s.Mmh55Root));
        Assert.Equal(GameTarget.Mmh55, s.DefaultChoice);
        i.Remembered = GameTarget.Stock;
        s = GameTargets.Resolve(i);
        Assert.True(s.NeedsPrompt);
        Assert.Equal(GameTarget.Stock, s.DefaultChoice);
        Assert.Equal(Stock, s.InitialRoot);
    }

    [Fact]
    public void SingleInstallNeverPrompts()
    {
        var i = In(Stock);
        i.Defaults = new[] { Mmh, Stock };
        i.Remembered = GameTarget.Mmh55;
        var s = GameTargets.Resolve(i);
        Assert.Equal(GameTarget.Stock, s.Decided);
        Assert.Null(s.Mmh55Root);
        var j = In(Mmh);
        j.Defaults = new[] { Mmh, Stock };
        j.Forced = GameTarget.Stock; // asked-for target missing: run what exists
        Assert.Equal(GameTarget.Mmh55, GameTargets.Resolve(j).Decided);
        Assert.False(GameTargets.Resolve(In()).Any);
    }

    [Fact]
    public void ForcedAndNonInteractiveSkipThePrompt()
    {
        var i = In(Stock, Mmh);
        i.Defaults = new[] { Mmh, Stock };
        i.Remembered = GameTarget.Stock;
        i.Forced = GameTarget.Mmh55;
        Assert.Equal(GameTarget.Mmh55, GameTargets.Resolve(i).Decided);
        i.Forced = GameTarget.Stock;
        Assert.Equal(Stock, GameTargets.Resolve(i).InitialRoot);
        // Dev/test runs keep the previous order (5.5 first), whatever was chosen last time.
        i.Forced = null;
        i.NonInteractive = true;
        Assert.Equal(GameTarget.Mmh55, GameTargets.Resolve(i).Decided);
    }

    [Fact]
    public void ExplicitRootDecides()
    {
        var i = In(Stock, Mmh);
        i.Defaults = new[] { Mmh, Stock };
        i.ExplicitRoot = Stock;
        i.Forced = GameTarget.Mmh55;
        var s = GameTargets.Resolve(i);
        Assert.Equal(GameTarget.Stock, s.Decided);
        Assert.Null(s.Mmh55Root);
    }

    [Fact]
    public void ConfigOrderBeatsDefaults()
    {
        var i = In(Stock, Mmh, Other);
        i.Defaults = new[] { Mmh, Stock };
        i.Configs = new[] { new GameDirConfig(null, Other, null) };
        var s = GameTargets.Resolve(i);
        Assert.Equal((Other, Mmh), (s.StockRoot, s.Mmh55Root));
        Assert.True(s.NeedsPrompt);
        // A legacy single path names one install; the other is still found, and without a prompt the legacy one runs.
        i.Configs = new[] { new GameDirConfig(Other, null, null) };
        i.NonInteractive = true;
        s = GameTargets.Resolve(i);
        Assert.Equal((Other, Mmh), (s.StockRoot, s.Mmh55Root));
        Assert.Equal(GameTarget.Stock, s.Decided);
        // Missing configured folders fall back to the defaults.
        i.Configs = new[] { new GameDirConfig(null, "/nowhere", "/nowhere/mmh55") };
        s = GameTargets.Resolve(i);
        Assert.Equal((Stock, Mmh), (s.StockRoot, s.Mmh55Root));
    }
}
