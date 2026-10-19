using System.Diagnostics;
using Godot;
using H5.Assets.Model;
using H5.Assets.Textures;
using H5.Core.IO;
using H5.Core.Xdb;
using SN = System.Numerics;

namespace H5WebSpike;

/// <summary>
/// Web spike scene: waits for the player to pick their paks, mounts them over Blob slices, loads one
/// creature (model + skeleton + idle animation + diffuse texture) through the game's own decoders and
/// renders it with the Compatibility (WebGL 2) renderer. Prints SPIKE_METRIC lines and SPIKE_DONE/SPIKE_FAIL.
/// The mesh/skeleton/animation conversion mirrors game/src/Render/CharacterBuilder + ModelBuilder (which
/// depend on the full Game singleton and are not linked into the spike).
/// </summary>
public partial class SpikeMain : Node3D
{
    const string DefaultCharacter = "Characters/Creatures/Haven/Footman.(Character).xdb";

    Label _hud = null!;
    Camera3D _camera = null!;
    bool _started;
    double _sinceShown = -1;
    int _framesSinceShown;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public override void _Ready()
    {
        _camera = new Camera3D { Position = new Vector3(0, 2, 6), Fov = 40 };
        AddChild(_camera);
        var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-45, 30, 0), LightEnergy = 1.2f };
        AddChild(sun);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.12f, 0.15f, 0.2f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
        };
        AddChild(new WorldEnvironment { Environment = env });
        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new Label { Position = new Vector2(10, 680), Text = "waiting for game data..." };
        layer.AddChild(_hud);
        if (OS.HasFeature("web") && H5PakJs.Param("master") is { Length: > 0 } master) _ = NetProbe.RunAsync(master);
        GD.Print($"SPIKE_READY renderer={RenderingServer.GetCurrentRenderingDriverName()} method={RenderingServer.GetCurrentRenderingMethod()} os={OS.GetName()}");
    }

    public override void _Process(double delta)
    {
        if (!_started && OS.HasFeature("web") && H5PakJs.Ready())
        {
            _started = true;
            _ = RunAsync();
        }
        if (_sinceShown >= 0)
        {
            _sinceShown += delta;
            _framesSinceShown++;
            if (_sinceShown >= 5 && _framesSinceShown > 0)
            {
                var fps = _framesSinceShown / _sinceShown;
                Metric("render", $"\"fps5s\":{fps:F1},\"engineFps\":{Engine.GetFramesPerSecond()},\"staticMemMB\":{OS.GetStaticMemoryUsage() / 1048576.0:F1},\"staticPeakMB\":{OS.GetStaticMemoryPeakUsage() / 1048576.0:F1},\"gcHeapMB\":{GC.GetTotalMemory(false) / 1048576.0:F1},\"gcCommittedMB\":{GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0:F1},\"videoMemMB\":{Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:F1},\"drawCalls\":{Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}");
                GD.Print("SPIKE_DONE");
                _sinceShown = -1;
            }
        }
    }

    static void Metric(string name, string json) => GD.Print($"SPIKE_METRIC {{\"name\":\"{name}\",{json}}}");

    async Task RunAsync()
    {
        try
        {
            var character = DefaultCharacter;
            var t0 = _clock.Elapsed.TotalMilliseconds;
            var fs = new PakFileSystem();
            foreach (var i in BrowserPaks.InLoadOrder())
            {
                var src = new BrowserBlobSource(i);
                var tm = _clock.Elapsed.TotalMilliseconds;
                await fs.MountAsync(src);
                GD.Print($"mounted {src.Name} ({src.Length / 1048576.0:F0} MB) in {_clock.Elapsed.TotalMilliseconds - tm:F0} ms");
            }
            var tMounted = _clock.Elapsed.TotalMilliseconds;
            Metric("mount", $"\"ms\":{tMounted - t0:F0},\"entries\":{fs.Paths.Count},\"reads\":{BrowserBlobSource.TotalReads},\"bytesReadMB\":{BrowserBlobSource.TotalBytesRead / 1048576.0:F1},\"gcHeapMB\":{GC.GetTotalMemory(false) / 1048576.0:F1}");
            H5PakJs.HidePicker();

            // The game's synchronous decoders, run against async-only archives: missing entries are
            // fetched and the (side-effect free) load is re-run with fresh loaders until it completes.
            int rounds = 0;
            var loaded = await fs.RunWithPrefetchAsync(() => { rounds++; return LoadCreature(fs, character); });
            var tDecoded = _clock.Elapsed.TotalMilliseconds;
            Metric("decode", $"\"ms\":{tDecoded - tMounted:F0},\"rounds\":{rounds},\"prefetchedKB\":{fs.PrefetchedBytes / 1024},\"reads\":{BrowserBlobSource.TotalReads},\"bytesReadMB\":{BrowserBlobSource.TotalBytesRead / 1048576.0:F1}");

            var node = Build(loaded);
            AddChild(node);
            Frame(node);
            var tBuilt = _clock.Elapsed.TotalMilliseconds;
            Metric("build", $"\"ms\":{tBuilt - tDecoded:F0},\"totalMs\":{tBuilt - t0:F0},\"bones\":{loaded.Skeleton?.Bones.Count ?? 0},\"parts\":{loaded.Model.Mesh.Parts.Count},\"vertices\":{loaded.Model.Mesh.Parts.Sum(p => p.Positions.Length)},\"idleTracks\":{loaded.Idle?.Tracks.Count ?? 0},\"textures\":{loaded.Textures.Count(t => t.Bytes != null)}");
            _hud.Text = $"{Path.GetFileName(character)}  |  mount {tMounted - t0:F0} ms, decode {tDecoded - tMounted:F0} ms ({rounds} rounds), build {tBuilt - tDecoded:F0} ms";
            fs.ClearPrefetched();
            _sinceShown = 0;
        }
        catch (Exception e)
        {
            GD.PrintErr("SPIKE_FAIL " + e);
            _hud.Text = "failed: " + e.Message;
        }
    }

    sealed record Loaded(ModelAsset Model, SkeletonData? Skeleton, AnimationClip? Idle, List<(string? Path, byte[]? Bytes)> Textures);

    /// <summary>Pure decode (no Godot objects): safe to re-run by RunWithPrefetchAsync.</summary>
    static Loaded LoadCreature(PakFileSystem fs, string characterPath)
    {
        var xdb = new XdbLoader(fs);
        var anims = new AnimSetLoader(xdb);
        var (modelPath, arenaSet, advSet) = anims.ReadCharacter(characterPath);
        if (modelPath == null) throw new InvalidDataException($"{characterPath}: no model");
        var model = anims.Install(new ModelLoader(xdb)).Load(modelPath);
        var skeleton = anims.LoadSkeletonForModel(modelPath);
        AnimationClip? idle = null;
        var set = arenaSet ?? advSet;
        if (set != null && anims.ReadAnimSet(set).FirstOrDefault(e => e.Kind.Equals("idle00", StringComparison.OrdinalIgnoreCase)) is { } entry)
            idle = anims.LoadClipByUid(entry.Uid);

        // One diffuse texture per material (Game.ResolveTexturePath: a .dds, or a (Texture) xdb's DestName).
        var textures = new List<(string?, byte[]?)>();
        foreach (var m in model.Materials)
        {
            string? texPath = null;
            if (m.DiffuseTexture != null)
            {
                var r = XdbRef.Parse(m.DiffuseTexture);
                texPath = r.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? r.Path
                    : fs.Exists(r.Path) && (string?)xdb.Load(r.Path).Root?.Element("DestName")?.Attribute("href") is { Length: > 0 } dest ? XdbRef.Parse(dest, r.Path).Path : null;
            }
            textures.Add((texPath, texPath != null && fs.TryReadAllBytes(texPath, out var b) ? b : null));
        }
        return new Loaded(model, skeleton, idle, textures);
    }

    // ---- Godot conversion (from game/src/Render) -------------------------------------------

    static Vector3 G(SN.Vector3 v) => new(v.X, v.Z, -v.Y);
    static Quaternion G(SN.Quaternion q) => new(q.X, q.Z, -q.Y, q.W);
    static Vector3 GS(SN.Vector3 s) => new(s.X, s.Z, s.Y);
    static Transform3D G(SN.Matrix4x4 m)
    {
        // ModelBuilder.ToGodot(Matrix4x4): G = C · Mᵀ · C⁻¹ (row-vector source, Z-up → Y-up).
        var x = G(new SN.Vector3(m.M11, m.M12, m.M13));
        var y = G(new SN.Vector3(m.M21, m.M22, m.M23));
        var z = G(new SN.Vector3(m.M31, m.M32, m.M33));
        return new Transform3D(new Basis(x, z, -y), G(new SN.Vector3(m.M41, m.M42, m.M43)));
    }

    Node3D Build(Loaded l)
    {
        var root = new Node3D { Name = "Creature" };
        var materials = new List<Material?>();
        int gpu = 0, soft = 0, kb = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (path, bytes) in l.Textures)
        {
            if (bytes == null) { materials.Add(null); continue; }
            kb += bytes.Length / 1024;
            // GPU path first: Godot's DDS loader keeps DXT compressed (WebGL needs WEBGL_compressed_texture_s3tc);
            // fall back to the game's software decoder (RGBA8) like Game.LoadDds does.
            var img = new Image();
            var dds = bytes;
            if (DdsMips.PadMipChain(ref dds) && img.LoadDdsFromBuffer(dds) == Error.Ok && img.IsCompressed()) gpu++;
            else
            {
                var rgba = DdsDecoder.Decode(bytes);
                img = Image.CreateFromData(rgba.Width, rgba.Height, false, Image.Format.Rgba8, rgba.Pixels);
                img.GenerateMipmaps();
                soft++;
            }
            materials.Add(new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img) });
        }
        Metric("texture", $"\"ms\":{sw.ElapsedMilliseconds},\"count\":{gpu + soft},\"gpuDxt\":{gpu},\"software\":{soft},\"ddsKB\":{kb},\"s3tc\":{RenderingServer.HasOsFeature("s3tc").ToString().ToLowerInvariant()}");
        var skel = new Skeleton3D { Name = "Skeleton3D" };
        root.AddChild(skel);
        var skeleton = l.Skeleton;
        bool skinned = skeleton is { Bones.Count: > 0 };
        if (skinned)
        {
            foreach (var b in skeleton!.Bones)
            {
                int i = skel.AddBone(Safe(b.Name, skel.GetBoneCount()));
                skel.SetBoneParent(i, b.Parent);
                skel.SetBoneRest(i, new Transform3D(new Basis(G(b.Rotation)).Scaled(GS(b.Scale)), G(b.Translation)));
            }
            skel.ResetBonePoses();
        }
        var mesh = new MeshInstance3D { Name = "Mesh", Mesh = BuildMesh(l.Model, skinned, materials) };
        if (skinned)
        {
            var skin = new Skin();
            for (int i = 0; i < skeleton!.Bones.Count; i++) skin.AddNamedBind(skel.GetBoneName(i), G(skeleton.Bones[i].InverseBind));
            mesh.Skin = skin;
            skel.AddChild(mesh);
            mesh.Skeleton = new NodePath("..");
        }
        else root.AddChild(mesh);

        if (skinned && l.Idle != null)
        {
            var player = new AnimationPlayer { Name = "AnimationPlayer" };
            root.AddChild(player);
            player.RootNode = new NodePath("..");
            var lib = new AnimationLibrary();
            var anim = ToGodot(l.Idle, skeleton!);
            anim.LoopMode = Animation.LoopModeEnum.Linear;
            lib.AddAnimation("idle00", anim);
            player.AddAnimationLibrary("h5", lib);
            player.Play("h5/idle00");
        }
        return root;
    }

    static string Safe(string name, int index) => string.IsNullOrEmpty(name) ? $"bone{index}" : name.Replace(':', '_').Replace('/', '_');

    static ArrayMesh BuildMesh(ModelAsset model, bool skinned, List<Material?> materials)
    {
        var mesh = new ArrayMesh();
        foreach (var part in model.Mesh.Parts)
        {
            if (part.Positions.Length == 0 || part.Indices.Length == 0) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = part.Positions.Select(G).ToArray();
            if (part.Normals.Length == part.Positions.Length) arrays[(int)Mesh.ArrayType.Normal] = part.Normals.Select(G).ToArray();
            if (part.UV0.Length == part.Positions.Length) arrays[(int)Mesh.ArrayType.TexUV] = part.UV0.Select(u => new Vector2(u.X, u.Y)).ToArray();
            if (skinned && part.Skin is { } skin && skin.Length == part.Positions.Length)
            {
                var bones = new int[skin.Length * 4];
                var weights = new float[skin.Length * 4];
                for (int i = 0; i < skin.Length; i++)
                {
                    var s = skin[i];
                    bones[i * 4] = s.W0 > 0 ? s.B0 : 0; bones[i * 4 + 1] = s.W1 > 0 ? s.B1 : 0;
                    bones[i * 4 + 2] = s.W2 > 0 ? s.B2 : 0; bones[i * 4 + 3] = s.W3 > 0 ? s.B3 : 0;
                    weights[i * 4] = s.W0; weights[i * 4 + 1] = s.W1; weights[i * 4 + 2] = s.W2; weights[i * 4 + 3] = s.W3;
                }
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }
            var idx = new int[part.Indices.Length];
            for (int t = 0; t + 2 < idx.Length; t += 3) { idx[t] = part.Indices[t]; idx[t + 1] = part.Indices[t + 2]; idx[t + 2] = part.Indices[t + 1]; }
            arrays[(int)Mesh.ArrayType.Index] = idx;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (part.MaterialIndex >= 0 && part.MaterialIndex < materials.Count && materials[part.MaterialIndex] is { } mat)
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
        }
        return mesh;
    }

    static Animation ToGodot(AnimationClip clip, SkeletonData skeleton)
    {
        var anim = new Animation { Length = Math.Max(clip.Duration, 1f / 30f) };
        var safe = skeleton.Bones.Select((b, i) => (b.Name, S: Safe(b.Name, i))).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().S);
        foreach (var tr in clip.Tracks)
        {
            if (!safe.TryGetValue(tr.BoneName, out var bone)) continue;
            var path = new NodePath($"Skeleton3D:{bone}");
            int p = anim.AddTrack(Animation.TrackType.Position3D); anim.TrackSetPath(p, path);
            if (tr.Translations.Length == 0) anim.PositionTrackInsertKey(p, 0, Vector3.Zero);
            for (int k = 0; k < tr.Translations.Length; k++) anim.PositionTrackInsertKey(p, tr.TranslationTimes[k], G(tr.Translations[k]));
            int r = anim.AddTrack(Animation.TrackType.Rotation3D); anim.TrackSetPath(r, path);
            if (tr.Rotations.Length == 0) anim.RotationTrackInsertKey(r, 0, Quaternion.Identity);
            for (int k = 0; k < tr.Rotations.Length; k++) anim.RotationTrackInsertKey(r, tr.RotationTimes[k], G(tr.Rotations[k]).Normalized());
            int s = anim.AddTrack(Animation.TrackType.Scale3D); anim.TrackSetPath(s, path);
            if (tr.Scales.Length == 0) anim.ScaleTrackInsertKey(s, 0, Vector3.One);
            for (int k = 0; k < tr.Scales.Length; k++) anim.ScaleTrackInsertKey(s, tr.ScaleTimes[k], GS(tr.Scales[k]));
        }
        return anim;
    }

    void Frame(Node3D node)
    {
        var aabb = new Aabb();
        bool first = true;
        foreach (var mi in node.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
        {
            var a = mi.GetAabb();
            aabb = first ? a : aabb.Merge(a);
            first = false;
        }
        var center = aabb.GetCenter();
        float r = Math.Max(aabb.Size.Length() * 0.5f, 0.5f);
        _camera.Position = center + new Vector3(0.6f, 0.35f, 1f).Normalized() * r * 3.2f;
        _camera.LookAt(center, Vector3.Up);
    }
}
