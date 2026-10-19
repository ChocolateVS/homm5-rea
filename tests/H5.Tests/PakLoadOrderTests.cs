using H5.Core.IO;

namespace H5.Tests;

/// <summary>Archive mount order for a stock install and for one carrying MMH55 (data/MMH55-*.pak + UserMODs/*.h5u).</summary>
public sealed class PakLoadOrderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "h5-pakorder-" + Guid.NewGuid().ToString("N"));

    public PakLoadOrderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "UserMODs"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    void Touch(params string[] parts) => File.WriteAllText(Path.Combine(new[] { _root }.Concat(parts).ToArray()), "x");

    List<string> Order() => GameInstall.Locate(_root).EnumeratePaks().Select(Path.GetFileName).ToList()!;

    [Fact]
    public void StockInstallMountsStockPaksInOrder()
    {
        foreach (var p in new[] { "a2p1-texts.pak", "texts.pak", "data.pak", "a2p1-data.pak", "sound.pak", "soundsfx.pak" })
            Touch("data", p);
        // FindFirstFile order; overrides are decided per entry by timestamp (PakTimestampTests).
        Assert.Equal(new[] { "a2p1-data.pak", "a2p1-texts.pak", "data.pak", "sound.pak", "soundsfx.pak", "texts.pak" }, Order());
        Assert.False(GameInstall.Locate(_root).IsMmh55);
    }

    [Fact]
    public void Mmh55PaksAndUserModsFollowStock()
    {
        foreach (var p in new[] { "data.pak", "a2p1-data.pak", "MMH55-Texts-EN.pak", "MMH55-Data.pak", "MMH55-Index.pak" })
            Touch("data", p);
        Touch("data", "notapak.txt");
        Touch("UserMODs", "MMH55-SPMaps.h5u");
        Touch("UserMODs", "MMH55-Cam-Maps.h5u");
        Touch("UserMODs", "loose.zip");
        Assert.Equal(new[]
        {
            "a2p1-data.pak", "data.pak", "MMH55-Data.pak", "MMH55-Index.pak", "MMH55-Texts-EN.pak",
            "MMH55-Cam-Maps.h5u", "MMH55-SPMaps.h5u", "loose.zip",
        }, Order());
        Assert.True(GameInstall.Locate(_root).IsMmh55);
    }
}

/// <summary>Malformed xdb files shipped by MMH55 5.5 that the original engine loads.</summary>
public sealed class XdbRepairTests
{
    [Fact]
    public void ClosesXmlDeclaration()
    {
        var doc = H5.Core.Xdb.XdbRepair.Load(System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?\r\n<Shot ObjectRecordID=\"1\"><A>2</A></Shot>"));
        Assert.Equal("2", doc.Root!.Element("A")!.Value);
    }

    [Fact]
    public void DropsBrokenCommentTag()
    {
        var doc = H5.Core.Xdb.XdbRepair.Load(System.Text.Encoding.UTF8.GetBytes(
            "<W><Children><Item href=\"a.xdb\"/>\t\t<-- !Item href=\"b.(WindowSimple).xdb#xpointer(/WindowSimple)\"/>\t<Item href=\"c.xdb\"/></Children></W>"));
        Assert.Equal(2, doc.Root!.Element("Children")!.Elements("Item").Count());
    }
}

/// <summary>H5_Game.exe FUN_009def50: a path in several archives resolves to the newest entry; ties keep the first mounted.</summary>
public sealed class PakTimestampTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "h5-paktime-" + Guid.NewGuid().ToString("N"));

    public PakTimestampTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string Zip(string name, params (string Path, string Text, DateTime Time)[] files)
    {
        var p = Path.Combine(_dir, name);
        using var z = System.IO.Compression.ZipFile.Open(p, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var f in files)
        {
            var e = z.CreateEntry(f.Path);
            e.LastWriteTime = f.Time;
            using var w = new StreamWriter(e.Open());
            w.Write(f.Text);
        }
        return p;
    }

    [Fact]
    public void NewestEntryWinsRegardlessOfMountOrder()
    {
        var t0 = new DateTime(2008, 5, 1, 10, 0, 0);
        var t1 = new DateTime(2025, 10, 2, 12, 0, 0);
        var a = Zip("a.pak", ("x.txt", "old-a", t0), ("y.txt", "new-a", t1), ("tie.txt", "first", t0));
        var b = Zip("b.pak", ("X.TXT", "new-b", t1), ("y.txt", "old-b", t0), ("tie.txt", "second", t0));
        using var fs = new PakFileSystem();
        fs.Mount(a);
        fs.Mount(b);
        string Read(string p) => System.Text.Encoding.UTF8.GetString(fs.ReadAllBytes(p));
        Assert.Equal("new-b", Read("x.txt"));
        Assert.Equal("new-a", Read("y.txt"));
        Assert.Equal("first", Read("tie.txt"));
    }
}
