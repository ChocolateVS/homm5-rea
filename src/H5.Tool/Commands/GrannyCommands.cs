using System.Diagnostics;
using System.Text;
using H5.Assets.Granny;

namespace H5.Tool.Commands;

static class GrannyCli
{
    /// <summary>Accepts a uid, a pak path or a file on disk.</summary>
    public static byte[]? Load(ToolContext ctx, string arg, out string resolved)
    {
        resolved = arg;
        if (File.Exists(arg)) return File.ReadAllBytes(arg);
        foreach (var dir in new[] { "", "bin/Skeletons/", "bin/animations/" })
            if (ctx.Fs.Exists(dir + arg)) { resolved = dir + arg; return ctx.Fs.ReadAllBytes(dir + arg); }
        return null;
    }
}

sealed class Gr2DumpCommand : ICommand
{
    public string Name => "gr2-dump";
    public string Help => "gr2-dump <uid|pakpath|file> [--types] [--depth N]  - print a Granny file's object tree";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        var data = GrannyCli.Load(ctx, args[0], out var resolved);
        if (data == null) { Console.WriteLine($"not found: {args[0]}"); return 1; }
        int depth = 4;
        int di = Array.IndexOf(args, "--depth");
        if (di >= 0 && di + 1 < args.Length) depth = int.Parse(args[di + 1]);

        var g = Gr2File.Read(data);
        Console.WriteLine($"{resolved}: format {g.FormatVersion}, tag 0x{g.Tag:X8}, {(g.Is64 ? 64 : 32)}-bit, {g.Sections.Count} sections");
        for (int i = 0; i < g.Sections.Count; i++)
        {
            var s = g.Sections[i];
            Console.WriteLine($"  section {i}: comp {s.Compression} {s.CompressedSize} -> {s.Size}, fixups {s.FixupCount}, marshal {s.MarshalCount}");
        }
        if (args.Contains("--types")) { Console.WriteLine(GrannyDebug.TypeTree(g.RootType)); }
        Console.WriteLine(GrannyDebug.ObjectTree(g.Root, depth));

        var anim = GrannyConvert.ReadAnimations(g);
        foreach (var a in anim)
            Console.WriteLine($"animation '{a.Name}' duration {a.Duration:F3}s tracks {a.Tracks.Count}");
        var skel = GrannyConvert.ReadSkeleton(g);
        if (skel != null)
            Console.WriteLine($"skeleton: {skel.Bones.Count} bones, root '{skel.Bones.FirstOrDefault()?.Name}'");
        return 0;
    }
}

sealed class Gr2StatsCommand : ICommand
{
    public string Name => "gr2-stats";
    public string Help => "gr2-stats [--formats]  - decode every skeleton and animation in the archives and report failures";

    public int Run(ToolContext ctx, string[] args)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();
        var curveFormats = new Dictionary<string, int>();
        int skelOk = 0, skelTotal = 0, animOk = 0, animTotal = 0, bones = 0, tracks = 0;
        var tags = new Dictionary<uint, int>();

        foreach (var path in ctx.Fs.Enumerate("bin/Skeletons/").Order())
        {
            skelTotal++;
            try
            {
                var g = Gr2File.Read(ctx.Fs.ReadAllBytes(path));
                tags[g.Tag] = tags.GetValueOrDefault(g.Tag) + 1;
                var s = GrannyConvert.ReadSkeleton(g) ?? throw new InvalidDataException("no skeleton");
                GrannyConvert.Validate(s);
                bones += s.Bones.Count;
                skelOk++;
            }
            catch (Exception e) { failures.Add($"{path}: {e.GetType().Name}: {e.Message}"); }
        }

        foreach (var path in ctx.Fs.Enumerate("bin/animations/").Order())
        {
            animTotal++;
            try
            {
                var g = Gr2File.Read(ctx.Fs.ReadAllBytes(path));
                tags[g.Tag] = tags.GetValueOrDefault(g.Tag) + 1;
                var clips = GrannyConvert.ReadAnimations(g, curveFormats);
                if (clips.Count == 0) throw new InvalidDataException("no animation");
                foreach (var c in clips) { GrannyConvert.Validate(c); tracks += c.Tracks.Count; }
                animOk++;
            }
            catch (Exception e) { failures.Add($"{path}: {e.GetType().Name}: {e.Message}"); }
        }

