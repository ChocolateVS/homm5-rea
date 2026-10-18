using System.Numerics;
using H5.Assets.Model;

namespace H5.Assets.Geometry;

/// <summary>Expands a raw <see cref="GeometryFile"/> into engine-neutral <see cref="MeshData"/>.</summary>
public static class GeometryConverter
{
    public const float UvScale = 1f / 2048f;

    /// <param name="meshNames">MeshNames from the Geometry xdb (optional).</param>
    /// <param name="materialQuantities">MaterialQuantities from the Geometry xdb: number of
    /// material slots (= parts) per mesh. Part j of mesh i uses material sum(q[0..i)) + j.</param>
    public static MeshData ToMeshData(GeometryFile g, IReadOnlyList<string>? meshNames = null, IReadOnlyList<int>? materialQuantities = null)
    {
        var md = new MeshData();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int materialBase = 0;
        for (int mi = 0; mi < g.Meshes.Count; mi++)
        {
            var mesh = g.Meshes[mi];
            string baseName = meshNames != null && mi < meshNames.Count ? meshNames[mi] : $"mesh{mi}";
            for (int pi = 0; pi < mesh.Parts.Count; pi++)
            {
                var part = ToPart(mesh.Parts[pi]);
                part.Name = mesh.Parts.Count == 1 ? baseName : $"{baseName}_{pi}";
                // A few blobs carry one more part than their stale xdb MaterialQuantities;
                // such extra parts reuse the mesh's last material slot.
                int quantity = materialQuantities != null && mi < materialQuantities.Count ? materialQuantities[mi] : mesh.Parts.Count;
                part.MaterialIndex = materialBase + Math.Min(pi, Math.Max(0, quantity - 1));
                foreach (var v in part.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                md.Parts.Add(part);
            }
            materialBase += materialQuantities != null && mi < materialQuantities.Count ? materialQuantities[mi] : mesh.Parts.Count;
        }
        if (md.Parts.Count > 0 && min.X <= max.X) { md.BoundsMin = min; md.BoundsMax = max; }
        return md;
    }

    public static Vector3 UnpackUnit(uint packed)
    {
        // D3DCOLOR byte order in memory is B,G,R,A; x is stored in R.
        float x = ((packed >> 16) & 0xFF) / 127.5f - 1f;
        float y = ((packed >> 8) & 0xFF) / 127.5f - 1f;
        float z = (packed & 0xFF) / 127.5f - 1f;
        var v = new Vector3(x, y, z);
        var len = v.Length();
        return len > 1e-6f ? v / len : Vector3.UnitZ;
    }

    public static MeshPart ToPart(GeometryPart src)
    {
        int n = src.Vertices.Length;
        var part = new MeshPart
        {
            Positions = new Vector3[n],
            Normals = new Vector3[n],
            UV0 = new Vector2[n],
            Tangents = new Vector4[n],
            Indices = new int[src.Triangles.Length],
        };
        bool hasUv1 = false;
        foreach (var v in src.Vertices) if (v.U1 != 0 || v.V1 != 0) { hasUv1 = true; break; }
        if (hasUv1) part.UV1 = new Vector2[n];
        if (src.IsSkinned) part.Skin = new SkinInfluence[n];

        for (int i = 0; i < n; i++)
        {
            var v = src.Vertices[i];
            int pi = src.VertexPosition[i];
            part.Positions[i] = new Vector3(src.Positions[pi * 3], src.Positions[pi * 3 + 1], src.Positions[pi * 3 + 2]);
            var nrm = UnpackUnit(v.Normal);
            var tan = UnpackUnit(v.Tangent);
            var bin = UnpackUnit(v.Binormal);
            part.Normals[i] = nrm;
            float w = Vector3.Dot(Vector3.Cross(nrm, tan), bin) < 0 ? -1f : 1f;
            part.Tangents![i] = new Vector4(tan, w);
            part.UV0[i] = new Vector2(v.U0 * UvScale, v.V0 * UvScale);
            if (hasUv1) part.UV1![i] = new Vector2(v.U1 * UvScale, v.V1 * UvScale);
            if (src.IsSkinned)
            {
                var s = src.Skin[pi];
                part.Skin![i] = Normalize(new SkinInfluence { B0 = s.B0, B1 = s.B1, B2 = s.B2, B3 = s.B3, W0 = s.W0, W1 = s.W1, W2 = s.W2, W3 = s.W3 });
            }
        }
        for (int i = 0; i < src.Triangles.Length; i++) part.Indices[i] = src.Triangles[i];
        return part;
    }

    static SkinInfluence Normalize(SkinInfluence s)
    {
        // Unused slots carry arbitrary bone ids with zero weight; point them at the first bone.
        if (s.W1 <= 0) { s.W1 = 0; s.B1 = s.B0; }
        if (s.W2 <= 0) { s.W2 = 0; s.B2 = s.B0; }
        if (s.W3 <= 0) { s.W3 = 0; s.B3 = s.B0; }
        float sum = s.W0 + s.W1 + s.W2 + s.W3;
        if (sum > 1e-6f) { s.W0 /= sum; s.W1 /= sum; s.W2 /= sum; s.W3 /= sum; }
        else { s.W0 = 1; }
        return s;
    }
}
