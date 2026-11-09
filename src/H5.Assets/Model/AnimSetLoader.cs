using System.Collections.Concurrent;
using System.Xml.Linq;
using H5.Assets.Granny;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Assets.Model;

/// <summary>One entry of an (AnimSet): an animation "kind" such as idle00, move, attack00, hit, death.</summary>
public sealed class AnimSetEntry
{
    public string Kind = "";
    /// <summary>Pak path of the BasicSkelAnim descriptor.</summary>
    public string DescriptorPath = "";
    /// <summary>uid of the binary blob in bin/animations/.</summary>
    public string Uid = "";
    public float MovementSpeed;
    public float SpeedFactor = 1;
    public string? SoundHref;
    public string? EffectHref;
}

/// <summary>A loaded animation: baked clip plus the Granny text events of its source.</summary>
public sealed class LoadedAnimation
{
    public required AnimSetEntry Entry;
    public required AnimationClip Clip;
    /// <summary>Animator-placed text events, e.g. "action" at the moment an attack lands.</summary>
    public required IReadOnlyList<GrannyTextEvent> Events;
    /// <summary>The decoded Granny animation (curves, marks); null when the clip came from <see cref="AnimSetLoader.DecodedCache"/>.</summary>
    public GrannyAnimation? Source;
}

/// <summary>
/// Resolves Nival descriptors to Granny binaries: (Character) → (Model) skeleton uid,
/// (AnimSet) → kind → (BasicSkelAnim) uid → bin/animations/&lt;uid&gt;. Decoded skeletons and
/// animations are cached per uid. Thread-safe.
/// </summary>
public sealed class AnimSetLoader
{
    readonly XdbLoader _xdb;
    readonly PakFileSystem _fs;
    readonly ConcurrentDictionary<string, SkeletonData?> _skeletons = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, GrannyAnimation?> _animations = new(StringComparer.OrdinalIgnoreCase);

    public float SampleRate { get; init; } = GrannyConvert.DefaultSampleRate;

    /// <summary>
    /// Decoded skeletons and baked clips kept between sessions (the browser build's IndexedDB store; none on the
    /// desktop). A hit skips reading the Granny file from the pak, decoding it and baking the clip; the result is the same.
    /// </summary>
    public static IDecodedCache? DecodedCache { get; set; }

    public static string SkeletonCacheKey(string uid) => $"skel/{DecodedCacheFormat.Version}/{uid}";
    public string ClipCacheKey(string uid) => $"clip/{DecodedCacheFormat.Version}/{SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{uid}";

    /// <summary>The <see cref="DecodedCache"/> keys a model + animation set reads (to fetch them ahead).</summary>
    public IEnumerable<string> CacheKeys(string modelPath, string? animSetPath)
    {
        if (SkeletonUidForModel(modelPath) is { } su) yield return SkeletonCacheKey(su);
        if (animSetPath == null) yield break;
        foreach (var e in ReadAnimSet(animSetPath)) yield return ClipCacheKey(e.Uid);
    }

    public AnimSetLoader(XdbLoader xdb)
    {
        _xdb = xdb;
        _fs = xdb.FileSystem;
    }

    // ---- binaries by uid ---------------------------------------------------------------

    public SkeletonData? LoadSkeletonByUid(string uid) =>
        _skeletons.GetOrAdd(uid, u =>
        {
            var cache = DecodedCache;
            if (cache?.Get(SkeletonCacheKey(u)) is { } hit) return DecodedCacheFormat.ReadSkeleton(hit);
            if (!_fs.TryReadAllBytes("bin/Skeletons/" + u, out var d)) return null;
            var s = GrannyConvert.ReadSkeleton(Gr2File.Read(d));
            if (s != null) cache?.Put(SkeletonCacheKey(u), DecodedCacheFormat.WriteSkeleton(s));
            return s;
        });

    /// <summary>
    /// The baked clip of an animation uid (a new object on every call; clip name = Granny source name) and its text
    /// events, from the <see cref="DecodedCache"/> when it has them, else decoded, baked and stored there.
    /// </summary>
    public (AnimationClip Clip, IReadOnlyList<GrannyTextEvent> Events, GrannyAnimation? Source)? LoadBakedByUid(string uid)
    {
        var cache = DecodedCache;
        var key = cache == null ? null : ClipCacheKey(uid);
        if (key != null && cache!.Get(key) is { } hit)
        {
            var (c, ev) = DecodedCacheFormat.ReadClip(hit);
            return (c, ev, null);
        }
        var src = LoadAnimationByUid(uid);
        if (src == null) return null;
        var clip = GrannyConvert.ToClip(src, SampleRate);
        if (key != null) cache!.Put(key, DecodedCacheFormat.WriteClip(clip, src.TextEvents));
        return (clip, src.TextEvents, src);
    }

