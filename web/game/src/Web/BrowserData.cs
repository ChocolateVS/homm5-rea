using System.Runtime.InteropServices.JavaScript;
using H5.Core.IO;

namespace H5Reborn.Web;

/// <summary>[JSImport] bindings to wwwroot/h5data.js (the player's own install files, never hosted).</summary>
static partial class H5DataJs
{
    /// <summary>Resolves once the page has a complete set of files (picked now, or kept in OPFS from a past visit).</summary>
    [JSImport("globalThis.h5data.waitReady")]
    internal static partial Task WaitReady();
    [JSImport("globalThis.h5data.count")] internal static partial int Count();
    /// <summary>Path relative to the install root, '/' separated (e.g. "data/data.pak", "Maps/x.h5m").</summary>
    [JSImport("globalThis.h5data.name")] internal static partial string Name(int i);
    [JSImport("globalThis.h5data.size")] internal static partial double Size(int i);
    [JSImport("globalThis.h5data.status")] internal static partial void Status(string text);

    /// <summary>
    /// Synchronous slice read (a synchronous Range request on the file's blob: URL, the only synchronous file read
    /// a browser's main thread has). Writes straight into wasm memory; returns the bytes read.
    /// </summary>
    [JSImport("globalThis.h5data.readSync")]
    internal static partial int ReadSync(int file, double offset, int length, [JSMarshalAs<JSType.MemoryView>] Span<byte> dst);
}

/// <summary>
/// One picked browser file as a synchronous <see cref="IRandomAccessSource"/>. Small reads go through a cache of
/// aligned blocks: a zip's local header and its (usually small) entry data are adjacent, so one request serves both,
/// and neighbouring entries (same folder) often share a block.
/// </summary>
sealed class BrowserFileSource(int index, string name, long length) : IRandomAccessSource
{
    const int Block = 256 << 10, MaxCachedBlocks = 64, Chunk = 8 << 20;
    // Shared by every source opened for the same file (PakArchive and map loaders open their own).
    static readonly Dictionary<(int, long), byte[]> Cache = new();
    static readonly LinkedList<(int, long)> Lru = new();
    public static long Requests, BytesRequested;

    public string Name { get; } = name;
    public long Length { get; } = length;
    public bool SupportsSyncRead => true;

    public void ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > Length) throw new EndOfStreamException($"{Name}: read past end at {offset}");
        if (buffer.Length <= Block)
        {
            for (int done = 0; done < buffer.Length;)
            {
                long pos = offset + done, blockStart = pos / Block * Block;
                var block = GetBlock(blockStart);
                int from = (int)(pos - blockStart), n = Math.Min(buffer.Length - done, block.Length - from);
                block.AsSpan(from, n).CopyTo(buffer[done..]);
                done += n;
            }
            return;
        }
        for (int done = 0; done < buffer.Length;)
        {
            int n = Math.Min(Chunk, buffer.Length - done);
            Fetch(offset + done, buffer.Slice(done, n));
            done += n;
        }
    }

    byte[] GetBlock(long start)
    {
        var key = (index, start);
        if (Cache.TryGetValue(key, out var b)) { Lru.Remove(key); Lru.AddFirst(key); return b; }
        b = new byte[(int)Math.Min(Block, Length - start)];
        Fetch(start, b);
        Cache[key] = b;
        Lru.AddFirst(key);
        if (Lru.Count > MaxCachedBlocks) { Cache.Remove(Lru.Last!.Value); Lru.RemoveLast(); }
        return b;
    }

    void Fetch(long offset, Span<byte> dst)
    {
        int got = H5DataJs.ReadSync(index, offset, dst.Length, dst);
        Requests++;
        BytesRequested += got;
        if (got != dst.Length) throw new EndOfStreamException($"{Name}: short read at {offset}: {got}/{dst.Length}");
    }

    public ValueTask ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        ReadAt(offset, buffer.Span);
        return ValueTask.CompletedTask;
    }

    public void Dispose() { }
}

/// <summary>
/// Builds the virtual install the game runs on in the browser. The page hands over the player's files (install
/// folder, or data/*.pak alone); they appear under <see cref="Root"/> in the wasm file system:
/// <list type="bullet">
/// <item>archives (data/*.pak, UserMODs/*) and large files (music) are empty placeholders; reading them goes through
/// <see cref="RandomAccessSources.Opener"/> → <see cref="BrowserFileSource"/> (slices on demand, never whole);</item>
/// <item>small loose files (Maps, profiles, hwcursors, video descriptors…) are copied in, so ordinary
/// System.IO / Godot FileAccess code reads them unchanged.</item>
/// </list>
/// The game then finds the install through H5_GAME_DIR like on the desktop.
/// </summary>
public static class BrowserData
{
    public const string Root = "/h5install";
    const long CopyLimit = 16 << 20;

    public static async Task PrepareAsync()
    {
        H5DataJs.Status("waiting for game data");
        await H5DataJs.WaitReady();
        var started = DateTime.UtcNow;
        var virtualFiles = new Dictionary<string, BrowserFileSourceInfo>(StringComparer.OrdinalIgnoreCase);
        long copied = 0;
        int n = H5DataJs.Count();
        for (int i = 0; i < n; i++)
        {
            var rel = H5DataJs.Name(i).Replace('\\', '/').TrimStart('/');
            long size = (long)H5DataJs.Size(i);
            var path = Root + "/" + rel;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (IsStreamed(rel, size))
            {
                File.WriteAllBytes(path, Array.Empty<byte>());
                virtualFiles[path] = new BrowserFileSourceInfo(i, rel, size);
            }
            else
            {
                var bytes = new byte[size];
                if (size > 0 && H5DataJs.ReadSync(i, 0, (int)size, bytes) != size) throw new IOException($"short read: {rel}");
                File.WriteAllBytes(path, bytes);
                copied += size;
            }
        }
        RandomAccessSources.Opener = p =>
            virtualFiles.TryGetValue(Normalize(p), out var f) ? new BrowserFileSource(f.Index, f.Name, f.Length) : null;
        Environment.SetEnvironmentVariable("H5_GAME_DIR", Root);
        var msg = $"[H5] web: {n} files ({virtualFiles.Count} streamed, {copied / 1048576.0:F1} MB copied) in {(DateTime.UtcNow - started).TotalSeconds:F2}s";
        Console.WriteLine(msg);
        H5DataJs.Status("starting");
    }

    /// <summary>Archives and anything large stay in the browser and are read in slices.</summary>
    static bool IsStreamed(string rel, long size) =>
        size > CopyLimit
        || rel.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
        || rel.EndsWith(".h5u", StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith("UserMODs/", StringComparison.OrdinalIgnoreCase)
        || rel.StartsWith("music/", StringComparison.OrdinalIgnoreCase);

    static string Normalize(string p)
    {
        p = p.Replace('\\', '/');
        try { p = Path.GetFullPath(p); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        return p;
    }

    readonly record struct BrowserFileSourceInfo(int Index, string Name, long Length);
}
