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

    public void Play(string kind, bool loop = false)
    {
        if (!Has(kind)) return;
        var anim = Player.GetAnimation(Lib + kind);
        anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        Player.Play(Lib + kind);
    }

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

        var node = new Character { Name = name ?? System.IO.Path.GetFileNameWithoutExtension(modelPath) };
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
