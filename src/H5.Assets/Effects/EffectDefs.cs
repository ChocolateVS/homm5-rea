using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using H5.Core.IO;
using H5.Core.Xdb;
using H5.Assets.Geometry;

namespace H5.Assets.Effects;

// Engine-neutral descriptions of the original effect system, loaded from xdb:
//   (Effect)          → particle, model and light instances + Duration
//   ParticleInstance  → a placed particle bake (bin/effects/<uid>) with timing/texture slots
//   ModelInstance     → an animated mesh (e.g. weapon trails) with a BasicSkelAnim
//   LightInstance     → an AnimLight (bin/Lights/<uid>)
//   (Shot)            → projectiles: missile model/effect or a chain of line effects
//   (SpellVisual)     → spell hit effect + sound + decal + hit delay
// Positions are kept in source space (Z-up).

public sealed class InstanceTiming
{
    public float Speed = 1f;
    /// <summary>Delay before the instance starts, in seconds.</summary>
    public float Offset;
    /// <summary>Repeat period in seconds (0 = no repeat). Called CycleLength on ModelInstance.</summary>
    public float EndCycle;
    /// <summary>Number of cycles; 0 means loop forever.</summary>
    public int CycleCount = 1;
}

public sealed class ParticleInstanceDef
{
    public string ParticlePath = "";
    public string? BakeUid;
    public Vector2 WrapSize;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    /// <summary>Multiplies particle sizes (engine size factor) and, by the data's evidence, positions.</summary>
    public float Scale = 1f;
    public InstanceTiming Timing = new();
    public Vector2 Pivot;
    /// <summary>16 slots; null entries are empty slots. Particles index this array directly.</summary>
    public string?[] Textures = new string?[16];
    public bool Lit;
    public bool LeaveParticlesWhereStarted;
    public int GlueToBone;
    public string? GlueToNamedBone;
    public int Priority;
}

public sealed class ModelInstanceDef
{
    public string ModelPath = "";
    public string? SkelAnimPath;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public float Scale = 1f;
    public InstanceTiming Timing = new();
    public int GlueToBone;
    public string? GlueToNamedBone;
}

public sealed class LightInstanceDef
{
    public string? LightUid;
    public string? Node;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public float Scale = 1f;
    public InstanceTiming Timing = new();
    public int GlueToBone;
}

public sealed class EffectDef
{
    public string Path = "";
    public float Duration;
    public bool WindAffected;
    public float WindPower = 1f;
    public List<ParticleInstanceDef> Particles = new();
    public List<ModelInstanceDef> Models = new();
    public List<LightInstanceDef> Lights = new();
}

public sealed class MissileShotDef
{
    public string? ModelPath;
    public string? EffectPath;
    /// <summary>World units per millisecond.</summary>
    public float Speed;
    /// <summary>Arc height as a fraction of the flight distance.</summary>
    public float Height;
    public float ForwardOffset, StartZOffset, RightOffset;
}

public sealed class LineShotDef
{
    public List<string> Effects = new();
    public int SkipSegmentsNumber;
    public float SegmentLength;
    public int StepDelayMs;
    public int EffectLifeTimeMs;
}

public sealed class ShotDef
{
    public string Path = "";
    public string? StartEffectPath;
    public bool RotateStartEffectToTarget;
    public MissileShotDef Missile = new();
    public LineShotDef Line = new();
    public int StrikeDelayMs;
    public int DelayBetweenChainsMs;
    public string? StrikeVisualPath;
}

public sealed class SpellVisualDef
{
    public string Path = "";
    public string? EffectPath;
    public string? SoundPath;
    public string? DecalPath;
    public float HitDelay;
    public float ParticleDuration;
}

/// <summary>Animated light from bin/Lights/&lt;uid&gt;.</summary>
public sealed class AnimLightData
{
    public float Fps = 30f;
    public int Type;
    public float Length;
    public List<(short Frame, Vector3 Position)> Position = new();
    public List<(short Frame, Vector3 Color)> Color = new();
    /// <summary>Third track; by the data's ranges this behaves as intensity in percent (see LightInstanceNode).</summary>
    public List<(short Frame, float Range)> Range = new();
}

public sealed class EffectLoader(XdbLoader xdb)
{
    readonly Dictionary<string, EffectDef?> _effects = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ParticleBake?> _bakes = new(StringComparer.OrdinalIgnoreCase);

