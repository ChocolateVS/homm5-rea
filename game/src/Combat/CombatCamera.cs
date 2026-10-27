using Godot;
using H5Reborn.Arena;

namespace H5Reborn.Combat;

/// <summary>
/// The original combat camera (H5_Game.exe CCameraOperator driving a CCamera, configured by
/// CArenaCameraConfigurator). Evidence and addresses: docs/rules/CAMERA.md.
///
/// State is the exe's SCameraPos: Rod (eye distance behind the anchor), Pitch, Yaw (radians),
/// Anchor (world point). Engine space is Z-up with Y = north; Godot space maps (x, y, z) to
/// (x, z, −y). forward = qZ(yaw)·qX(pitch)·(0, 1, 0); eye = anchor − rod·forward (007cd770).
///
/// Combat init (00544100) creates one camera for the whole battle, sets the arena default pose
/// (007d3990) and applies the arena limits with limits ON (007d3730); tactics and combat use the
/// same camera — the exe never re-poses it when combat starts (005464a0).
///
/// Input (CCameraOperator::Update 007d4ee0, stock bindings in profiles/default_profile/input_a2.cfg):
/// arrows pan, PG_DOWN/PG_UP zoom, CTRL+arrows pitch/rotate, INSERT/DELETE lift, wheel zoom,
/// right-drag rotates/pitches (MOUSE_BUTTON1 = DirectInput rgbButtons[1]), middle-drag or
/// CTRL+right-drag pans, and the screen edge scrolls (combat_scroll_* setvars, 007ad410/007d2d90).
/// The target pose integrates input and clamps to hard limits (007d4340); the rendered pose glides
/// to it exponentially (007cccb0).
/// </summary>
public sealed class CombatCamera
{
    // ---- exe constants -------------------------------------------------------------------------

    /// <summary>Arena default and limits (007d3990, 007d3730).</summary>
    const float DefaultPitch = -0.7853982f, MinPitch = -1.0471976f, MaxPitch = -0.6108652f;
    const float MinYaw = -Mathf.Pi / 2, MaxYaw = Mathf.Pi / 2;
    const float DefaultFovDeg = 54f, NearClip = 1f, FarClip = 500f;
    /// <summary>Anchor sits this far below the arena centre (007d3990) and lift reaches this far below it (007d3730).</summary>
    const float AnchorDrop = 2f;

    /// <summary>Operator speeds: SCameraLimits defaults (DAT_00f33900) used by the arena configurator.</summary>
    const float PanSpeed = 1f, ZoomRodFactor = 5f, ZoomScale = 0.1f, RotateSpeed = 1f, PitchSpeed = 1f;
    const float PanUnitsPerInput = 10f;         // DAT_00e22820
    const float LiftUnitsPerInput = 15f;        // DAT_00f3fe3c
    const float MaxZoomInput = 5f;              // 007d4ee0 clamp
    const float AxisDominance = 10f;            // rotate/pitch mouse axis lock ratio

    /// <summary>Glide toward the target: remaining fraction per second and stop thresholds (007cccb0).</summary>
    const double RodBase = 1.0 / 300.0, AngleBase = 1.0 / 9000.0, AnchorBase = 1.1111111e-4;
    const float RodEps = 0.02f, PitchEps = 0.01f, AngleEps = 1e-5f;
    /// <summary>Anchor stop distance: every caller of 007cccb0 (007d5963, 007d54fe, 007cf5c8) passes 0.03 (0x3cf5c28f).</summary>
    const float AnchorEps = 0.03f;

    /// <summary>combat_scroll_speed / combat_max_scroll_speed / combat_scroll_speed_zoom_factor (profiles/autoexec_a2.cfg).</summary>
    const float ScrollSpeed = 1f, MaxScrollSpeed = 3f, ScrollZoomFactor = 3f;
    const float EdgePixels = 2f;

    // Mouse axis scaling. The exe accumulates DirectInput deltas × bindconfigure coefficients
    // (MOUSE_AXIS_X/Y 1.2, MOUSE_AXIS_Z 160) in fixed point; the final normalisation wasn't traced,
    // so these are calibrated approximations (docs/rules/CAMERA.md, "Open points").
    const float MouseAxisPerPixel = 1.2f / 1000f;
    const float WheelInputPerNotch = 120f / 160f;

