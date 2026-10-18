using Godot;
using H5.Adventure.Map;

namespace H5Reborn.Adventure;

/// <summary>
/// The adventure ("mission") camera with the stock tunables from profiles/autoexec_a2.cfg:
/// `mission_camera_def_pos 50 -53 16 25` (FOV, pitch, yaw, distance),
/// `mission_camera_limits 50 10 70 -55 -30 0 0` (distance 10..70, pitch −55..−30, yaw free),
/// zoom/pitch/yaw/scroll speeds 0.3 / 5 / 1.5 / 1.4. Controls follow input_a2.cfg: wheel and
/// PgUp/PgDn zoom, right-drag rotates (x) and pitches (y), middle-drag or Ctrl+left-drag
/// pans, arrows pan, Ctrl+arrows pitch/rotate, Insert/Delete rotate, Home resets,
/// and the screen edge scrolls.
/// Yaw 0 looks towards increasing map y (Godot −Z), as for the combat camera.
/// </summary>
public partial class AdvCamera : Camera3D
{
    public const float DefFov = 50, DefPitch = 53, DefYaw = 16, DefDistance = 25;
    public const float MinDistance = 10, MaxDistance = 70, MinPitch = 30, MaxPitch = 55;
    const float ZoomSpeed = 0.3f, PitchSpeed = 5f, YawSpeed = 1.5f, ScrollSpeed = 1.4f;
    const int EdgeMargin = 4;
    const float FollowRate = 6f;

    readonly AdvWorld _world;
    Vector2? _follow;
    Vector2 _target;   // map coordinates (tiles)
    float _yaw = DefYaw, _pitch = DefPitch, _distance = DefDistance;
    bool _rotating, _panning;

    /// <summary>When false, edge scrolling and keys are ignored (e.g. while a dialog is open).</summary>
    public bool InputEnabled { get; set; } = true;
    /// <summary>Screen-edge scroll direction this frame (for the Scroll_* cursors), zero if none.</summary>
    public Vector2I EdgeScroll { get; private set; }

    public AdvCamera(AdvWorld world)
    {
        _world = world;
        Fov = DefFov;
        Near = 1;
        Far = 500;
        KeepAspect = KeepAspectEnum.Height;
    }

    public Vector2 Target => _target;
    public float Yaw => _yaw;

    public override void _Ready() => Apply();

    public void CenterOn(Tile t, bool instant = true)
    {
        _target = new Vector2(t.X + 0.5f, t.Y + 0.5f);
        Apply();
    }

    /// <summary>
    /// Keeps the view on a moving point (a walking hero), easing towards it every frame rather
    /// than jumping tile by tile. <see cref="StopFollow"/> ends it.
    /// </summary>
    public void Follow(Vector3 world) => _follow = AdvWorld.WorldToMap(world);

    public void StopFollow() => _follow = null;

    public void SetYaw(float degrees)
    {
        _yaw = degrees;
        Apply();
    }

    public void ResetView()
    {
        _yaw = DefYaw;
        _pitch = DefPitch;
        _distance = DefDistance;
        Apply();
    }

    void Apply()
    {
        var map = _world.Map;
        _target.X = Math.Clamp(_target.X, 0, map.TilesX);
        _target.Y = Math.Clamp(_target.Y, 0, map.TilesY);
        _distance = Math.Clamp(_distance, MinDistance, MaxDistance);
        _pitch = Math.Clamp(_pitch, MinPitch, MaxPitch);
        float h = _world.HeightAt(_world.Floor, _target.X, _target.Y);
        var target = new Vector3(_target.X * AdvWorld.Unit, h, -_target.Y * AdvWorld.Unit);
        float p = Mathf.DegToRad(_pitch), y = Mathf.DegToRad(_yaw);
        var back = new Vector3(Mathf.Sin(y), 0, Mathf.Cos(y)) * (_distance * Mathf.Cos(p));
        LookAtFromPosition(target + back + new Vector3(0, _distance * Mathf.Sin(p), 0), target, Vector3.Up);
    }

