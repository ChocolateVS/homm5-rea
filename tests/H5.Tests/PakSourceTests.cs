using System.IO.Compression;
using System.Text;
using H5.Core.IO;

namespace H5.Tests;

public sealed class PakSourceTests
{
    static byte[] MakeZip(int padEntries = 0)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text, CompressionLevel level)
            {
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
            Add("GameMechanics/a.xdb", "<a>" + new string('x', 5000) + "</a>", CompressionLevel.Optimal);
            Add("bin/Geometries/stored", "stored-bytes", CompressionLevel.NoCompression);
            Add("Dir/", "", CompressionLevel.NoCompression);
            var rnd = new Random(1);
            for (int i = 0; i < padEntries; i++)
            {
                var b = new byte[4096]; rnd.NextBytes(b);
                using var s = zip.CreateEntry($"pad/{i}.bin", CompressionLevel.NoCompression).Open();
                s.Write(b);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void FileAndMemorySourcesReadTheSame()
    {
        var zip = MakeZip();
        var path = Path.Combine(Path.GetTempPath(), "h5-paksrc-" + Guid.NewGuid().ToString("N") + ".pak");
        File.WriteAllBytes(path, zip);
        try
        {
            using var a = new PakArchive(path);
            using var b = new PakArchive(new MemoryRandomAccessSource(zip));
            Assert.Equal(a.Entries, b.Entries);
            Assert.Equal(path, a.FilePath);
            foreach (var e in a.Entries)
                Assert.Equal(a.Read(e), b.Read(e));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AsyncReadsMatchSyncReads()
    {
        var zip = MakeZip();
        using var sync = new PakArchive(new MemoryRandomAccessSource(zip));
        using var async = await PakArchive.OpenAsync(new MemoryRandomAccessSource(zip, supportsSyncRead: false));
        Assert.Equal(sync.Entries, async.Entries);
        foreach (var e in sync.Entries)
            Assert.Equal(sync.Read(e), await async.ReadAsync(e));
    }

    [Fact]
    public async Task ReadsAreSlicedNotWholeArchive()
    {
        var zip = MakeZip(padEntries: 200); // ~800 KB
        var src = new MemoryRandomAccessSource(zip, supportsSyncRead: false);
        using var fs = new PakFileSystem();
        await fs.MountAsync(src);
        var bytes = await fs.ReadAllBytesAsync("/bin/Geometries/stored");
        Assert.Equal("stored-bytes", Encoding.UTF8.GetString(bytes));
        Assert.True(src.BytesRead < zip.Length / 4, $"read {src.BytesRead} of {zip.Length}");
    }

    [Fact]
    public void AsyncOnlySourceRejectsSyncConstruction() =>
        Assert.Throws<ArgumentException>(() => new PakArchive(new MemoryRandomAccessSource(MakeZip(), supportsSyncRead: false)));

    [Fact]
    public async Task SyncReadOnAsyncSourceNeedsPrefetch()
    {
        using var fs = new PakFileSystem();
        await fs.MountAsync(new MemoryRandomAccessSource(MakeZip(), supportsSyncRead: false));
        Assert.True(fs.Exists("GameMechanics/A.xdb"));
        var ex = Assert.Throws<PakEntryNotReadyException>(() => fs.ReadAllBytes("GameMechanics/a.xdb"));
        Assert.Equal("GameMechanics/a.xdb", ex.Path);
        await fs.PrefetchAsync(new[] { @"GameMechanics\a.xdb" });
        Assert.StartsWith("<a>", Encoding.UTF8.GetString(fs.ReadAllBytes("GameMechanics/a.xdb")));
        Assert.True(fs.PrefetchedBytes > 5000);
        fs.ClearPrefetched("GameMechanics/");
        Assert.Throws<PakEntryNotReadyException>(() => fs.ReadAllBytes("GameMechanics/a.xdb"));
    }

    [Fact]
    public async Task RunWithPrefetchRetriesEvenWhenMissesAreSwallowed()
    {
        using var fs = new PakFileSystem();
        await fs.MountAsync(new MemoryRandomAccessSource(MakeZip(), supportsSyncRead: false));
        int runs = 0;
        var result = await fs.RunWithPrefetchAsync(() =>
        {
            runs++;
            string first;
            try { first = fs.TryReadAllBytes("GameMechanics/a.xdb", out var d) ? d.Length.ToString() : "?"; }
            catch (PakEntryNotReadyException) { first = "swallowed"; }
            string second;
            try { second = Encoding.UTF8.GetString(fs.ReadAllBytes("bin/Geometries/stored")); }
            catch (Exception) { second = "swallowed"; } // a decoder that hides errors
            return first + "|" + second;
        });
        Assert.Equal("5007|stored-bytes", result);
        Assert.Equal(2, runs); // both misses recorded in round 1
    }

    [Fact]
    public void SyncSourcesKeepDesktopBehaviour()
    {
        using var fs = new PakFileSystem();
        fs.Mount(new MemoryRandomAccessSource(MakeZip(), "mem.pak"));
        Assert.Equal("stored-bytes", Encoding.UTF8.GetString(fs.ReadAllBytes("bin/geometries/STORED")));
        Assert.Equal("mem.pak", fs.SourceOf("bin/Geometries/stored"));
        Assert.False(fs.Exists("Dir/"));
    }
}
