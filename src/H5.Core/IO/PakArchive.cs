using System.IO.Compression;
using System.Text;

namespace H5.Core.IO;

/// <summary>
/// Minimal zip reader for Nival .pak files. data.pak has more than 65535
/// entries without Zip64 records, so the 16-bit entry count in the end-of-central-
/// directory record overflows and System.IO.Compression rejects the file. We walk
/// the central directory by its byte size instead of trusting the count.
/// </summary>
public sealed class PakArchive : IDisposable
{
    public readonly record struct Entry(string Name, long LocalHeaderOffset, int Method, long CompressedSize, long Size);

    readonly FileStream _stream;
    readonly object _lock = new();
    public string FilePath { get; }
    public IReadOnlyList<Entry> Entries { get; }

    public PakArchive(string path)
    {
        FilePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        Entries = ReadCentralDirectory();
    }

    List<Entry> ReadCentralDirectory()
    {
        // EOCD is within the last 64KiB + 22 bytes.
        long tailLen = Math.Min(_stream.Length, 65557);
        var tail = new byte[tailLen];
        _stream.Position = _stream.Length - tailLen;
        _stream.ReadExactly(tail);
        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(tail, i) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException($"{FilePath}: no end of central directory record");

        uint cdSize = BitConverter.ToUInt32(tail, eocd + 12);
        uint cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
        var cd = new byte[cdSize];
        _stream.Position = cdOffset;
        _stream.ReadExactly(cd);

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
            var enc = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.Latin1;
            var name = enc.GetString(cd, p + 46, nameLen);
            list.Add(new Entry(name, local, method, csize, usize));
            p += 46 + nameLen + extraLen + commentLen;
        }
        return list;
    }

    public byte[] Read(in Entry e)
    {
        byte[] comp;
        lock (_lock)
        {
            var hdr = new byte[30];
            _stream.Position = e.LocalHeaderOffset;
            _stream.ReadExactly(hdr);
            if (BitConverter.ToUInt32(hdr, 0) != 0x04034b50)
                throw new InvalidDataException($"{FilePath}: bad local header for {e.Name}");
            int skip = BitConverter.ToUInt16(hdr, 26) + BitConverter.ToUInt16(hdr, 28);
            _stream.Position = e.LocalHeaderOffset + 30 + skip;
            comp = new byte[e.CompressedSize];
            _stream.ReadExactly(comp);
        }
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

    public void Dispose() => _stream.Dispose();
}
