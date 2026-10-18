using Godot;
using H5.Assets.Effects;
using H5.Assets.Model;
using H5Reborn.Render;
using SN = System.Numerics;

namespace H5Reborn.Effects;

/// <summary>
/// Plays an original (Effect) xdb: its particle bakes, animated model instances and animated
/// lights, with the engine's timing rules. Frees itself once every instance has finished and
/// the effect's Duration has passed (unless looping forever, in which case call Stop()).
/// </summary>
public partial class EffectPlayer : Node3D
{
    public EffectDef Def { get; private set; } = null!;
    public float Elapsed { get; private set; }
    /// <summary>Speed multiplier for the whole effect (1 = original speed).</summary>
    public float TimeScale = 1f;
    /// <summary>Fade/intensity multiplier applied to particle colours (engine "SetFade").</summary>
    public float Fade = 1f;
    public bool AutoFree = true;
    public event Action<EffectPlayer>? Finished;

    readonly List<IEffectInstance> _instances = new();
    bool _stopping, _done;

    internal interface IEffectInstance
    {
        /// <summary>Advances to <paramref name="elapsed"/> seconds; returns true once finished.</summary>
        bool Update(float elapsed, EffectPlayer owner);
        void Stop(float elapsed);
    }

    /// <summary>Spawns an effect under <paramref name="parent"/> at the given local transform.</summary>
    public static EffectPlayer? Play(string effectHref, Node3D parent, Transform3D? local = null)
    {
        var def = EffectAssets.Loader.LoadEffect(effectHref);
        if (def == null) { GD.PushWarning($"[H5] effect not found: {effectHref}"); return null; }
        var player = new EffectPlayer { Def = def, Name = System.IO.Path.GetFileNameWithoutExtension(def.Path) };
        parent.AddChild(player);
        if (local is { } t) player.Transform = t;
        player.Build();
        return player;
    }

    /// <summary>Spawns an effect at a world position (added to <paramref name="world"/>).</summary>
    public static EffectPlayer? PlayAt(string effectHref, Node3D world, Vector3 position, float yaw = 0f) =>
        Play(effectHref, world, new Transform3D(new Basis(Vector3.Up, yaw), position));

    void Build()
    {
        foreach (var p in Def.Particles)
            if (EffectAssets.Loader.LoadBake(p.BakeUid) is { Particles.Length: > 0 } bake)
            {
                var node = new ParticleInstanceNode(p, bake);
                AddChild(node);
                _instances.Add(node);
            }
        foreach (var m in Def.Models)
        {
            var node = ModelInstanceNode.Create(m);
            if (node == null) continue;
            AddChild(node);
            _instances.Add(node);
        }
        foreach (var l in Def.Lights)
            if (EffectAssets.Loader.LoadLight(l.LightUid) is { } data)
            {
                var node = new LightInstanceNode(l, data);
                AddChild(node);
                _instances.Add(node);
            }
        Update(0f);
    }

    /// <summary>Stops emitting: running particles finish, looping instances end.</summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        foreach (var i in _instances) i.Stop(Elapsed);
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        Elapsed += (float)delta * TimeScale;
        Update(Elapsed);
    }

    /// <summary>Jumps to <paramref name="time"/> seconds (for previews; set TimeScale = 0 to hold).</summary>
    public void Seek(float time)
    {
        Elapsed = time;
        AutoFree = false;
        Update(time);
    }

    void Update(float elapsed)
    {
        bool all = true;
        foreach (var i in _instances) all &= i.Update(elapsed, this);
        if (all && (elapsed >= Def.Duration || _stopping || _instances.Count == 0) && elapsed > 0f)
        {
            _done = true;
            Finished?.Invoke(this);
            if (AutoFree) QueueFree();
        }
    }

    /// <summary>Source-space row-vector placement → Godot world as a System.Numerics matrix.</summary>
    internal static SN.Matrix4x4 PlacementToWorld(SN.Vector3 position, SN.Quaternion rotation, float scale, Transform3D global)
    {
        // Scale applies to the bake's positions as well as its sizes: creature hit bakes reach
        // ~2.7 units and are authored with Scale 0.5-0.7 to fit a single cell.
        var src = SN.Matrix4x4.CreateScale(scale) * SN.Matrix4x4.CreateFromQuaternion(rotation) * SN.Matrix4x4.CreateTranslation(position);
        // Z-up source (x, y, z) → Godot (x, z, -y).
        var c = new SN.Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var b = global.Basis;
        var g = new SN.Matrix4x4(
            b.Column0.X, b.Column0.Y, b.Column0.Z, 0,
            b.Column1.X, b.Column1.Y, b.Column1.Z, 0,
            b.Column2.X, b.Column2.Y, b.Column2.Z, 0,
            global.Origin.X, global.Origin.Y, global.Origin.Z, 1);
        return src * c * g;
    }
}

