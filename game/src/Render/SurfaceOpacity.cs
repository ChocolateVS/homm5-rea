using Godot;
using H5.Assets.Model;

namespace H5Reborn.Render;

/// <summary>
/// Animated per-mesh opacity, as the original drives it: Granny vector tracks named
/// "Transparency" (whole model) and "&lt;MeshShape&gt;.Transparency" (one mesh) use Maya's
/// convention (1 = invisible). The original multiplies the object's alpha constant into the
/// vertex alpha (vs: oD0.w = v1.w * c29.w; ps: r0.a = t0.a * v0.a), so opacity scales alpha;
/// additive/premultiplied materials therefore fade their colour too.
/// Surfaces are matched to meshes by name: a part is named after its Maya mesh
/// ("GlowShape"), or "&lt;mesh&gt;_&lt;n&gt;" when a mesh has several parts.
/// </summary>
public sealed class SurfaceOpacity
{
    readonly string[] _surfaceMesh;

    public SurfaceOpacity(ModelAsset asset)
    {
        var names = new List<string>();
        foreach (var part in asset.Mesh.Parts)
        {
            if (part.Positions.Length == 0 || part.Indices.Length == 0) continue;
            names.Add(part.Name);
        }
        _surfaceMesh = names.ToArray();
    }

    public int SurfaceCount => _surfaceMesh.Length;

    /// <summary>True when the clip fades anything (a track that ever leaves opacity 1).</summary>
    public static bool Animates(AnimationClip? clip) =>
        clip != null && clip.Scalars.Any(kv => IsTransparency(kv.Key) && kv.Value.Max > 0.001f);

    static bool IsTransparency(string key) =>
        key == "Transparency" || key.EndsWith(".Transparency", StringComparison.Ordinal);

    /// <summary>Opacity (0..1) of each surface at time t.</summary>
    public void Evaluate(AnimationClip clip, float t, Span<float> opacity)
    {
        float global = clip.Scalars.TryGetValue("Transparency", out var g) ? 1f - Math.Clamp(g.Sample(t), 0f, 1f) : 1f;
        for (int s = 0; s < _surfaceMesh.Length; s++)
        {
            float o = global;
            var track = FindMeshTrack(clip, _surfaceMesh[s]);
            if (track != null) o *= 1f - Math.Clamp(track.Sample(t), 0f, 1f);
            opacity[s] = o;
        }
    }

    static ScalarTrack? FindMeshTrack(AnimationClip clip, string partName)
    {
        if (clip.Scalars.TryGetValue(partName + ".Transparency", out var exact)) return exact;
        // Multi-part meshes: "<mesh>_<n>".
        int us = partName.LastIndexOf('_');
        if (us > 0 && int.TryParse(partName[(us + 1)..], out _)
            && clip.Scalars.TryGetValue(partName[..us] + ".Transparency", out var byMesh)) return byMesh;
        // Exporters sometimes keep a namespace prefix ("ns:GlowShape.Transparency").
        foreach (var (key, track) in clip.Scalars)
            if (key.EndsWith(":" + partName + ".Transparency", StringComparison.Ordinal)) return track;
        return null;
    }
}
