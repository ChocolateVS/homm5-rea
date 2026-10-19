namespace H5.Tool.Commands;

/// <summary>
/// Diagnostic for locomotion clips: per-bone parent and translation drift over the clip, so it
/// is visible which bones carry the authored forward travel (see CharacterBuilder.AddInPlaceTrack).
/// </summary>
sealed class Gr2DriftCommand : ICommand
{
    public string Name => "gr2-drift";
    public string Help => "gr2-drift <Character.xdb> [kind] [--arena] [--strip out.png [--old]] | --all  - per-bone drift of a locomotion clip (default: adventure move); --strip renders 8 in-place frames (--old: body-bone-only extraction)";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        var xdb = new H5.Core.Xdb.XdbLoader(ctx.Fs);
        var anims = new H5.Assets.Model.AnimSetLoader(xdb);
        if (args[0] == "--all") return All(ctx, anims, args.Contains("--arena"));
        var (model, arena, adv) = anims.ReadCharacter(args[0]);
        string kind = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "move";
        var setPath = args.Contains("--arena") ? arena ?? adv : adv ?? arena;
        var skel = anims.LoadSkeletonForModel(model!);
        var set = anims.LoadAnimSet(setPath!);
        if (skel == null || !set.TryGetValue(kind, out var a)) { Console.WriteLine("kinds: " + string.Join(",", set.Keys)); return 1; }
        int si = Array.IndexOf(args, "--strip");
        if (si >= 0) return Strip(ctx, anims, model!, a, args[si + 1], args.Contains("--old"));
        Console.WriteLine($"model {model}\nset {setPath}\n{kind}: duration {a.Clip.Duration:F3} MovementSpeed {a.Entry.MovementSpeed} SpeedFactor {a.Entry.SpeedFactor} expected drift {a.Entry.MovementSpeed * a.Clip.Duration:F3}");
        foreach (var tr in a.Clip.Tracks)
        {
            int bi = skel.IndexOf(tr.BoneName);
            string parent = bi >= 0 && skel.Bones[bi].Parent >= 0 ? skel.Bones[skel.Bones[bi].Parent].Name : "-";
            var d = tr.Translations.Length > 1 ? tr.Translations[^1] - tr.Translations[0] : default;
            Console.WriteLine($"  {bi,3} {tr.BoneName,-28} parent {parent,-24} keys {tr.Translations.Length,3} drift ({d.X:F3},{d.Y:F3},{d.Z:F3})");
        }
        // Model-space travel of every bone over the clip (end world position minus start).
        var w0 = H5.Assets.Granny.GrannyPose.AnimatedWorld(skel, a.Clip, 0);
        var w1 = H5.Assets.Granny.GrannyPose.AnimatedWorld(skel, a.Clip, a.Clip.Duration);
        Console.WriteLine("model-space travel (bones moving more than 10% of the expected drift):");
        for (int i = 0; i < skel.Bones.Count; i++)
        {
            var d = w1[i].Translation - w0[i].Translation;
            if (d.Length() > a.Entry.MovementSpeed * a.Clip.Duration * 0.1f)
                Console.WriteLine($"  {i,3} {skel.Bones[i].Name,-28} ({d.X:F3},{d.Y:F3},{d.Z:F3})");
        }
        return 0;
    }

    /// <summary>
    /// Side view of 8 frames over one cycle with the in-place shift applied and a fixed camera, so
    /// anything still travelling (or snapping back at the loop) is visible. --old reproduces the
    /// former extraction that removed the drift from the best-matching bone only.
    /// </summary>
    static int Strip(ToolContext ctx, H5.Assets.Model.AnimSetLoader anims, string model, H5.Assets.Model.LoadedAnimation a, string outPng, bool old)
    {
        var (asset, rigid) = SkinCli.LoadSkinned(ctx, anims, model);
        var skel = asset.Skeleton!;
        var clip = a.Clip;
        var travel = H5.Assets.Granny.GrannyPose.LocomotionTravel(skel, clip, a.Entry.MovementSpeed) ?? default;
        string? oldBone = null;
        if (old)
        {
            float expected = a.Entry.MovementSpeed * clip.Duration, bestErr = float.MaxValue;
            foreach (var tr in clip.Tracks)
            {
                if (tr.Translations.Length < 2) continue;
                var d = tr.Translations[^1] - tr.Translations[0];
                float drift = MathF.Sqrt(d.X * d.X + d.Y * d.Y), err = MathF.Abs(drift - expected);
                if (drift > expected * 0.5f && err < bestErr) { oldBone = tr.BoneName; bestErr = err; }
            }
            var b = clip.Tracks.First(t => t.BoneName == oldBone);
            var total = b.Translations[^1] - b.Translations[0];
            float t0 = b.TranslationTimes[0], span = b.TranslationTimes[^1] - t0;
            for (int k = 0; k < b.Translations.Length; k++)
            {
                float f = (b.TranslationTimes[k] - t0) / span;
                b.Translations[k] -= new System.Numerics.Vector3(total.X * f, total.Y * f, 0);
            }
        }
        var bind = asset.Mesh;
        var lo = bind.BoundsMin - new System.Numerics.Vector3(2, 2, 0);
        var hi = bind.BoundsMax + new System.Numerics.Vector3(2, 2, 0.5f);
        const int n = 8, size = 320;
        var strip = new H5.Assets.Textures.RgbaImage(size * n, size);
        for (int i = 0; i < n; i++)
        {
            float time = clip.Duration * (i + 0.5f) / n;
            var world = H5.Assets.Granny.GrannyPose.AnimatedWorld(skel, clip, time);
            if (!old)
            {
                var shift = System.Numerics.Matrix4x4.CreateTranslation(-travel * (time / clip.Duration));
                for (int j = 0; j < world.Length; j++) world[j] *= shift;
            }
            var skin = H5.Assets.Granny.GrannyPose.SkinMatrices(skel, world);
            asset.Mesh = H5.Assets.Granny.GrannyPose.Deform(bind, skin, pi => pi < rigid.Length ? rigid[pi] : -1);
            asset.Mesh.BoundsMin = lo; asset.Mesh.BoundsMax = hi; // fixed camera
            var img = ExportMeshCommand.RenderPreview(ctx, asset, 90f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int sx = x * img.Width / size, sy = y * img.Height / size;
                    Array.Copy(img.Pixels, (sy * img.Width + sx) * 4, strip.Pixels, (y * strip.Width + i * size + x) * 4, 4);
                }
        }
        File.WriteAllBytes(outPng, H5.Assets.Textures.PngWriter.Encode(strip));
        Console.WriteLine($"{outPng}: {(old ? $"old extraction on {oldBone}" : $"rigid in-place shift {travel}")}");
        return 0;
    }

    /// <summary>Every (Character): move clips with more than one drifting bone (only one gets extracted).</summary>
    static int All(ToolContext ctx, H5.Assets.Model.AnimSetLoader anims, bool arenaSet)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int clips = 0, multi = 0;
        foreach (var path in ctx.Fs.Enumerate("").Where(p => p.EndsWith(".(Character).xdb", StringComparison.OrdinalIgnoreCase)).Order())
        {
            try
            {
                var (model, arena, adv) = anims.ReadCharacter(path);
                var setPath = arenaSet ? arena : adv;
                if (model == null || setPath == null || !seen.Add(model + "|" + setPath)) continue;
                var skel = anims.LoadSkeletonForModel(model);
                var set = anims.LoadAnimSet(setPath);
                if (skel == null || !set.TryGetValue("move", out var a) || a.Entry.MovementSpeed <= 0) continue;
                clips++;
                float expected = a.Entry.MovementSpeed * a.Clip.Duration;
                var drifting = new List<string>();
                foreach (var tr in a.Clip.Tracks)
                {
                    if (tr.Translations.Length < 2) continue;
                    var d = tr.Translations[^1] - tr.Translations[0];
                    float drift = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
                    if (drift > expected * 0.1f)
                    {
                        int bi = skel.IndexOf(tr.BoneName);
                        string parent = bi >= 0 && skel.Bones[bi].Parent >= 0 ? skel.Bones[skel.Bones[bi].Parent].Name : "-";
                        drifting.Add($"{tr.BoneName}<{parent}>={drift:F2}");
                    }
                }
                if (drifting.Count > 1) multi++;
                Console.WriteLine($"{(drifting.Count > 1 ? "MULTI " : "      ")}{path} exp {expected:F2}: {string.Join(" ", drifting)}");
            }
            catch (Exception e) { Console.WriteLine($"  {path}: {e.Message}"); }
        }
        Console.WriteLine($"move clips {clips}, with several drifting bones {multi}");
        return 0;
    }
}
