using Godot;
using H5.Adventure.Engine;
using H5.Adventure.Map;

namespace H5Reborn.Adventure;

/// <summary>
/// The adventure ("mission") camera: H5_Game.exe's CCameraOperator (007d4ee0) with the mission
/// configurator and the profiles/autoexec_a2.cfg values, modelled by <see cref="MissionCamera"/>
/// (docs/rules/CAMERA.md, "Adventure camera"). The target pose integrates input and the soft/hard
/// limits; the rendered pose glides to it (007cccb0). Controls follow input_a2.cfg (adventure
/// section): wheel and PgUp/PgDn zoom, arrows pan, Ctrl+arrows pitch/rotate, Insert/Delete rotate,
/// right-drag (MOUSE_BUTTON1) rotates and pitches, middle-drag or Ctrl+right-drag pans, Home resets,
/// and the screen edge scrolls. The FOV is horizontal up to 4:3-tall screens (00cf7c30).
/// </summary>
public partial class AdvCamera : Camera3D
{
    public const float DefFov = MissionCamera.Fov, DefDistance = MissionCamera.DefRod;
    /// <summary>Stock hard rod limits (mission_camera_limits).</summary>
    public const float MinDistance = 10, MaxDistance = 70;

    /// <summary>Mouse axis scaling, shared with the combat camera (calibrated, see CAMERA.md open points).</summary>
    const float MouseAxisPerPixel = 1.2f / 1000f, WheelInputPerNotch = 120f / 160f;
    const float EdgePixels = 2f, DragThreshold = 4f;

    readonly AdvWorld _world;
    MissionCamera? _cam;
    Vector3? _follow;
    bool _followSuspended;
    Vector2 _mouseRotPitch, _mousePan;
    float _mouseZoom;
    bool _rightDown, _rightDragged, _middleDown;
    Vector2 _rightDownAt;

    /// <summary>When false, edge scrolling and keys are ignored (e.g. while a dialog is open).</summary>
    public bool InputEnabled { get; set; } = true;
    /// <summary>Screen-edge scroll direction this frame (for the Scroll_* cursors), zero if none.</summary>
    public Vector2I EdgeScroll { get; private set; }

    public AdvCamera(AdvWorld world)
    {
        _world = world;
        Fov = DefFov;
        Near = 1;
        Far = 1000;
    }

    MissionCamera Cam => _cam ??= new MissionCamera(_world.Map.TilesX, _world.Map.TilesY);

    /// <summary>Rendered look-at point in map tiles.</summary>
    public Vector2 Target => new(Cam.Current.X / AdvWorld.Unit, Cam.Current.Y / AdvWorld.Unit);
    /// <summary>Rendered yaw in degrees (0 looks towards increasing map y, positive turns counter-clockwise).</summary>
    public float Yaw => Mathf.RadToDeg(Cam.Current.Yaw);
    public float Distance => Cam.Current.Rod;

    public override void _Ready() { Configure(); Apply(); }

    public void CenterOn(Tile t, bool instant = true)
    {
        Cam.Target.X = (t.X + 0.5f) * AdvWorld.Unit;
        Cam.Target.Y = (t.Y + 0.5f) * AdvWorld.Unit;
        if (instant) { Cam.Current.X = Cam.Target.X; Cam.Current.Y = Cam.Target.Y; }
        Apply();
    }

    /// <summary>
    /// Keeps the view on a moving point (a walking hero): the operator's followed object (007d4ee0,
    /// +0x188 / state +0x190) sets the target anchor and the glide smooths it. Manual camera input
    /// suspends following until <see cref="StopFollow"/>.
    /// </summary>
    public void Follow(Vector3 world) => _follow = world;

    /// <summary>Starts a camera tour on the mission camera (<see cref="MissionCameraTour"/>); following stops.</summary>
    public MissionCameraTour StartTour(IReadOnlyList<TourStop> stops)
    {
        StopFollow();
        return new MissionCameraTour(Cam, stops);
    }

    public void StopFollow() { _follow = null; _followSuspended = false; }

    public void SetYaw(float degrees)
    {
        Cam.Target.Yaw = Cam.Current.Yaw = Mathf.DegToRad(degrees);
        Apply();
    }

    /// <summary>Dev: sets the pitch (degrees, clamped to the current hard limits) at once.</summary>
    public void SetPitch(float degrees)
    {
        Configure();
        Cam.Target.Pitch = Cam.Current.Pitch = Math.Clamp(Mathf.DegToRad(degrees), Cam.Hard.MinPitch, Cam.Hard.MaxPitch);
        Apply();
    }

