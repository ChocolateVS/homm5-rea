using System.Runtime.InteropServices.JavaScript;
using H5.Core.IO;

namespace H5WebSpike;

/// <summary>[JSImport] bindings to wwwroot/h5pak.js (the player's own picked files).</summary>
static partial class H5PakJs
{
    [JSImport("globalThis.h5pak.ready")] internal static partial bool Ready();
    [JSImport("globalThis.h5pak.count")] internal static partial int Count();
    [JSImport("globalThis.h5pak.name")] internal static partial string Name(int i);
    [JSImport("globalThis.h5pak.size")] internal static partial double Size(int i);
    [JSImport("globalThis.h5pak.hidePicker")] internal static partial void HidePicker();
    [JSImport("globalThis.h5pak.param")] internal static partial string? Param(string name);

    /// <summary>Starts Blob.slice(offset, offset+length).arrayBuffer(); resolves to a token for <see cref="Take"/>.</summary>
    [JSImport("globalThis.h5pak.read")]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<int> Read(int file, double offset, int length);

    /// <summary>Copies the bytes of a finished read into wasm memory (one memcpy, no managed array marshalling).</summary>
    [JSImport("globalThis.h5pak.take")]
    internal static partial void Take(int token, [JSMarshalAs<JSType.MemoryView>] Span<byte> dst);
}

/// <summary>
/// A picked browser file as an async-only <see cref="IRandomAccessSource"/>. Each read is one Blob slice;
/// large reads are chunked so no single JS ArrayBuffer gets huge.
/// </summary>
sealed class BrowserBlobSource(int index) : IRandomAccessSource
{
    const int Chunk = 16 << 20;
    public string Name { get; } = H5PakJs.Name(index);
    public long Length { get; } = (long)H5PakJs.Size(index);
    public bool SupportsSyncRead => false;
    public static long TotalBytesRead, TotalReads;

    public void ReadAt(long offset, Span<byte> buffer) =>
        throw new NotSupportedException($"{Name}: browser files are read asynchronously");

    public async ValueTask ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        for (int done = 0; done < buffer.Length;)
        {
            int n = Math.Min(Chunk, buffer.Length - done);
            int token = await H5PakJs.Read(index, offset + done, n);
            H5PakJs.Take(token, buffer.Span.Slice(done, n));
            done += n;
            TotalReads++;
            TotalBytesRead += n;
        }
    }

    public void Dispose() { }
}

static class BrowserPaks
{
    // Same layering as GameInstall.EnumeratePaks: stock paks in this order, then user mods by name.
    static readonly string[] StockPaks = { "data.pak", "texts.pak", "sound.pak", "soundsfx.pak", "a2p1-data.pak", "a2p1-texts.pak" };

    public static IEnumerable<int> InLoadOrder()
    {
        var all = Enumerable.Range(0, H5PakJs.Count()).Select(i => (i, name: H5PakJs.Name(i).Replace('\\', '/'))).ToList();
        foreach (var stock in StockPaks)
            foreach (var f in all.Where(f => !IsMod(f.name) && string.Equals(Path.GetFileName(f.name), stock, StringComparison.OrdinalIgnoreCase)))
                yield return f.i;
        foreach (var f in all.Where(f => IsMod(f.name)).OrderBy(f => Path.GetFileName(f.name), StringComparer.OrdinalIgnoreCase))
            yield return f.i;
    }

    static bool IsMod(string name) => name.StartsWith("usermods/", StringComparison.OrdinalIgnoreCase);
}
