using System.Globalization;
using Godot;
using H5.Assets.Model;
using H5Reborn.Render;

namespace H5Reborn.Effects;

/// <summary>
/// Dev scene: `-- --view-effect=&lt;xdb&gt; [--effect-time=0.5] [--effect-yaw=0.6]`.
/// Accepts an (Effect), (SpellVisual), (Spell) or (Shot) xdb, or `char:&lt;Character.xdb&gt;:&lt;kind&gt;`
/// to play a creature animation with its anim-set effect (e.g. weapon trails).
/// With --effect-time the effect is frozen at that time for screenshots.
/// </summary>
public partial class EffectViewer : Node3D
{
    static string? _target;
    static float? _time;
    static float _yaw = 0.6f;
    static float _startDist = 9f;

    public static bool HandleArg(string arg)
    {
        if (arg.StartsWith("--view-effect="))
        {
            _target = arg["--view-effect=".Length..];
            Callable.From(Open).CallDeferred();
            return true;
        }
        if (arg.StartsWith("--effect-time=")) { _time = float.Parse(arg["--effect-time=".Length..], CultureInfo.InvariantCulture); return true; }
        if (arg.StartsWith("--effect-yaw=")) { _yaw = float.Parse(arg["--effect-yaw=".Length..], CultureInfo.InvariantCulture); return true; }
        if (arg.StartsWith("--effect-dist=")) { _startDist = float.Parse(arg["--effect-dist=".Length..], CultureInfo.InvariantCulture); return true; }
        return false;
    }

    static void Open()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CurrentScene?.QueueFree();
        var v = new EffectViewer();
        tree.Root.AddChild(v);
        tree.CurrentScene = v;
    }

    Camera3D _cam = null!;
    float _dist = 9f;
    Vector3 _focus = new(0, 1f, 0);
    readonly List<EffectPlayer> _players = new();
    Label _label = null!;
    float _t;

    public override void _Ready()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.08f, 0.09f, 0.11f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.4f, 0.4f, 0.45f),
            },
        });
        var sun = new DirectionalLight3D { LightEnergy = 0.8f };
        AddChild(sun);
        sun.LookAt(new Vector3(-1, -2, -1));
        // Dark checker ground (1-unit cells, like the combat grid) for scale.
        var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(16, 16) } };
        var img = Image.CreateEmpty(16, 16, false, Image.Format.Rgb8);
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) img.SetPixel(x, y, ((x + y) & 1) == 0 ? new Color(0.22f, 0.24f, 0.22f) : new Color(0.18f, 0.2f, 0.18f));
        ground.MaterialOverride = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
        AddChild(ground);

        // Size reference: a footman (one combat cell) standing 2.5 cells to the side.
        try
        {
            var footman = CharacterBuilder.Build("Characters/Creatures/Haven/Footman.(Character).xdb");
            footman.Position = new Vector3(2.5f, 0, 0);
            AddChild(footman);
        }
        catch (Exception e) { GD.PushWarning($"[H5] viewer reference model: {e.Message}"); }

        _dist = _startDist;
        _cam = new Camera3D { Fov = 50 };
        AddChild(_cam);
        _label = new Label { Position = new Vector2(8, 8) };
        AddChild(_label);
        UpdateCamera();
        Spawn();
    }

    void Spawn()
    {
        var target = _target ?? "Effects/_(Effect)/Spells/FireBallHit.xdb";
        _label.Text = target;
        if (target.StartsWith("char:"))
        {
            var parts = target["char:".Length..].Split(':');
            SpawnCharacter(parts[0], parts.Length > 1 ? parts[1] : "attack00");
            return;
        }
        var root = EffectAssets.Loader.TryLoadRoot(target);
        switch (root?.Name.LocalName)
        {
            case "Effect":
                if (EffectPlayer.PlayAt(target, this, Vector3.Zero) is { } fx) _players.Add(fx);
                break;
            case "SpellVisual":
                if (SpellVisualPlayer.Play(target, this, Vector3.Zero) is { } sv) _players.Add(sv);
                break;
            case "Spell":
                if (SpellVisualPlayer.PlaySpell(target, this, Vector3.Zero) is { } sp) _players.Add(sp);
                break;
            case "Shot":
            {
                // Shooter at -4, target at +4 along X; the frozen time shows the missile mid-flight.
                var shooter = new Node3D { Position = new Vector3(-4, 0, 0) };
                AddChild(shooter);
                shooter.LookAt(new Vector3(4, 0, 0), Vector3.Up);
                var marker = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 1.6f }, Position = new Vector3(4, 0.8f, 0) };
                AddChild(marker);
                var shot = ProjectilePlayer.Fire(target, this, shooter, new Vector3(4, 1.0f, 0));
                if (shot != null)
                {
                    _label.Text += $"  flight {shot.FlightTime:F3}s";
                    if (_time is { } ft) shot.HoldAt(ft);
                }
                break;
            }
            default:
                _label.Text += "  (unknown type)";
                break;
        }
    }

    Character? _character;
    string _kind = "";

    void SpawnCharacter(string characterXdb, string kind)
    {
        _character = CharacterBuilder.Build(characterXdb);
        AddChild(_character);
        _kind = kind;
        _character.Play(kind);
        if (_time is { } t) { _character.Player.Seek(t, update: true); _character.Player.Pause(); }
        // The anim-set entry names the effect played with this animation.
        var anims = new AnimSetLoader(Game.Instance.Xdb);
        var (_, set, _) = anims.ReadCharacter(characterXdb);
        var entry = set == null ? null : anims.ReadAnimSet(set).FirstOrDefault(e => e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        string? effect = null;
        if (entry?.EffectHref is { } href) effect = href;
        else if (entry != null && Game.Instance.Xdb.Load(entry.DescriptorPath).Root?.Element("Effect")?.Attribute("href")?.Value is { Length: > 0 } h)
            effect = H5.Core.Xdb.XdbRef.Parse(h, entry.DescriptorPath).Path;
        _label.Text += $"\neffect: {effect ?? "(none)"}";
        if (effect != null && EffectPlayer.Play(effect, _character) is { } fx) _players.Add(fx);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_time is not { } freeze) return;
        foreach (var p in _players)
            if (IsInstanceValid(p)) { p.TimeScale = 0f; p.Seek(freeze); }
    }

    void UpdateCamera()
    {
        // Similar to the combat camera: elevated three-quarter view.
        var dir = new Vector3(Mathf.Sin(_yaw), 0.75f, Mathf.Cos(_yaw)).Normalized();
        _cam.Position = _focus + dir * _dist;
        _cam.LookAt(_focus);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left } mm) { _yaw -= mm.Relative.X * 0.01f; UpdateCamera(); }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }) { _dist *= 0.9f; UpdateCamera(); }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }) { _dist *= 1.1f; UpdateCamera(); }
    }
}
