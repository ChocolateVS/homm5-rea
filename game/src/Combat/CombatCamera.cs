using Godot;
using H5Reborn.Arena;

namespace H5Reborn.Combat;

/// <summary>
/// Player control of the combat camera with the stock bindings
/// (profiles/default_profile/input_a2.cfg, common + combat_screen sections):
/// arrows = camera_forward / camera_strafe, PG_UP/PG_DOWN and the wheel = camera_zoom,
/// CTRL+UP/DOWN = camera_pitch, CTRL+LEFT/RIGHT = camera_rotate, INSERT/DELETE =
/// camera_lift, right button drag = camera_strafe_mouse / camera_forward_mouse.
/// Speeds use the stock setvars combat_scroll_speed (1), combat_max_scroll_speed (3)
/// and combat_scroll_speed_zoom_factor (3). The original's combat_camera_limits are
/// not set by any shipped config, so the limits below keep the field in view.
/// </summary>
public sealed class CombatCamera
{
    const float ScrollSpeed = 1f, MaxScrollSpeed = 3f, ZoomFactor = 3f;
    const float CellsPerSecond = 6f;
    const float MinDistance = 12f, MaxDistance = 40f, MinPitch = 25f, MaxPitch = 80f;
    const float PitchDegPerSecond = 40f, YawDegPerSecond = 60f, LiftPerSecond = 4f;

    readonly ArenaScene _arena;
    float _yaw = ArenaScene.DefaultYawDeg, _pitch = ArenaScene.DefaultPitchDeg, _distance = ArenaScene.DefaultDistanceForField, _lift;
    Vector2 _target = ArenaScene.DefaultTarget;
    float _held;

    public CombatCamera(ArenaScene arena) => _arena = arena;

    // Scripted view changes (tactics view → combat view) glide over this many seconds.
    const float TransitionSeconds = 1.0f;
    (float Pitch, float Yaw, float Distance, Vector2 Target, float Fov) _from, _to;
    float _transition = -1;

    /// <summary>Behind the deploying army, looking along the 12-cell axis (stock tactics view).</summary>
    public void ShowTacticsView(int side, bool instant) =>
        SetView(ArenaScene.TacticsPitchDeg, ArenaScene.TacticsYawDeg(side), ArenaScene.TacticsDistanceForField,
            ArenaScene.TacticsTarget(side), ArenaScene.TacticsFovDeg, instant);

    /// <summary>The default combat view: attacker on the left.</summary>
    public void ShowBattleView(bool instant) =>
        SetView(ArenaScene.DefaultPitchDeg, ArenaScene.DefaultYawDeg, ArenaScene.DefaultDistanceForField,
            ArenaScene.DefaultTarget, ArenaScene.DefaultFovDeg, instant);

    public void SetView(float pitch, float yaw, float distance, Vector2 target, float fov, bool instant)
    {
        _lift = 0;
        _to = (pitch, yaw, distance, target, fov);
        if (instant) { _transition = -1; Set(_to); return; }
        _from = (_pitch, _yaw, _distance, _target, _arena.Camera.Fov);
        // Turn the short way round.
        while (_to.Yaw - _from.Yaw > 180) _from.Yaw += 360;
        while (_to.Yaw - _from.Yaw < -180) _from.Yaw -= 360;
        _transition = 0;
    }

    void Set((float Pitch, float Yaw, float Distance, Vector2 Target, float Fov) v)
    {
        (_pitch, _yaw, _distance, _target) = (v.Pitch, v.Yaw, v.Distance, v.Target);
        _arena.Camera.Fov = v.Fov;
        Apply();
    }

