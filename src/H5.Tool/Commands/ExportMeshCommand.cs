using System.Numerics;
using System.Text;
using System.Xml.Linq;
using H5.Assets.Export;
using H5.Assets.Geometry;
using H5.Assets.Model;
using H5.Assets.Textures;
using H5.Core.Xdb;

namespace H5.Tool.Commands;

sealed class ExportMeshCommand : ICommand
{
    public string Name => "export-mesh";
    public string Help => "export-mesh <ModelXdbPath> <outDir>  - export a (Model) xdb to glTF 2.0 (+ preview.png)";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine(Help); return 1; }
        var loader = new ModelLoader(new XdbLoader(ctx.Fs));
        var asset = loader.Load(args[0]);
        var name = Path.GetFileNameWithoutExtension(args[0]).Replace(".(Model)", "");
        GltfExporter.Export(ctx.Fs, asset, args[1], name);
        var png = Path.Combine(args[1], name + "_preview.png");
        File.WriteAllBytes(png, PngWriter.Encode(RenderPreview(ctx, asset)));
        Console.WriteLine($"{asset.Mesh.Parts.Count} parts, {asset.Mesh.Parts.Sum(p => p.Indices.Length / 3)} triangles, {asset.Materials.Count} materials, bounds {asset.Mesh.BoundsMin}..{asset.Mesh.BoundsMax}");
        Console.WriteLine($"wrote {Path.Combine(args[1], name + ".gltf")} and {png}");
        return 0;
    }

    internal static RgbaImage RenderPreview(ToolContext ctx, ModelAsset asset, float yaw = 30f)
    {
        var textures = asset.Materials.Select(m =>
        {
            if (m.DiffuseTexture == null || !ctx.Fs.TryReadAllBytes(m.DiffuseTexture, out var dds)) return null;
            try { return DdsDecoder.Decode(dds); } catch (NotSupportedException) { return null; }
        }).ToList();
        return PreviewRenderer.Render(asset.Mesh, textures, 512, yaw);
    }
}

sealed class RenderMeshCommand : ICommand
{
    public string Name => "render-mesh";
    public string Help => "render-mesh <ModelXdbPath> <out.png> [yawDegrees]  - software-render a model in bind pose";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine(Help); return 1; }
        var asset = new ModelLoader(new XdbLoader(ctx.Fs)).Load(args[0]);
        float yaw = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 30f;
        File.WriteAllBytes(args[1], PngWriter.Encode(ExportMeshCommand.RenderPreview(ctx, asset, yaw)));
        return 0;
    }
}

sealed class GeomStatsCommand : ICommand
{
    public string Name => "geom-stats";
    public string Help => "geom-stats  - decode every bin/Geometries blob and cross-check against xdb descriptors";

    public int Run(ToolContext ctx, string[] args)
    {
        var fs = ctx.Fs;
        bool verbose = args.Contains("-v");
        var blobs = fs.Enumerate("bin/Geometries/").ToList();
        var descs = FindGeometryDescs(ctx);
        int ok = 0, fail = 0, parts = 0, skinned = 0, tris = 0, matMismatch = 0, meshMismatch = 0;
        int boundsChecked = 0, boundsOk = 0;
        var failures = new List<string>();
        var worst = new List<(float Err, string Uid)>();
        foreach (var path in blobs)
        {
            var uid = path[(path.LastIndexOf('/') + 1)..];
            try
            {
                var g = GeometryFile.Decode(fs.ReadAllBytes(path));
                ok++;
                var md = GeometryConverter.ToMeshData(g);
                parts += md.Parts.Count;
                skinned += g.Meshes.SelectMany(m => m.Parts).Count(p => p.IsSkinned);
                tris += md.Parts.Sum(p => p.Indices.Length / 3);
                if (descs.TryGetValue(uid, out var d))
                {
                    if (d.MaterialQuantities.Count > 0 && d.MaterialQuantities.Count == g.Meshes.Count
                        && !d.MaterialQuantities.Select((q, i) => q == g.Meshes[i].Parts.Count).All(x => x))
                    {
                        matMismatch++;
                        if (verbose) Console.WriteLine($"  matq {uid}: xdb [{string.Join(",", d.MaterialQuantities)}] blob [{string.Join(",", g.Meshes.Select(m => m.Parts.Count))}]");
                    }
                    if (d.MeshNames.Count > 0 && d.MeshNames.Count != g.Meshes.Count)
                    {
                        meshMismatch++;
                        if (verbose) Console.WriteLine($"  meshes {uid}: xdb {d.MeshNames.Count} blob {g.Meshes.Count}");
                    }
                    if (md.Parts.Count > 0 && d.Size != Vector3.Zero)
                    {
                        boundsChecked++;
                        var size = md.BoundsMax - md.BoundsMin;
                        var center = (md.BoundsMax + md.BoundsMin) * 0.5f;
                        float err = MathF.Max((size - d.Size).Length(), (center - d.Center).Length()) / MathF.Max(0.01f, d.Size.Length());
                        if (err < 0.01f) boundsOk++;
                        worst.Add((err, uid));
                    }
                }
            }
            catch (Exception e)
            {
                fail++;
                if (failures.Count < 20) failures.Add($"{uid}: {e.Message}");
            }
        }
        Console.WriteLine($"geometries: {blobs.Count}, decoded {ok}, failed {fail}");
        Console.WriteLine($"parts {parts} (skinned {skinned}), triangles {tris}");
        Console.WriteLine($"xdb descriptors found: {descs.Count}; material-quantity mismatches {matMismatch}, mesh-count mismatches {meshMismatch}");
        Console.WriteLine($"bounds within 1% of xdb Size/Center: {boundsOk}/{boundsChecked}");
        foreach (var w in worst.OrderByDescending(w => w.Err).Take(args.Contains("-v") ? 15 : 0))
            Console.WriteLine($"  bounds err {w.Err:F3} {w.Uid} xdb size {descs[w.Uid].Size} center {descs[w.Uid].Center} decoded {Bounds(fs, w.Uid)}");
        foreach (var f in failures) Console.WriteLine("  FAIL " + f);
        return fail == 0 ? 0 : 2;
    }

    static string Bounds(H5.Core.IO.PakFileSystem fs, string uid)
    {
        var md = GeometryConverter.ToMeshData(GeometryFile.Decode(fs.ReadAllBytes("bin/Geometries/" + uid)));
        return $"size {md.BoundsMax - md.BoundsMin} center {(md.BoundsMax + md.BoundsMin) * 0.5f} parts {md.Parts.Count} verts {md.Parts.Sum(p => p.Positions.Length)}";
    }

    /// <summary>Scans every xdb for Geometry elements (inline or standalone) and maps uid → descriptor.</summary>
    internal static Dictionary<string, GeometryDesc> FindGeometryDescs(ToolContext ctx)
    {
        var result = new Dictionary<string, GeometryDesc>(StringComparer.OrdinalIgnoreCase);
        var marker = Encoding.ASCII.GetBytes("<Geometry");
        foreach (var path in ctx.Fs.Paths.Where(p => p.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var bytes = ctx.Fs.ReadAllBytes(path);
            if (bytes.AsSpan().IndexOf(marker) < 0) continue;
            XDocument doc;
            try { doc = XDocument.Load(new MemoryStream(bytes)); } catch { continue; }
            foreach (var g in doc.Descendants("Geometry"))
            {
                if (g.Element("uid") == null) continue;
                var d = ModelLoader.ReadGeometryDesc(g);
                if (d.Uid.Length > 0) result.TryAdd(d.Uid, d);
            }
        }
        return result;
    }
}
