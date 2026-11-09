using System.Numerics;
using System.Text;
using H5.Assets.Granny;

namespace H5.Assets.Model;

/// <summary>
/// A store of decoded assets that outlives the session (the browser build keeps it in IndexedDB, see
/// web/game/src/Web/WebDecodedCache.cs). Keys name the decoded thing (e.g. "clip/1/30/&lt;uid&gt;"); the store itself
/// separates installs (it prefixes the keys with the install's data fingerprint), so a changed pak never serves stale data.
/// </summary>
public interface IDecodedCache
{
    byte[]? Get(string key);
    void Put(string key, byte[] data);
}

/// <summary>
/// Binary form of the decoded skeletons and baked clips kept in an <see cref="IDecodedCache"/>. Floats are written as
/// their bits, so a clip read back is identical to the one that was baked.
/// </summary>
public static class DecodedCacheFormat
{
    /// <summary>Bump when the decoders or the baking change what they produce (old entries are then never read).</summary>
    public const int Version = 1;

    public static byte[] WriteSkeleton(SkeletonData s)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(s.Bones.Count);
            foreach (var b in s.Bones)
            {
                w.Write(b.Name); w.Write(b.Parent);
                W(w, b.Translation); W(w, b.Rotation); W(w, b.Scale);
                var m = b.InverseBind;
                foreach (var f in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) w.Write(f);
            }
        }
        return ms.ToArray();
    }

    public static SkeletonData ReadSkeleton(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var s = new SkeletonData();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var b = new Bone { Name = r.ReadString(), Parent = r.ReadInt32(), Translation = V3(r), Rotation = Q(r), Scale = V3(r) };
            var f = new float[16];
            for (int k = 0; k < 16; k++) f[k] = r.ReadSingle();
            b.InverseBind = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            s.Bones.Add(b);
        }
        return s;
    }

    /// <summary>A baked clip plus the Granny text events of its source animation.</summary>
    public static byte[] WriteClip(AnimationClip c, IReadOnlyList<GrannyTextEvent> events)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(c.Name); w.Write(c.Duration);
            w.Write(c.Tracks.Count);
            foreach (var t in c.Tracks)
            {
                w.Write(t.BoneName);
                W(w, t.TranslationTimes); WArr(w, t.Translations);
                W(w, t.RotationTimes); WArr(w, t.Rotations);
                W(w, t.ScaleTimes); WArr(w, t.Scales);
            }
            w.Write(c.Scalars.Count);
            foreach (var (name, s) in c.Scalars) { w.Write(name); W(w, s.Times); W(w, s.Values); }
            w.Write(events.Count);
            foreach (var e in events) { w.Write(e.Track); w.Write(e.Time); w.Write(e.Text); }
        }
        return ms.ToArray();
    }

    public static (AnimationClip Clip, List<GrannyTextEvent> Events) ReadClip(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var c = new AnimationClip { Name = r.ReadString(), Duration = r.ReadSingle() };
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var t = new BoneTrack { BoneName = r.ReadString() };
            t.TranslationTimes = Fs(r); t.Translations = RArr<Vector3>(r);
            t.RotationTimes = Fs(r); t.Rotations = RArr<Quaternion>(r);
            t.ScaleTimes = Fs(r); t.Scales = RArr<Vector3>(r);
            c.Tracks.Add(t);
        }
        int ns = r.ReadInt32();
        for (int i = 0; i < ns; i++) { var name = r.ReadString(); c.Scalars[name] = new ScalarTrack { Times = Fs(r), Values = Fs(r) }; }
        var events = new List<GrannyTextEvent>();
        int ne = r.ReadInt32();
        for (int i = 0; i < ne; i++) events.Add(new GrannyTextEvent(r.ReadString(), r.ReadSingle(), r.ReadString()));
        return (c, events);
    }

    static void W(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static void W(BinaryWriter w, Quaternion q) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
    static void W(BinaryWriter w, float[] a) => WArr(w, a);
    // Arrays of floats / float vectors as their raw little-endian bytes (count first): the same bytes as writing each
    // float in turn, in one call.
    static void WArr<T>(BinaryWriter w, T[] a) where T : unmanaged
    {
        w.Write(a.Length);
        if (BitConverter.IsLittleEndian) { w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())); return; }
        foreach (var f in System.Runtime.InteropServices.MemoryMarshal.Cast<T, float>(a.AsSpan())) w.Write(f);
    }
    static T[] RArr<T>(BinaryReader r) where T : unmanaged
    {
        var a = new T[r.ReadInt32()];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<T, float>(a.AsSpan());
        if (BitConverter.IsLittleEndian) { r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())); return a; }
        for (int i = 0; i < floats.Length; i++) floats[i] = r.ReadSingle();
        return a;
    }
    static Vector3 V3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    static Quaternion Q(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    static float[] Fs(BinaryReader r) => RArr<float>(r);
}
