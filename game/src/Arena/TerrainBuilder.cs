using Godot;
using H5.Assets.Terrain;

namespace H5Reborn.Arena;

/// <summary>
/// Builds a terrain mesh from a decoded GroundTerrain.bin. Layers are blended in
/// draw order: layer 0 is the base and each later layer is laid over the result
/// with its per-vertex weight (bilinear between vertices), as in the original.
/// Weights are packed four layers per RGBA texture at vertex resolution so the
/// GPU's bilinear filter reproduces the vertex interpolation.
/// </summary>
public static class TerrainBuilder
{
    public const int MaxLayers = 16;
    const int LayerTextureSize = 1024;

    /// <summary>
    /// Map tiles covered by one repeat of a terrain texture. The data has no value for
    /// this (MappingSize is 0); 8 matches the scale of ground detail to 1-tile props.
    /// </summary>
    public static float TilesPerTexture = 8f;

    static Shader? _shader;

    public static MeshInstance3D Build(TerrainData t, IReadOnlyList<string?> layerTextures, float heightOffset = 0)
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
            verts[i] = ArenaGrid.MapToWorld(x, y, t.Heights.Data[i] + heightOffset);
            var n = normals[i];
            norms[i] = new Vector3(n.X, n.Z, -n.Y);
            uv[i] = new Vector2(x, y);
            uv2[i] = new Vector2((x + 0.5f) / vw, (y + 0.5f) / vh);
        }

        // a→b runs along map +x, a→c along map +y (Godot −z). Seen from above, (a, c, b)
        // is clockwise, which Godot treats as front-facing.
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
        mesh.SurfaceSetMaterial(0, BuildMaterial(t, layerTextures));

        return new MeshInstance3D { Name = "Terrain", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    static ShaderMaterial BuildMaterial(TerrainData t, IReadOnlyList<string?> layerTextures)
    {
        _shader ??= GD.Load<Shader>("res://src/Arena/Terrain.gdshader");
        var mat = new ShaderMaterial { Shader = _shader };
        int count = Math.Min(t.Layers.Count, MaxLayers);

        var images = new Godot.Collections.Array<Image>();
        for (int i = 0; i < count; i++)
        {
            Image? img = null;
            if (layerTextures[i] is { } path && Game.Instance.LoadTexture(path) is { } tex)
                img = tex.GetImage();
            img ??= Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.Rgba8);
            if (img.GetWidth() != LayerTextureSize || img.GetHeight() != LayerTextureSize)
                img.Resize(LayerTextureSize, LayerTextureSize, Image.Interpolation.Cubic);
            img.ClearMipmaps();
            img.GenerateMipmaps();
            images.Add(img);
        }
        var array = new Texture2DArray();
        array.CreateFromImages(images);
        mat.SetShaderParameter("layers", array);
        mat.SetShaderParameter("layer_count", count);
        mat.SetShaderParameter("tiles_per_texture", TilesPerTexture);

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
            var weights = ImageTexture.CreateFromImage(Image.CreateFromData(vw, vh, false, Image.Format.Rgba8, data));
            mat.SetShaderParameter($"weights{w}", weights);
        }
        return mat;
    }
}
