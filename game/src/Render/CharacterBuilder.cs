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
        var anim = Player.GetAnimation(Lib + kind);
        anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        Player.SpeedScale = SpeedFactor(kind);
        Player.Play(Lib + kind, blendSeconds > 0 ? blendSeconds : 0);
        CurrentKind = kind;
    }

    /// <summary>The kind most recently started with <see cref="Play(string, bool, float)"/>.</summary>
    public string? CurrentKind { get; private set; }

    /// <summary>The (AnimSet) the clips came from (for each kind's Sound/Effect refs), if any.</summary>
    public string? AnimSetPath { get; internal set; }

    internal const string Lib = "h5/";
}

public static class CharacterBuilder
{
    static AnimSetLoader? _anims;
    static AnimSetLoader Anims => _anims ??= new AnimSetLoader(Game.Instance.Xdb);

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
        var asset = new ModelLoader(Game.Instance.Xdb).Load(modelPath);
        var skeleton = Anims.LoadSkeletonForModel(modelPath);
        var animations = setPath != null ? Anims.LoadAnimSet(setPath) : new();

        var node = new Character { Name = name ?? System.IO.Path.GetFileNameWithoutExtension(modelPath), AnimSetPath = setPath };
        if (skeleton == null || skeleton.Bones.Count == 0)
        {
            // Rigid model with no skeleton: static mesh, empty player so callers needn't special-case.
            node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = ModelBuilder.BuildMesh(asset, skinned: false) });
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

        // Skinning uses Granny's stored inverse bind matrices (not the inverse of the rest
        // pose, which differs for some skeletons — see docs/formats/granny.md).
        var skin = new Skin();
        for (int i = 0; i < skeleton.Bones.Count; i++)
            skin.AddNamedBind(skel.GetBoneName(i), ModelBuilder.ToGodot(skeleton.Bones[i].InverseBind));

        var mesh = new MeshInstance3D { Name = "Mesh", Mesh = ModelBuilder.BuildMesh(asset, skinned: true), Skin = skin };
        skel.AddChild(mesh);
        mesh.Skeleton = new NodePath("..");

        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        node.AddChild(player);
        player.RootNode = new NodePath("..");
        var lib = new AnimationLibrary();
        foreach (var (kind, loaded) in animations)
        {
            float speed = loaded.Entry.MovementSpeed;
            node.SpeedFactors[kind] = loaded.Entry.SpeedFactor > 0 ? loaded.Entry.SpeedFactor : 1f;
            if (speed > 0)
            {
                ExtractRootMotion(loaded.Clip, speed);
                node.GroundSpeeds[kind] = speed * node.SpeedFactors[kind];
            }
            lib.AddAnimation(kind, ToGodot(loaded.Clip, skeleton));
            node.Events[kind] = loaded.Events.Select(e => (e.Text, e.Time)).ToList();
        }
        player.AddAnimationLibrary(Character.Lib.TrimEnd('/'), lib);
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
    /// Locomotion clips carry their forward travel in a body bone (Footman move: Hips goes from
    /// y −0.13 to −4.69 over the 0.8 s cycle — exactly MovementSpeed 5.7 units/s), and Granny's
    /// root-motion fields are unused (RootMotion null, LoopTranslation 0). Played as-is on a node
    /// that also moves along the path, the body runs ahead and snaps back every loop. The original
    /// moves the creature at MovementSpeed and plays the cycle in place, so the bone whose
    /// horizontal drift matches MovementSpeed × duration has that linear drift removed.
    /// </summary>
    static void ExtractRootMotion(AnimationClip clip, float movementSpeed)
    {
        if (clip.Duration <= 0) return;
        float expected = movementSpeed * clip.Duration;
        BoneTrack? best = null;
        float bestErr = float.MaxValue;
        foreach (var tr in clip.Tracks)
        {
            if (tr.Translations.Length < 2) continue;
            var d = tr.Translations[^1] - tr.Translations[0];
            float drift = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            float err = MathF.Abs(drift - expected);
            if (drift > expected * 0.5f && err < bestErr) { best = tr; bestErr = err; }
        }
        if (best == null) return;
        var first = best.Translations[0];
        var total = best.Translations[^1] - first;
        float t0 = best.TranslationTimes[0], span = best.TranslationTimes[^1] - t0;
        if (span <= 0) return;
        for (int k = 0; k < best.Translations.Length; k++)
        {
            float f = (best.TranslationTimes[k] - t0) / span;
            var p = best.Translations[k];
            best.Translations[k] = new System.Numerics.Vector3(p.X - total.X * f, p.Y - total.Y * f, p.Z);
        }
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