    public XdbLoader Xdb => xdb;

    static float Fl(XElement? e, float def = 0f) =>
        e != null && float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
    static int In(XElement? e, int def = 0) =>
        e != null && int.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;
    static bool Bo(XElement? e) => string.Equals(e?.Value, "true", StringComparison.OrdinalIgnoreCase);
    static Vector3 V3(XElement? e) => e == null ? Vector3.Zero : new(Fl(e.Element("x")), Fl(e.Element("y")), Fl(e.Element("z")));
    static Vector2 V2(XElement? e) => e == null ? Vector2.Zero : new(Fl(e.Element("x")), Fl(e.Element("y")));
    static Quaternion Q(XElement? e) => e == null ? Quaternion.Identity
        : new(Fl(e.Element("x")), Fl(e.Element("y")), Fl(e.Element("z")), Fl(e.Element("w"), 1f));

    /// <summary>Resolves an href to a pak path (no pointer); null when empty.</summary>
    static string? PathOf(XElement? refEl, string currentFile)
    {
        var href = (string?)refEl?.Attribute("href");
        if (string.IsNullOrEmpty(href) || href.StartsWith("#n:inline")) return null;
        var r = XdbRef.Parse(href, currentFile);
        return r.IsEmpty ? null : r.Path;
    }

    static InstanceTiming Timing(XElement e, string cycleName = "EndCycle") => new()
    {
        Speed = Fl(e.Element("Speed"), 1f),
        Offset = Fl(e.Element("Offset")),
        EndCycle = Fl(e.Element(cycleName)),
        CycleCount = In(e.Element("CycleCount"), 1),
    };

    public EffectDef? LoadEffect(string pathOrHref)
    {
        var path = XdbRef.Parse(pathOrHref).Path;
        if (string.IsNullOrEmpty(path)) return null;
        lock (_effects)
            if (_effects.TryGetValue(path, out var cached)) return cached;
        EffectDef? def = null;
        if (TryLoadRoot(path) is { Name.LocalName: "Effect" } root)
            def = ReadEffect(root, path);
        lock (_effects) _effects[path] = def;
        return def;
    }

