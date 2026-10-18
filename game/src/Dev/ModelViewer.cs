using Godot;
using H5.Assets.Model;
using H5Reborn.Render;

namespace H5Reborn.Dev;

/// <summary>
/// Developer scene for inspecting original models: `-- --view-model=_(Model)/Creatures/Haven/Footman.(Model).xdb`.
/// Drag with the left mouse button to orbit, wheel to zoom.
/// </summary>
public partial class ModelViewer : Node3D
{
    public static string ModelPath = "_(Model)/Creatures/Haven/Footman.(Model).xdb";
    /// <summary>When set (`--view-character=`), shows an animated (Character) instead of a bare model.</summary>
    public static string? CharacterPath;
    /// <summary>`--anim=attack00` plays that kind; `--anim-time=0.7` freezes it at that time.</summary>
    public static string? Anim;
    public static float? AnimTime;

    Camera3D _camera = null!;
    float _yaw = 0.6f, _pitch = 0.25f, _distance = 5f;
    Vector3 _target;

    public override void _Ready()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.15f, 0.17f, 0.2f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.45f, 0.45f, 0.5f),
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { ShadowEnabled = true };
        AddChild(sun);
        sun.LookAt(new Vector3(-1, -2, -1.5f));

        Aabb aabb;
        if (CharacterPath != null)
        {
            var character = CharacterBuilder.Build(CharacterPath);
            AddChild(character);
            if (Anim != null && character.Has(Anim))
            {
                character.Play(Anim, loop: AnimTime == null);
                if (AnimTime is { } t) { character.Player.Seek(t, update: true); character.Player.Pause(); }
            }
            var mesh = character.FindChild("Mesh", recursive: true, owned: false) as MeshInstance3D;
            aabb = mesh?.GetAabb() ?? new Aabb(-Vector3.One, Vector3.One * 2);
            var anims = string.Join(" ", character.Player.GetAnimationList().Select(a => a.Replace(Character.Lib, "")));
            AddChild(new Label { Text = $"{CharacterPath}\n{anims}", Position = new Vector2(8, 8) });
        }
        else
        {
            var model = new ModelLoader(Game.Instance.Xdb).Load(ModelPath);
            AddChild(ModelBuilder.Build(model));
            var min = ModelBuilder.ToGodot(model.Mesh.BoundsMin);
            var max = ModelBuilder.ToGodot(model.Mesh.BoundsMax);
            aabb = new Aabb(min, Vector3.Zero).Expand(max);
            AddChild(new Label { Text = ModelPath, Position = new Vector2(8, 8) });
        }
        _target = aabb.GetCenter();
        _distance = Math.Max(2f, aabb.Size.Length() * 1.3f);

        _camera = new Camera3D { Fov = 40 };
        AddChild(_camera);
        UpdateCamera();
    }

    void UpdateCamera()
    {
        var dir = new Vector3(Mathf.Sin(_yaw) * Mathf.Cos(_pitch), Mathf.Sin(_pitch), Mathf.Cos(_yaw) * Mathf.Cos(_pitch));
        _camera.Position = _target + dir * _distance;
        _camera.LookAt(_target);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left } mm)
        {
            _yaw -= mm.Relative.X * 0.01f;
            _pitch = Math.Clamp(_pitch + mm.Relative.Y * 0.01f, -1.4f, 1.4f);
            UpdateCamera();
        }
        else if (e is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) _distance *= 0.9f;
            else if (mb.ButtonIndex == MouseButton.WheelDown) _distance *= 1.1f;
            UpdateCamera();
        }
    }
}
