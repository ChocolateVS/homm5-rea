using System.Text;

namespace H5.Assets.Granny;

/// <summary>Granny member types (granny_member_type).</summary>
public enum GrMemberType : uint
{
    End = 0,
    Inline = 1,
    Reference = 2,
    ReferenceToArray = 3,
    ArrayOfReferences = 4,
    VariantReference = 5,
    Unsupported = 6,
    ReferenceToVariantArray = 7,
    String = 8,
    Transform = 9,
    Real32 = 10,
    Int8 = 11,
    UInt8 = 12,
    BinormalInt8 = 13,
    NormalUInt8 = 14,
    Int16 = 15,
    UInt16 = 16,
    BinormalInt16 = 17,
    NormalUInt16 = 18,
    Int32 = 19,
    UInt32 = 20,
    Real16 = 21,
    EmptyReference = 22,
}

public sealed class GrMember
{
    public GrMemberType Type;
    public string Name = "";
    public GrType? Children;
    public int ArraySize;
    public override string ToString() => $"{Type} {Name}{(ArraySize > 0 ? $"[{ArraySize}]" : "")}";
}

/// <summary>A struct type described inside the file (an End-terminated member list).</summary>
public sealed class GrType
{
    public readonly List<GrMember> Members = new();
    public int Size;
    public override string ToString() => "{" + string.Join("; ", Members) + "}";
}

/// <summary>Granny transform: flags, translation, rotation quaternion (xyzw), 3x3 scale/shear.</summary>
public struct GrTransform
{
    public uint Flags;
    public float Tx, Ty, Tz;
    public float Rx, Ry, Rz, Rw;
    public float[] ScaleShear; // 9 floats, row-major
}

/// <summary>A decoded struct instance: member name → value.</summary>
public sealed class GrObject
{
    public readonly GrType Type;
    public readonly Dictionary<string, object?> Fields = new(StringComparer.Ordinal);
    public GrObject(GrType type) => Type = type;

    public object? this[string name] => Fields.TryGetValue(name, out var v) ? v : null;
    public bool Has(string name) => Fields.ContainsKey(name);

    public string? Str(string name) => this[name] as string;
    public GrObject? Obj(string name) => this[name] as GrObject;
    public IReadOnlyList<object?> List(string name) => this[name] as IReadOnlyList<object?> ?? Array.Empty<object?>();

    public float Float(string name) => this[name] switch
    {
        float f => f, int i => i, uint u => u, short s => s, ushort us => us, byte b => b, sbyte sb => sb,
        _ => 0f,
    };

    public int Int(string name) => this[name] switch
    {
        int i => i, uint u => (int)u, short s => s, ushort us => us, byte b => b, sbyte sb => sb, float f => (int)f,
        _ => 0,
    };

    /// <summary>A Real32/Int/... array member (fixed array or reference-to-array of scalars) as floats.</summary>
    public float[] Floats(string name)
    {
        var v = this[name];
        if (v is float[] fa) return fa;
        if (v is IReadOnlyList<object?> list)
        {
            // Reference-to-array of single-scalar structs (e.g. Real32 Real) or plain values.
            var r = new float[list.Count];
            for (int i = 0; i < list.Count; i++)
                r[i] = list[i] switch
                {
                    float f => f,
                    GrObject o when o.Type.Members.Count == 1 => o.Float(o.Type.Members[0].Name),
                    int n => n, uint u => u, short s => s, ushort us => us, byte b => b, sbyte sb => sb,
                    _ => 0f,
                };
            return r;
        }
        return Array.Empty<float>();
    }

    public override string ToString() => $"GrObject({string.Join(", ", Fields.Keys)})";
}

/// <summary>
/// Reader for Granny 2 .gr2 files (file format 6/7, 32- or 64-bit, little-endian).
/// Sections are decompressed (Oodle1), pointer fixups are recorded, and the root object is
/// decoded generically using the type tree stored in the file itself.
/// See docs/formats/granny.md.
/// </summary>
public sealed class Gr2File
{
    static readonly byte[] MagicLE32 = { 0xB8, 0x67, 0xB0, 0xCA, 0xF8, 0x6D, 0xB1, 0x0F, 0x84, 0x72, 0x8C, 0x7E, 0x5E, 0x19, 0x00, 0x1E };
    static readonly byte[] MagicLE32v7 = { 0x29, 0xDE, 0x6C, 0xC0, 0xBA, 0xA4, 0x53, 0x2B, 0x25, 0xF5, 0xB7, 0xA5, 0xF6, 0x66, 0xE2, 0xEE };
    static readonly byte[] MagicLE64v7 = { 0xE5, 0x9B, 0x49, 0x5E, 0x6F, 0x63, 0x1F, 0x14, 0x1E, 0x13, 0xEB, 0xA9, 0x90, 0xBE, 0xED, 0xC4 };