    EffectDef ReadEffect(XElement root, string path)
    {
        var def = new EffectDef
        {
            Path = path,
            Duration = Fl(root.Element("Duration")),
            WindAffected = Bo(root.Element("WindAffected")),
            WindPower = Fl(root.Element("WindPower"), 1f),
        };
        foreach (var item in root.Element("Instances")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (xdb.Resolve(item, path) is { Name.LocalName: "ParticleInstance" } pi)
                def.Particles.Add(ReadParticleInstance(pi, SourceOf(item, path)));
        foreach (var item in root.Element("Models")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (xdb.Resolve(item, path) is { Name.LocalName: "ModelInstance" } mi && SourceOf(item, path) is var mf)
                def.Models.Add(new ModelInstanceDef
                {
                    ModelPath = PathOf(mi.Element("Model"), mf) ?? "",
                    SkelAnimPath = PathOf(mi.Element("SkelAnim"), mf),
                    Position = V3(mi.Element("Position")),
                    Rotation = Q(mi.Element("Rotation")),
                    Scale = Fl(mi.Element("Scale"), 1f),
                    Timing = Timing(mi, "CycleLength"),
                    GlueToBone = In(mi.Element("GlueToBone")),
                    GlueToNamedBone = NullIfEmpty(mi.Element("GlueToNamedBone")?.Value),
                });
        foreach (var item in root.Element("Lights")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (xdb.Resolve(item, path) is { Name.LocalName: "LightInstance" } li)
            {
                var lf = SourceOf(item, path);
                var anim = xdb.Resolve(li.Element("Light"), lf);
                def.Lights.Add(new LightInstanceDef
                {
                    LightUid = NullIfEmpty(anim?.Element("uid")?.Value?.Trim()),
                    Node = NullIfEmpty(anim?.Element("SelectNode")?.Value),
                    Position = V3(li.Element("Position")),
                    Rotation = Q(li.Element("Rotation")),
                    Scale = Fl(li.Element("Scale"), 1f),
                    Timing = Timing(li),
                    GlueToBone = In(li.Element("GlueToBone")),
                });
            }
        return def;
    }

    /// <summary>
    /// The file an Item's object lives in: its own file for external refs, else the container.
    /// Relative hrefs inside the object resolve against that file.
    /// </summary>
    static string SourceOf(XElement item, string containerFile)
    {
        var href = (string?)item.Attribute("href");
        if (string.IsNullOrEmpty(href) || href.StartsWith("#")) return containerFile;
        var r = XdbRef.Parse(href, containerFile);
        return r.IsEmpty ? containerFile : r.Path;
    }

    ParticleInstanceDef ReadParticleInstance(XElement pi, string path)
    {
        var def = new ParticleInstanceDef
        {
            ParticlePath = PathOf(pi.Element("Particle"), path) ?? (((string?)pi.Element("Particle")?.Attribute("href"))?.StartsWith("#n:inline") == true ? path + "#inline" : ""),
            Position = V3(pi.Element("Position")),
            Rotation = Q(pi.Element("Rotation")),
            Scale = Fl(pi.Element("Scale"), 1f),
            Timing = Timing(pi),
            Pivot = V2(pi.Element("Pivot")),
            Lit = pi.Element("Light")?.Value == "L_LIT",
            LeaveParticlesWhereStarted = Bo(pi.Element("LeaveParticlesWhereStarted")),
            GlueToBone = In(pi.Element("GlueToBone")),
            GlueToNamedBone = NullIfEmpty(pi.Element("GlueToNamedBone")?.Value),
            Priority = In(pi.Element("Priority")),
        };
        int slot = 0;
        foreach (var t in pi.Element("Textures")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            if (slot >= 16) break;
            def.Textures[slot++] = PathOf(t, path);
        }
        var particleRef = pi.Element("Particle");
        bool inline = ((string?)particleRef?.Attribute("href"))?.StartsWith("#n:inline") == true;
        if ((inline || ParticlePathOk(def.ParticlePath)) && xdb.Resolve(particleRef, path) is { Name.LocalName: "Particle" } particle)
        {
            def.BakeUid = NullIfEmpty(particle.Element("uid")?.Value?.Trim());
            def.WrapSize = V2(particle.Element("WrapSize"));
        }
        return def;
    }

    public ParticleBake? LoadBake(string? uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        lock (_bakes)
            if (_bakes.TryGetValue(uid, out var cached)) return cached;
        ParticleBake? bake = xdb.FileSystem.TryReadAllBytes("bin/effects/" + uid, out var data) ? ParticleBake.Read(data) : null;
        lock (_bakes) _bakes[uid] = bake;
        return bake;
    }

    public AnimLightData? LoadLight(string? uid)
    {
        if (string.IsNullOrEmpty(uid) || !xdb.FileSystem.TryReadAllBytes("bin/Lights/" + uid, out var d)) return null;
        return ReadAnimLight(d);
    }

    /// <summary>
    /// bin/Lights blob: Nival chunk stream whose chunk 1 is the light: 1 fps, 2 type,
    /// 3 length (s), 4 position keys (u16 frame + vec3), 5 colour keys (u16 + RGB floats),
    /// 6 range keys (u16 + float).
    /// </summary>
    public static AnimLightData ReadAnimLight(ReadOnlySpan<byte> d)
    {
        var light = new AnimLightData();
        var top = NivalChunks.Read(d, 0, d.Length);
        if (NivalChunks.Find(top, 1) is not { } body) return light;
        var ch = NivalChunks.Read(d, body);
        if (NivalChunks.Find(ch, 1) is { Length: 4 } fps) light.Fps = BitConverter.ToSingle(d.Slice(fps.Offset, 4));
        if (NivalChunks.Find(ch, 2) is { Length: 4 } type) light.Type = BitConverter.ToInt32(d.Slice(type.Offset, 4));
        if (NivalChunks.Find(ch, 3) is { Length: 4 } len) light.Length = BitConverter.ToSingle(d.Slice(len.Offset, 4));
        foreach (var (id, stride) in new[] { (4, 14), (5, 14), (6, 6) })
        {
            if (NivalChunks.Find(ch, (byte)id) is not { } vec) continue;
            var inner = NivalChunks.Read(d, vec);
            if (NivalChunks.Find(inner, 2) is not { } pod) continue;
            var (count, off, length) = NivalChunks.PodVectorRaw(d, pod);
            for (int i = 0; i < count && (i + 1) * stride <= length; i++)
            {
                var e = d.Slice(off + i * stride, stride);
                short frame = BitConverter.ToInt16(e);
                if (id == 6) light.Range.Add((frame, BitConverter.ToSingle(e[2..])));
                else
                {
                    var v = new Vector3(BitConverter.ToSingle(e[2..]), BitConverter.ToSingle(e[6..]), BitConverter.ToSingle(e[10..]));
                    if (id == 4) light.Position.Add((frame, v)); else light.Color.Add((frame, v));
                }
            }
        }
        return light;
    }

    public ShotDef? LoadShot(string pathOrHref)
    {
        var path = XdbRef.Parse(pathOrHref).Path;
        if (string.IsNullOrEmpty(path) || !xdb.FileSystem.Exists(path)) return null;
        var root = xdb.Load(path).Root!;
        var shot = new ShotDef
        {
            Path = path,
            StartEffectPath = PathOf(root.Element("StartEffect"), path),
            RotateStartEffectToTarget = Bo(root.Element("RotateStartEffectToTarget")),
            StrikeDelayMs = Math.Max(0, In(root.Element("StrikeDelay"))),
            DelayBetweenChainsMs = Math.Max(0, In(root.Element("DelayBetweenChains"))),
            StrikeVisualPath = PathOf(root.Element("StrikeVisualEffect"), path),
        };
        if (root.Element("MissileShot") is { } m)
            shot.Missile = new MissileShotDef
            {
                ModelPath = PathOf(m.Element("Model"), path),
                EffectPath = PathOf(m.Element("Effect"), path),
                Speed = Fl(m.Element("Speed")),
                Height = Fl(m.Element("Height")),
                ForwardOffset = Fl(m.Element("ForwardOffset")),
                StartZOffset = Fl(m.Element("StartZOffset")),
                RightOffset = Fl(m.Element("RightOffset")),
            };
        if (root.Element("LineShot") is { } l)
        {
            shot.Line.SkipSegmentsNumber = In(l.Element("SkipSegmentsNumber"));
            shot.Line.SegmentLength = Fl(l.Element("SegmentLength"));
            shot.Line.StepDelayMs = In(l.Element("StepDelay"));
            shot.Line.EffectLifeTimeMs = In(l.Element("EffectLifeTime"));
            foreach (var e in l.Element("Effects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                if (PathOf(e, path) is { } p) shot.Line.Effects.Add(p);
        }
        return shot;
    }

    public SpellVisualDef? LoadSpellVisual(string pathOrHref)
    {
        var path = XdbRef.Parse(pathOrHref).Path;
        if (string.IsNullOrEmpty(path) || !xdb.FileSystem.Exists(path)) return null;
        var root = xdb.Load(path).Root!;
        return new SpellVisualDef
        {
            Path = path,
            EffectPath = PathOf(root.Element("Effect"), path),
            SoundPath = PathOf(root.Element("SoundEffect"), path),
            DecalPath = PathOf(root.Element("Decal"), path),
            HitDelay = Fl(root.Element("HitDelay")),
            ParticleDuration = Fl(root.Element("ParticleDuration")),
        };
    }

    /// <summary>The spell's (SpellVisual)s in order, from its "visuals" list.</summary>
    public List<SpellVisualDef> LoadSpellVisuals(string spellXdbPath)
    {
        var list = new List<SpellVisualDef>();
        spellXdbPath = PakFileSystem.Normalize(spellXdbPath);
        if (!xdb.FileSystem.Exists(spellXdbPath)) return list;
        foreach (var item in xdb.Load(spellXdbPath).Root?.Element("visuals")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (PathOf(item, spellXdbPath) is { } p && LoadSpellVisual(p) is { } v) list.Add(v);
        return list;
    }

    /// <summary>Root element, or null for missing or corrupt files (a few stock xdbs are zero-filled).</summary>
    public XElement? TryLoadRoot(string path)
    {
        if (!xdb.FileSystem.Exists(path)) return null;
        try { return xdb.Load(path).Root; }
        catch (System.Xml.XmlException) { return null; }
    }

    bool ParticlePathOk(string p) => p.Length > 0 && xdb.FileSystem.Exists(p) && TryLoadRoot(p) != null;

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
