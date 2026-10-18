using System.Buffers.Binary;
using System.Text;

namespace H5.Core.Ui.Binary;

/// <summary>
/// Reader for Nival's binary structure-saver format, used by every blob under
/// <c>bin/</c> (fonts, geometries, skeletons, animations) and by terrain files.
///
/// A stream is a flat sequence of chunks: <c>[id:u8][length][payload]</c>. The length is
/// variable-width: if the low bit of the first byte is 0 the length is <c>byte &gt;&gt; 1</c>
/// (one byte), otherwise it is <c>u32 &gt;&gt; 1</c> (four bytes, little-endian). Payloads are
/// either raw values or further chunk sequences; the format itself does not say which,
/// so callers navigate by id. Arrays are a chunk containing one id-1 chunk per element
/// (plus id-2 holding the count in some writers); hash maps store id-3 = count,
/// id-4 = bucket count, then alternating id-1 key / id-2 value chunks.
/// See docs/formats/terrain.md.
/// </summary>
public readonly struct ChunkSpan
{
    readonly byte[] _data;
    public readonly int Offset;
    public readonly int Length;

    public ChunkSpan(byte[] data) : this(data, 0, data.Length) { }
    public ChunkSpan(byte[] data, int offset, int length) { _data = data; Offset = offset; Length = length; }

    public ReadOnlySpan<byte> Bytes => _data.AsSpan(Offset, Length);
    public bool IsEmpty => Length == 0;

    /// <summary>Enumerates child chunks. Throws if the payload is not a valid chunk sequence.</summary>
    public List<Chunk> Children()
    {
        if (!TryChildren(out var list))
            throw new InvalidDataException($"Payload at 0x{Offset:X} ({Length} bytes) is not a chunk sequence");
        return list;
    }

    public bool TryChildren(out List<Chunk> list)
    {
        list = new List<Chunk>();
        int p = Offset, end = Offset + Length;
        while (p < end)
        {
            if (p + 2 > end) return false;
            byte id = _data[p];
            int len, hdr;
            if ((_data[p + 1] & 1) == 0) { len = _data[p + 1] >> 1; hdr = 2; }
            else
            {
                if (p + 5 > end) return false;
                len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(p + 1)) >> 1);
                hdr = 5;
            }
            if (len < 0 || p + hdr + len > end) return false;
            list.Add(new Chunk(id, new ChunkSpan(_data, p + hdr, len)));
            p += hdr + len;
        }
        return true;
    }

    public Chunk? Find(int id)
    {
        foreach (var c in Children()) if (c.Id == id) return c;
        return null;
    }

    public ChunkSpan Get(int id) => Find(id)?.Data ?? throw new InvalidDataException($"Missing chunk {id} at 0x{Offset:X}");

    public IEnumerable<ChunkSpan> All(int id) => Children().Where(c => c.Id == id).Select(c => c.Data);

    public int AsInt32() => Length switch
    {
        0 => 0,
        1 => _data[Offset],
        2 => BinaryPrimitives.ReadInt16LittleEndian(Bytes),
        _ => BinaryPrimitives.ReadInt32LittleEndian(Bytes),
    };

    public float AsSingle() => Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(Bytes) : 0f;
    public bool AsBool() => Length > 0 && _data[Offset] != 0;
    public string AsString() => Encoding.Latin1.GetString(Bytes);

    public int GetInt(int id, int fallback = 0) => Find(id)?.Data.AsInt32() ?? fallback;
    public float GetFloat(int id, float fallback = 0) => Find(id)?.Data.AsSingle() ?? fallback;
}

public readonly record struct Chunk(byte Id, ChunkSpan Data);

/// <summary>
/// Common envelope of a top-level bin/ object (fonts):
/// <c>[4]=format version, [1]{[1]=type id}, [0]=object table, [2]{[1]{[0]=type id, [1]=fields}}, [5]=empty</c>.
/// Terrain files instead use <c>[4]=version, [1]{[1]=fields}</c>.
/// </summary>
public static class NivalObject
{
    public static ChunkSpan Payload(byte[] file)
    {
        var root = new ChunkSpan(file);
        var two = root.Find(2);
        if (two is { } t && !t.Data.IsEmpty)
        {
            var obj = t.Data.Get(1);
            return obj.Find(1)?.Data ?? obj;
        }
        return root.Get(1).Get(1);
    }
}
