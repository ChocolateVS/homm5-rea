using H5.Assets.Terrain;
using H5.Core.IO;
using H5.Core.Ui;
using H5.Core.Xdb;

namespace H5.Scene.Tests;

/// <summary>Shared install fixture; tests no-op when the original game is not present.</summary>
public sealed class InstallFixture : IDisposable
{
    public PakFileSystem? Fs { get; }
    public XdbLoader? Xdb { get; }

    public InstallFixture()
    {
        try { Fs = PakFileSystem.OpenInstall(GameInstall.Locate()); Xdb = new XdbLoader(Fs); }
        catch (DirectoryNotFoundException) { }
    }

    public void Dispose() => Fs?.Dispose();
}

public class InstallTests(InstallFixture install) : IClassFixture<InstallFixture>
{
    [Fact]
    public void MainMenuLayoutMatchesOriginal()
    {
        if (install.Xdb == null) return;
        var root = new UiLoader(install.Xdb).Load("UI/MainMenu2.(WindowScreen).xdb");
        UiLayout.Apply(root);
        var single = root.Find("SingleGameBtn")!;
        Assert.Equal(new UiRect(738, 491, 229, 34), single.Rect);
        Assert.Equal("single", single.ButtonStates[0].OnEnter.Single().Argument);
        Assert.Equal("Single Player", single.Text?.Raw);
        Assert.Equal("UI/Consts/Tags/menu.txt", single.ButtonStates[0].Visuals["Normal"].TextFormatPath);
    }

    [Fact]
    public void FontsDecode()
    {
        if (install.Xdb == null) return;
        var table = UiFontTable.Load(install.Xdb);
        Assert.Equal(14, table.Fonts.Count);
        var header = table.Find("Header", 28)!; // h2 asks for 28; nearest atlas
        Assert.True(header.Glyphs.Count > 200);
        var s = header.Glyph('S')!.Value;
        Assert.True(s.Advance > 0 && s.Width >= s.B);
    }

    [Fact]
    public void MarkupExpandsMacrosToFontAndColor()
    {
        if (install.Xdb == null) return;
        var runs = new UiMarkup(install.Xdb).Parse("<menu>Quit");
        var r = Assert.Single(runs);
        Assert.Equal("Header", r.Style.Face);
        Assert.Equal(28, r.Style.Size);
        Assert.Equal(0xFF294761u, r.Style.Color);
    }

    [Fact]
    public void AllTerrainsDecode()
    {
        if (install.Fs == null) return;
        var files = install.Fs.Paths.Where(p => p.EndsWith("Terrain.bin", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(files.Count > 250);
        foreach (var p in files)
        {
            var t = TerrainReader.Read(install.Fs.ReadAllBytes(p));
            Assert.Equal(t.TilesX + 1, t.Heights.Width);
            Assert.All(t.Layers, l => Assert.Equal(t.TilesX + 1, l.Weights.Width));
        }
    }

    [Fact]
    public void BeachArenaHasExpectedLayers()
    {
        if (install.Fs == null) return;
        var t = TerrainReader.Read(install.Fs.ReadAllBytes("Maps/CombatArenas/Beach_Grass_Small_01/GroundTerrain.bin"));
        Assert.Equal((72, 72), (t.TilesX, t.TilesY));
        Assert.Equal(5, t.Layers.Count);
        Assert.Equal("MapObjects/_(AdvMapTile)/Grass/Dark_Grass.xdb", t.Layers[0].TilePath, ignoreCase: true);
        Assert.All(t.Layers[0].Weights.Data, w => Assert.Equal(255, w));
    }
}