    public int FormatVersion { get; private set; }
    public uint Tag { get; private set; }
    public bool Is64 { get; private set; }
    public int PointerSize => Is64 ? 8 : 4;
    public GrObject Root { get; private set; } = null!;
    public GrType RootType { get; private set; } = null!;
    public IReadOnlyList<SectionInfo> Sections => _sections;

    public readonly record struct SectionInfo(uint Compression, uint DataOffset, uint CompressedSize, uint Size,
        uint Alignment, uint Stop0, uint Stop1, uint FixupOffset, uint FixupCount, uint MarshalOffset, uint MarshalCount);

    byte[] _data = Array.Empty<byte>();
    long[] _sectionBase = Array.Empty<long>();
    readonly List<SectionInfo> _sections = new();
    readonly Dictionary<long, long> _pointers = new();
    readonly Dictionary<long, GrType> _types = new();
    readonly Dictionary<(long, GrType), GrObject> _objects = new();

    public static bool IsGr2(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && (data[..16].SequenceEqual(MagicLE32) || data[..16].SequenceEqual(MagicLE32v7) || data[..16].SequenceEqual(MagicLE64v7));

    public static Gr2File Read(byte[] file)
    {
        var g = new Gr2File();
        g.Load(file);
        return g;
    }

    void Load(byte[] f)
    {
        var magic = f.AsSpan(0, 16);
        if (magic.SequenceEqual(MagicLE64v7)) Is64 = true;
        else if (!magic.SequenceEqual(MagicLE32) && !magic.SequenceEqual(MagicLE32v7))
            throw new InvalidDataException("Not a little-endian Granny 2 file");

        int headerFormat = BitConverter.ToInt32(f, 0x14);
        if (headerFormat != 0) throw new InvalidDataException($"Unsupported GR2 header format {headerFormat}");

        const int info = 0x20;
        FormatVersion = BitConverter.ToInt32(f, info);
        uint totalSize = BitConverter.ToUInt32(f, info + 4);
        uint infoSize = BitConverter.ToUInt32(f, info + 12);
        int sectionCount = BitConverter.ToInt32(f, info + 16);
        uint typeSec = BitConverter.ToUInt32(f, info + 20), typeOff = BitConverter.ToUInt32(f, info + 24);
        uint rootSec = BitConverter.ToUInt32(f, info + 28), rootOff = BitConverter.ToUInt32(f, info + 32);
        Tag = BitConverter.ToUInt32(f, info + 36);
        if (FormatVersion != 6 && FormatVersion != 7) throw new InvalidDataException($"Unsupported GR2 file format {FormatVersion}");
        if (totalSize > f.Length) throw new InvalidDataException("GR2 truncated");

        int sh = info + (int)infoSize;
        for (int i = 0; i < sectionCount; i++, sh += 44)
        {
            uint U(int k) => BitConverter.ToUInt32(f, sh + k * 4);
            _sections.Add(new SectionInfo(U(0), U(1), U(2), U(3), U(4), U(5), U(6), U(7), U(8), U(9), U(10)));
        }

        // Concatenate decompressed sections into one address space.
        _sectionBase = new long[sectionCount];
        long total = 0;
        for (int i = 0; i < sectionCount; i++) { _sectionBase[i] = total; total += _sections[i].Size; }
        _data = new byte[total];
        for (int i = 0; i < sectionCount; i++)
        {
            var s = _sections[i];
            if (s.Size == 0) continue;
            byte[] content = s.Compression switch
            {
                0 => f.AsSpan((int)s.DataOffset, (int)s.Size).ToArray(),
                1 or 2 => Oodle1.Decompress(f.AsSpan((int)s.DataOffset, (int)s.CompressedSize), (int)s.Size, (int)s.Stop0, (int)s.Stop1),
                _ => throw new NotSupportedException($"GR2 section compression {s.Compression}"),
            };
            Buffer.BlockCopy(content, 0, _data, (int)_sectionBase[i], content.Length);
        }

        // Pointer fixups: {srcOffset, dstSection, dstOffset} triples stored per section.
        for (int i = 0; i < sectionCount; i++)
        {
            var s = _sections[i];
            for (int k = 0; k < s.FixupCount; k++)
            {
                int p = (int)s.FixupOffset + k * 12;
                uint src = BitConverter.ToUInt32(f, p), dstSec = BitConverter.ToUInt32(f, p + 4), dst = BitConverter.ToUInt32(f, p + 8);
                if (dstSec >= sectionCount) throw new InvalidDataException("GR2 fixup to bad section");
                _pointers[_sectionBase[i] + src] = _sectionBase[dstSec] + dst;
            }
        }

        RootType = ReadType(_sectionBase[typeSec] + typeOff);
        Root = ReadObject(_sectionBase[rootSec] + rootOff, RootType);
    }

    /// <summary>Follows the pointer stored at absolute address <paramref name="at"/>; -1 if null.</summary>
    long Ptr(long at) => _pointers.TryGetValue(at, out var t) ? t : -1;

    int TypeNodeSize => Is64 ? 44 : 32;

    GrType ReadType(long at)
    {
        if (_types.TryGetValue(at, out var t)) return t;
        t = new GrType();
        _types[at] = t; // register first: types may be self-referential
        long p = at;
        int ps = PointerSize;
        while (true)
        {
            var mt = (GrMemberType)BitConverter.ToUInt32(_data, (int)p);
            if (mt == GrMemberType.End) break;
            if ((uint)mt > (uint)GrMemberType.EmptyReference) throw new InvalidDataException($"GR2 bad member type {(uint)mt}");
            var m = new GrMember { Type = mt };
            long nameAt = Ptr(p + 4);
            m.Name = nameAt >= 0 ? CString(nameAt) : "";
            long childAt = Ptr(p + 4 + ps);
            if (childAt >= 0) m.Children = ReadType(childAt);
            m.ArraySize = BitConverter.ToInt32(_data, (int)(p + 4 + 2 * ps));
            t.Members.Add(m);
            p += TypeNodeSize;
        }
        t.Size = t.Members.Sum(MemberSize);
        return t;
    }

    int MemberSize(GrMember m)
    {
        int ps = PointerSize;
        int one = m.Type switch
        {
            GrMemberType.Inline => m.Children?.Size ?? 0,
            GrMemberType.Reference or GrMemberType.String or GrMemberType.EmptyReference => ps,
            GrMemberType.ReferenceToArray or GrMemberType.ArrayOfReferences => 4 + ps,
            GrMemberType.VariantReference => 2 * ps,
            GrMemberType.ReferenceToVariantArray => 2 * ps + 4,
            GrMemberType.Transform => 68,
            GrMemberType.Real32 or GrMemberType.Int32 or GrMemberType.UInt32 => 4,
            GrMemberType.Int16 or GrMemberType.UInt16 or GrMemberType.BinormalInt16 or GrMemberType.NormalUInt16 or GrMemberType.Real16 => 2,
            GrMemberType.Int8 or GrMemberType.UInt8 or GrMemberType.BinormalInt8 or GrMemberType.NormalUInt8 => 1,
            _ => 0,
        };
        return one * Math.Max(1, m.ArraySize);
    }

    string CString(long at)
    {
        int end = (int)at;
        while (end < _data.Length && _data[end] != 0) end++;
        return Encoding.ASCII.GetString(_data, (int)at, end - (int)at);
    }

    GrObject ReadObject(long at, GrType type)
    {
        if (_objects.TryGetValue((at, type), out var o)) return o;
        o = new GrObject(type);
        _objects[(at, type)] = o;
        long p = at;
        foreach (var m in type.Members)
        {
            int count = Math.Max(1, m.ArraySize);
            int size1 = MemberSize(m) / count;
            if (m.ArraySize > 0 && IsScalar(m.Type))
            {
                var arr = new float[count];
                for (int i = 0; i < count; i++) arr[i] = Convert.ToSingle(ReadScalar(m.Type, p + i * size1));
                o.Fields[m.Name] = arr;
            }
            else if (m.ArraySize > 0)
            {
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++) list.Add(ReadValue(m, p + i * size1));
                o.Fields[m.Name] = list;
            }
            else
                o.Fields[m.Name] = ReadValue(m, p);
            p += MemberSize(m);
        }
        return o;
    }

