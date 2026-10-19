using System.IO.Compression;
using System.Text;

namespace H5.Core.IO;

/// <summary>
/// Minimal zip reader for Nival .pak files. data.pak has more than 65535
/// entries without Zip64 records, so the 16-bit entry count in the end-of-central-
/// directory record overflows and System.IO.Compression rejects the file. We walk
/// the central directory by its byte size instead of trusting the count.
/// Reads go through an <see cref="IRandomAccessSource"/> (file on desktop, browser Blob slices on
/// the web); only the tail, the central directory and the requested entries are ever read.
/// </summary>
public sealed class PakArchive : IDisposable
{
    /// <param name="DosTime">Central-directory modification stamp as (date &lt;&lt; 16) | time; decides path clashes (see PakFileSystem).</param>
    public readonly record struct Entry(string Name, long LocalHeaderOffset, int Method, long CompressedSize, long Size, uint DosTime = 0);

    readonly IRandomAccessSource _source;
    public string FilePath => _source.Name;
    public IRandomAccessSource Source => _source;
    public IReadOnlyList<Entry> Entries { get; }

    public PakArchive(string path) : this(new FileRandomAccessSource(path)) { }

    /// <summary>Opens over a source that supports synchronous reads. Takes ownership of <paramref name="source"/>.</summary>
    public PakArchive(IRandomAccessSource source)
    {
        _source = source;
        if (!source.SupportsSyncRead)
            throw new ArgumentException($"{source.Name}: async-only source, use PakArchive.OpenAsync", nameof(source));
        var tail = new byte[TailLength(source)];
        source.ReadAt(source.Length - tail.Length, tail);
        var (cdOffset, cdSize) = FindCentralDirectory(tail, source.Name);
        var cd = new byte[cdSize];
        source.ReadAt(cdOffset, cd);
        Entries = ParseCentralDirectory(cd);
    }

    PakArchive(IRandomAccessSource source, List<Entry> entries)
    {
        _source = source;
        Entries = entries;
    }

    /// <summary>Opens over any source, reading the directory asynchronously. Takes ownership of <paramref name="source"/>.</summary>
    public static async Task<PakArchive> OpenAsync(IRandomAccessSource source, CancellationToken ct = default)
    {
        var tail = new byte[TailLength(source)];
        await source.ReadAtAsync(source.Length - tail.Length, tail, ct).ConfigureAwait(false);
        var (cdOffset, cdSize) = FindCentralDirectory(tail, source.Name);
        var cd = new byte[cdSize];
        await source.ReadAtAsync(cdOffset, cd, ct).ConfigureAwait(false);
        return new PakArchive(source, ParseCentralDirectory(cd));
    }

    // EOCD is within the last 64KiB + 22 bytes.
    static int TailLength(IRandomAccessSource s) => (int)Math.Min(s.Length, 65557);

    static (long Offset, uint Size) FindCentralDirectory(byte[] tail, string name)
    {
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(tail, i) == 0x06054b50)
                return (BitConverter.ToUInt32(tail, i + 16), BitConverter.ToUInt32(tail, i + 12));
        throw new InvalidDataException($"{name}: no end of central directory record");
    }

    static List<Entry> ParseCentralDirectory(byte[] cd)
    {
        var list = new List<Entry>();
        int p = 0;
        while (p + 46 <= cd.Length && BitConverter.ToUInt32(cd, p) == 0x02014b50)
        {
            ushort flags = BitConverter.ToUInt16(cd, p + 8);
            ushort method = BitConverter.ToUInt16(cd, p + 10);
            uint csize = BitConverter.ToUInt32(cd, p + 20);
            uint usize = BitConverter.ToUInt32(cd, p + 24);
            ushort nameLen = BitConverter.ToUInt16(cd, p + 28);
            ushort extraLen = BitConverter.ToUInt16(cd, p + 30);
            ushort commentLen = BitConverter.ToUInt16(cd, p + 32);
            uint local = BitConverter.ToUInt32(cd, p + 42);
            uint dosTime = (uint)BitConverter.ToUInt16(cd, p + 14) << 16 | BitConverter.ToUInt16(cd, p + 12);
            var enc = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.Latin1;
            var name = enc.GetString(cd, p + 46, nameLen);
            list.Add(new Entry(name, local, method, csize, usize, dosTime));
            p += 46 + nameLen + extraLen + commentLen;
        }
        return list;
    }

    public byte[] Read(in Entry e)
    {
        var hdr = new byte[30];
        _source.ReadAt(e.LocalHeaderOffset, hdr);
        var comp = new byte[e.CompressedSize];
        _source.ReadAt(DataOffset(e, hdr), comp);
        return Decompress(e, comp);
    }

    public async Task<byte[]> ReadAsync(Entry e, CancellationToken ct = default)
    {
        // The local header's name/extra lengths are read together with a guess of the data
        // (central-directory name length + slack) to make one slice read in the common case.
        int guess = 30 + Encoding.UTF8.GetByteCount(e.Name) + 64;
        long want = Math.Min(guess + e.CompressedSize, _source.Length - e.LocalHeaderOffset);
        var buf = new byte[want];
        await _source.ReadAtAsync(e.LocalHeaderOffset, buf, ct).ConfigureAwait(false);
        long data = DataOffset(e, buf);
        long rel = data - e.LocalHeaderOffset;
        byte[] comp;
        if (rel + e.CompressedSize <= buf.Length)
            comp = buf.AsSpan((int)rel, (int)e.CompressedSize).ToArray();
        else
        {
            comp = new byte[e.CompressedSize];
            await _source.ReadAtAsync(data, comp, ct).ConfigureAwait(false);
        }
        return Decompress(e, comp);
    }

    long DataOffset(in Entry e, byte[] hdr)
    {
        if (BitConverter.ToUInt32(hdr, 0) != 0x04034b50)
            throw new InvalidDataException($"{FilePath}: bad local header for {e.Name}");
        int skip = BitConverter.ToUInt16(hdr, 26) + BitConverter.ToUInt16(hdr, 28);
        return e.LocalHeaderOffset + 30 + skip;
    }

    byte[] Decompress(in Entry e, byte[] comp)
    {
        switch (e.Method)
        {
            case 0: return comp;
            case 8:
                var output = new byte[e.Size];
                using (var ds = new DeflateStream(new MemoryStream(comp), CompressionMode.Decompress))
                    ds.ReadExactly(output);
                return output;
            default:
                throw new NotSupportedException($"{FilePath}: compression method {e.Method} for {e.Name}");
        }
    }

    public void Dispose() => _source.Dispose();
}