    /// <summary>Sets the zoom distance (clamped to the current hard limits) at once.</summary>
    public void SetDistance(float distance)
    {
        Configure();
        Cam.Target.Rod = Cam.Current.Rod = Math.Clamp(distance, Cam.Hard.MinRod, Cam.Hard.MaxRod);
        Apply();
    }

    /// <summary>HOME (set_camera_default_pos): default rod/pitch/yaw, anchor kept.</summary>
    public void ResetView()
    {
        Cam.ResetToDefault();
        Apply();
    }

    /// <summary>
    /// Profile variable of the "Extended map zoom" option — a user-requested H5Reborn extension,
    /// on by default: rod up to 160 and pitch −85°…−20° (stock 10…70, −55°…−30°). With it off the
    /// stock limits apply unchanged.
    /// </summary>
    public const string ExtendedZoomVar = "h5r_adv_extended_zoom";

    /// <summary>Maximum distance with extended zoom (≈2.3× the stock 70).</summary>
    public const float ExtendedMaxDistance = 160;

    public static bool ExtendedZoom => H5Reborn.Options.OptionsStore.GetBool(ExtendedZoomVar, true);

    /// <summary>Current maximum zoom-out distance.</summary>
    public static float MaxZoom => ExtendedZoom ? ExtendedMaxDistance : MaxDistance;

    void Configure()
    {
        bool ext = ExtendedZoom;
        Cam.Hard = ext ? MissionCamera.ExtendedHard : MissionCamera.StockHard;
        Cam.Soft = ext ? MissionCamera.ExtendedSoft : MissionCamera.StockSoft;
    }

    void Apply()
    {
        var p = Cam.Current;
        _world.OnCameraDistance(p.Rod);
        Far = Math.Max(1000, p.Rod * 8);
        // Eye = anchor − rod·forward (007cd770); forward = qZ(yaw)·qX(pitch) applied to +Y.
        float h = _world.HeightAt(_world.Floor, p.X / AdvWorld.Unit, p.Y / AdvWorld.Unit);
        var anchor = new Vector3(p.X, h, -p.Y);
        float cp = Mathf.Cos(p.Pitch), sp = Mathf.Sin(p.Pitch), sy = Mathf.Sin(p.Yaw), cy = Mathf.Cos(p.Yaw);
        var fwd = new Vector3(-sy * cp, sp, -cy * cp);       // engine (−sin y·cos p, cos y·cos p, sin p) in Godot axes
        var up = new Vector3(sy * sp, cp, cy * sp);
        var eye = anchor - fwd * p.Rod;
        LookAtFromPosition(eye, eye + fwd, up);
        SetProjection(this, DefFov, GetViewport()?.GetVisibleRect().Size ?? new Vector2(4, 3));
    }