    static bool IsScalar(GrMemberType t) => t is >= GrMemberType.Real32 and <= GrMemberType.Real16;

    object ReadScalar(GrMemberType t, long p)
    {
        int i = (int)p;
        return t switch
        {
            GrMemberType.Real32 => BitConverter.ToSingle(_data, i),
            GrMemberType.Int8 or GrMemberType.BinormalInt8 => (sbyte)_data[i],
            GrMemberType.UInt8 or GrMemberType.NormalUInt8 => _data[i],
            GrMemberType.Int16 or GrMemberType.BinormalInt16 => BitConverter.ToInt16(_data, i),
            GrMemberType.UInt16 or GrMemberType.NormalUInt16 => BitConverter.ToUInt16(_data, i),
            GrMemberType.Int32 => BitConverter.ToInt32(_data, i),
            GrMemberType.UInt32 => BitConverter.ToUInt32(_data, i),
            GrMemberType.Real16 => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(_data, i)),
            _ => 0,
        };
    }

    object? ReadValue(GrMember m, long p)
    {
        int ps = PointerSize;
        switch (m.Type)
        {
            case GrMemberType.Inline:
                return m.Children == null ? null : ReadObject(p, m.Children);
            case GrMemberType.Reference:
            {
                long t = Ptr(p);
                return t < 0 || m.Children == null ? null : ReadObject(t, m.Children);
            }
            case GrMemberType.ReferenceToArray:
            {
                int n = BitConverter.ToInt32(_data, (int)p);
                long t = Ptr(p + 4);
                if (n <= 0 || t < 0 || m.Children == null) return Array.Empty<object?>();
                return ReadArray(t, n, m.Children);
            }
            case GrMemberType.ArrayOfReferences:
            {
                int n = BitConverter.ToInt32(_data, (int)p);
                long t = Ptr(p + 4);
                var list = new List<object?>(Math.Max(n, 0));
                if (t >= 0 && m.Children != null)
                    for (int i = 0; i < n; i++)
                    {
                        long e = Ptr(t + i * ps);
                        list.Add(e < 0 ? null : ReadObject(e, m.Children));
                    }
                return list;
            }
            case GrMemberType.VariantReference:
            {
                long tp = Ptr(p), op = Ptr(p + ps);
                if (tp < 0 || op < 0) return null;
                return ReadObject(op, ReadType(tp));
            }
            case GrMemberType.ReferenceToVariantArray:
            {
                long tp = Ptr(p);
                int n = BitConverter.ToInt32(_data, (int)(p + ps));
                long t = Ptr(p + ps + 4);
                if (tp < 0 || t < 0 || n <= 0) return Array.Empty<object?>();
                return ReadArray(t, n, ReadType(tp));
            }
            case GrMemberType.String:
            {
                long t = Ptr(p);
                return t < 0 ? null : CString(t);
            }
            case GrMemberType.Transform:
            {
                int i = (int)p;
                float F(int k) => BitConverter.ToSingle(_data, i + 4 + k * 4);
                var ss = new float[9];
                for (int k = 0; k < 9; k++) ss[k] = F(7 + k);
                return new GrTransform
                {
                    Flags = BitConverter.ToUInt32(_data, i),
                    Tx = F(0), Ty = F(1), Tz = F(2),
                    Rx = F(3), Ry = F(4), Rz = F(5), Rw = F(6),
                    ScaleShear = ss,
                };
            }
            case GrMemberType.EmptyReference:
            case GrMemberType.Unsupported:
                return null;
            default:
                return ReadScalar(m.Type, p);
        }
    }

    IReadOnlyList<object?> ReadArray(long at, int n, GrType elem)
    {
        // Arrays of a single scalar member (e.g. {Real32 Real}) are flattened for speed.
        if (elem.Members.Count == 1 && elem.Members[0].ArraySize == 0 && IsScalar(elem.Members[0].Type))
        {
            var t = elem.Members[0].Type;
            var list = new object?[n];
            for (int i = 0; i < n; i++) list[i] = ReadScalar(t, at + (long)i * elem.Size);
            return list;
        }
        var r = new List<object?>(n);
        for (int i = 0; i < n; i++) r.Add(ReadObject(at + (long)i * elem.Size, elem));
        return r;
    }

    /// <summary>Raw bytes of the decompressed address space (for diagnostics).</summary>
    public ReadOnlySpan<byte> RawData => _data;
}