    // ---- state ---------------------------------------------------------------------------------

    struct Pose { public float Rod, Pitch, Yaw; public Vector3 Anchor; }   // Anchor in engine space

    readonly ArenaScene _arena;
    Pose _target, _current;
    float _minRod, _maxRod, _minZ, _maxZ;
    Rect2 _pan;                 // engine-space XY bounds for the anchor
    float _edgeHeld;
    Vector2 _mouseRotPitch, _mousePan;
    float _mouseZoom;
    bool _rightDown, _rightDragged, _middleDown;
    Vector2 _rightDownAt;

    /// <summary>A cinematic (combat track camera) pose overrides the operator while set.</summary>
    CinematicPose? _cinematic;

    // ---- "Extended combat camera" (H5Reborn extension, off by default) ---------------------------

    /// <summary>
    /// Profile variable of the "Extended combat camera" option: a user-requested extension (not in the
    /// original) that relaxes the arena camera limits. Off by default, so the stock limits of 007d3730
    /// stay in force unless the player opts in.
    /// </summary>
    public const string ExtendedVar = "h5r_combat_extended_camera";
    public static bool Extended => H5Reborn.Options.OptionsStore.GetBool(ExtendedVar, false);

    /// <summary>Extended limits: free rotation, pitch −85°..−10°, rod ×0.5 .. ×1.6 of the stock range, 8 more units of pan.</summary>
    const float ExtMinPitch = -1.4835298f, ExtMaxPitch = -0.17453292f;
    const float ExtMinRodFactor = 0.5f, ExtMaxRodFactor = 1.6f, ExtPanMargin = 8f;

    float LimMinPitch => Extended ? ExtMinPitch : MinPitch;
    float LimMaxPitch => Extended ? ExtMaxPitch : MaxPitch;
    float LimMinRod => Extended ? _minRod * ExtMinRodFactor : _minRod;
    float LimMaxRod => Extended ? _maxRod * ExtMaxRodFactor : _maxRod;
    Rect2 LimPan => Extended ? _pan.Grow(ExtPanMargin) : _pan;

    public CombatCamera(ArenaScene arena)
    {
        _arena = arena;
        Configure();
        _current = _target;
        Apply();
    }

    /// <summary>True when the last right-button press turned into a camera drag (suppresses the info click).</summary>
    public bool RightDragged => _rightDragged;

    // ---- configuration (CArenaCameraConfigurator) -------------------------------------------------

    /// <summary>Internal grid width (field + 2-cell margins each side): the key of the exe's rod tables.</summary>
    static int InternalWidth => ArenaGrid.Width + 2 * ArenaGrid.MarginX;

    /// <summary>Default / max / min rod in cells per internal width (007d34c0, 007d3650, 007d35a0).</summary>
    static (float Def, float Max, float Min) RodTable(int w) => w switch
    {
        11 => (11f, 11f, 6.6f),
        14 => (15f, 17.5f, 7f),
        16 => (21f, 21f, 5f),
        18 or 20 => (24f, 28f, 14.4f),
        _ => (30f, 30f, 18f),
    };

    /// <summary>
    /// Screens taller than 4:3 pull the camera back by √(H/W·4/3) (007d34c0); on 4:3 and wider the
    /// horizontal FOV is fixed so nothing changes.
    /// </summary>
    float AspectRodFactor()
    {
        var size = _arena.GetViewport().GetVisibleRect().Size;
        float k = size.X > 0 ? size.Y / size.X * (4f / 3f) : 1f;
        return k > 1f ? Mathf.Sqrt(k) : 1f;
    }

    void Configure()
    {
        var (def, max, min) = RodTable(InternalWidth);
        float aspect = AspectRodFactor();
        _minRod = min * 2f;
        _maxRod = max * 2f * aspect;

        // Arena centre (the configurator's arena +0x74) and field extent.
        var centre = ToEngine(_arena.Grid.GridCenter);
        var a = ToEngine(_arena.Grid.CellCenter(0, 0));
        var b = ToEngine(_arena.Grid.CellCenter(ArenaGrid.Width, ArenaGrid.Height));
        // Pan rectangle: the field ±1 unit (007d3730: x0−1 .. x0+2(W−4)+1, likewise in y).
        _pan = new Rect2(Mathf.Min(a.X, b.X) - 1f, Mathf.Min(a.Y, b.Y) - 1f, Mathf.Abs(b.X - a.X) + 2f, Mathf.Abs(b.Y - a.Y) + 2f);
        // Lift range: centre.z − 2 .. centre.z + W (007d3730).
        _minZ = centre.Z - AnchorDrop;
        _maxZ = centre.Z + InternalWidth;

        _target = new Pose
        {
            Rod = def * 2f * aspect,
            Pitch = DefaultPitch,
            Yaw = 0f,
            Anchor = new Vector3(centre.X, centre.Y, centre.Z - AnchorDrop),
        };
    }

