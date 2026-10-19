using Godot;
using H5.Assets.Model;
using H5.Combat;
using H5.Core.Ui;
using H5Reborn.Arena;
using H5Reborn.Render;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// On-screen presentation of one battle unit: the animated original character plus
/// the stack-count plaque (UI/CombatArena-FPP-2/StackInfo, whose visual state shows
/// whether the stack carries positive, negative or mixed spell effects).
/// The view keeps its own "displayed" position/count so playback can lag the state.
/// </summary>
public partial class UnitView : Node3D
{
    public int UnitId { get; }
    public int Side { get; private set; }
    public int Size { get; }
    public string CreatureId { get; }
    public Node3D Model { get; private set; } = null!;
    public Character? Character => Model as Character;
    public Cell Cell { get; set; }
    public int Count { get; set; }
    public bool Dead { get; set; }
    public bool IsWarMachine { get; }
    public UiNode? Plaque { get; private set; }
    UiNode? _plaqueText;

    public UnitView(Unit u)
    {
        UnitId = u.Id;
        Side = u.Side;
        Size = u.Size;
        CreatureId = u.CreatureId;
        Cell = u.Pos;
        Count = u.Count;
        IsWarMachine = u.IsWarMachine;
        Name = $"Unit{u.Id}_{u.CreatureId}";
    }

    public void Build(ArenaScene arena, Control plaqueLayer)
    {
        Model = BuildModel(arena);
        AddChild(Model);
        Position = arena.Grid.StackCenter(new Vector2I(Cell.X, Cell.Y), Size);
        Rotation = new Vector3(0, ArenaGrid.FacingYaw(Side), 0);

        if (!IsWarMachine)
        {
            var w = UiResources.Instance.LoadWindow("UI/CombatArena-FPP-2/StackInfo.(WindowMSButton).xdb");
            UiLayout.Apply(w);
            Plaque = new UiNode(w) { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(44, 18) };
            foreach (var child in w.Children)
            {
                var n = new UiNode(child) { Size = Plaque.Size, MouseFilter = Control.MouseFilterEnum.Ignore };
                Plaque.AddChild(n);
                _plaqueText = n;
            }
            plaqueLayer.AddChild(Plaque);
            UpdatePlaque();
        }
    }

    Node3D BuildModel(ArenaScene arena)
    {
        string? characterPath = IsWarMachine ? WarMachineCharacter(CreatureId) : CreatureModels.CharacterPathFor(CreatureId);
        if (characterPath != null && Game.Instance.Fs.Exists(characterPath))
        {
            try { return CharacterBuilder.Build(characterPath); }
            catch (Exception e) { GD.PushWarning($"[H5] unit model {characterPath}: {e.Message}"); }
        }
        return new Node3D();
    }

    static string? WarMachineCharacter(string id)
    {
        if (!Game.Instance.Db.WarMachines.ContainsKey(id) || CreatureModels.VisualFor(id) is not { } visual) return null;
        var r = H5.Core.Xdb.XdbRef.Parse((string?)Game.Instance.Xdb.Load(visual).Root!.Element("AnimCharacter")?.Attribute("href"), visual);
        return r.IsEmpty ? null : r.Path;
    }

    /// <summary>0 normal, 1 negative, 2 positive, 3 mixed (matches the StackInfo button states).</summary>
    public int EffectState { get; set; }

    public void UpdatePlaque()
    {
        if (Plaque == null) return;
        Plaque.SetState(EffectState);
        var text = Count.ToString();
        _plaqueText?.SetText(text);
        Plaque.Size = new Vector2(Math.Max(28, 9 * text.Length + 14), 18);
        if (_plaqueText != null) _plaqueText.Size = Plaque.Size;
        Plaque.Visible = !Dead && Count > 0 && Visible;
    }