/// <summary>A particle bake drawn as camera-facing quads rebuilt every frame (like the original).</summary>
internal partial class ParticleInstanceNode : MeshInstance3D, EffectPlayer.IEffectInstance
{
    readonly ParticleInstanceDef _def;
    readonly ParticleBake _bake;
    readonly EffectAssets.Atlas? _atlas;
    readonly ImmediateMesh _mesh = new();
    readonly ShaderMaterial _material;
    readonly List<ParticleQuad> _quads = new();
    float _stopFrame = -1f;
    int _cycleOverride = -1;

    public ParticleInstanceNode(ParticleInstanceDef def, ParticleBake bake)
    {
        _def = def;
        _bake = bake;
        _atlas = EffectAssets.GetAtlas(def.Textures);
        _material = new ShaderMaterial { Shader = EffectAssets.ParticleShader };
        if (_atlas != null) _material.SetShaderParameter("atlas", _atlas.Texture);
        Mesh = _mesh;
        CastShadow = ShadowCastingSetting.Off;
        // Quads are built in world space each frame.
        TopLevel = true;
        Name = System.IO.Path.GetFileNameWithoutExtension(def.ParticlePath);
    }

    public void Stop(float elapsed)
    {
        // Newest cycle stops spawning; no new cycles start.
        var cycles = ParticleSystem.Cycles(_bake, _def.Timing, elapsed, out _);
        _stopFrame = cycles.Count > 0 ? cycles[0].Frame : 0f;
        _cycleOverride = Math.Max(1, cycles.Count > 0 ? cycles[0].CycleIndex : 1);
    }

    public bool Update(float elapsed, EffectPlayer owner)
    {
        _mesh.ClearSurfaces();
        var cycles = ParticleSystem.Cycles(_bake, _def.Timing, elapsed, out bool finished, _cycleOverride);
        if (cycles.Count == 0 || _atlas == null) return finished || _atlas == null;
        var cam = GetViewport()?.GetCamera3D();
        if (cam == null) return finished;

        GlobalTransform = Transform3D.Identity;
        var world = EffectPlayer.PlacementToWorld(_def.Position, _def.Rotation, _def.Scale, owner.GlobalTransform);
        var cb = cam.GlobalBasis;
        SN.Vector3 V(Vector3 v) => new(v.X, v.Y, v.Z);
        SN.Vector3 right = V(cb.X), up = V(cb.Y), fwd = V(-cb.Z);
        SN.Matrix4x4.Invert(world, out var inv);
        var camLocal = SN.Vector3.Transform(V(cam.GlobalPosition), inv);

        _quads.Clear();
        float intensity = owner.Fade; // L_LIT instances would also scale by scene lighting
        ParticleSystem.BuildQuads(_bake, _def, cycles, world, right, up, fwd, camLocal, intensity, _quads, _stopFrame, _def.Textures.Length);
        // Quads on empty texture slots draw nothing; drop them first so an all-empty frame never
        // opens a surface (ImmediateMesh logs "No vertices were added" for empty surfaces).
        _quads.RemoveAll(q => _atlas.Slots[q.TextureSlot] == null);
        if (_quads.Count == 0) return finished;
        // Back to front.
        _quads.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _material);
        foreach (var q in _quads)
        {
            var uv = _atlas.Slots[q.TextureSlot]!.Value;
            var col = new Color(((q.Color >> 16) & 0xff) / 255f, ((q.Color >> 8) & 0xff) / 255f, (q.Color & 0xff) / 255f, (q.Color >> 24) / 255f);
            void Vtx(SN.Vector3 p, float u, float v) { _mesh.SurfaceSetColor(col); _mesh.SurfaceSetUV(new Vector2(u, v)); _mesh.SurfaceAddVertex(new Vector3(p.X, p.Y, p.Z)); }
            // Corners: 0 bottom-left, 1 bottom-right, 2 top-right, 3 top-left.
            Vtx(q.C0, uv.U0, uv.VBottom); Vtx(q.C1, uv.U1, uv.VBottom); Vtx(q.C2, uv.U1, uv.VTop);
            Vtx(q.C0, uv.U0, uv.VBottom); Vtx(q.C2, uv.U1, uv.VTop); Vtx(q.C3, uv.U0, uv.VTop);
        }
        _mesh.SurfaceEnd();
        return finished;
    }
}