    /// <summary>
    /// Tactics phase: the exe keeps the combat camera it set up at combat init (no tactics pose exists in
    /// H5_Game.exe; see docs/rules/CAMERA.md). Kept for callers; does not move the camera.
    /// </summary>
    public void ShowTacticsView(int side, bool instant) { if (instant) Snap(); }

    /// <summary>Combat start: the original does not re-pose the camera (005464a0). Kept for callers.</summary>
    public void ShowBattleView(bool instant) { if (instant) Snap(); }

    /// <summary>Back to the arena default pose (used when a battle (re)starts).</summary>
    public void Reset(bool instant)
    {
        Configure();
        if (instant) Snap();
    }

    void Snap() { _current = _target; Apply(); }

    // ---- per-frame update (CCameraOperator::Update) ----------------------------------------------

    public void Process(double delta, bool keyboardFree)
    {
        // The exe caps the frame step at 100 ms.
        float dt = Mathf.Min((float)delta, 0.1f);
        if (dt <= 0) return;

        RunDevScript(dt);

        // Key axes: a held key contributes its level × elapsed time (the input layer's +/- commands).
        float keyFwd = 0, keyStrafe = 0, keyZoom = 0, keyPitch = 0, keyRotate = 0, keyLift = 0;
        if (keyboardFree && _cinematic == null)
        {
            bool ctrl = Held(Key.Ctrl);
            if (ctrl)
            {
                if (Held(Key.Up)) keyPitch -= 1;      // -camera_pitch CTRL+UP
                if (Held(Key.Down)) keyPitch += 1;    // +camera_pitch CTRL+DOWN
                if (Held(Key.Right)) keyRotate += 1;  // +camera_rotate CTRL+RIGHT
                if (Held(Key.Left)) keyRotate -= 1;
            }
            else
            {
                if (Held(Key.Up)) keyFwd += 1;        // +camera_forward UP
                if (Held(Key.Down)) keyFwd -= 1;
                if (Held(Key.Right)) keyStrafe += 1;  // +camera_strafe RIGHT
                if (Held(Key.Left)) keyStrafe -= 1;
            }
            if (Held(Key.Pagedown)) keyZoom += 1;     // +camera_zoom PG_DOWN
            if (Held(Key.Pageup)) keyZoom -= 1;
            if (Held(Key.Insert)) keyLift += 1;       // combat_screen: +camera_lift INSERT
            if (Held(Key.Delete)) keyLift -= 1;
        }

        // Sign table from FUN_007dd490 with the stock (non-inverted) flags: rotate +1, pitch −1,
        // forward +1, strafe +1 for both keys and mouse.
        float pitchIn = -(keyPitch * dt) - _mouseRotPitch.Y;
        float rotateIn = keyRotate * dt + _mouseRotPitch.X;
        float fwdIn = (keyFwd * dt + _mousePan.Y) * PanUnitsPerInput;
        float strafeIn = (keyStrafe * dt + _mousePan.X) * PanUnitsPerInput;
        float zoomIn = keyZoom * dt + _mouseZoom;
        float liftIn = keyLift * dt;
        _mouseRotPitch = Vector2.Zero; _mousePan = Vector2.Zero; _mouseZoom = 0;

        if (Mathf.Abs(zoomIn) > 100f) zoomIn = 0;
        if (Mathf.Abs(pitchIn) * AxisDominance < Mathf.Abs(rotateIn)) pitchIn = 0;
        if (Mathf.Abs(rotateIn) * AxisDominance < Mathf.Abs(pitchIn)) rotateIn = 0;

        // Speeds scale from ×1 (fully zoomed in) to ×2 (fully out) (007d49e0).
        float minRod = LimMinRod, maxRod = LimMaxRod;
        float zoomFrac = maxRod > minRod ? Mathf.Clamp((_current.Rod - minRod) / (maxRod - minRod), 0, 1) : 0;
        float f = 1f + zoomFrac;

        _target.Pitch = Mathf.Clamp(_target.Pitch + pitchIn * PitchSpeed, LimMinPitch, LimMaxPitch);
        _target.Yaw -= rotateIn * f * RotateSpeed;
        // Stock: yaw ±π/2. Extended: free rotation, kept in ±π (the glide turns the short way, 007cccb0).
        _target.Yaw = Extended ? Mathf.PosMod(_target.Yaw + Mathf.Pi, Mathf.Tau) - Mathf.Pi : Mathf.Clamp(_target.Yaw, MinYaw, MaxYaw);
        zoomIn = Mathf.Clamp(zoomIn, -MaxZoomInput, MaxZoomInput);
        _target.Rod = Mathf.Clamp(_target.Rod + (Mathf.Abs(_current.Rod) * ZoomRodFactor + 1f) * ZoomScale * zoomIn, minRod, maxRod);
        _target.Anchor.Z = Mathf.Clamp(_target.Anchor.Z + liftIn * LiftUnitsPerInput, _minZ, _maxZ);

        // Edge scrolling: speed ramps at combat_scroll_speed per second up to combat_max_scroll_speed,
        // scaled by (1 + zoom × combat_scroll_speed_zoom_factor) (007d2c80, 007d2d90).
        var edge = keyboardFree && _cinematic == null ? EdgeDirection() : Vector2.Zero;
        if (edge != Vector2.Zero)
        {
            _edgeHeld += dt;
            float s = Mathf.Min(_edgeHeld * ScrollSpeed, MaxScrollSpeed);
            float k = s * (1f + zoomFrac * ScrollZoomFactor) * PanUnitsPerInput * dt;
            fwdIn += edge.Y * k / f;
            strafeIn += edge.X * k / f;
        }
        else _edgeHeld = 0;

        if (fwdIn != 0 || strafeIn != 0)
        {
            // Pan along the ground projection of the view (007d45a0 forward, 007d4ae0 right).
            var fwd = new Vector2(-Mathf.Sin(_target.Yaw), Mathf.Cos(_target.Yaw));
            var right = new Vector2(Mathf.Cos(_target.Yaw), Mathf.Sin(_target.Yaw));
            var d = (fwd * fwdIn + right * strafeIn) * f * PanSpeed;
            _target.Anchor.X += d.X;
            _target.Anchor.Y += d.Y;
        }
        var pan = LimPan;
        _target.Anchor.X = Mathf.Clamp(_target.Anchor.X, pan.Position.X, pan.End.X);
        _target.Anchor.Y = Mathf.Clamp(_target.Anchor.Y, pan.Position.Y, pan.End.Y);

        if (_cinematic != null) return;
        Glide(dt);
        Apply();
    }