    public GrannyAnimation? LoadAnimationByUid(string uid) =>
        _animations.GetOrAdd(uid, u => _fs.TryReadAllBytes("bin/animations/" + u, out var d)
            ? GrannyConvert.ReadGrannyAnimations(Gr2File.Read(d)).FirstOrDefault() : null);

    /// <summary>Baked clip for an animation uid (clip name = Granny source name).</summary>
    public AnimationClip? LoadClipByUid(string uid) => LoadBakedByUid(uid)?.Clip;

    /// <summary>Plugs this loader into <see cref="ModelLoader"/>'s skeleton/animation hooks.</summary>
    public ModelLoader Install(ModelLoader modelLoader)
    {
        modelLoader.SkeletonProvider = LoadSkeletonByUid;
        modelLoader.AnimationProvider = LoadClipByUid;
        return modelLoader;
    }

    // ---- descriptors ------------------------------------------------------------------

    /// <summary>The skeleton uid of a (Model) descriptor (inline or referenced Skeleton).</summary>
    public string? SkeletonUidForModel(string modelPath)
    {
        modelPath = PakFileSystem.Normalize(modelPath);
        var root = _xdb.Load(modelPath).Root;
        var skel = _xdb.Resolve(root?.Element("Skeleton"), modelPath);
        return Uid(skel);
    }

    public SkeletonData? LoadSkeletonForModel(string modelPath)
    {
        var uid = SkeletonUidForModel(modelPath);
        return uid == null ? null : LoadSkeletonByUid(uid);
    }

    /// <summary>Reads the kind → descriptor table of an (AnimSet) xdb.</summary>
    public List<AnimSetEntry> ReadAnimSet(string animSetPath)
    {
        animSetPath = PakFileSystem.Normalize(animSetPath);
        var list = new List<AnimSetEntry>();
        var root = _xdb.Load(animSetPath).Root;
        foreach (var item in root?.Element("animations")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var anim = item.Element("Anim");
            var href = (string?)anim?.Attribute("href");
            var bsa = _xdb.Resolve(anim, animSetPath);
            var uid = Uid(bsa);
            if (uid == null) continue;
            var descriptor = string.IsNullOrEmpty(href) || href.StartsWith('#') ? animSetPath : XdbRef.Parse(href, animSetPath).Path;
            list.Add(new AnimSetEntry
            {
                Kind = (string?)item.Element("Kind") ?? "",
                DescriptorPath = descriptor,
                Uid = uid,
                MovementSpeed = ParseFloat(bsa!.Element("MovementSpeed")),
                SpeedFactor = bsa.Element("SpeedFactor") is { } sf ? ParseFloat(sf) : 1f,
                SoundHref = Href(bsa.Element("Sound")),
                EffectHref = Href(bsa.Element("Effect")),
            });
        }
        return list;
    }

    /// <summary>Loads every animation of an (AnimSet), keyed by kind (clip names are the kinds).</summary>
    public Dictionary<string, LoadedAnimation> LoadAnimSet(string animSetPath)
    {
        var result = new Dictionary<string, LoadedAnimation>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ReadAnimSet(animSetPath))
        {
            if (LoadBakedByUid(e.Uid) is not { } baked) continue;
            var (clip, events, source) = baked;
            clip.Name = e.Kind;
            result[e.Kind] = new LoadedAnimation { Entry = e, Clip = clip, Events = events, Source = source };
        }
        return result;
    }

    /// <summary>Paths referenced by a (Character) descriptor.</summary>
    public (string? Model, string? ArenaAnimSet, string? AdvMapAnimSet) ReadCharacter(string characterPath)
    {
        characterPath = PakFileSystem.Normalize(characterPath);
        var root = _xdb.Load(characterPath).Root;
        string? P(string name)
        {
            var h = Href(root?.Element(name));
            return h == null ? null : XdbRef.Parse(h, characterPath).Path;
        }
        return (P("Model"), P("ArenaAnimSet"), P("AdvMapAnimSet"));
    }

    /// <summary>
    /// Fills <see cref="ModelAsset.Skeleton"/> and <see cref="ModelAsset.Animations"/> for a model,
    /// using the given (AnimSet) (combat or adventure-map set). Existing entries are kept.
    /// </summary>
    public void Attach(ModelAsset asset, string modelPath, string? animSetPath)
    {
        asset.Skeleton ??= LoadSkeletonForModel(modelPath);
        if (animSetPath == null) return;
        foreach (var (kind, a) in LoadAnimSet(animSetPath))
            asset.Animations.TryAdd(kind, a.Clip);
    }

    static string? Uid(XElement? e)
    {
        var u = (string?)e?.Element("uid");
        return string.IsNullOrWhiteSpace(u) ? null : u.Trim();
    }

    static string? Href(XElement? e)
    {
        var h = (string?)e?.Attribute("href");
        return string.IsNullOrEmpty(h) ? null : h;
    }

    static float ParseFloat(XElement? e) =>
        e != null && float.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
}