        Console.WriteLine($"skeletons:  {skelOk}/{skelTotal} ok ({bones} bones)");
        Console.WriteLine($"animations: {animOk}/{animTotal} ok ({tracks} tracks)");
        Console.WriteLine("tags: " + string.Join(", ", tags.Select(kv => $"0x{kv.Key:X8}={kv.Value}")));
        Console.WriteLine("curve formats: " + string.Join(", ", curveFormats.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var f in failures.Take(40)) Console.WriteLine("  FAIL " + f);
        if (failures.Count > 40) Console.WriteLine($"  ... {failures.Count - 40} more");
        Console.WriteLine($"time {sw.Elapsed.TotalSeconds:F1}s");
        return failures.Count == 0 ? 0 : 2;
    }
}

static class GrannyDebug
{
    public static string TypeTree(GrType t, int indent = 0, HashSet<GrType>? seen = null)
    {
        seen ??= new HashSet<GrType>();
        var sb = new StringBuilder();
        if (!seen.Add(t)) { sb.Append(' ', indent).AppendLine("(recursive)"); return sb.ToString(); }
        foreach (var m in t.Members)
        {
            sb.Append(' ', indent).AppendLine(m.ToString());
            if (m.Children != null) sb.Append(TypeTree(m.Children, indent + 2, seen));
        }
        seen.Remove(t);
        return sb.ToString();
    }

    public static string ObjectTree(object? v, int depth, int indent = 0)
    {
        var sb = new StringBuilder();
        Write(sb, v, depth, indent, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object? v, int depth, int indent, HashSet<object> seen)
    {
        switch (v)
        {
            case GrObject o:
                if (!seen.Add(o)) { sb.AppendLine("(cycle)"); return; }
                sb.AppendLine();
                foreach (var (k, fv) in o.Fields)
                {
                    sb.Append(' ', indent).Append(k).Append(": ");
                    if (depth <= 0 && fv is GrObject or IReadOnlyList<object?>) { sb.AppendLine("..."); continue; }
                    Write(sb, fv, depth - 1, indent + 2, seen);
                }
                seen.Remove(o);
                break;
            case float[] fa:
                sb.AppendLine("[" + string.Join(", ", fa.Take(16).Select(f => f.ToString("G6"))) + (fa.Length > 16 ? $", ... ({fa.Length})" : "") + "]");
                break;
            case IReadOnlyList<object?> list:
                if (list.Count > 0 && list[0] is not GrObject)
                {
                    sb.AppendLine($"({list.Count}) [" + string.Join(", ", list.Take(12)) + (list.Count > 12 ? ", ..." : "") + "]");
                    break;
                }
                sb.AppendLine($"array({list.Count})");
                for (int i = 0; i < Math.Min(list.Count, 3); i++)
                {
                    sb.Append(' ', indent).Append($"[{i}]");
                    Write(sb, list[i], depth - 1, indent + 2, seen);
                }
                if (list.Count > 3) sb.Append(' ', indent).AppendLine("...");
                break;
            case GrTransform t:
                sb.AppendLine($"T(flags {t.Flags} t=({t.Tx:G5},{t.Ty:G5},{t.Tz:G5}) r=({t.Rx:G5},{t.Ry:G5},{t.Rz:G5},{t.Rw:G5}) ss=[{string.Join(",", t.ScaleShear.Select(f => f.ToString("G4")))}])");
                break;
            case null: sb.AppendLine("null"); break;
            default: sb.AppendLine(v.ToString()); break;
        }
    }
}

sealed class Gr2CurveCommand : ICommand
{
    public string Name => "gr2-curve";
    public string Help => "gr2-curve <anim uid> <bone> [skeleton uid]  - print a track's curves, samples and bind pose";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine(Help); return 1; }
        var data = GrannyCli.Load(ctx, args[0], out _);
        if (data == null) { Console.WriteLine("not found"); return 1; }
        var a = GrannyConvert.ReadGrannyAnimations(Gr2File.Read(data))[0];
        Console.WriteLine($"duration {a.Duration} step {a.TimeStep} oversampling {a.Oversampling} accum {a.AccumulationFlags} loop {a.LoopTranslation}");
        foreach (var e in a.TextEvents) Console.WriteLine($"  text '{e.Track}' t={e.Time} '{e.Text}'");
        foreach (var (k, v) in a.VectorTracks) Console.WriteLine($"  vector '{k}' deg {v.Degree} dim {v.Dimension} knots [{string.Join(",", v.Knots.Take(8))}] ctrl [{string.Join(",", v.Controls.Take(8))}]");
        var t = a.TransformTracks.FirstOrDefault(x => x.Name == args[1]);
        if (t == null) { Console.WriteLine("tracks: " + string.Join(",", a.TransformTracks.Select(x => x.Name))); return 1; }
        foreach (var (n, c) in new[] { ("pos", t.Position), ("rot", t.Orientation), ("ss", t.ScaleShear) })
            Console.WriteLine($"{n}: deg {c.Degree} dim {c.Dimension} knots({c.Knots.Length}) [{string.Join(", ", c.Knots.Take(10).Select(f => f.ToString("G4")))}{(c.Knots.Length > 10 ? $" ... {c.Knots[^1]:G4}" : "")}]  controls[0..{Math.Min(c.Controls.Length, 8)}] [{string.Join(", ", c.Controls.Take(8).Select(f => f.ToString("G4")))}]");
        for (int i = 0; i <= 8; i++)
        {
            float time = a.Duration * i / 8;
            Console.WriteLine($"  t={time:F3} pos {t.SamplePosition(time)} rot {t.SampleOrientation(time)}");
        }
        if (args.Length > 2 && GrannyCli.Load(ctx, args[2], out _) is { } sd)
        {
            var b = GrannyConvert.ReadSkeleton(Gr2File.Read(sd))!.Bones.FirstOrDefault(x => x.Name == args[1]);
            if (b != null) Console.WriteLine($"bind: t {b.Translation} r {b.Rotation} s {b.Scale}");
        }
        return 0;
    }
}