    /// <summary>Keeps the plaque under the creature's feet, on the camera's side.</summary>
    public void PositionPlaque(Camera3D cam)
    {
        if (Plaque == null) return;
        if (Dead || !Visible) { Plaque.Visible = false; return; }
        // Stock places the plaque at the stack's front-bottom corner; we use the footprint
        // corner nearest the camera, slightly above the ground.
        var basePos = GlobalPosition + new Vector3(0, 0.05f, 0) + (cam.GlobalPosition - GlobalPosition).Normalized() * (0.45f * Size * ArenaGrid.TileSize);
        if (cam.IsPositionBehind(basePos)) { Plaque.Visible = false; return; }
        var screen = cam.UnprojectPosition(basePos);
        Plaque.Position = (screen - Plaque.Size / 2).Round();
        Plaque.Visible = Count > 0;
    }

    float _height = -1;

    /// <summary>Where flying signs start: above the creature's head (bind-pose mesh top).</summary>
    public Vector3 SignAnchor()
    {
        if (_height < 0)
        {
            _height = 1.8f;
            if (Model.FindChild("Mesh", recursive: true, owned: false) is MeshInstance3D mi && mi.Mesh != null)
            {
                var aabb = mi.GetAabb();
                _height = Math.Max(0.6f, aabb.End.Y);
            }
        }
        return GlobalPosition + new Vector3(0, _height, 0);
    }

    public void Play(string kind, bool loop = false)
    {
        if (Character is { } c && c.Has(kind)) c.Play(kind, loop, BlendSeconds(c.CurrentKind, kind));
    }

    /// <summary>combat_anim_blend setvar default (exe 0054ed50: 300 ms).</summary>
    public const float AnimBlendMs = 300f;

    /// <summary>
    /// Cross-fade between two clips, exe 0054d700: the combat_anim_blend time when going from an
    /// idle (idle00/01/02) into stir00/stir01/move, or from any non-idle clip into an idle;
    /// every other change is an immediate cut (attacks, hits and deaths start at once).
    /// </summary>
    public static float BlendSeconds(string? from, string to)
    {
        static bool IsIdle(string? k) => k is "idle00" or "idle01" or "idle02";
        static bool IsStirOrMove(string k) => k is "stir00" or "stir01" or "move";
        if (from == null || from == to) return 0f;
        if (IsStirOrMove(to) && IsIdle(from)) return AnimBlendMs / 1000f;
        if (IsIdle(to) && !IsIdle(from)) return AnimBlendMs / 1000f;
        return 0f;
    }

    /// <summary>
    /// Locomotion speed in world units per second, exe 008b85e0: the move clip's (BasicSkelAnim)
    /// MovementSpeed × SpeedFactor × 0.001 units/ms, or the default 0.003 units/ms when the clip
    /// has no MovementSpeed; times combat_time_factor (1.0).
    /// </summary>
    public float LocomotionSpeed => (MoveSpeed > 0 ? MoveSpeed : 3.0f) * CombatTimeFactor;

    public const float CombatTimeFactor = 1.0f;

    /// <summary>
    /// Time to turn in place through <paramref name="angle"/> radians, exe 00a44030:
    /// (1/speed[units/ms]) × angle/π ms, or 300 ms × angle/π when the speed is unknown.
    /// </summary>
    public float TurnSeconds(float angle)
    {
        float a = Math.Abs(angle);
        float speedPerMs = LocomotionSpeed / 1000f;
        float ms = speedPerMs > 0 ? 1f / speedPerMs * a / Mathf.Pi : 300f * a / Mathf.Pi;
        return ms / 1000f;
    }

    /// <summary>Shortest signed yaw difference (radians), wrapped into ±π like the exe's fmod by 2π.</summary>
    public static float YawDelta(float from, float to) => Mathf.Wrap(to - from, -Mathf.Pi, Mathf.Pi);

    public float YawTowards(Vector3 worldTarget)
    {
        var d = worldTarget - GlobalPosition;
        return Mathf.Atan2(d.X, d.Z);
    }