    /// <summary>
    /// The exe's projection (00cf7c30): with H/W ≤ 0.75 the FOV is horizontal; taller screens keep the
    /// vertical extent of 4:3 (tan(v/2) = 0.75·tan(fov/2)). As for the combat camera, screens wider than
    /// 16:9 keep the vertical extent of 16:9 (user-requested ultrawide extension), so 4:3…16:9 match the original.
    /// </summary>
    public static void SetProjection(Camera3D cam, float fovDeg, Vector2 size)
    {
        float hw = size.X > 0 ? size.Y / size.X : 0.75f;
        float t = Mathf.Tan(Mathf.DegToRad(fovDeg) * 0.5f);
        if (hw < 0.5625f) { cam.KeepAspect = KeepAspectEnum.Height; cam.Fov = Mathf.RadToDeg(2f * Mathf.Atan(0.5625f * t)); }
        else if (hw <= 0.75f) { cam.KeepAspect = KeepAspectEnum.Width; cam.Fov = fovDeg; }
        else { cam.KeepAspect = KeepAspectEnum.Height; cam.Fov = Mathf.RadToDeg(2f * Mathf.Atan(0.75f * t)); }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        Configure();
        float keyFwd = 0, keyStrafe = 0, keyZoom = 0, keyPitch = 0, keyRotate = 0;
        var edge = Vector2I.Zero;
        if (InputEnabled)
        {
            if (Input.IsKeyPressed(Key.Ctrl))
            {
                if (Input.IsKeyPressed(Key.Up)) keyPitch -= 1;      // -camera_pitch CTRL+UP
                if (Input.IsKeyPressed(Key.Down)) keyPitch += 1;    // +camera_pitch CTRL+DOWN
                if (Input.IsKeyPressed(Key.Right)) keyRotate += 1;  // +camera_rotate CTRL+RIGHT
                if (Input.IsKeyPressed(Key.Left)) keyRotate -= 1;
            }
            else
            {
                if (Input.IsKeyPressed(Key.Up)) keyFwd += 1;        // +camera_forward UP
                if (Input.IsKeyPressed(Key.Down)) keyFwd -= 1;
                if (Input.IsKeyPressed(Key.Right)) keyStrafe += 1;  // +camera_strafe RIGHT
                if (Input.IsKeyPressed(Key.Left)) keyStrafe -= 1;
            }
            if (Input.IsKeyPressed(Key.Delete)) keyRotate += 1;     // adventure: +camera_rotate DELETE
            if (Input.IsKeyPressed(Key.Insert)) keyRotate -= 1;     // -camera_rotate INSERT
            if (Input.IsKeyPressed(Key.Pagedown)) keyZoom += 1;     // +camera_zoom PG_DOWN
            if (Input.IsKeyPressed(Key.Pageup)) keyZoom -= 1;
            if (!_rightDown && !_middleDown) edge = EdgeDirection();
        }
        else { _mouseRotPitch = Vector2.Zero; _mousePan = Vector2.Zero; _mouseZoom = 0; }
        EdgeScroll = new Vector2I(edge.X, -edge.Y);

        // Sign table 007dd490 (stock): rotate +1, pitch −1, forward +1, strafe +1.
        float pitchIn = -(keyPitch * dt) - _mouseRotPitch.Y;
        float rotateIn = keyRotate * dt + _mouseRotPitch.X;
        float fwdIn = keyFwd * dt + _mousePan.Y;
        float strafeIn = keyStrafe * dt + _mousePan.X;
        float zoomIn = keyZoom * dt + _mouseZoom;
        _mouseRotPitch = Vector2.Zero; _mousePan = Vector2.Zero; _mouseZoom = 0;

        if (_follow is { } f)
        {
            if (pitchIn != 0 || rotateIn != 0 || fwdIn != 0 || strafeIn != 0 || zoomIn != 0 || edge != Vector2I.Zero)
                _followSuspended = true;
            if (!_followSuspended)
            {
                var m = AdvWorld.WorldToMap(f);
                Cam.Target.X = m.X * AdvWorld.Unit;
                Cam.Target.Y = m.Y * AdvWorld.Unit;
            }
        }
        Cam.EdgeScroll(dt, edge.X, edge.Y);
        Cam.Step(dt, rotateIn, pitchIn, fwdIn, strafeIn, zoomIn);
        Apply();
    }

    /// <summary>Cursor at the screen edge (window focused): x −1 left / +1 right, y +1 top (forward) / −1 bottom.</summary>
    Vector2I EdgeDirection()
    {
        var vp = GetViewport();
        if (!DisplayServer.WindowIsFocused()) return Vector2I.Zero;
        var size = vp.GetVisibleRect().Size;
        var m = vp.GetMousePosition();
        if (m.X < 0 || m.Y < 0 || m.X > size.X || m.Y > size.Y) return Vector2I.Zero;
        var dir = Vector2I.Zero;
        if (m.X <= EdgePixels) dir.X = -1; else if (m.X >= size.X - 1 - EdgePixels) dir.X = 1;
        if (m.Y <= EdgePixels) dir.Y = 1; else if (m.Y >= size.Y - 1 - EdgePixels) dir.Y = -1;
        return dir;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!InputEnabled) return;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
                // -camera_zoom_mouse MOUSE_AXIS_Z: wheel away from the user zooms in.
                _mouseZoom += wheel.ButtonIndex == MouseButton.WheelUp ? -WheelInputPerNotch : WheelInputPerNotch;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rb:
                _rightDown = rb.Pressed;
                if (rb.Pressed) { _rightDragged = false; _rightDownAt = rb.Position; }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _middleDown = mb.Pressed;
                break;
            case InputEventMouseMotion mm:
                var d = mm.Relative * MouseAxisPerPixel;
                if (_middleDown || (_rightDown && Input.IsKeyPressed(Key.Ctrl)))
                {
                    // -camera_strafe_mouse / +camera_forward_mouse: MOUSE_BUTTON2 or CTRL+MOUSE_BUTTON1.
                    _mousePan += new Vector2(-d.X, d.Y);
                    if (_rightDown) _rightDragged = true;
                }
                else if (_rightDown)
                {
                    // A press only becomes a drag after a few pixels, so a plain right click still opens info.
                    if (!_rightDragged && mm.Position.DistanceTo(_rightDownAt) < DragThreshold) break;
                    _rightDragged = true;
                    _mouseRotPitch += new Vector2(d.X, -d.Y);   // +camera_rotate_mouse X, -camera_pitch_mouse Y
                }
                break;
            case InputEventKey { Keycode: Key.Home, Pressed: true, Echo: false }:
                ResetView(); break;
        }
    }
}