    void Glide(float dt)
    {
        _current.Rod = Approach(_target.Rod, _current.Rod, RodBase, dt, RodEps);
        _current.Pitch = Approach(_target.Pitch, _current.Pitch, AngleBase, dt, PitchEps);
        // Yaw turns the short way round (007cccb0 wraps the difference into ±π).
        float dYaw = _current.Yaw - _target.Yaw;
        _current.Yaw = float.IsFinite(dYaw) ? _target.Yaw + (Mathf.PosMod(dYaw + Mathf.Pi, Mathf.Tau) - Mathf.Pi) : _target.Yaw;
        _current.Yaw = Approach(_target.Yaw, _current.Yaw, AngleBase, dt, AngleEps);
        _current.Anchor.X = Approach(_target.Anchor.X, _current.Anchor.X, AnchorBase, dt, AnchorEps);
        _current.Anchor.Y = Approach(_target.Anchor.Y, _current.Anchor.Y, AnchorBase, dt, AnchorEps);
        _current.Anchor.Z = Approach(_target.Anchor.Z, _current.Anchor.Z, AnchorBase, dt, AnchorEps);
    }

    /// <summary>FUN_007ccc10 / 007cccb0: target + (current − target)·base^dt; inside eps the pose stays where it is (no snap).</summary>
    static float Approach(float target, float current, double basePerSecond, float dt, float eps)
    {
        if (Mathf.Abs(target - current) < eps) return current;
        return (float)(target + (current - target) * Math.Pow(basePerSecond, dt));
    }