    /// <summary>
    /// Turns in place to <paramref name="yaw"/> the way the original does (exe 008b88b0): the move
    /// clip plays during the turn, which lasts <see cref="TurnSeconds"/> for the angle.
    /// </summary>
    public async Task TurnTo(float yaw, bool playMoveClip = true)
    {
        float delta = YawDelta(Rotation.Y, yaw);
        if (Math.Abs(delta) < 0.01f) { Rotation = new Vector3(0, yaw, 0); return; }
        float secs = TurnSeconds(delta);
        if (playMoveClip && Has("move")) Play("move", loop: true);
        float start = Rotation.Y;
        var tw = CreateTween();
        tw.TweenMethod(Callable.From<float>(k => Rotation = new Vector3(0, start + delta * k, 0)), 0f, 1f, Math.Max(0.016f, secs));
        await ToSignal(tw, Tween.SignalName.Finished);
    }

    Dictionary<string, AnimSetEntry>? _animEntries;

    /// <summary>The arena (AnimSet) entries of this creature, by kind (sound and effect refs).</summary>
    AnimSetEntry? Entry(string kind)
    {
        if (_animEntries == null)
        {
            _animEntries = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = IsWarMachine ? null : CreatureModels.CharacterPathFor(CreatureId);
                var loader = new AnimSetLoader(Game.Instance.Xdb);
                if (path != null && loader.ReadCharacter(path).ArenaAnimSet is { } set)
                    foreach (var e in loader.ReadAnimSet(set)) _animEntries[e.Kind] = e;
            }
            catch (Exception e) { GD.PushWarning($"[H5] anim set for {CreatureId}: {e.Message}"); }
        }
        return _animEntries.GetValueOrDefault(kind);
    }

    /// <summary>
    /// Plays an animation the way the original does for one-shot actions: the clip, its
    /// (BasicSkelAnim) sound and its effect (e.g. a sword trail) attached to the character.
    /// </summary>
    public void PlayAction(string kind, bool loop = false)
    {
        Play(kind, loop);
        if (Entry(kind) is not { } e) return;
        if (e.SoundHref is { } snd) Audio.SoundLibrary.Play(snd, this);
        if (!loop && e.EffectHref is { } fx && Model is { } m) Effects.EffectPlayer.Play(fx, m);
    }

    public bool Has(string kind) => Character?.Has(kind) ?? false;

    /// <summary>Real-time length of a clip (its (BasicSkelAnim) SpeedFactor applied).</summary>
    public float Length(string kind) =>
        Character is { } c && c.Has(kind) ? (float)c.Player.GetAnimation(Render.Character.Lib + kind).Length / c.SpeedFactor(kind) : 0f;

    /// <summary>Real-time moment of the animator's "action" event (the hit), or a fraction of the clip.</summary>
    public float ActionTime(string kind)
    {
        if (Character is { } c && c.Events.TryGetValue(kind, out var evs))
            foreach (var (name, time) in evs)
                if (name.Contains("action", StringComparison.OrdinalIgnoreCase) || name.Contains("hit", StringComparison.OrdinalIgnoreCase))
                    return time / c.SpeedFactor(kind);
        return Length(kind) * 0.55f;
    }

    /// <summary>Ground speed of the move clip in world units per second (0 when unknown).</summary>
    public float MoveSpeed => Character is { } c && c.GroundSpeeds.TryGetValue("move", out var s) ? s : 0f;

    public void Idle()
    {
        if (!Dead) Play("idle00", loop: true);
    }

    public void FaceTowards(Vector3 worldTarget)
    {
        var d = worldTarget - GlobalPosition;
        d.Y = 0;
        if (d.LengthSquared() < 1e-4f) return;
        // Models face Godot +Z at yaw 0 (see ArenaGrid.FacingYaw).
        Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Z), 0);
    }

    public override void _ExitTree() => Plaque?.QueueFree();
}
