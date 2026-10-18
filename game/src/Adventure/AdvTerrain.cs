using Godot;
using H5.Assets.Terrain;
using H5.Core.Xdb;
using H5Reborn.Arena;

namespace H5Reborn.Adventure;

/// <summary>
/// Terrain of one adventure-map floor, built from the decoded GroundTerrain.bin /
/// UndergroundTerrain.bin exactly like the combat terrain (TerrainBuilder) but with
/// up to 32 blended layers. One map tile = one world unit; map (x, y, h) → Godot
/// (x, h, −y), so tile (tx, ty) is centred at (tx + 0.5, h, −(ty + 0.5)).
/// </summary>
public static class AdvTerrain
{
    public const int MaxLayers = 32;
    const int MaxLayerTextureSize = 1024;

    /// <summary>Per tile type: the textures, water flag and minimap colour from its AdvMapTile xdb.</summary>
    public sealed record TileInfo(string? Texture, bool Water, Color Minimap, string Type);

    static readonly Dictionary<string, TileInfo> _tiles = new(StringComparer.OrdinalIgnoreCase);
    static Shader? _shader;

    public static TileInfo Tile(string tilePath)
    {
        if (_tiles.TryGetValue(tilePath, out var info)) return info;
        var xdb = Game.Instance.Xdb;
        info = new TileInfo(null, false, new Color(0.3f, 0.3f, 0.3f), "");
        if (!string.IsNullOrEmpty(tilePath) && Game.Instance.Fs.Exists(tilePath))
        {
            var root = xdb.Load(tilePath).Root!;
            string? Href(string name)
            {
                var h = (string?)root.Element(name)?.Attribute("href");
                return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, tilePath).Path;
            }
            var material = Href("Material") ?? "";
            var mc = root.Element("MinimapColor");
            float F(string n) => float.TryParse((string?)mc?.Element(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0.3f;
            info = new TileInfo(Href("Texture"), material.Contains("Water", StringComparison.OrdinalIgnoreCase),
                new Color(F("x"), F("y"), F("z")), (string?)root.Element("Type") ?? "");
        }
        return _tiles[tilePath] = info;
    }

    public static MeshInstance3D Build(TerrainData t)
    {
        int vw = t.Heights.Width, vh = t.Heights.Height;
        var normals = t.ComputeNormals();
        var verts = new Vector3[vw * vh];
        var norms = new Vector3[vw * vh];
        var uv = new Vector2[vw * vh];
        var uv2 = new Vector2[vw * vh];
        for (int y = 0; y < vh; y++)
        for (int x = 0; x < vw; x++)
        {
            int i = y * vw + x;
            verts[i] = new Vector3(x * AdvWorld.Unit, t.Heights.Data[i], -y * AdvWorld.Unit);
            var n = normals[i];
            norms[i] = new Vector3(n.X, n.Z, -n.Y);
            uv[i] = new Vector2(x, y);
            uv2[i] = new Vector2((x + 0.5f) / vw, (y + 0.5f) / vh);
        }

        // Same winding as the combat terrain: (a, c, b) is clockwise seen from above.
        var idx = new int[(vw - 1) * (vh - 1) * 6];
        int k = 0;
        for (int y = 0; y < vh - 1; y++)
        for (int x = 0; x < vw - 1; x++)
        {
            int a = y * vw + x, b = a + 1, c = a + vw, d = c + 1;
            idx[k++] = a; idx[k++] = c; idx[k++] = b;
            idx[k++] = b; idx[k++] = c; idx[k++] = d;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, BuildMaterial(t));
        return new MeshInstance3D { Name = "Terrain", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    static ShaderMaterial BuildMaterial(TerrainData t)
    {
        _shader ??= GD.Load<Shader>("res://src/Adventure/AdvTerrain.gdshader");
        var mat = new ShaderMaterial { Shader = _shader };
        int count = Math.Min(t.Layers.Count, MaxLayers);

        var sources = new List<Image?>();
        int size = 64;
        for (int i = 0; i < count; i++)
        {
            Image? img = null;
            if (Tile(t.Layers[i].TilePath).Texture is { } path && Game.Instance.LoadTexture(path) is { } tex)
            {
                img = tex.GetImage();
                if (img.IsCompressed()) img.Decompress();
                size = Math.Max(size, Math.Min(MaxLayerTextureSize, Math.Max(img.GetWidth(), img.GetHeight())));
            }
            sources.Add(img);
        }
        var images = new Godot.Collections.Array<Image>();
        var water = new float[MaxLayers];
        for (int i = 0; i < count; i++)
        {
            var img = sources[i] ?? Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            img.Convert(Image.Format.Rgba8);
            if (img.GetWidth() != size || img.GetHeight() != size) img.Resize(size, size, Image.Interpolation.Cubic);
            img.ClearMipmaps();
            img.GenerateMipmaps();
            images.Add(img);
            water[i] = Tile(t.Layers[i].TilePath).Water ? 1f : 0f;
        }
        var array = new Texture2DArray();
        array.CreateFromImages(images);
        mat.SetShaderParameter("layers", array);
        mat.SetShaderParameter("layer_count", count);
        mat.SetShaderParameter("tiles_per_texture", TerrainBuilder.TilesPerTexture);
        mat.SetShaderParameter("layer_water", water);

        int vw = t.Heights.Width, vh = t.Heights.Height;
        for (int w = 0; w < MaxLayers / 4; w++)
        {
            var data = new byte[vw * vh * 4];
            for (int c = 0; c < 4; c++)
            {
                int layer = w * 4 + c;
                if (layer >= count) continue;
                var src = t.Layers[layer].Weights;
                for (int y = 0; y < vh; y++)
                for (int x = 0; x < vw; x++)
                    data[(y * vw + x) * 4 + c] = x < src.Width && y < src.Height ? src[x, y] : (byte)0;
            }
            mat.SetShaderParameter($"weights{w}", ImageTexture.CreateFromImage(Image.CreateFromData(vw, vh, false, Image.Format.Rgba8, data)));
        }
        return mat;
    }

    /// <summary>
    /// Minimap base image (one pixel per tile): each layer's MinimapColor blended in
    /// draw order with the layer's average weight over the tile's four corners.
    /// </summary>
    public static Image MinimapImage(TerrainData t)
    {
        var img = Image.CreateEmpty(t.TilesX, t.TilesY, false, Image.Format.Rgba8);
        var colors = t.Layers.Select(l => Tile(l.TilePath).Minimap).ToArray();
        for (int y = 0; y < t.TilesY; y++)
        for (int x = 0; x < t.TilesX; x++)
        {
            var col = colors.Length > 0 ? colors[0] : Colors.Black;
            for (int i = 1; i < colors.Length; i++)
            {
                var g = t.Layers[i].Weights;
                if (x + 1 >= g.Width || y + 1 >= g.Height) continue;
                float w = (g[x, y] + g[x + 1, y] + g[x, y + 1] + g[x + 1, y + 1]) / (4f * 255f);
                if (w > 0) col = col.Lerp(colors[i], w);
            }
            img.SetPixel(x, y, col);
        }
        return img;
    }
}