    /// <summary>Pans by a screen-space amount (in tiles), relative to the current yaw.</summary>
    void Pan(float right, float forward)
    {
        float y = Mathf.DegToRad(_yaw);
        // Forward (screen up) is towards increasing map y when yaw is 0.
        var fwd = new Vector2(-Mathf.Sin(y), Mathf.Cos(y));
        var rgt = new Vector2(Mathf.Cos(y), Mathf.Sin(y));
        // Pan amounts arrive in world units; the target is kept in tiles.
        _target += (rgt * right + fwd * forward) / AdvWorld.Unit;
        Apply();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (_follow is { } f)
        {
            // Exponential ease: no overshoot, no per-tile jumps.
            _target = _target.Lerp(f, 1f - Mathf.Exp(-FollowRate * dt));
            if (!InputEnabled) { Apply(); EdgeScroll = Vector2I.Zero; return; }
        }
        if (!InputEnabled) { EdgeScroll = Vector2I.Zero; return; }
        float speed = ScrollSpeed * _distance * dt;
        float right = 0, forward = 0;
        bool ctrl = Input.IsKeyPressed(Key.Ctrl);
        if (!ctrl)
        {
            if (Input.IsKeyPressed(Key.Left)) right -= 1;
            if (Input.IsKeyPressed(Key.Right)) right += 1;
            if (Input.IsKeyPressed(Key.Up)) forward += 1;
            if (Input.IsKeyPressed(Key.Down)) forward -= 1;
        }
        else
        {
            if (Input.IsKeyPressed(Key.Up)) _pitch -= PitchSpeed * 10 * dt;
            if (Input.IsKeyPressed(Key.Down)) _pitch += PitchSpeed * 10 * dt;
            if (Input.IsKeyPressed(Key.Left)) _yaw -= YawSpeed * 40 * dt;
            if (Input.IsKeyPressed(Key.Right)) _yaw += YawSpeed * 40 * dt;
        }
        if (Input.IsKeyPressed(Key.Delete)) _yaw += YawSpeed * 40 * dt;
        if (Input.IsKeyPressed(Key.Insert)) _yaw -= YawSpeed * 40 * dt;
        if (Input.IsKeyPressed(Key.Pagedown)) _distance += ZoomSpeed * _distance * 3 * dt;
        if (Input.IsKeyPressed(Key.Pageup)) _distance -= ZoomSpeed * _distance * 3 * dt;

        // Screen-edge scrolling (only while the window has the mouse).
        var vp = GetViewport();
        var m = vp.GetMousePosition();
        var size = vp.GetVisibleRect().Size;
        var edge = Vector2I.Zero;
        if (DisplayServer.WindowIsFocused() && m.X >= 0 && m.Y >= 0 && m.X < size.X && m.Y < size.Y && !_rotating && !_panning)
        {
            if (m.X < EdgeMargin) edge.X = -1; else if (m.X > size.X - EdgeMargin) edge.X = 1;
            if (m.Y < EdgeMargin) edge.Y = -1; else if (m.Y > size.Y - EdgeMargin) edge.Y = 1;
        }
        EdgeScroll = edge;
        right += edge.X;
        forward -= edge.Y;
        if (right != 0 || forward != 0) Pan(right * speed, forward * speed);
        else Apply();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!InputEnabled) return;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _distance -= ZoomSpeed * _distance * 0.35f; Apply(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _distance += ZoomSpeed * _distance * 0.35f; Apply(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } b:
                _rotating = b.Pressed; break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } b:
                _panning = b.Pressed; break;
            case InputEventMouseMotion mm when _rotating:
                _yaw += mm.Relative.X * YawSpeed * 0.2f;
                _pitch -= mm.Relative.Y * PitchSpeed * 0.04f;
                Apply();
                break;
            case InputEventMouseMotion mm when _panning || (mm.ButtonMask.HasFlag(MouseButtonMask.Left) && Input.IsKeyPressed(Key.Ctrl)):
                Pan(-mm.Relative.X * _distance * 0.0025f, mm.Relative.Y * _distance * 0.0025f);
                break;
            case InputEventKey { Keycode: Key.Home, Pressed: true }:
                ResetView(); break;
        }
    }
}