sealed class Gr2CheckSetsCommand : ICommand
{
    public string Name => "gr2-check-sets";
    public string Help => "gr2-check-sets  - resolve every (Character) model+animset and cross-check tracks against skeletons";

    public int Run(ToolContext ctx, string[] args)
    {
        var xdb = new H5.Core.Xdb.XdbLoader(ctx.Fs);
        var loader = new H5.Assets.Model.AnimSetLoader(xdb);
        int chars = 0, okChars = 0, sets = 0, anims = 0, unmatchedTracks = 0, tracks = 0, knotPastEnd = 0;
        int emptyPosNonIdentityBind = 0, emptyRotNonIdentityBind = 0, emptySsNonIdentityBind = 0, emptyChecked = 0;
        var problems = new List<string>();
        var kinds = new Dictionary<string, int>();
        foreach (var path in ctx.Fs.Enumerate("").Where(p => p.EndsWith(".(Character).xdb", StringComparison.OrdinalIgnoreCase)).Order())
        {
            chars++;
            try
            {
                var (model, arena, adv) = loader.ReadCharacter(path);
                if (model == null || !ctx.Fs.Exists(model)) { problems.Add($"{path}: no model"); continue; }
                var skel = loader.LoadSkeletonForModel(model);
                if (skel == null) { problems.Add($"{path}: no skeleton for {model}"); continue; }
                var names = new HashSet<string>(skel.Bones.Select(b => b.Name));
                foreach (var set in new[] { arena, adv })
                {
                    if (set == null || !ctx.Fs.Exists(set)) continue;
                    sets++;
                    foreach (var (kind, a) in loader.LoadAnimSet(set))
                    {
                        anims++;
                        kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                        foreach (var t in a.Source.TransformTracks)
                        {
                            tracks++;
                            var bi = skel.IndexOf(t.Name);
                            if (bi < 0) { unmatchedTracks++; continue; }
                            var b = skel.Bones[bi];
                            foreach (var c in new[] { t.Position, t.Orientation, t.ScaleShear })
                                if (!c.IsEmpty && c.Knots[^1] > a.Source.Duration + 1e-3f) knotPastEnd++;
                            if (t.Position.IsEmpty) { emptyChecked++; if (b.Translation.Length() > 1e-4f) emptyPosNonIdentityBind++; }
                            if (t.Orientation.IsEmpty && MathF.Abs(MathF.Abs(b.Rotation.W) - 1) > 1e-4f) emptyRotNonIdentityBind++;
                            if (t.ScaleShear.IsEmpty && (b.Scale - System.Numerics.Vector3.One).Length() > 1e-4f) emptySsNonIdentityBind++;
                        }
                    }
                }
                okChars++;
            }
            catch (Exception e) { problems.Add($"{path}: {e.GetType().Name}: {e.Message}"); }
        }
        Console.WriteLine($"characters {okChars}/{chars}, animsets {sets}, animations {anims}, tracks {tracks} (unmatched {unmatchedTracks}), knots past duration {knotPastEnd}");
        Console.WriteLine($"empty channels whose bind pose is not identity: pos {emptyPosNonIdentityBind}/{emptyChecked}, rot {emptyRotNonIdentityBind}, scale {emptySsNonIdentityBind}");
        Console.WriteLine("kinds: " + string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(25).Select(k => $"{k.Key}={k.Value}")));
        foreach (var p in problems.Take(30)) Console.WriteLine("  " + p);
        if (problems.Count > 30) Console.WriteLine($"  ... {problems.Count - 30} more");
        return 0;
    }
}

