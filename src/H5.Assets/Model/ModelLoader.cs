using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using H5.Assets.Geometry;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Assets.Model;

/// <summary>Geometry descriptor fields from a Geometry xdb element.</summary>
public sealed class GeometryDesc
{
    public string Uid = "";
    public Vector3 Size, Center;
    public List<int> MaterialQuantities = new();
    public List<string> MeshNames = new();
    public string BinPath => "bin/Geometries/" + Uid;
}

/// <summary>
/// Loads a "(Model)" xdb (materials + geometry + skeleton) into a <see cref="ModelAsset"/>.
/// Skeletons and animations are Granny data decoded by another component, supplied
/// through <see cref="SkeletonProvider"/> and <see cref="AnimationProvider"/>.
/// </summary>
public sealed class ModelLoader(XdbLoader xdb)
{
    /// <summary>Skeleton uid (bin/Skeletons/&lt;uid&gt;) → decoded skeleton, or null if unavailable.</summary>
    public Func<string, SkeletonData?>? SkeletonProvider { get; set; }

    /// <summary>Animation uid (bin/animations/&lt;uid&gt;) → decoded clip, or null if unavailable.</summary>
    public Func<string, AnimationClip?>? AnimationProvider { get; set; }

    PakFileSystem Fs => xdb.FileSystem;

    public ModelAsset Load(string modelXdbPath)
    {
        modelXdbPath = PakFileSystem.Normalize(modelXdbPath);
        var root = xdb.Load(modelXdbPath).Root ?? throw new InvalidDataException($"{modelXdbPath}: empty");
        var asset = new ModelAsset { SourcePath = modelXdbPath };

        foreach (var item in root.Element("Materials")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var (mat, matFile) = ResolveWithFile(item, modelXdbPath);
            asset.Materials.Add(mat != null ? ReadMaterial(mat, matFile) : new MaterialData());
        }

        var (geomEl, geomFile) = ResolveWithFile(root.Element("Geometry"), modelXdbPath);
        if (geomEl != null)
        {
            var desc = ReadGeometryDesc(geomEl);
            if (Fs.TryReadAllBytes(desc.BinPath, out var bin))
                asset.Mesh = GeometryConverter.ToMeshData(GeometryFile.Decode(bin), desc.MeshNames, desc.MaterialQuantities);
        }

        var (skelEl, _) = ResolveWithFile(root.Element("Skeleton"), modelXdbPath);
        var skelUid = (string?)skelEl?.Element("uid");
        if (!string.IsNullOrEmpty(skelUid) && SkeletonProvider != null)
            asset.Skeleton = SkeletonProvider(skelUid);
        return asset;
    }

    /// <summary>Loads the clips of an "(AnimSet)" xdb, keyed by Kind (idle00, move, attack00, ...).</summary>
    public Dictionary<string, AnimationClip> LoadAnimSet(string animSetXdbPath)
    {
        var result = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        if (AnimationProvider == null) return result;
        animSetXdbPath = PakFileSystem.Normalize(animSetXdbPath);
        var root = xdb.Load(animSetXdbPath).Root;
        foreach (var item in root?.Element("animations")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var kind = (string?)item.Element("Kind") ?? "";
            var (anim, _) = ResolveWithFile(item.Element("Anim"), animSetXdbPath);
            var uid = (string?)anim?.Element("uid");
            if (string.IsNullOrEmpty(uid)) continue;
            var clip = AnimationProvider(uid);
            if (clip == null) continue;
            if (string.IsNullOrEmpty(clip.Name)) clip.Name = kind;
            result[kind] = clip;
        }
        return result;
    }

    public static GeometryDesc ReadGeometryDesc(XElement g) => new()
    {
        Uid = ((string?)g.Element("uid") ?? "").Trim(),
        Size = ReadVec3(g.Element("Size")),
        Center = ReadVec3(g.Element("Center")),
        MaterialQuantities = g.Element("MaterialQuantities")?.Elements("Item").Select(e => (int)e).ToList() ?? new(),
        MeshNames = g.Element("MeshNames")?.Elements("Item").Select(e => (string)e).ToList() ?? new(),
    };

    MaterialData ReadMaterial(XElement m, string file) => new()
    {
        DiffuseTexture = TexturePath(m.Element("Texture"), file),
        BumpTexture = TexturePath(m.Element("Bump"), file),
        GlossTexture = TexturePath(m.Element("Gloss"), file),
        MirrorTexture = TexturePath(m.Element("Mirror"), file),
        AlphaMode = (string?)m.Element("AlphaMode") ?? "AM_OPAQUE",
        TwoSided = string.Equals((string?)m.Element("Is2Sided"), "true", StringComparison.OrdinalIgnoreCase),
        SpecFactor = ParseFloat((string?)m.Element("SpecFactor")),
        SpecColor = ReadVec3(m.Element("SpecColor")),
        LightingMode = (string?)m.Element("LightingMode") ?? "L_NORMAL",
    };

    /// <summary>Texture href → path of the .dds in the archives (via the Texture xdb's DestName).</summary>
    public string? TexturePath(XElement? texRef, string currentFile)
    {
        var href = (string?)texRef?.Attribute("href");
        if (string.IsNullOrEmpty(href)) return null;
        var r = XdbRef.Parse(href, currentFile);
        if (r.IsEmpty) return null;
        if (r.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return Fs.Exists(r.Path) ? r.Path : null;
        if (!Fs.Exists(r.Path)) return null;
        var tex = xdb.Load(r.Path).Root;
        var dest = (string?)tex?.Element("DestName")?.Attribute("href");
        if (string.IsNullOrEmpty(dest))
        {
            var guess = System.IO.Path.ChangeExtension(r.Path, ".dds");
            return Fs.Exists(guess) ? guess : null;
        }
        var ddsPath = XdbRef.Parse(dest, r.Path).Path;
        return Fs.Exists(ddsPath) ? ddsPath : null;
    }

    (XElement? Element, string File) ResolveWithFile(XElement? refEl, string currentFile)
    {
        if (refEl == null) return (null, currentFile);
        var href = (string?)refEl.Attribute("href");
        if (string.IsNullOrEmpty(href) || href.StartsWith("#n:inline")) return (xdb.Resolve(refEl, currentFile), currentFile);
        var r = XdbRef.Parse(href, currentFile);
        return (xdb.Resolve(refEl, currentFile), r.Path);
    }

    static Vector3 ReadVec3(XElement? e) => e == null ? default
        : new Vector3(ParseFloat((string?)e.Element("x")), ParseFloat((string?)e.Element("y")), ParseFloat((string?)e.Element("z")));

    static float ParseFloat(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
}