    Vector2 EdgeDirection()
    {
        var vp = _arena.GetViewport();
        if (!vp.GetWindow().HasFocus()) return Vector2.Zero;
        var size = vp.GetVisibleRect().Size;
        var m = vp.GetMousePosition();
        if (m.X < 0 || m.Y < 0 || m.X > size.X || m.Y > size.Y) return Vector2.Zero;
        var dir = Vector2.Zero;
        if (m.X <= EdgePixels) dir.X = -1; else if (m.X >= size.X - 1 - EdgePixels) dir.X = 1;
        if (m.Y <= EdgePixels) dir.Y = 1; else if (m.Y >= size.Y - 1 - EdgePixels) dir.Y = -1;
        return dir;
    }

    // ---- dev input script (`--camera-script=`) --------------------------------------------------

    /// <summary>
    /// Scripted input for off-screen verification, steps separated by ';':
    /// `wait:S`, `hold:KEY:S` (KEY = up/down/left/right/pgup/pgdn/ins/del, optionally `ctrl+`),
    /// `wheel:N` (notches, negative = towards the user = zoom in), `rdrag:DX,DY`, `mdrag:DX,DY` (pixels).
    /// </summary>
    public static string? DevScript;
    Queue<string>? _devSteps;
    float _devWait;
    readonly HashSet<Key> _devHeld = new();

    bool Held(Key k) => Input.IsKeyPressed(k) || _devHeld.Contains(k);