static class SkinCli
{
    /// <summary>Loads model + skeleton and the per-part rigid attach bones (field 12) in MeshData part order.</summary>
    public static (H5.Assets.Model.ModelAsset Asset, int[] RigidBones) LoadSkinned(ToolContext ctx, H5.Assets.Model.AnimSetLoader anims, string modelPath)
    {
        var xdb = new H5.Core.Xdb.XdbLoader(ctx.Fs);
        var ml = new H5.Assets.Model.ModelLoader(xdb) { SkeletonProvider = anims.LoadSkeletonByUid };
        var asset = ml.Load(modelPath);
        var rigid = new List<int>();
        var root = xdb.Load(modelPath).Root!;
        var geom = xdb.Resolve(root.Element("Geometry"), H5.Core.IO.PakFileSystem.Normalize(modelPath));
        var uid = (string?)geom?.Element("uid");
        if (uid != null && ctx.Fs.TryReadAllBytes("bin/Geometries/" + uid, out var bin))
            foreach (var m in H5.Assets.Geometry.GeometryFile.Decode(bin).Meshes)
                foreach (var p in m.Parts) rigid.Add(p.IsSkinned ? -1 : p.AttachBone);
        return (asset, rigid.ToArray());
    }
}

sealed class Gr2PoseCommand : ICommand
{
    public string Name => "gr2-pose";
    public string Help => "gr2-pose <Character.xdb> <kind> <time|N%> <out.png> [yaw]  - render a skinned model mid-animation";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 4) { Console.WriteLine(Help); return 1; }
        var xdb = new H5.Core.Xdb.XdbLoader(ctx.Fs);
        var anims = new H5.Assets.Model.AnimSetLoader(xdb);
        var (model, arena, adv) = anims.ReadCharacter(args[0]);
        var (asset, rigid) = SkinCli.LoadSkinned(ctx, anims, model!);
        if (asset.Skeleton == null) { Console.WriteLine("no skeleton"); return 1; }
        var set = anims.LoadAnimSet(arena ?? adv!);
        if (!set.TryGetValue(args[1], out var a)) { Console.WriteLine("kinds: " + string.Join(",", set.Keys)); return 1; }
        float time = args[2].EndsWith('%')
            ? a.Clip.Duration * float.Parse(args[2].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100f
            : float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        float yaw = args.Length > 4 ? float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 30f;
        var world = H5.Assets.Granny.GrannyPose.AnimatedWorld(asset.Skeleton, a.Clip, time);
        var skin = H5.Assets.Granny.GrannyPose.SkinMatrices(asset.Skeleton, world);
        asset.Mesh = H5.Assets.Granny.GrannyPose.Deform(asset.Mesh, skin, pi => pi < rigid.Length ? rigid[pi] : -1);
        File.WriteAllBytes(args[3], H5.Assets.Textures.PngWriter.Encode(ExportMeshCommand.RenderPreview(ctx, asset, yaw)));
        Console.WriteLine($"{args[1]} at {time:F3}/{a.Clip.Duration:F3}s, events: {string.Join(", ", a.Events.Select(e => $"{e.Text}@{e.Time:F3}"))}; bounds {asset.Mesh.BoundsMin}..{asset.Mesh.BoundsMax}");
        return 0;
    }
}

sealed class Gr2SkinCheckCommand : ICommand
{
    public string Name => "gr2-skin-check";
    public string Help => "gr2-skin-check  - verify skin bone indices / inverse binds against skeletons for every (Character)";

