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

    /// <summary>The basis change is a proper rotation, so a quaternion's vector part maps like a vector.</summary>
    public static Quaternion ToGodot(SN.Quaternion q) => new(q.X, q.Z, -q.Y, q.W);

    /// <summary>Per-axis scale under the axis permutation x→x, y→z, z→y.</summary>
    public static Vector3 ScaleToGodot(SN.Vector3 s) => new(s.X, s.Z, s.Y);

    /// <summary>
    /// Converts a source row-vector matrix (v' = v·M, translation in the last row) into a
    /// Godot transform: G = C · Mᵀ · C⁻¹ with C the Z-up → Y-up basis change.
    /// </summary>
    public static Transform3D ToGodot(SN.Matrix4x4 m)
    {
        var x = ToGodot(new SN.Vector3(m.M11, m.M12, m.M13));
        var y = ToGodot(new SN.Vector3(m.M21, m.M22, m.M23));
        var z = ToGodot(new SN.Vector3(m.M31, m.M32, m.M33));
        var t = ToGodot(new SN.Vector3(m.M41, m.M42, m.M43));
        // Source basis vector images expressed in Godot space; Godot axis X = source X,
        // Godot Y = source Z, Godot Z = -source Y.
        var basis = new Basis(x, z, -y);
        return new Transform3D(basis, t);
    }

    public static Node3D Build(ModelAsset model)
    {
        var root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(model.SourcePath) };
        root.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = BuildMesh(model, skinned: false) });
        return root;
    }

    /// <summary>Builds the surfaces; with <paramref name="skinned"/> the bone indices/weights are included.</summary>
    public static ArrayMesh BuildMesh(ModelAsset model, bool skinned)
    {
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
            if (skinned && part.Skin is { } skin && skin.Length == part.Positions.Length)
            {
                var bones = new int[skin.Length * 4];
                var weights = new float[skin.Length * 4];
                for (int i = 0; i < skin.Length; i++)
                {
                    var s = skin[i];
                    // Slots with zero weight may hold filler indices; point them at bone 0.
                    bones[i * 4] = s.W0 > 0 ? s.B0 : 0;
                    bones[i * 4 + 1] = s.W1 > 0 ? s.B1 : 0;
                    bones[i * 4 + 2] = s.W2 > 0 ? s.B2 : 0;
                    bones[i * 4 + 3] = s.W3 > 0 ? s.B3 : 0;
                    weights[i * 4] = s.W0; weights[i * 4 + 1] = s.W1;
                    weights[i * 4 + 2] = s.W2; weights[i * 4 + 3] = s.W3;
                }
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }

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
        return mesh;
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
            // Nival "overlay" materials are alpha-blended layers drawn over the terrain and
            // scenery (ground decals, cliff skirts with ragged alpha edges, cave floors);
            // they are lit like any surface. The _ZWRITE variant also writes depth.
            case "AM_OVERLAY":
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.RenderPriority = 1;
                break;
            case "AM_OVERLAY_ZWRITE":
                mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;
                mat.RenderPriority = 1;
                break;
        }
        if (m.LightingMode == "L_NONE") mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        return mat;
    }
}
