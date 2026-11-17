using H5.Core.IO;

namespace H5.Tests;

/// <summary>Key-binding file lookup as the exe (00834a30 copies, 008352a0 / 00834200 executes).</summary>
public sealed class ProfileInputConfigTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "h5-profinput-" + Guid.NewGuid().ToString("N"));
    string Install => Path.Combine(_tmp, "install");
    string Profile => Path.Combine(_tmp, "user", "profiles", "Player");

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    void StockInstall()
    {
        var d = Path.Combine(Install, "Profiles", "Default_Profile");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "input_a2.cfg"), "bind MB_Move_Hero 'SPACE'");
        File.WriteAllText(Path.Combine(d, "user_a2.cfg"), "setvar x 1");
    }

    [Fact]
    public void FreshProfileWithoutInputFileFallsBackToInstallDefault()
    {
        StockInstall();
        Directory.CreateDirectory(Profile);
        var p = ProfileInputConfig.Resolve(Install, Profile);
        Assert.NotNull(p);
        Assert.Equal("bind MB_Move_Hero 'SPACE'", File.ReadAllText(p!));
    }

    [Fact]
    public void NewProfileGetsTheStockCopiesAndUsesThemWithoutTheInstall()
    {
        StockInstall();
        Assert.Equal(new[] { "user_a2.cfg", "input_a2.cfg" }, ProfileInputConfig.CopyStockFiles(Install, Profile));
        // The install's files are gone (browser with .pak files only): the profile's copy still binds the keys.
        Directory.Delete(Install, true);
        Assert.Equal(Path.Combine(Profile, "input_a2.cfg"), ProfileInputConfig.Resolve(Install, Profile));
        Assert.Equal(Path.Combine(Profile, "input_a2.cfg"), ProfileInputConfig.Resolve(null, Profile));
    }

    [Fact]
    public void ExistingProfileCopyIsNotOverwritten()
    {
        StockInstall();
        Directory.CreateDirectory(Profile);
        File.WriteAllText(Path.Combine(Profile, "input_a2.cfg"), "mine");
        Assert.Equal(new[] { "user_a2.cfg" }, ProfileInputConfig.CopyStockFiles(Install, Profile));
        Assert.Equal("mine", File.ReadAllText(ProfileInputConfig.Resolve(Install, Profile)!));
    }

    [Fact]
    public void NoFileAnywhereMeansNoBindings()
    {
        Directory.CreateDirectory(Profile);
        Assert.Empty(ProfileInputConfig.CopyStockFiles(Install, Profile));
        Assert.Null(ProfileInputConfig.Resolve(Install, Profile));
        Assert.Null(ProfileInputConfig.Resolve(null, null));
    }

    [Fact]
    public void ProfileWithUserFileButNoInputFileExecutesNothing()
    {
        // 008352a0: the profile is used when its user_a2.cfg exists; its missing input_a2.cfg then binds nothing.
        StockInstall();
        Directory.CreateDirectory(Profile);
        File.WriteAllText(Path.Combine(Profile, "user_a2.cfg"), "");
        Assert.Null(ProfileInputConfig.Resolve(Install, Profile));
    }
}
