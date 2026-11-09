using Godot;
using H5.Assets.Model;

namespace H5Reborn.Render;

/// <summary>
/// Builds an animated original character: (Character) xdb → model + Granny skeleton +
/// arena (or adventure-map) animation set. The result is a Node3D holding a Skeleton3D,
/// the skinned mesh, and an AnimationPlayer whose animations are named by the original
/// kinds (idle00, move, attack00, hit, death, …).
/// </summary>
public partial class Character : Node3D
{
    public AnimationPlayer Player { get; internal set; } = null!;
    public Skeleton3D Skeleton { get; internal set; } = null!;
    /// <summary>Animator-placed events per kind, e.g. ("action", 0.767) — when an attack lands.</summary>
    public Dictionary<string, List<(string Name, float Time)>> Events { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string kind) => Player.HasAnimation(Lib + kind);

    /// <summary>(BasicSkelAnim) SpeedFactor per kind: the clip's playback rate.</summary>
    public Dictionary<string, float> SpeedFactors { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Ground speed (world units per second) of locomotion kinds: (BasicSkelAnim) MovementSpeed ×
    /// SpeedFactor. The clip's own forward travel is extracted at build time, so the caller moves
    /// the node at this speed while the clip plays in place.
    /// </summary>
    public Dictionary<string, float> GroundSpeeds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public float SpeedFactor(string kind) => SpeedFactors.TryGetValue(kind, out var f) && f > 0 ? f : 1f;

    public void Play(string kind, bool loop = false) => Play(kind, loop, 0f);

    /// <summary>Plays a clip, cross-fading from the current one over <paramref name="blendSeconds"/> (0 = cut).</summary>
    public void Play(string kind, bool loop, float blendSeconds)
    {
        if (!Has(kind)) return;
        var mode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        var anim = Player.GetAnimation(Lib + kind);
        // Clips shared with other characters (CharacterBuilder.ShareScope) are never changed: the variant with the wanted
        // loop mode takes the clip's place in this character's library.
        if (SharedClips != null && SharedClips.TryGetValue(kind, out var variants))
        {
            if (anim.LoopMode != mode) Library!.AddAnimation(kind, variants.Get(mode));
        }
        else anim.LoopMode = mode;
        Player.SpeedScale = SpeedFactor(kind);
        Player.Play(Lib + kind, blendSeconds > 0 ? blendSeconds : 0);
        CurrentKind = kind;
    }

    /// <summary>The kind most recently started with <see cref="Play(string, bool, float)"/>.</summary>
    public string? CurrentKind { get; private set; }

    /// <summary>The (AnimSet) the clips came from (for each kind's Sound/Effect refs), if any.</summary>
    public string? AnimSetPath { get; internal set; }

    internal const string Lib = "h5/";

    /// <summary>This character's own animation library (its clips may be shared, see <see cref="SharedClips"/>).</summary>
    internal AnimationLibrary? Library;
    /// <summary>Per kind, the shared clip in its two loop modes (characters built inside a CharacterBuilder.ShareScope).</summary>
    internal Dictionary<string, CharacterBuilder.ClipVariants>? SharedClips;
}

public static class CharacterBuilder
{
    static AnimSetLoader? _anims;
    static AnimSetLoader Anims => _anims ??= new AnimSetLoader(Game.Instance.Xdb);

    /// <summary>Forgets the decoded skeletons and animations (decoded again on next use); see <see cref="Game.ReleaseUnused"/>.</summary>
    public static void ReleaseDecoded() { if (_shareDepth == 0) _anims = null; }

    public static Character Build(string characterXdbPath, bool adventureMap = false)
    {
        var (modelPath, arenaSet, advSet) = Anims.ReadCharacter(characterXdbPath);
        if (modelPath == null) throw new InvalidOperationException($"{characterXdbPath} has no model");
        var setPath = adventureMap ? advSet ?? arenaSet : arenaSet ?? advSet;
        return BuildFromModel(modelPath, setPath, System.IO.Path.GetFileNameWithoutExtension(characterXdbPath));
    }

    /// <summary>
    /// Builds an animated node straight from a (Model) and an (AnimSet) — how adventure-map
    /// objects reference their visuals (AdvMap*Shared Model/AnimSet), and how player-coloured
    /// hero variants (Character ColourModels) are drawn.
    /// </summary>
    public static Character BuildFromModel(string modelPath, string? setPath, string? name = null)
    {
        Template t;
        if (_shareDepth > 0)
        {
            var key = modelPath + "|" + setPath;
            if (!_templates.TryGetValue(key, out t!)) using (Dev.LoadTrace.Time("ch template")) _templates[key] = t = BuildTemplate(modelPath, setPath);
        }
        else t = BuildTemplate(modelPath, setPath);
        using var _ = Dev.LoadTrace.Time("ch instance");
        return Instantiate(t, name ?? System.IO.Path.GetFileNameWithoutExtension(modelPath), shared: _shareDepth > 0);
    }