/// <summary>An animated mesh effect (weapon trails, glows) with its BasicSkelAnim.</summary>
internal partial class ModelInstanceNode : Node3D, EffectPlayer.IEffectInstance
{
    ModelInstanceDef _def = null!;
    AnimationPlayer? _player;
    Animation? _anim;
    bool _stopped;
    // Animated per-mesh transparency (weapon trails flash in and fade out this way).
    AnimationClip? _clip;
    SurfaceOpacity? _opacity;
    MeshInstance3D? _mesh;
    float[] _opacityBuf = Array.Empty<float>();

    static AnimSetLoader? _anims;
    static AnimSetLoader Anims => _anims ??= new AnimSetLoader(Game.Instance.Xdb);

    public static ModelInstanceNode? Create(ModelInstanceDef def)
    {
        if (string.IsNullOrEmpty(def.ModelPath) || !Game.Instance.Fs.Exists(def.ModelPath)) return null;
        ModelAsset asset;
        try { asset = new ModelLoader(Game.Instance.Xdb).Load(def.ModelPath); }
        catch (Exception e) { GD.PushWarning($"[H5] effect model {def.ModelPath}: {e.Message}"); return null; }

        var node = new ModelInstanceNode { _def = def, Name = System.IO.Path.GetFileNameWithoutExtension(def.ModelPath) };
        var src = SN.Matrix4x4.CreateFromQuaternion(def.Rotation) * SN.Matrix4x4.CreateTranslation(def.Position);
        node.Transform = ModelBuilder.ToGodot(src).ScaledLocal(Vector3.One * def.Scale);

        var skeleton = Anims.LoadSkeletonForModel(def.ModelPath);
        AnimationClip? clip = null;
        if (def.SkelAnimPath != null && Game.Instance.Xdb.Load(def.SkelAnimPath).Root?.Element("uid")?.Value?.Trim() is { Length: > 0 } uid)
            clip = Anims.LoadClipByUid(uid);

        var materials = EffectMaterials.ForModel(asset);
        if (skeleton is { Bones.Count: > 0 } && clip != null)
        {
            var skel = new Skeleton3D { Name = "Skeleton3D" };
            var names = new string[skeleton.Bones.Count];
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                var b = skeleton.Bones[i];
                names[i] = SafeName(b.Name, i);
                int bi = skel.AddBone(names[i]);
                skel.SetBoneParent(bi, b.Parent);
                skel.SetBoneRest(bi, new Transform3D(new Basis(ModelBuilder.ToGodot(b.Rotation)).Scaled(ModelBuilder.ScaleToGodot(b.Scale)), ModelBuilder.ToGodot(b.Translation)));
            }
            skel.ResetBonePoses();
            node.AddChild(skel);
            var skin = new Skin();
            for (int i = 0; i < skeleton.Bones.Count; i++) skin.AddNamedBind(names[i], ModelBuilder.ToGodot(skeleton.Bones[i].InverseBind));
            var mesh = new MeshInstance3D { Name = "Mesh", Mesh = ModelBuilder.BuildMesh(asset, skinned: true), Skin = skin, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            EffectMaterials.Apply(mesh, asset, materials);
            skel.AddChild(mesh);
            mesh.Skeleton = new NodePath("..");

            node._anim = ToGodot(clip, skeleton, names);
            node._player = new AnimationPlayer { Name = "AnimationPlayer" };
            node.AddChild(node._player);
            node._player.RootNode = new NodePath("..");
            var lib = new AnimationLibrary();
            lib.AddAnimation("fx", node._anim);
            node._player.AddAnimationLibrary("", lib);
            // Driven manually from Update so timing follows the effect clock exactly.
            node._player.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            node._player.Play("fx");
            node.SetupOpacity(asset, mesh, clip);
        }
        else
        {
            var mesh = new MeshInstance3D { Name = "Mesh", Mesh = ModelBuilder.BuildMesh(asset, skinned: false), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            EffectMaterials.Apply(mesh, asset, materials);
            node.AddChild(mesh);
        }
        node.Visible = false;
        return node;
    }

    public void Stop(float elapsed) => _stopped = true;

    public bool Update(float elapsed, EffectPlayer owner)
    {
        var t = _def.Timing;
        float local = (elapsed - t.Offset) * t.Speed;
        float length = _anim != null ? (float)_anim.Length : 0f;
        if (local < 0f) { Visible = false; return false; }
        float period = t.EndCycle > 0f ? t.EndCycle : length;
        int cycle = period > 0f ? (int)(local / period) : 0;
        bool finished = _stopped || (t.CycleCount != 0 && (period <= 0f ? local > 0f : cycle >= t.CycleCount));
        Visible = !finished;
        if (finished) return true;
        if (_player != null && _anim != null)
        {
            float animTime = Math.Min(local - cycle * period, length);
            _player.Seek(animTime, update: true);
            ApplyOpacity(animTime);
        }
        return false;
    }

    void SetupOpacity(ModelAsset asset, MeshInstance3D mesh, AnimationClip clip)
    {
        if (!SurfaceOpacity.Animates(clip)) return;
        _clip = clip;
        _mesh = mesh;
        _opacity = new SurfaceOpacity(asset);
        _opacityBuf = new float[_opacity.SurfaceCount];
        // Each surface gets its own material so meshes can fade independently.
        for (int s = 0; s < _opacity.SurfaceCount && s < mesh.GetSurfaceOverrideMaterialCount(); s++)
        {
            var m = mesh.GetSurfaceOverrideMaterial(s) ?? mesh.Mesh.SurfaceGetMaterial(s);
            if (m != null) mesh.SetSurfaceOverrideMaterial(s, (Material)m.Duplicate());
        }
        ApplyOpacity(0f);
    }

    void ApplyOpacity(float t)
    {
        if (_opacity == null || _clip == null || _mesh == null) return;
        _opacity.Evaluate(_clip, t, _opacityBuf);
        for (int s = 0; s < _opacityBuf.Length; s++)
            switch (_mesh.GetSurfaceOverrideMaterial(s))
            {
                case ShaderMaterial sm: sm.SetShaderParameter("opacity", _opacityBuf[s]); break;
                case BaseMaterial3D bm:
                    bm.Transparency = _opacityBuf[s] < 0.999f ? BaseMaterial3D.TransparencyEnum.Alpha : bm.Transparency;
                    bm.AlbedoColor = new Color(bm.AlbedoColor, _opacityBuf[s]);
                    break;
            }
    }

    static string SafeName(string name, int index) =>
        string.IsNullOrEmpty(name) ? $"bone{index}" : name.Replace(':', '_').Replace('/', '_');

    static Animation ToGodot(AnimationClip clip, SkeletonData skeleton, string[] names)
    {
        var anim = new Animation { Length = Math.Max(clip.Duration, 1f / 30f) };
        var byName = new Dictionary<string, string>();
        for (int i = 0; i < skeleton.Bones.Count; i++) byName.TryAdd(skeleton.Bones[i].Name, names[i]);
        foreach (var tr in clip.Tracks)
        {
            if (!byName.TryGetValue(tr.BoneName, out var bone)) continue;
            var path = new NodePath($"Skeleton3D:{bone}");
            int p = anim.AddTrack(Animation.TrackType.Position3D); anim.TrackSetPath(p, path);
            if (tr.Translations.Length == 0) anim.PositionTrackInsertKey(p, 0, Vector3.Zero);
            for (int k = 0; k < tr.Translations.Length; k++) anim.PositionTrackInsertKey(p, tr.TranslationTimes[k], ModelBuilder.ToGodot(tr.Translations[k]));
            int r = anim.AddTrack(Animation.TrackType.Rotation3D); anim.TrackSetPath(r, path);
            if (tr.Rotations.Length == 0) anim.RotationTrackInsertKey(r, 0, Quaternion.Identity);
            for (int k = 0; k < tr.Rotations.Length; k++) anim.RotationTrackInsertKey(r, tr.RotationTimes[k], ModelBuilder.ToGodot(tr.Rotations[k]).Normalized());
            int s = anim.AddTrack(Animation.TrackType.Scale3D); anim.TrackSetPath(s, path);
            if (tr.Scales.Length == 0) anim.ScaleTrackInsertKey(s, 0, Vector3.One);
            for (int k = 0; k < tr.Scales.Length; k++) anim.ScaleTrackInsertKey(s, tr.ScaleTimes[k], ModelBuilder.ScaleToGodot(tr.Scales[k]));
        }
        return anim;
    }
}

/// <summary>
/// An AnimLight: an omni light following colour/intensity/position keyframes.
/// Interpretation inferred from the data (the exe's light code wasn't traced): the third track
/// is intensity in percent (fireball peaks at 99.5, Armageddon ramps to 300), positions are in
/// Maya's Y-up space (Armageddon descends from height 15 to 4), and the reach is a fixed
/// radius scaled by the instance Scale.
/// </summary>
internal partial class LightInstanceNode : OmniLight3D, EffectPlayer.IEffectInstance
{
    const float Reach = 8f;
    readonly LightInstanceDef _def;
    readonly AnimLightData _data;
    bool _stopped;