    public void Process(double delta, bool keyboardFree)
    {
        float dt = (float)delta;
        if (_transition >= 0)
        {
            _transition = Math.Min(1, _transition + dt / TransitionSeconds);
            float k = _transition * _transition * (3 - 2 * _transition);
            Set((Mathf.Lerp(_from.Pitch, _to.Pitch, k), Mathf.Lerp(_from.Yaw, _to.Yaw, k), Mathf.Lerp(_from.Distance, _to.Distance, k),
                _from.Target.Lerp(_to.Target, k), Mathf.Lerp(_from.Fov, _to.Fov, k)));
            if (_transition >= 1) _transition = -1;
            return;
        }
        bool ctrl = Input.IsKeyPressed(Key.Ctrl);
        Vector2 move = Vector2.Zero;
        float pitch = 0, yaw = 0, lift = 0, zoom = 0;
        if (keyboardFree)
        {
            if (ctrl)
            {
                if (Input.IsKeyPressed(Key.Up)) pitch -= 1;
                if (Input.IsKeyPressed(Key.Down)) pitch += 1;
                if (Input.IsKeyPressed(Key.Right)) yaw += 1;
                if (Input.IsKeyPressed(Key.Left)) yaw -= 1;
            }
            else
            {
                if (Input.IsKeyPressed(Key.Up)) move.Y += 1;
                if (Input.IsKeyPressed(Key.Down)) move.Y -= 1;
                if (Input.IsKeyPressed(Key.Right)) move.X += 1;
                if (Input.IsKeyPressed(Key.Left)) move.X -= 1;
            }
            if (Input.IsKeyPressed(Key.Pagedown)) zoom += 1;
            if (Input.IsKeyPressed(Key.Pageup)) zoom -= 1;
            if (Input.IsKeyPressed(Key.Insert)) lift += 1;
            if (Input.IsKeyPressed(Key.Delete)) lift -= 1;
        }
        bool any = move != Vector2.Zero || pitch != 0 || yaw != 0 || lift != 0 || zoom != 0;
        if (!any) { _held = 0; return; }
        // Scrolling accelerates from combat_scroll_speed to combat_max_scroll_speed while held,
        // and is faster when zoomed out (combat_scroll_speed_zoom_factor).
        _held += dt;
        float speed = Math.Min(MaxScrollSpeed, ScrollSpeed + _held) * (1 + (ZoomFactor - 1) * (_distance - MinDistance) / (MaxDistance - MinDistance));
        if (move != Vector2.Zero) Pan(move.Normalized() * speed * CellsPerSecond * dt);
        _pitch = Math.Clamp(_pitch + pitch * PitchDegPerSecond * dt, MinPitch, MaxPitch);
        _yaw += yaw * YawDegPerSecond * dt;
        _lift = Math.Clamp(_lift + lift * LiftPerSecond * dt, -2f, 8f);
        _distance = Math.Clamp(_distance + zoom * speed * CellsPerSecond * dt, MinDistance, MaxDistance);
        Apply();
    }

    /// <summary>Mouse wheel (camera_zoom_mouse) and right-drag panning.</summary>
    public bool HandleInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } mb && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            _distance = Math.Clamp(_distance * (mb.ButtonIndex == MouseButton.WheelUp ? 0.9f : 1.1f), MinDistance, MaxDistance);
            Apply();
            return true;
        }
        if (e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Right } mm && mm.Relative.LengthSquared() > 0)
        {
            Pan(new Vector2(-mm.Relative.X, mm.Relative.Y) * 0.03f * (_distance / ArenaScene.DefaultDistance));
            return true;
        }
        return false;
    }

    void Pan(Vector2 screenDelta)
    {
        // Screen right/up mapped to grid axes for the current yaw (yaw 0 looks along +rows).
        float y = Mathf.DegToRad(_yaw);
        var right = new Vector2(Mathf.Cos(y), Mathf.Sin(y));
        var forward = new Vector2(-Mathf.Sin(y), Mathf.Cos(y));
        _target += right * screenDelta.X + forward * screenDelta.Y;
        _target = new Vector2(Math.Clamp(_target.X, -2f, ArenaGrid.Width + 2f), Math.Clamp(_target.Y, -2f, ArenaGrid.Height + 2f));
        Apply();
    }

    void Apply()
    {
        _arena.PlaceCamera(_arena.Camera, _pitch, _yaw, _distance, _target);
        if (_lift != 0) _arena.Camera.GlobalPosition += new Vector3(0, _lift, 0);
    }
}