    /// <summary>
    /// Everything a character of one model + animation set is built from: the decoded skeleton, the mesh, the skin and
    /// the converted clips. Inside a <see cref="ShareScope"/> one template serves every instance (a map with 300
    /// animated objects decoded and baked each set once per object before); each instance still gets its own nodes and
    /// its own animation library; the clips are shared and never changed (<see cref="ClipVariants"/>).
    /// </summary>
    sealed class Template
    {
        public required string? SetPath;
        public SkeletonData? Skeleton;
        public required ArrayMesh Mesh;
        public Skin? Skin;
        public readonly List<(string Kind, ClipVariants Anim, float SpeedFactor, float? GroundSpeed, List<(string Name, float Time)> Events)> Clips = new();
        public readonly Dictionary<string, ClipVariants> ByKind = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A converted clip as built (loop mode None) and, made on first use, its looping copy. Neither is changed afterwards.</summary>
    internal sealed class ClipVariants(Animation once)
    {
        public readonly Animation Once = once;
        Animation? _loop;
        public Animation Get(Animation.LoopModeEnum mode)
        {
            if (mode == Animation.LoopModeEnum.None) return Once;
            if (_loop == null) { _loop = (Animation)Once.Duplicate(); _loop.LoopMode = Animation.LoopModeEnum.Linear; }
            return _loop;
        }
    }

    static readonly Dictionary<string, Template> _templates = new();
    static int _shareDepth;

    /// <summary>
    /// While open, characters of the same model + animation set share their decoded data, mesh and skin (each still has its
    /// own nodes and clips); the shared data is dropped when the last scope closes. The result is the same as without.
    /// </summary>
    public static ShareToken ShareScope() { _shareDepth++; return new ShareToken(true); }

    public readonly struct ShareToken(bool open) : IDisposable
    {
        public void Dispose()
        {
            if (!open) return;
            if (--_shareDepth == 0) { _templates.Clear(); _animSets.Clear(); }
        }
    }

    static readonly Dictionary<string, Dictionary<string, LoadedAnimation>> _animSets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An animation set's decoded clips, once per <see cref="ShareScope"/> (read only by the builder).</summary>
    static Dictionary<string, LoadedAnimation> SharedAnimSet(string setPath)
    {
        if (!_animSets.TryGetValue(setPath, out var set)) _animSets[setPath] = set = Anims.LoadAnimSet(setPath);
        return set;
    }

    static Template BuildTemplate(string modelPath, string? setPath)
    {
        var asset = new ModelLoader(Game.Instance.Xdb).Load(modelPath);
        var skeleton = Anims.LoadSkeletonForModel(modelPath);
        var animations = setPath == null ? new() : _shareDepth > 0 ? SharedAnimSet(setPath) : Anims.LoadAnimSet(setPath);
        if (skeleton == null || skeleton.Bones.Count == 0)
            return new Template { SetPath = setPath, Mesh = ModelBuilder.BuildMesh(asset, skinned: false) };

        var t = new Template { SetPath = setPath, Skeleton = skeleton, Mesh = ModelBuilder.BuildMesh(asset, skinned: true) };
        // Skinning uses Granny's stored inverse bind matrices (not the inverse of the rest
        // pose, which differs for some skeletons — see docs/formats/granny.md).
        t.Skin = new Skin();
        for (int i = 0; i < skeleton.Bones.Count; i++)
            t.Skin.AddNamedBind(SafeBoneName(skeleton.Bones[i].Name, i), ModelBuilder.ToGodot(skeleton.Bones[i].InverseBind));
        foreach (var (kind, loaded) in animations)
        {
            float speed = loaded.Entry.MovementSpeed;
            float factor = loaded.Entry.SpeedFactor > 0 ? loaded.Entry.SpeedFactor : 1f;
            var travel = speed > 0 ? H5.Assets.Granny.GrannyPose.LocomotionTravel(skeleton, loaded.Clip, speed) : null;
            var anim = ToGodot(loaded.Clip, skeleton);
            AddInPlaceTrack(anim, travel, loaded.Clip.Duration);
            var v = new ClipVariants(anim);
            t.Clips.Add((kind, v, factor, speed > 0 ? speed * factor : null, loaded.Events.Select(e => (e.Text, e.Time)).ToList()));
            t.ByKind[kind] = v;
        }
        return t;
    }

    static Character Instantiate(Template t, string name, bool shared)
    {
        var node = new Character { Name = name, AnimSetPath = t.SetPath };
        if (t.Skeleton is not { } skeleton)
        {
            // Rigid model with no skeleton: static mesh, empty player so callers needn't special-case.
            node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = t.Mesh });
            node.Skeleton = new Skeleton3D { Name = "Skeleton3D" };
            node.AddChild(node.Skeleton);
            node.Player = new AnimationPlayer { Name = "AnimationPlayer" };
            node.AddChild(node.Player);
            return node;
        }

        var skel = new Skeleton3D { Name = "Skeleton3D" };
        foreach (var b in skeleton.Bones)
        {
            int i = skel.AddBone(SafeBoneName(b.Name, skel.GetBoneCount()));
            skel.SetBoneParent(i, b.Parent);
            skel.SetBoneRest(i, new Transform3D(new Basis(ModelBuilder.ToGodot(b.Rotation)).Scaled(ModelBuilder.ScaleToGodot(b.Scale)),
                ModelBuilder.ToGodot(b.Translation)));
        }
        skel.ResetBonePoses();
        node.AddChild(skel);
        node.Skeleton = skel;