    public LightInstanceNode(LightInstanceDef def, AnimLightData data)
    {
        _def = def;
        _data = data;
        ShadowEnabled = false;
        OmniAttenuation = 1f;
        OmniRange = Reach * Math.Max(0.01f, def.Scale);
        Visible = false;
    }

    public void Stop(float elapsed) => _stopped = true;

    public bool Update(float elapsed, EffectPlayer owner)
    {
        var t = _def.Timing;
        float local = (elapsed - t.Offset) * t.Speed;
        if (local < 0f) { Visible = false; return false; }
        float length = _data.Length;
        float period = t.EndCycle > 0f ? t.EndCycle : length;
        if (period > 0f && t.CycleCount != 1)
        {
            int cycle = (int)(local / period);
            if (t.CycleCount != 0 && cycle >= t.CycleCount) local = length + 1f;
            else local -= cycle * period;
        }
        bool finished = _stopped || local >= length;
        Visible = !finished;
        if (finished) return true;
        float frame = local * _data.Fps;
        var c = Sample(_data.Color, frame);
        LightColor = new Color(c.X, c.Y, c.Z);
        LightEnergy = _data.Range.Count > 0 ? Sample(_data.Range, frame) / 100f : 1f;
        var p = _data.Position.Count > 0 ? Sample(_data.Position, frame) : SN.Vector3.Zero;
        Position = new Vector3(p.X, p.Y, p.Z) * _def.Scale + ModelBuilder.ToGodot(_def.Position);
        return false;
    }

    static SN.Vector3 Sample(List<(short Frame, SN.Vector3 V)> keys, float frame)
    {
        if (keys.Count == 0) return SN.Vector3.One;
        if (frame <= keys[0].Frame) return keys[0].V;
        for (int i = 1; i < keys.Count; i++)
            if (frame <= keys[i].Frame)
            {
                float f = (frame - keys[i - 1].Frame) / Math.Max(1, keys[i].Frame - keys[i - 1].Frame);
                return SN.Vector3.Lerp(keys[i - 1].V, keys[i].V, f);
            }
        return keys[^1].V;
    }

    static float Sample(List<(short Frame, float V)> keys, float frame)
    {
        if (keys.Count == 0) return 10f;
        if (frame <= keys[0].Frame) return keys[0].V;
        for (int i = 1; i < keys.Count; i++)
            if (frame <= keys[i].Frame)
            {
                float f = (frame - keys[i - 1].Frame) / Math.Max(1, keys[i].Frame - keys[i - 1].Frame);
                return keys[i - 1].V + (keys[i].V - keys[i - 1].V) * f;
            }
        return keys[^1].V;
    }
}
