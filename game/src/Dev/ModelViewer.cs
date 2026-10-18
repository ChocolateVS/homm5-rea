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

        var model = new ModelLoader(Game.Instance.Xdb).Load(ModelPath);
        var node = ModelBuilder.Build(model);
        AddChild(node);

        var min = ModelBuilder.ToGodot(model.Mesh.BoundsMin);
        var max = ModelBuilder.ToGodot(model.Mesh.BoundsMax);
        var aabb = new Aabb(min, Vector3.Zero).Expand(max);
        _target = aabb.GetCenter();
        _distance = Math.Max(2f, aabb.Size.Length() * 1.3f);

        _camera = new Camera3D { Fov = 40 };
        AddChild(_camera);
        UpdateCamera();
        AddChild(new Label { Text = ModelPath, Position = new Vector2(8, 8) });
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
