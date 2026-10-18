using System.Numerics;
using H5.Assets.Model;
using H5.Assets.Textures;

namespace H5.Assets.Export;

/// <summary>
/// Tiny software rasterizer for format regression checks: renders a model (bind pose)
/// with its diffuse textures, orthographic, Z-up source space, to an RGBA image.
/// </summary>
public static class PreviewRenderer
{
    /// <param name="textures">Per material index decoded diffuse texture (may contain nulls).</param>
    /// <param name="yawDegrees">Camera rotation around Z; 0 looks from -Y towards +Y.</param>
    public static RgbaImage Render(MeshData mesh, IReadOnlyList<RgbaImage?> textures, int size = 512, float yawDegrees = 30f, float pitchDegrees = 20f)
    {
        var img = new RgbaImage(size, size);
        var depth = new float[size * size];
        Array.Fill(depth, float.MaxValue);
        for (int i = 0; i < size * size; i++) { img.Pixels[i * 4] = 40; img.Pixels[i * 4 + 1] = 44; img.Pixels[i * 4 + 2] = 52; img.Pixels[i * 4 + 3] = 255; }

        var center = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
        float radius = MathF.Max(1e-3f, (mesh.BoundsMax - mesh.BoundsMin).Length() * 0.5f);
        var view = Matrix4x4.CreateTranslation(-center)
                   * Matrix4x4.CreateRotationZ(-yawDegrees * MathF.PI / 180f)
                   * Matrix4x4.CreateRotationX(-pitchDegrees * MathF.PI / 180f);
        float scale = size * 0.48f / radius;
        var light = Vector3.Normalize(new Vector3(-0.4f, -0.7f, 0.6f));

        foreach (var part in mesh.Parts)
        {
            var tex = part.MaterialIndex < textures.Count ? textures[part.MaterialIndex] : null;
            int n = part.Positions.Length;
            var sx = new float[n]; var sy = new float[n]; var sz = new float[n];
            for (int i = 0; i < n; i++)
            {
                var p = Vector3.Transform(part.Positions[i], view);
                // screen: x right, y = up (source Z after pitch), depth = view Y (towards +Y is far)
                sx[i] = size * 0.5f + p.X * scale;
                sy[i] = size * 0.5f - p.Z * scale;
                sz[i] = p.Y;
            }
            for (int t = 0; t + 2 < part.Indices.Length; t += 3)
            {
                int a = part.Indices[t], b = part.Indices[t + 1], c = part.Indices[t + 2];
                var faceNormal = Vector3.Normalize(part.Normals[a] + part.Normals[b] + part.Normals[c]);
                float shade = 0.6f + 0.6f * MathF.Abs(Vector3.Dot(faceNormal, light));
                Raster(img, depth, size, sx, sy, sz, a, b, c, part, tex, shade);
            }
        }
        return img;
    }

    static void Raster(RgbaImage img, float[] depth, int size, float[] sx, float[] sy, float[] sz, int a, int b, int c, MeshPart part, RgbaImage? tex, float shade)
    {
        float minX = MathF.Max(0, MathF.Floor(MathF.Min(sx[a], MathF.Min(sx[b], sx[c]))));
        float maxX = MathF.Min(size - 1, MathF.Ceiling(MathF.Max(sx[a], MathF.Max(sx[b], sx[c]))));
        float minY = MathF.Max(0, MathF.Floor(MathF.Min(sy[a], MathF.Min(sy[b], sy[c]))));
        float maxY = MathF.Min(size - 1, MathF.Ceiling(MathF.Max(sy[a], MathF.Max(sy[b], sy[c]))));
        float area = (sx[b] - sx[a]) * (sy[c] - sy[a]) - (sx[c] - sx[a]) * (sy[b] - sy[a]);
        if (MathF.Abs(area) < 1e-8f) return;
        for (int y = (int)minY; y <= (int)maxY; y++)
            for (int x = (int)minX; x <= (int)maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((sx[b] - px) * (sy[c] - py) - (sx[c] - px) * (sy[b] - py)) / area;
                float w1 = ((sx[c] - px) * (sy[a] - py) - (sx[a] - px) * (sy[c] - py)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * sz[a] + w1 * sz[b] + w2 * sz[c];
                int idx = y * size + x;
                if (z >= depth[idx]) continue;
                uint col = 0xFFB4B4B4;
                if (tex != null)
                {
                    var uv = part.UV0[a] * w0 + part.UV0[b] * w1 + part.UV0[c] * w2;
                    col = tex.Sample(uv.X, uv.Y);
                    if ((col >> 24) < 96) continue; // alpha test
                }
                depth[idx] = z;
                img.Pixels[idx * 4] = (byte)Math.Min(255, (col & 255) * shade);
                img.Pixels[idx * 4 + 1] = (byte)Math.Min(255, ((col >> 8) & 255) * shade);
                img.Pixels[idx * 4 + 2] = (byte)Math.Min(255, ((col >> 16) & 255) * shade);
                img.Pixels[idx * 4 + 3] = 255;
            }
    }
}
