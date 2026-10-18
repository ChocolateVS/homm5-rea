using System.Numerics;
using H5.Core.Ui.Binary;

namespace H5.Assets.Terrain;

/// <summary>A row-major grid of samples (index = y * Width + x).</summary>
public sealed class Grid<T>
{
    public readonly int Width, Height;
    public readonly T[] Data;
    public Grid(int w, int h, T[] data) { Width = w; Height = h; Data = data; }
    public T this[int x, int y] => Data[y * Width + x];
}

/// <summary>One texture layer of the terrain: an AdvMapTile and its per-vertex blend weights.</summary>
public sealed class TerrainLayer
{
    /// <summary>Pak path of the (AdvMapTile) xdb, e.g. "MapObjects/_(AdvMapTile)/Grass/Grass.xdb".</summary>
    public string TilePath = "";
    /// <summary>Blend weight 0..255 per terrain vertex ((TilesX+1)×(TilesY+1)).</summary>
    public Grid<byte> Weights = null!;
}

/// <summary>
/// Decoded GroundTerrain.bin / UndergroundTerrain.bin (adventure maps and combat arenas).
/// Grid sizes are in map tiles; vertex grids have one extra row and column.
/// One map tile = one world unit; the combat arena's 12×10 combat grid is laid on
/// top of this terrain (see docs/formats/arena.md).
/// </summary>
public sealed class TerrainData
{
    public int TilesX, TilesY;
    /// <summary>Layers in draw order: layer 0 is the base (usually all 255), later layers blend over it.</summary>
    public List<TerrainLayer> Layers = new();
    /// <summary>Vertex heights in world units (chunk 5).</summary>
    public Grid<float> Heights = null!;
    /// <summary>Per-vertex flags (chunk 7). Observed values 0/16 on arenas; see docs/formats/terrain.md.</summary>
    public Grid<byte>? VertexFlags;
    /// <summary>Per-vertex byte map (chunk 8), usually zero.</summary>
    public Grid<byte>? VertexMap8;
    /// <summary>Half-tile resolution byte map (chunk 10), (2·TilesX+1)².</summary>
    public Grid<byte>? HalfTileMap;
    /// <summary>Optional second float vertex grid (chunk 13), present on some maps.</summary>
    public Grid<float>? Heights2;
    /// <summary>Per-vertex byte map (chunk 15), usually 1.</summary>
    public Grid<byte>? VertexMap15;
    /// <summary>Raw byte from chunk 14.</summary>
    public byte Flags14;
    public int Version;

    public float HeightAt(int vx, int vy) =>
        Heights[Math.Clamp(vx, 0, Heights.Width - 1), Math.Clamp(vy, 0, Heights.Height - 1)];

    /// <summary>Bilinear height at a position in tile units.</summary>
    public float SampleHeight(float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float h00 = HeightAt(x0, y0), h10 = HeightAt(x0 + 1, y0), h01 = HeightAt(x0, y0 + 1), h11 = HeightAt(x0 + 1, y0 + 1);
        return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
    }

    /// <summary>Per-vertex normals from central differences (Z-up, one world unit per tile).</summary>
    public Vector3[] ComputeNormals()
    {
        int w = Heights.Width, h = Heights.Height;
        var n = new Vector3[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float dx = HeightAt(x + 1, y) - HeightAt(x - 1, y);
            float dy = HeightAt(x, y + 1) - HeightAt(x, y - 1);
            n[y * w + x] = Vector3.Normalize(new Vector3(-dx, -dy, 2f));
        }
        return n;
    }
}

public static class TerrainReader
{
    public static TerrainData Read(byte[] file)
    {
        var top = new ChunkSpan(file);
        var t = new TerrainData { Version = top.GetInt(4) };
        var root = top.Get(1).Get(1);
        t.TilesX = root.GetInt(2);
        t.TilesY = root.GetInt(3);

        foreach (var c in root.Children())
        {
            switch (c.Id)
            {
                case 4: ReadLayers(c.Data, t); break;
                case 5: t.Heights = FloatGrid(c.Data); break;
                case 7: t.VertexFlags = ByteGrid(c.Data); break;
                case 8: t.VertexMap8 = ByteGrid(c.Data); break;
                case 10: t.HalfTileMap = ByteGrid(c.Data); break;
                case 13: if (c.Data.Find(3) != null) t.Heights2 = FloatGrid(c.Data); break;
                case 14: t.Flags14 = (byte)c.Data.AsInt32(); break;
                case 15: if (c.Data.Find(3) != null) t.VertexMap15 = ByteGrid(c.Data); break;
            }
        }
        if (t.Heights == null) throw new InvalidDataException("terrain has no height grid");
        return t;
    }

    static void ReadLayers(ChunkSpan arr, TerrainData t)
    {
        foreach (var el in arr.All(1))
        {
            var layer = new TerrainLayer { Weights = ByteGrid(el.Get(2)) };
            var name = el.Find(3)?.Data;
            if (name is { } n && n.Find(3) is { } s) layer.TilePath = Core.Xdb.XdbRef.Parse(s.Data.AsString().TrimEnd('\0')).Path;
            t.Layers.Add(layer);
        }
    }

    static Grid<byte> ByteGrid(ChunkSpan c)
    {
        int w = c.GetInt(1), h = c.GetInt(2);
        var d = c.Find(3)?.Data.Bytes.ToArray() ?? new byte[w * h];
        if (d.Length != w * h) Array.Resize(ref d, w * h);
        return new Grid<byte>(w, h, d);
    }

    static Grid<float> FloatGrid(ChunkSpan c)
    {
        int w = c.GetInt(1), h = c.GetInt(2);
        var raw = c.Get(3).Bytes;
        var d = new float[w * h];
        for (int i = 0; i < d.Length && i * 4 + 4 <= raw.Length; i++)
            d[i] = BitConverter.ToSingle(raw.Slice(i * 4, 4));
        return new Grid<float>(w, h, d);
    }
}