        var mesh = new MeshInstance3D { Name = "Mesh", Mesh = t.Mesh, Skin = t.Skin };
        skel.AddChild(mesh);
        mesh.Skeleton = new NodePath("..");

        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        node.AddChild(player);
        player.RootNode = new NodePath("..");
        var lib = new AnimationLibrary();
        foreach (var (kind, anim, factor, ground, events) in t.Clips)
        {
            node.SpeedFactors[kind] = factor;
            if (ground is { } g) node.GroundSpeeds[kind] = g;
            lib.AddAnimation(kind, anim.Once);
            node.Events[kind] = shared ? new List<(string, float)>(events) : events;
        }
        player.AddAnimationLibrary(Character.Lib.TrimEnd('/'), lib);
        node.Library = lib;
        if (shared) node.SharedClips = t.ByKind;
        node.Player = player;
        if (node.Has("idle00")) node.Play("idle00", loop: true);
        return node;
    }

    /// <summary>
    /// Godot bone names may not be empty or contain ':' or '/'; some original skeletons use
    /// Maya namespaces ("Horse:Spine"). Map them consistently for bones, binds and tracks.
    /// </summary>
    static string SafeBoneName(string name, int index) =>
        string.IsNullOrEmpty(name) ? $"bone{index}" : name.Replace(':', '_').Replace('/', '_');

    /// <summary>
    /// Locomotion clips carry their forward travel in the skeleton (Footman move: Hips goes from
    /// y −0.13 to −4.69 over the 0.8 s cycle — exactly MovementSpeed 5.7 units/s), and Granny's
    /// root-motion fields are unused (RootMotion null, LoopTranslation 0; the exe samples with
    /// accumulation off: GrannySetTrackGroupAccumulation(control, 0, 0) at 0089313f). The node walks
    /// at MovementSpeed, so the cycle is played in place by shifting the whole skeleton back by the
    /// clip's linear model-space travel. The shift must be rigid: weapons, shields, banners and reins
    /// are root-level bones that travel exactly like the body; removing the drift from the body bone
    /// alone (as before) left them running ahead and snapping back once per cycle. Every clip gets
    /// the track (zero when in place) so cross-fades between clips blend it.
    /// </summary>
    static void AddInPlaceTrack(Animation anim, System.Numerics.Vector3? travel, float duration)
    {
        int t = anim.AddTrack(Animation.TrackType.Position3D);
        anim.TrackSetPath(t, new NodePath("Skeleton3D"));
        anim.PositionTrackInsertKey(t, 0, Vector3.Zero);
        if (travel is { } d && duration > 0)
            anim.PositionTrackInsertKey(t, duration, -ModelBuilder.ToGodot(d));
    }

    static Animation ToGodot(AnimationClip clip, SkeletonData skeleton)
    {
        var anim = new Animation { Length = Math.Max(clip.Duration, 1f / 30f) };
        var bones = new HashSet<string>(skeleton.Bones.Select(b => b.Name));
        var safe = skeleton.Bones.Select((b, i) => (b.Name, Safe: SafeBoneName(b.Name, i))).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Safe);
        foreach (var tr in clip.Tracks)
        {
            if (!bones.Contains(tr.BoneName)) continue;
            var path = new NodePath($"Skeleton3D:{safe[tr.BoneName]}");

            // A track channel with no keys is identity in Granny, not "keep rest pose",
            // so every channel of a present track gets at least one explicit key.
            int p = anim.AddTrack(Animation.TrackType.Position3D);
            anim.TrackSetPath(p, path);
            if (tr.Translations.Length == 0) anim.PositionTrackInsertKey(p, 0, Vector3.Zero);
            for (int k = 0; k < tr.Translations.Length; k++)
                anim.PositionTrackInsertKey(p, tr.TranslationTimes[k], ModelBuilder.ToGodot(tr.Translations[k]));

            int r = anim.AddTrack(Animation.TrackType.Rotation3D);
            anim.TrackSetPath(r, path);
            if (tr.Rotations.Length == 0) anim.RotationTrackInsertKey(r, 0, Quaternion.Identity);
            for (int k = 0; k < tr.Rotations.Length; k++)
                anim.RotationTrackInsertKey(r, tr.RotationTimes[k], ModelBuilder.ToGodot(tr.Rotations[k]).Normalized());

            int s = anim.AddTrack(Animation.TrackType.Scale3D);
            anim.TrackSetPath(s, path);
            if (tr.Scales.Length == 0) anim.ScaleTrackInsertKey(s, 0, Vector3.One);
            for (int k = 0; k < tr.Scales.Length; k++)
                anim.ScaleTrackInsertKey(s, tr.ScaleTimes[k], ModelBuilder.ScaleToGodot(tr.Scales[k]));
        }
        return anim;
    }
}
