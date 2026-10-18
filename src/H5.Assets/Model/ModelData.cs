using System.Numerics;

namespace H5.Assets.Model;

// Engine-neutral model representation produced by the Nival format decoders and
// consumed by the Godot client (and the glTF exporter). Coordinates are kept in the
// original engine's space (Z-up, right-handed, metres); converting to Godot's Y-up
// happens in the client so decoders stay faithful to the source data.

/// <summary>One drawable sub-mesh with a single material slot.</summary>
public sealed class MeshPart
{
    public string Name = "";
    public int MaterialIndex;
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector2[] UV0 = Array.Empty<Vector2>();
    /// <summary>Optional extra UV set (lightmaps on map objects).</summary>
    public Vector2[]? UV1;
    /// <summary>Optional tangents (xyz) with handedness in w.</summary>
    public Vector4[]? Tangents;
    public uint[]? Colors;
    /// <summary>Up to 4 influences per vertex; indices refer to <see cref="SkeletonData.Bones"/>.</summary>
    public SkinInfluence[]? Skin;
    /// <summary>Triangle list, counter-clockwise front faces in source space.</summary>
    public int[] Indices = Array.Empty<int>();
}

public struct SkinInfluence
{
    public int B0, B1, B2, B3;
    public float W0, W1, W2, W3;
}

public sealed class MeshData
{
    public List<MeshPart> Parts = new();
    public Vector3 BoundsMin, BoundsMax;
}

public sealed class Bone
{
    public string Name = "";
    public int Parent = -1;
    /// <summary>Local bind transform relative to the parent.</summary>
    public Vector3 Translation;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;
    /// <summary>Inverse of the bone's world-space bind matrix (for skinning).</summary>
    public Matrix4x4 InverseBind = Matrix4x4.Identity;
}

public sealed class SkeletonData
{
    public List<Bone> Bones = new();
    public int IndexOf(string name) => Bones.FindIndex(b => b.Name == name);
}

/// <summary>Per-bone keyframe track. Each channel has its own key times (seconds).</summary>
public sealed class BoneTrack
{
    public string BoneName = "";
    public float[] TranslationTimes = Array.Empty<float>();
    public Vector3[] Translations = Array.Empty<Vector3>();
    public float[] RotationTimes = Array.Empty<float>();
    public Quaternion[] Rotations = Array.Empty<Quaternion>();
    public float[] ScaleTimes = Array.Empty<float>();
    public Vector3[] Scales = Array.Empty<Vector3>();
}

public sealed class AnimationClip
{
    public string Name = "";
    public float Duration;
    public List<BoneTrack> Tracks = new();
    /// <summary>
    /// One-dimensional animated attributes (Granny vector tracks), keyed by their Maya name:
    /// "Transparency" (whole model) or "&lt;MeshShape&gt;.Transparency" (one mesh), plus a few
    /// scene markers (Mark, Start, End…). Transparency follows Maya: 1 = invisible.
    /// </summary>
    public Dictionary<string, ScalarTrack> Scalars = new(StringComparer.Ordinal);
}

/// <summary>A baked scalar curve: linear interpolation between keys.</summary>
public sealed class ScalarTrack
{
    public float[] Times = Array.Empty<float>();
    public float[] Values = Array.Empty<float>();

    public float Sample(float t)
    {
        if (Values.Length == 0) return 0f;
        if (Values.Length == 1 || t <= Times[0]) return Values[0];
        if (t >= Times[^1]) return Values[^1];
        int lo = 0, hi = Times.Length - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Times[mid] <= t) lo = mid; else hi = mid; }
        float span = Times[hi] - Times[lo];
        return span > 0 ? Values[lo] + (Values[hi] - Values[lo]) * ((t - Times[lo]) / span) : Values[lo];
    }

    public float Min => Values.Length == 0 ? 0f : Values.Min();
    public float Max => Values.Length == 0 ? 0f : Values.Max();
}

/// <summary>Material parameters taken from the (Material) xdb; textures are pak paths.</summary>
public sealed class MaterialData
{
    public string? DiffuseTexture;
    public string? BumpTexture;
    public string? GlossTexture;
    public string? MirrorTexture;
    public string AlphaMode = "AM_OPAQUE";
    public bool TwoSided;
    public float SpecFactor;
    public Vector3 SpecColor;
    public string LightingMode = "L_NORMAL";
}

public sealed class ModelAsset
{
    public string SourcePath = "";
    public MeshData Mesh = new();
    public SkeletonData? Skeleton;
    public List<MaterialData> Materials = new();
    public Dictionary<string, AnimationClip> Animations = new(StringComparer.OrdinalIgnoreCase);
}
