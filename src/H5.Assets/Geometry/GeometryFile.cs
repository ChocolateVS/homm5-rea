using System.Runtime.InteropServices;

namespace H5.Assets.Geometry;

/// <summary>Packed vertex as stored in bin/Geometries (20 bytes). See docs/formats/geometry.md.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedVertex
{
    /// <summary>Texture coordinates, signed 1/2048 fixed point.</summary>
    public short U0, V0, U1, V1;
    /// <summary>Unit vectors packed like D3DCOLOR (bytes B,G,R,A = z,y,x,unused), value = b/127.5 - 1.</summary>
    public uint Normal, Tangent, Binormal;
}

/// <summary>Per-position skin record (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedSkin
{
    public float W0, W1, W2, W3;
    /// <summary>Same weights quantised to bytes (sum 255), as uploaded to the GPU.</summary>
    public byte QW0, QW1, QW2, QW3;
    public byte B0, B1, B2, B3;
}

/// <summary>One material-homogeneous group of triangles inside a mesh.</summary>
public sealed class GeometryPart
{
    public float[] Positions = Array.Empty<float>();      // xyz per position
    public PackedVertex[] Vertices = Array.Empty<PackedVertex>();
    public PackedSkin[] Skin = Array.Empty<PackedSkin>(); // per position; empty for static meshes
    public ushort[] VertexPosition = Array.Empty<ushort>();  // vertex -> position index
    public ushort[] VertexSmoothGroup = Array.Empty<ushort>(); // vertex -> shared normal id (unused for rendering)
    public ushort[] Triangles = Array.Empty<ushort>();    // 3 vertex indices per triangle
    public int TriangleCount;
    /// <summary>Field 10: per-part float (texel density / LOD metric; not needed for rendering).</summary>
    public float Field10;
    /// <summary>Field 11: one byte, appears to be uninitialised padding in the exporter.</summary>
    public byte Field11;
    /// <summary>Field 12: -1 for skinned parts, otherwise the bone the rigid part is attached to.</summary>
    public int AttachBone;

    public int PositionCount => Positions.Length / 3;
    public bool IsSkinned => Skin.Length > 0;
}

public sealed class GeometryMesh
{
    public List<GeometryPart> Parts = new();
}

/// <summary>Raw decoded contents of a bin/Geometries/&lt;uid&gt; file.</summary>
public sealed class GeometryFile
{
    public int Version;
    public List<GeometryMesh> Meshes = new();
    /// <summary>Byte flag stored next to the mesh list (values 0x00, 0x01, 0x40 seen).</summary>
    public byte Flags;

    public static GeometryFile Decode(ReadOnlySpan<byte> d)
    {
        var top = NivalChunks.Read(d, 0, d.Length);
        var g = new GeometryFile();
        if (NivalChunks.Find(top, 4) is { } ver) g.Version = NivalChunks.ReadInt(d, ver);
        var body = NivalChunks.Find(top, 1) ?? throw new InvalidDataException("geometry: no body chunk");
        var bodyCh = NivalChunks.Read(d, body);
        if (NivalChunks.Find(bodyCh, 3) is { } fl && fl.Length > 0) g.Flags = d[fl.Offset];
        if (NivalChunks.Find(bodyCh, 2) is not { } meshes) return g;

        foreach (var meshChunk in NivalChunks.StructVectorElements(d, meshes))
        {
            var mesh = new GeometryMesh();
            foreach (var partChunk in NivalChunks.StructVectorElements(d, meshChunk))
                mesh.Parts.Add(DecodePart(d, partChunk));
            g.Meshes.Add(mesh);
        }
        return g;
    }

    static GeometryPart DecodePart(ReadOnlySpan<byte> d, Chunk partChunk)
    {
        var f = NivalChunks.Read(d, partChunk);
        var p = new GeometryPart();
        foreach (var c in f)
        {
            switch (c.Id)
            {
                case 2: p.Positions = NivalChunks.ReadPodVector<float>(d, c); break;
                case 3: p.Vertices = NivalChunks.ReadPodVector<PackedVertex>(d, c); break;
                case 4: p.Skin = NivalChunks.ReadPodVector<PackedSkin>(d, c); break;
                case 5: p.VertexPosition = NivalChunks.ReadPodVector<ushort>(d, c); break;
                case 6: p.VertexSmoothGroup = NivalChunks.ReadPodVector<ushort>(d, c); break;
                case 7: p.Triangles = NivalChunks.ReadPodVector<ushort>(d, c); break;
                case 9: p.TriangleCount = NivalChunks.ReadInt(d, c); break;
                case 10: p.Field10 = NivalChunks.ReadFloat(d, c); break;
                case 11: p.Field11 = c.Length > 0 ? d[c.Offset] : (byte)0; break;
                case 12: p.AttachBone = NivalChunks.ReadInt(d, c); break;
            }
        }
        Validate(p);
        return p;
    }

    static void Validate(GeometryPart p)
    {
        if (p.Positions.Length % 3 != 0) throw new InvalidDataException("positions not xyz");
        if (p.VertexPosition.Length != p.Vertices.Length) throw new InvalidDataException("vertex/position map length mismatch");
        if (p.Triangles.Length % 3 != 0) throw new InvalidDataException("triangle list not a multiple of 3");
        if (p.Skin.Length != 0 && p.Skin.Length != p.PositionCount) throw new InvalidDataException("skin count != position count");
        int pc = p.PositionCount, vc = p.Vertices.Length;
        foreach (var i in p.VertexPosition) if (i >= pc) throw new InvalidDataException("vertex position index out of range");
        foreach (var i in p.Triangles) if (i >= vc) throw new InvalidDataException("triangle index out of range");
    }
}