    void RunDevScript(float dt)
    {
        if (DevScript == null) return;
        _devSteps ??= new Queue<string>(DevScript.Split(';', StringSplitOptions.RemoveEmptyEntries));
        if (_devWait > 0) { _devWait -= dt; if (_devWait > 0) return; _devHeld.Clear(); }
        while (_devSteps.Count > 0)
        {
            var p = _devSteps.Dequeue().Split(':');
            float Num(int i) => float.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
            switch (p[0])
            {
                case "wait": _devWait = Num(1); return;
                case "hold":
                    foreach (var name in p[1].Split('+'))
                        _devHeld.Add(name switch
                        {
                            "ctrl" => Key.Ctrl, "up" => Key.Up, "down" => Key.Down, "left" => Key.Left, "right" => Key.Right,
                            "pgup" => Key.Pageup, "pgdn" => Key.Pagedown, "ins" => Key.Insert, "del" => Key.Delete, _ => Key.None,
                        });
                    _devWait = Num(2);
                    return;
                case "wheel": _mouseZoom += Num(1) * WheelInputPerNotch; break;
                case "rdrag":
                {
                    var xy = p[1].Split(',');
                    var d = new Vector2(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)) * MouseAxisPerPixel;
                    _mouseRotPitch += new Vector2(d.X, -d.Y);
                    break;
                }
                case "mdrag":
                {
                    var xy = p[1].Split(',');
                    var d = new Vector2(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)) * MouseAxisPerPixel;
                    _mousePan += new Vector2(-d.X, d.Y);
                    break;
                }
            }
        }
    }

    // ---- mouse ---------------------------------------------------------------------------------

    /// <summary>
    /// Wheel zoom (−camera_zoom_mouse MOUSE_AXIS_Z), right-drag rotate/pitch (camera_rotate_mouse,
    /// −camera_pitch_mouse), middle-drag or CTRL+right-drag pan (−camera_strafe_mouse,
    /// +camera_forward_mouse). Returns true when the event was consumed by the camera.
    /// </summary>
    public bool HandleInput(InputEvent e)
    {
        if (_cinematic != null) return false;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                if (!wheel.Pressed) return true;
                _mouseZoom += wheel.ButtonIndex == MouseButton.WheelUp ? -WheelInputPerNotch : WheelInputPerNotch;
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rb:
                _rightDown = rb.Pressed;
                if (rb.Pressed) { _rightDragged = false; _rightDownAt = rb.Position; }
                return false; // the release still reaches the screen (info on a plain click)
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _middleDown = mb.Pressed;
                return true;
            case InputEventMouseMotion mm:
                var d = mm.Relative * MouseAxisPerPixel;
                if (_middleDown || (_rightDown && Input.IsKeyPressed(Key.Ctrl)))
                {
                    _mousePan += new Vector2(-d.X, d.Y);
                    if (_rightDown) _rightDragged = true;
                    return true;
                }
                if (_rightDown)
                {
                    // A press only becomes a drag after a few pixels, so a plain right click still opens info.
                    if (!_rightDragged && mm.Position.DistanceTo(_rightDownAt) < 4f) return false;
                    _rightDragged = true;
                    _mouseRotPitch += new Vector2(d.X, -d.Y);
                    return true;
                }
                return false;
        }
        return false;
    }

    // ---- cinematic override --------------------------------------------------------------------

    /// <summary>A combat track camera pose (eye position, rod 0) in engine space.</summary>
    public readonly record struct CinematicPose(Vector3 Eye, float Pitch, float Yaw, float FovDeg);

    public bool CinematicActive => _cinematic != null;

    public void SetCinematic(CinematicPose pose)
    {
        if (!float.IsFinite(pose.Pitch) || !float.IsFinite(pose.Yaw) || !float.IsFinite(pose.Eye.X) || !float.IsFinite(pose.Eye.Y)
            || !float.IsFinite(pose.Eye.Z)) return;
        _cinematic = pose;
        ApplyPose(0f, pose.Pitch, pose.Yaw, pose.Eye, pose.FovDeg);
    }

    /// <summary>Ends a cinematic: the operator glides from the cinematic pose back to the player's camera.</summary>
    public void EndCinematic()
    {
        if (_cinematic is not { } c) return;
        _cinematic = null;
        _current = new Pose { Rod = 0f, Pitch = c.Pitch, Yaw = c.Yaw, Anchor = c.Eye };
    }

    // ---- placement -----------------------------------------------------------------------------

    void Apply() => ApplyPose(_current.Rod, _current.Pitch, _current.Yaw, _current.Anchor, DefaultFovDeg);

    void ApplyPose(float rod, float pitch, float yaw, Vector3 anchorEngine, float fovDeg)
    {
        var cam = _arena.Camera;
        var fwd = new Vector3(-Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch));
        var up = new Vector3(Mathf.Sin(yaw) * Mathf.Sin(pitch), -Mathf.Cos(yaw) * Mathf.Sin(pitch), Mathf.Cos(pitch));
        var eye = anchorEngine - fwd * rod;
        var gEye = ToGodot(eye);
        cam.LookAtFromPosition(gEye, gEye + ToGodot(fwd), ToGodot(up));
        cam.Near = NearClip;
        cam.Far = FarClip;
        SetFov(cam, fovDeg);
    }

    /// <summary>
    /// The exe's projection (00cf7c30): with H/W ≤ 0.75 the FOV is horizontal; taller screens keep
    /// the vertical extent a 4:3 screen would have (tan(v/2) = 0.75·tan(fov/2)).
    /// Extension (user-requested ultrawide support): screens wider than 16:9 keep the vertical
    /// extent of 16:9 (tan(v/2) = 0.5625·tan(fov/2)) and show more at the sides (Hor+), so
    /// 4:3 to 16:9 match the original exactly.
    /// </summary>
    void SetFov(Camera3D cam, float fovDeg)
    {
        var size = _arena.GetViewport().GetVisibleRect().Size;
        float hw = size.X > 0 ? size.Y / size.X : 0.75f;
        if (hw < 0.5625f)
        {
            cam.KeepAspect = Camera3D.KeepAspectEnum.Height;
            cam.Fov = Mathf.RadToDeg(2f * Mathf.Atan(0.5625f * Mathf.Tan(Mathf.DegToRad(fovDeg) * 0.5f)));
        }
        else if (hw <= 0.75f)
        {
            cam.KeepAspect = Camera3D.KeepAspectEnum.Width;
            cam.Fov = fovDeg;
        }
        else
        {
            cam.KeepAspect = Camera3D.KeepAspectEnum.Height;
            cam.Fov = Mathf.RadToDeg(2f * Mathf.Atan(0.75f * Mathf.Tan(Mathf.DegToRad(fovDeg) * 0.5f)));
        }
    }

    public static Vector3 ToEngine(Vector3 g) => new(g.X, -g.Z, g.Y);
    public static Vector3 ToGodot(Vector3 e) => new(e.X, e.Z, -e.Y);
}