    public int Run(ToolContext ctx, string[] args)
    {
        var xdb = new H5.Core.Xdb.XdbLoader(ctx.Fs);
        var anims = new H5.Assets.Model.AnimSetLoader(xdb);
        int models = 0, skinned = 0, outOfRange = 0, bindMismatch = 0, nearOk = 0, nearTotal = 0, rigidParts = 0, rigidOut = 0;
        double sumOwn = 0, sumOther = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ctx.Fs.Enumerate("").Where(p => p.EndsWith(".(Character).xdb", StringComparison.OrdinalIgnoreCase)).Order())
        {
            try
            {
                var (model, _, _) = anims.ReadCharacter(path);
                if (model == null || !ctx.Fs.Exists(model) || !seen.Add(model)) continue;
                var (asset, rigid) = SkinCli.LoadSkinned(ctx, anims, model);
                var s = asset.Skeleton;
                if (s == null) continue;
                models++;
                // Rebuilt bind world vs stored inverse bind.
                var bw = H5.Assets.Granny.GrannyPose.BindWorld(s);
                for (int i = 0; i < s.Bones.Count; i++)
                {
                    var prod = s.Bones[i].InverseBind * bw[i];
                    if (MaxDiff(prod, System.Numerics.Matrix4x4.Identity) > 2e-3f) { bindMismatch++; break; }
                }
                var joints = bw.Select(m => m.Translation).ToArray();
                var rnd = new Random(1);
                for (int pi = 0; pi < asset.Mesh.Parts.Count; pi++)
                {
                    var p = asset.Mesh.Parts[pi];
                    if (p.Skin == null)
                    {
                        if (pi < rigid.Length && rigid[pi] >= 0) { rigidParts++; if (rigid[pi] >= s.Bones.Count) rigidOut++; }
                        continue;
                    }
                    skinned++;
                    for (int v = 0; v < p.Positions.Length; v += 7)
                    {
                        var sk = p.Skin[v];
                        if (Math.Max(Math.Max(sk.B0, sk.B1), Math.Max(sk.B2, sk.B3)) >= s.Bones.Count) { outOfRange++; continue; }
                        int dom = sk.B0; float dw = sk.W0;
                        if (sk.W1 > dw) { dom = sk.B1; dw = sk.W1; }
                        if (sk.W2 > dw) { dom = sk.B2; dw = sk.W2; }
                        if (sk.W3 > dw) { dom = sk.B3; }
                        // Distance to the dominant bone's joint and to its parent (bones span joint→child),
                        // compared with a random bone of the same skeleton.
                        float own = Vector3Dist(p.Positions[v], joints[dom]);
                        int par = s.Bones[dom].Parent;
                        if (par >= 0) own = Math.Min(own, Vector3Dist(p.Positions[v], joints[par]));
                        float other = Vector3Dist(p.Positions[v], joints[rnd.Next(s.Bones.Count)]);
                        sumOwn += own; sumOther += other; nearTotal++;
                        if (own <= other) nearOk++;
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"  {path}: {e.GetType().Name}: {e.Message}"); }
        }
        Console.WriteLine($"models {models}, skinned parts {skinned}, rigid parts {rigidParts} (attach bone out of range {rigidOut}), skin indices out of range {outOfRange}");
        Console.WriteLine($"skeletons whose rebuilt bind world != inverse(InverseWorldTransform): {bindMismatch}");
        Console.WriteLine($"vertex nearer its dominant bone than a random bone: {nearOk}/{nearTotal} ({100.0 * nearOk / Math.Max(1, nearTotal):F1}%), mean dist own {sumOwn / Math.Max(1, nearTotal):F3} vs random {sumOther / Math.Max(1, nearTotal):F3}");
        return 0;
    }

    static float Vector3Dist(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Distance(a, b);

    static float MaxDiff(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
    {
        float m = 0;
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) m = Math.Max(m, Math.Abs(a[r, c] - b[r, c]));
        return m;
    }
}

sealed class Gr2ExportCommand : ICommand
{
    public string Name => "gr2-export";
    public string Help => "gr2-export <Character.xdb> <outDir> [kind,kind...]  - export a character to glTF with skeleton and animations";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine(Help); return 1; }
        var xdb = new H5.Core.Xdb.XdbLoader(ctx.Fs);
        var anims = new H5.Assets.Model.AnimSetLoader(xdb);
        var ml = anims.Install(new H5.Assets.Model.ModelLoader(xdb));
        var (model, arena, adv) = anims.ReadCharacter(args[0]);
        if (model == null) { Console.WriteLine("no model"); return 1; }
        var asset = ml.Load(model);
        var set = arena ?? adv;
        if (set != null)
            foreach (var (kind, clip) in ml.LoadAnimSet(set))
            {
                if (args.Length > 2 && !args[2].Split(',').Contains(kind)) continue;
                clip.Name = kind;
                asset.Animations[kind] = clip;
            }
        var name = Path.GetFileName(args[0]).Replace(".(Character).xdb", "");
        H5.Assets.Export.GltfExporter.Export(ctx.Fs, asset, args[1], name);
        Console.WriteLine($"{name}: {asset.Skeleton?.Bones.Count ?? 0} bones, animations [{string.Join(", ", asset.Animations.Keys)}] -> {Path.Combine(args[1], name + ".gltf")}");
        return 0;
    }
}
