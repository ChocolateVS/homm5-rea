using System.Numerics;
using H5.Assets.Model;

namespace H5.Assets.Granny;

/// <summary>
/// Pose evaluation and CPU skinning in source space. Matrices use System.Numerics
/// row-vector convention (v' = v * M): local = Scale * Rotation * Translation,
/// world = local * parentWorld, skin = inverseBind * world. This matches Granny's
/// stored InverseWorldTransform layout (translation in the last row).
/// </summary>
public static class GrannyPose
{
    public static Matrix4x4 Local(Vector3 t, Quaternion r, Vector3 s) =>
        Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);

    /// <summary>World matrices of the bind pose, rebuilt from the local bone transforms.</summary>
    public static Matrix4x4[] BindWorld(SkeletonData s)
    {
        var w = new Matrix4x4[s.Bones.Count];
        for (int i = 0; i < w.Length; i++)
        {
            var b = s.Bones[i];
            var l = Local(b.Translation, b.Rotation, b.Scale);
            w[i] = b.Parent >= 0 ? l * w[b.Parent] : l;
        }
        return w;
    }

    /// <summary>
    /// Horizontal model-space travel of a locomotion clip over its duration, or null when no bone
    /// travels (in-place clip). Move clips carry their forward travel in the skeleton itself: the body
    /// AND every root-level prop bone (weapons, shields, banners, reins) move by the same amount
    /// (`h5tool gr2-drift --all`: 68 of 91 adventure move clips have several such bones). The travel is
    /// taken from the bone whose horizontal travel best matches MovementSpeed × duration, so the whole
    /// skeleton can be shifted back rigidly while the node walks at MovementSpeed.
    /// </summary>
    public static Vector3? LocomotionTravel(SkeletonData s, AnimationClip clip, float movementSpeed)
    {
        if (clip.Duration <= 0 || movementSpeed <= 0 || s.Bones.Count == 0) return null;
        float expected = movementSpeed * clip.Duration;
        var w0 = AnimatedWorld(s, clip, 0);
        var w1 = AnimatedWorld(s, clip, clip.Duration);
        Vector3? best = null;
        float bestErr = float.MaxValue;
        for (int i = 0; i < w0.Length; i++)
        {
            var d = w1[i].Translation - w0[i].Translation;
            float drift = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            float err = MathF.Abs(drift - expected);
            if (drift > expected * 0.5f && err < bestErr) { best = new Vector3(d.X, d.Y, 0); bestErr = err; }
        }
        return best;
    }

    /// <summary>
    /// World matrices with <paramref name="clip"/> applied at <paramref name="time"/>.
    /// Bones without a track keep their rest pose; a track channel without keys is identity
    /// (Granny treats a curve with no knots as an identity curve; <see cref="GrannyConvert.ToClip"/>
    /// already bakes those as explicit identity keys).
    /// </summary>
    public static Matrix4x4[] AnimatedWorld(SkeletonData s, AnimationClip clip, float time)
    {
        var tracks = new Dictionary<string, BoneTrack>(StringComparer.Ordinal);
        foreach (var t in clip.Tracks) tracks.TryAdd(t.BoneName, t);
        var w = new Matrix4x4[s.Bones.Count];
        for (int i = 0; i < w.Length; i++)
        {
            var b = s.Bones[i];
            Matrix4x4 l;
            if (tracks.TryGetValue(b.Name, out var tr))
                l = Local(
                    tr.Translations.Length > 0 ? SampleVec(tr.TranslationTimes, tr.Translations, time) : Vector3.Zero,
                    tr.Rotations.Length > 0 ? SampleQuat(tr.RotationTimes, tr.Rotations, time) : Quaternion.Identity,
                    tr.Scales.Length > 0 ? SampleVec(tr.ScaleTimes, tr.Scales, time) : Vector3.One);
            else
                l = Local(b.Translation, b.Rotation, b.Scale);
            w[i] = b.Parent >= 0 ? l * w[b.Parent] : l;
        }
        return w;
    }

    public static Matrix4x4[] SkinMatrices(SkeletonData s, Matrix4x4[] world)
    {
        var m = new Matrix4x4[world.Length];
        for (int i = 0; i < m.Length; i++) m[i] = s.Bones[i].InverseBind * world[i];
        return m;
    }

    static int Segment(float[] times, float t, out float f)
    {
        if (times.Length == 1 || t <= times[0]) { f = 0; return 0; }
        if (t >= times[^1]) { f = 0; return times.Length - 1; }
        int lo = 0, hi = times.Length - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (times[mid] <= t) lo = mid; else hi = mid; }
        float span = times[hi] - times[lo];
        f = span > 0 ? (t - times[lo]) / span : 0;
        return lo;
    }

    public static Vector3 SampleVec(float[] times, Vector3[] v, float t)
    {
        int i = Segment(times, t, out float f);
        return i + 1 < v.Length && f > 0 ? Vector3.Lerp(v[i], v[i + 1], f) : v[i];
    }

    public static Quaternion SampleQuat(float[] times, Quaternion[] q, float t)
    {
        int i = Segment(times, t, out float f);
        return i + 1 < q.Length && f > 0 ? Quaternion.Slerp(q[i], q[i + 1], f) : q[i];
    }

    /// <summary>
    /// Returns a copy of <paramref name="mesh"/> deformed by <paramref name="skin"/> matrices.
    /// Skinned parts use their per-vertex influences; rigid parts (no Skin array) are moved by
    /// <paramref name="rigidBone"/>(part index) when it returns a bone index, else left in place.
    /// </summary>
    public static MeshData Deform(MeshData mesh, Matrix4x4[] skin, Func<int, int>? rigidBone = null)
    {
        var result = new MeshData { BoundsMin = new Vector3(float.MaxValue), BoundsMax = new Vector3(float.MinValue) };
        for (int pi = 0; pi < mesh.Parts.Count; pi++)
        {
            var p = mesh.Parts[pi];
            var q = new MeshPart
            {
                Name = p.Name, MaterialIndex = p.MaterialIndex, UV0 = p.UV0, UV1 = p.UV1, Colors = p.Colors,
                Skin = p.Skin, Indices = p.Indices, Tangents = p.Tangents,
                Positions = new Vector3[p.Positions.Length], Normals = new Vector3[p.Normals.Length],
            };
            int rigid = p.Skin == null && rigidBone != null ? rigidBone(pi) : -1;
            for (int v = 0; v < p.Positions.Length; v++)
            {
                Matrix4x4 m;
                if (p.Skin != null)
                {
                    var s = p.Skin[v];
                    m = Weighted(skin, s.B0, s.W0) + Weighted(skin, s.B1, s.W1) + Weighted(skin, s.B2, s.W2) + Weighted(skin, s.B3, s.W3);
                    float wsum = s.W0 + s.W1 + s.W2 + s.W3;
                    if (wsum <= 1e-6f) m = Matrix4x4.Identity;
                }
                else m = rigid >= 0 && rigid < skin.Length ? skin[rigid] : Matrix4x4.Identity;
                q.Positions[v] = Vector3.Transform(p.Positions[v], m);
                if (v < p.Normals.Length) q.Normals[v] = Vector3.Normalize(Vector3.TransformNormal(p.Normals[v], m));
                result.BoundsMin = Vector3.Min(result.BoundsMin, q.Positions[v]);
                result.BoundsMax = Vector3.Max(result.BoundsMax, q.Positions[v]);
            }
            result.Parts.Add(q);
        }
        return result;
    }

    static Matrix4x4 Weighted(Matrix4x4[] skin, int bone, float w) =>
        w > 0 && bone >= 0 && bone < skin.Length ? skin[bone] * w : default;
}
