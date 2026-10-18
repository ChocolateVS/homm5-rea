using Godot;
using H5.Assets.Model;
using SN = System.Numerics;

namespace H5Reborn.Render;

/// <summary>
/// Converts decoded original models (engine-neutral <see cref="ModelAsset"/>) into
/// Godot nodes. Source space is Z-up right-handed; Godot is Y-up right-handed,
/// so (x, y, z) maps to (x, z, -y). Source triangles are counter-clockwise while
/// Godot treats clockwise as front-facing, so each triangle's winding is reversed.
/// </summary>
public static class ModelBuilder
{
    public static Vector3 ToGodot(SN.Vector3 v) => new(v.X, v.Z, -v.Y);

    public static Node3D Build(ModelAsset model)
    {
        var root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(model.SourcePath) };
        var mesh = new ArrayMesh();
        var materials = model.Materials.Select(BuildMaterial).ToList();

        foreach (var part in model.Mesh.Parts)
        {
            if (part.Positions.Length == 0 || part.Indices.Length == 0) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = part.Positions.Select(ToGodot).ToArray();
            if (part.Normals.Length == part.Positions.Length)
                arrays[(int)Mesh.ArrayType.Normal] = part.Normals.Select(ToGodot).ToArray();
            if (part.UV0.Length == part.Positions.Length)
                arrays[(int)Mesh.ArrayType.TexUV] = part.UV0.Select(u => new Vector2(u.X, u.Y)).ToArray();
            if (part.UV1 is { } uv1 && uv1.Length == part.Positions.Length)
                arrays[(int)Mesh.ArrayType.TexUV2] = uv1.Select(u => new Vector2(u.X, u.Y)).ToArray();

            var idx = new int[part.Indices.Length];
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                idx[t] = part.Indices[t];
                idx[t + 1] = part.Indices[t + 2];
                idx[t + 2] = part.Indices[t + 1];
            }
            arrays[(int)Mesh.ArrayType.Index] = idx;

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var surface = mesh.GetSurfaceCount() - 1;
            mesh.SurfaceSetName(surface, part.Name);
            if (part.MaterialIndex >= 0 && part.MaterialIndex < materials.Count)
                mesh.SurfaceSetMaterial(surface, materials[part.MaterialIndex]);
        }

        root.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });
        return root;
    }

    static Material BuildMaterial(MaterialData m)
    {
        var mat = new StandardMaterial3D
        {
            CullMode = m.TwoSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
            Roughness = 1f - Math.Clamp(m.SpecFactor, 0f, 1f) * 0.5f,
        };
        if (m.DiffuseTexture != null && Game.Instance.LoadTexture(m.DiffuseTexture) is { } tex)
            mat.AlbedoTexture = tex;
        switch (m.AlphaMode)
        {
            case "AM_ALPHA_TEST":
                mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                mat.AlphaScissorThreshold = 0.5f;
                break;
            case "AM_TRANSPARENT":
            case "AM_ALPHA_BLEND":
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                break;
        }
        if (m.LightingMode == "L_NONE") mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        return mat;
    }
}
