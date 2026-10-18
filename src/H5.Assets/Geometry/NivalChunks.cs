namespace H5.Assets.Geometry;

/// <summary>
/// Nival's binary structure-saver container, used by bin/Geometries (and other bin/ blobs).
/// A stream is a sequence of chunks: [id:u8][size][payload]. The size is
/// variable-length: if the low bit of the first byte is 0 the size is that byte >> 1,
/// otherwise it is the little-endian u32 starting at that byte, >> 1.
/// Payloads of structured fields are themselves chunk sequences; vectors are
/// encoded as chunk 1 = u32 element count followed by one chunk per element
/// (id 2 for raw POD arrays, ids 1.. for structured elements).
/// </summary>
public readonly struct Chunk
{
    public readonly byte Id;
    public readonly int Offset;
    public readonly int Length;

    public Chunk(byte id, int offset, int length) { Id = id; Offset = offset; Length = length; }

    public int End => Offset + Length;
}

public static class NivalChunks
{
    public static List<Chunk> Read(ReadOnlySpan<byte> data, int start, int end)
    {
        var list = new List<Chunk>();
        int p = start;
        while (p < end)
        {
            if (p + 2 > end) throw new InvalidDataException($"truncated chunk header at {p}");
            byte id = data[p];
            int len, hdr;
            if ((data[p + 1] & 1) == 0) { len = data[p + 1] >> 1; hdr = 2; }
            else
            {
                if (p + 5 > end) throw new InvalidDataException($"truncated chunk size at {p}");
                len = (int)(BitConverter.ToUInt32(data.Slice(p + 1, 4)) >> 1);
                hdr = 5;
            }
            if (p + hdr + len > end) throw new InvalidDataException($"chunk {id} at {p} overruns parent ({len} bytes)");
            list.Add(new Chunk(id, p + hdr, len));
            p += hdr + len;
        }
        return list;
    }

    public static List<Chunk> Read(ReadOnlySpan<byte> data, Chunk parent) => Read(data, parent.Offset, parent.End);

    public static Chunk? Find(List<Chunk> chunks, byte id)
    {
        foreach (var c in chunks) if (c.Id == id) return c;
        return null;
    }

    /// <summary>Reads a vector of POD elements: [1]=count, [2]=raw bytes.</summary>
    public static T[] ReadPodVector<T>(ReadOnlySpan<byte> data, Chunk vector) where T : unmanaged
    {
        var ch = Read(data, vector);
        var count = Find(ch, 1) is { } c ? BitConverter.ToInt32(data.Slice(c.Offset, 4)) : 0;
        if (count == 0) return Array.Empty<T>();
        var raw = Find(ch, 2) ?? throw new InvalidDataException("vector without payload");
        int elem = System.Runtime.InteropServices.Marshal.SizeOf<T>();
        if (raw.Length % elem != 0)
            throw new InvalidDataException($"vector payload {raw.Length} not a multiple of {elem}");
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(data.Slice(raw.Offset, raw.Length)).ToArray();
    }

    /// <summary>Raw bytes of a POD vector plus its element count.</summary>
    public static (int Count, int Offset, int Length) PodVectorRaw(ReadOnlySpan<byte> data, Chunk vector)
    {
        var ch = Read(data, vector);
        var count = Find(ch, 1) is { } c ? BitConverter.ToInt32(data.Slice(c.Offset, 4)) : 0;
        var raw = Find(ch, 2);
        return raw is { } r ? (count, r.Offset, r.Length) : (count, 0, 0);
    }

    /// <summary>
    /// Element chunks of a structured vector: chunk 2 holds the u32 element count and
    /// every element is a chunk with id 1 (unlike POD vectors, where 1 = count, 2 = data).
    /// </summary>
    public static List<Chunk> StructVectorElements(ReadOnlySpan<byte> data, Chunk vector)
    {
        var ch = Read(data, vector);
        var elements = ch.Where(c => c.Id == 1).ToList();
        if (Find(ch, 2) is { Length: 4 } cnt && BitConverter.ToInt32(data.Slice(cnt.Offset, 4)) != elements.Count)
            throw new InvalidDataException($"struct vector declares {BitConverter.ToInt32(data.Slice(cnt.Offset, 4))} elements, has {elements.Count}");
        return elements;
    }

    public static int ReadInt(ReadOnlySpan<byte> data, Chunk c) => c.Length >= 4 ? BitConverter.ToInt32(data.Slice(c.Offset, 4)) : (c.Length == 1 ? data[c.Offset] : 0);
    public static float ReadFloat(ReadOnlySpan<byte> data, Chunk c) => c.Length >= 4 ? BitConverter.ToSingle(data.Slice(c.Offset, 4)) : 0f;
}
