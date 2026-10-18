using System.Numerics;
using H5.Assets.Model;

namespace H5.Assets.Granny;

/// <summary>
/// Converts decoded Granny object graphs into the engine-neutral model types.
/// Values stay in the source space (Z-up, metres), as ModelData requires.
/// </summary>
public static class GrannyConvert
{
    /// <summary>Default resampling rate for curves of degree &gt; 1.</summary>
    public const float DefaultSampleRate = 30f;

    public static SkeletonData? ReadSkeleton(Gr2File g)
    {
        var skel = g.Root.List("Skeletons").OfType<GrObject>().FirstOrDefault();
        if (skel == null) return null;
        var s = new SkeletonData();
        foreach (var b in skel.List("Bones").OfType<GrObject>())
        {
            var t = b["Transform"] is GrTransform tr ? tr : default;
            var bone = new Bone
            {
                Name = b.Str("Name") ?? "",
                Parent = b.Int("ParentIndex"),
                Translation = new Vector3(t.Tx, t.Ty, t.Tz),
                Rotation = t.ScaleShear == null ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(t.Rx, t.Ry, t.Rz, t.Rw)),
                Scale = t.ScaleShear == null ? Vector3.One : new Vector3(t.ScaleShear[0], t.ScaleShear[4], t.ScaleShear[8]),
            };
            var inv = b.Floats("InverseWorldTransform");
            if (inv.Length == 16)
                bone.InverseBind = new Matrix4x4(
                    inv[0], inv[1], inv[2], inv[3],
                    inv[4], inv[5], inv[6], inv[7],
                    inv[8], inv[9], inv[10], inv[11],
                    inv[12], inv[13], inv[14], inv[15]);
            s.Bones.Add(bone);
        }
        return s;
    }

    public static List<GrannyAnimation> ReadGrannyAnimations(Gr2File g)
    {
        var result = new List<GrannyAnimation>();
        foreach (var a in g.Root.List("Animations").OfType<GrObject>())
        {
            foreach (var grp in a.List("TrackGroups").OfType<GrObject>())
            {
                var anim = new GrannyAnimation
                {
                    Name = a.Str("Name") ?? "",
                    Duration = a.Float("Duration"),
                    TimeStep = a.Float("TimeStep"),
                    Oversampling = a.Float("Oversampling"),
                    TrackGroupName = grp.Str("Name") ?? "",
                    AccumulationFlags = grp.Int("AccumulationFlags"),
                };
                var loop = grp.Floats("LoopTranslation");
                if (loop.Length == 3) anim.LoopTranslation = new Vector3(loop[0], loop[1], loop[2]);

                foreach (var tt in grp.List("TransformTracks").OfType<GrObject>())
                    anim.TransformTracks.Add(new GrannyTransformTrack
                    {
                        Name = tt.Str("Name") ?? "",
                        Position = GrannyCurve.FromObject(tt.Obj("PositionCurve"), 3),
                        Orientation = GrannyCurve.FromObject(tt.Obj("OrientationCurve"), 4),
                        ScaleShear = GrannyCurve.FromObject(tt.Obj("ScaleShearCurve"), 9),
                    });

                foreach (var vt in grp.List("VectorTracks").OfType<GrObject>())
                    anim.VectorTracks[vt.Str("Name") ?? ""] = GrannyCurve.FromObject(vt.Obj("ValueCurve"), Math.Max(1, vt.Int("Dimension")));

                foreach (var tx in grp.List("TextTracks").OfType<GrObject>())
                    foreach (var e in tx.List("Entries").OfType<GrObject>())
                        anim.TextEvents.Add(new GrannyTextEvent(tx.Str("Name") ?? "", e.Float("TimeStamp"), e.Str("Text") ?? ""));

                result.Add(anim);
            }
        }
        return result;
    }

    public static List<AnimationClip> ReadAnimations(Gr2File g, Dictionary<string, int>? formatStats = null, float sampleRate = DefaultSampleRate)
    {
        var clips = new List<AnimationClip>();
        foreach (var a in ReadGrannyAnimations(g))
        {
            if (formatStats != null)
                foreach (var t in a.TransformTracks)
                {
                    Count(formatStats, "pos", t.Position);
                    Count(formatStats, "rot", t.Orientation);
                    Count(formatStats, "ss", t.ScaleShear);
                }
            clips.Add(ToClip(a, sampleRate));
        }
        return clips;
    }

    static void Count(Dictionary<string, int> stats, string channel, GrannyCurve c)
    {
        string key = c.IsEmpty ? $"{channel}:empty" : c.IsConstant ? $"{channel}:const/{c.Dimension}" : $"{channel}:deg{c.Degree}/{c.Dimension}";
        stats[key] = stats.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// Bakes Granny curves into keyframe tracks. Constant curves become one key, linear and step
    /// curves keep their knots exactly, higher degrees are resampled at <paramref name="sampleRate"/>
    /// (plus the end time). Empty curves are Granny identity curves and are baked as a single
    /// identity key (zero translation, identity rotation, unit scale) so every consumer — glTF,
    /// Godot, CPU skinning — reproduces Granny's result instead of falling back to the bind pose.
    /// </summary>
    public static AnimationClip ToClip(GrannyAnimation a, float sampleRate = DefaultSampleRate)
    {
        var clip = new AnimationClip { Name = a.Name, Duration = a.Duration };
        foreach (var t in a.TransformTracks)
        {
            var track = new BoneTrack { BoneName = t.Name };
            track.TranslationTimes = KeyTimes(t.Position, a.Duration, sampleRate);
            track.Translations = t.Position.IsEmpty ? new[] { Vector3.Zero } : track.TranslationTimes.Select(t.SamplePosition).ToArray();
            track.RotationTimes = KeyTimes(t.Orientation, a.Duration, sampleRate);
            track.Rotations = t.Orientation.IsEmpty ? new[] { Quaternion.Identity } : track.RotationTimes.Select(t.SampleOrientation).ToArray();
            MakeQuaternionsContinuous(track.Rotations);
            track.ScaleTimes = KeyTimes(t.ScaleShear, a.Duration, sampleRate);
            track.Scales = t.ScaleShear.IsEmpty ? new[] { Vector3.One } : track.ScaleTimes.Select(time =>
            {
                Span<float> m = stackalloc float[9];
                t.SampleScaleShear(time, m);
                return new Vector3(m[0], m[4], m[8]);
            }).ToArray();
            clip.Tracks.Add(track);
        }
        foreach (var (name, curve) in a.VectorTracks)
        {
            if (curve.Dimension != 1 || curve.IsEmpty) continue;
            var times = KeyTimes(curve, a.Duration, sampleRate);
            var values = new float[times.Length];
            Span<float> v = stackalloc float[1];
            for (int i = 0; i < times.Length; i++) { curve.Evaluate(times[i], v); values[i] = v[0]; }
            clip.Scalars[name] = new ScalarTrack { Times = times, Values = values };
        }
        return clip;
    }

    static float[] KeyTimes(GrannyCurve c, float duration, float rate)
    {
        if (c.IsEmpty) return new[] { 0f };
        if (c.IsConstant) return new[] { 0f };
        if (c.Degree <= 1) return (float[])c.Knots.Clone();
        float end = Math.Max(duration, c.Knots[^1]);
        int n = Math.Max(2, (int)MathF.Ceiling(end * rate) + 1);
        var times = new float[n];
        for (int i = 0; i < n; i++) times[i] = Math.Min(end, i / rate);
        times[^1] = end;
        return times;
    }

    /// <summary>Flips quaternion signs so consecutive keys take the short path.</summary>
    public static void MakeQuaternionsContinuous(Quaternion[] q)
    {
        for (int i = 1; i < q.Length; i++)
            if (Quaternion.Dot(q[i - 1], q[i]) < 0) q[i] = -q[i];
    }

    public static void Validate(SkeletonData s)
    {
        if (s.Bones.Count == 0) throw new InvalidDataException("skeleton has no bones");
        int roots = 0;
        for (int i = 0; i < s.Bones.Count; i++)
        {
            var b = s.Bones[i];
            if (b.Parent < 0) roots++;
            else if (b.Parent >= i) throw new InvalidDataException($"bone {b.Name} parent {b.Parent} not before it");
            if (!Finite(b.Translation) || !Finite(b.Scale) || !float.IsFinite(b.Rotation.W))
                throw new InvalidDataException($"bone {b.Name} has non-finite transform");
        }
        if (roots != 1) throw new InvalidDataException($"skeleton has {roots} roots");
    }

    public static void Validate(AnimationClip c)
    {
        if (!(c.Duration >= 0) || c.Duration > 600) throw new InvalidDataException($"implausible duration {c.Duration}");
        foreach (var t in c.Tracks)
        {
            if (t.Translations.Any(v => !Finite(v))) throw new InvalidDataException($"track {t.BoneName}: non-finite translation");
            if (t.Scales.Any(v => !Finite(v))) throw new InvalidDataException($"track {t.BoneName}: non-finite scale");
            if (t.Rotations.Any(q => !float.IsFinite(q.X + q.Y + q.Z + q.W) || MathF.Abs(q.Length() - 1) > 1e-3f))
                throw new InvalidDataException($"track {t.BoneName}: bad rotation");
            if (!Sorted(t.TranslationTimes) || !Sorted(t.RotationTimes) || !Sorted(t.ScaleTimes))
                throw new InvalidDataException($"track {t.BoneName}: key times not sorted");
        }
    }

    static bool Sorted(float[] a)
    {
        for (int i = 1; i < a.Length; i++) if (a[i] < a[i - 1]) return false;
        return true;
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
