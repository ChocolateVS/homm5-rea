namespace H5.Adventure.Engine;

/// <summary>Rod / pitch / yaw ranges of an SCameraLimits block (radians; yaw 0..0 = free).</summary>
public readonly record struct CameraRange(float MinRod, float MaxRod, float MinPitch, float MaxPitch, float MinYaw, float MaxYaw)
{
    public bool YawFree => MinYaw == 0 && MaxYaw == 0;
}

/// <summary>Camera pose: rod (distance), pitch (negative looks down), yaw (0 looks north, +Y), anchor (world units).</summary>
public struct CameraPose
{
    public float Rod, Pitch, Yaw, X, Y;
}

/// <summary>
/// The adventure ("mission") camera of H5_Game.exe: CCameraOperator::Update (007d4ee0) with the mission
/// configurator (vtable 00e44688: limits 007d3180, default pose 007d3310) and the profile values of
/// profiles/autoexec_a2.cfg (`mission_camera_*`, parsed by 0058a930 / 0058a790 / 0058ae10…, stored at
/// 00f338f8 / 00f33900 / 00f3393c / 00f339f0). Pure math; docs/rules/CAMERA.md "Adventure camera".
/// Coordinates are the exe's: x east, y north, 2 world units per tile.
/// </summary>
public sealed class MissionCamera
{
    const float Deg = MathF.PI / 180f;

    // ---- autoexec_a2.cfg ------------------------------------------------------------------------

    /// <summary>mission_camera_def_pos 50 -53 16 25 (FOV, pitch, yaw, rod).</summary>
    public const float Fov = 50f, DefRod = 25f;
    public static readonly float DefPitch = -53f * Deg, DefYaw = 16f * Deg;

    /// <summary>mission_camera_limits 50 10 70 -55 -30 0 0 and mission_camera_softlimits 50 11 69 -53 -32 0 0.</summary>
    public static readonly CameraRange StockHard = new(10, 70, -55f * Deg, -30f * Deg, 0, 0);
    public static readonly CameraRange StockSoft = new(11, 69, -53f * Deg, -32f * Deg, 0, 0);

    /// <summary>
    /// H5Reborn extension ("Extended map zoom", user-requested, not in the original): zoom out to 160 and
    /// tilt from nearly top-down to a low view. Same 1-unit soft margins as the stock limits.
    /// </summary>
    public static readonly CameraRange ExtendedHard = new(10, 160, -85f * Deg, -20f * Deg, 0, 0);
    public static readonly CameraRange ExtendedSoft = new(11, 159, -84f * Deg, -21f * Deg, 0, 0);

    /// <summary>SCameraLimits speeds after autoexec (mission_camera_zoom_speed 0.3, _pitch_speed 5, _yaw_speed 1.5,
    /// _scroll_speed 1.4; attenuation and zoom acceleration keep their defaults 1 and 5, DAT_00f33918/28).</summary>
    public const float Attenuation = 1f, ScrollSpeed = 1.4f, ZoomAccel = 5f, ZoomSpeed = 0.3f, YawSpeed = 1.5f, PitchSpeed = 5f;

    /// <summary>adventure_scroll_speed (default 1, 005a00a0), adventure_max_scroll_speed 3, adventure_scroll_speed_zoom_factor 0.</summary>
    public const float EdgeScrollSpeed = 1f, EdgeMaxScrollSpeed = 3f, EdgeScrollZoomFactor = 0f;

    // ---- exe constants --------------------------------------------------------------------------

    const float PanUnitsPerInput = 10f;   // DAT_00e22820
    const float MaxZoomInput = 5f;        // clamp in 007d4ee0
    const float IgnoreZoomAbove = 100f;   // DAT_00e182c0
    const float AxisDominance = 10f;      // DAT_00e22820 (rotate/pitch axis lock)
    const float MaxStep = 0.1f;           // 100 ms frame cap

    /// <summary>Glide (007cccb0 / 007ccc10): remaining fraction per second and stop thresholds.</summary>
    const double RodBase = 1.0 / 300.0, AngleBase = 1.0 / 9000.0, AnchorBase = 1.1111111e-4;
    const float RodEps = 0.02f, PitchEps = 0.01f, YawEps = 1e-5f, AnchorEps = 0.03f;

    /// <summary>Soft-limit spring-back (007d4340): base 0.1 per second, stop 0.03 (rod, pitch) / 1e-5 (yaw).</summary>
    const double SpringBase = 0.1;
    const float SpringEps = 0.03f, SpringYawEps = 1e-5f;

    // ---- state ----------------------------------------------------------------------------------

    public CameraRange Hard = StockHard, Soft = StockSoft;
    /// <summary>Map extent in world units (2 × tiles): the configurator's pan rectangle 0..2W × 0..2H (007d3180).</summary>
    public float MapWidth, MapHeight;
    public CameraPose Target, Current;
    float _edgeHeld = -1;

    public MissionCamera(int tilesX, int tilesY)
    {
        MapWidth = 2f * tilesX;
        MapHeight = 2f * tilesY;
        // Default pose 007d3310: def_pos rod/pitch/yaw, anchor at the map centre (W, H).
        Target = new CameraPose { Rod = DefRod, Pitch = DefPitch, Yaw = DefYaw, X = tilesX, Y = tilesY };
        Current = Target;
    }

    /// <summary>set_camera_default_pos (HOME, 0058c280): def_pos rod/pitch/yaw, anchor kept, applied at once (operator +0x50 with snap).</summary>
    public void ResetToDefault()
    {
        Current.Rod = DefRod; Current.Pitch = DefPitch; Current.Yaw = DefYaw;
        Target = Current;
    }

    public void Snap() => Current = Target;

    /// <summary>Horizontal unit vectors of the view: ground-forward (007d45a0) and right (007d4ae0).</summary>
    public static (float fx, float fy, float rx, float ry) GroundAxes(float yaw) =>
        (-MathF.Sin(yaw), MathF.Cos(yaw), MathF.Cos(yaw), MathF.Sin(yaw));

    /// <summary>Pan/rotate speed factor 007d49e0: 1 + (rod − softMin)/(softMax − softMin), not clamped above.
    /// Always from the stock soft range so the extension keeps the stock feel inside the stock range.</summary>
    public static float SpeedFactor(float rod)
    {
        float lo = MathF.Max(0, StockSoft.MinRod), hi = MathF.Min(1000, StockSoft.MaxRod);
        return lo != hi && rod > lo ? 1f + (rod - lo) / (hi - lo) : 1f;
    }

    /// <summary>
    /// One operator update. Inputs are command units for this frame (keys: level × seconds; mouse: axis units),
    /// signs as the input layer delivers them (007dd490: rotate +, pitch −, forward +, strafe +).
    /// </summary>
    public void Step(float dt, float rotateIn, float pitchIn, float forwardIn, float strafeIn, float zoomIn)
    {
        dt = MathF.Min(dt, MaxStep);
        if (dt <= 0) return;
        if (MathF.Abs(zoomIn) > IgnoreZoomAbove) zoomIn = 0;
        if (MathF.Abs(pitchIn) * AxisDominance < MathF.Abs(rotateIn)) pitchIn = 0;
        if (MathF.Abs(rotateIn) * AxisDominance < MathF.Abs(pitchIn)) rotateIn = 0;

        float f = SpeedFactor(Current.Rod);
        Target.Pitch = SoftAdd(Target.Pitch, Soft.MinPitch, Soft.MaxPitch, Hard.MinPitch, Hard.MaxPitch, pitchIn * PitchSpeed, dt, SpringEps);
        float dYaw = f * YawSpeed * rotateIn;
        if (Hard.YawFree && Soft.YawFree) Target.Yaw -= dYaw;
        else Target.Yaw = SoftAdd(Target.Yaw, Soft.MinYaw, Soft.MaxYaw, Hard.MinYaw, Hard.MaxYaw, -dYaw, dt, SpringYawEps);
        zoomIn = Math.Clamp(zoomIn, -MaxZoomInput, MaxZoomInput);
        Target.Rod = SoftAdd(Target.Rod, Soft.MinRod, Soft.MaxRod, Hard.MinRod, Hard.MaxRod,
            (MathF.Abs(Current.Rod) * ZoomAccel + 1f) * ZoomSpeed * zoomIn, dt, SpringEps);

        // Pan along the ground axes of the rendered view (007d45a0 / 007d4ae0 → 007d4a70: × f × scroll speed).
        var (fx, fy, rx, ry) = GroundAxes(Current.Yaw);
        float k = f * ScrollSpeed * PanUnitsPerInput;
        Target.X += (fx * forwardIn + rx * strafeIn) * k;
        Target.Y += (fy * forwardIn + ry * strafeIn) * k;
        ClampAnchor(ref Target);

        Glide(dt * Attenuation);
        ClampAnchor(ref Current);
    }

    /// <summary>
    /// Screen-edge scrolling (adventure screen 0059e1f0 → 007d2c80 / 007d2d90): the speed starts at
    /// adventure_scroll_speed, then grows with the time the cursor has been at the edge up to the max;
    /// ×(1 + zoom fraction × zoom factor 0) × 10 along the ground axes. The new anchor is clamped to
    /// 1 … 2W − 1 and applied at once (operator +0x50 with snap), so it cancels a running glide.
    /// Returns true when it scrolled.
    /// </summary>
    public bool EdgeScroll(float dt, int dirX, int dirY)
    {
        if (dirX == 0 && dirY == 0) { _edgeHeld = -1; return false; }
        float s = _edgeHeld < 0 ? EdgeScrollSpeed : MathF.Min(_edgeHeld * EdgeScrollSpeed, EdgeMaxScrollSpeed);
        _edgeHeld = MathF.Max(_edgeHeld, 0) + MathF.Min(dt, MaxStep);
        float zoomFrac = Hard.MaxRod > Hard.MinRod ? (Hard.MaxRod - Current.Rod) / (Hard.MaxRod - Hard.MinRod) : 1f;   // 007d47f0
        float k = s * (zoomFrac * EdgeScrollZoomFactor + 1f) * PanUnitsPerInput * MathF.Min(dt, MaxStep);
        var (fx, fy, rx, ry) = GroundAxes(Current.Yaw);
        Current.X = Math.Clamp(Current.X + (fx * dirY + rx * dirX) * k, 1f, MathF.Max(1f, MapWidth - 1f));
        Current.Y = Math.Clamp(Current.Y + (fy * dirY + ry * dirX) * k, 1f, MathF.Max(1f, MapHeight - 1f));
        Target = Current;
        return true;
    }

    void ClampAnchor(ref CameraPose p)
    {
        p.X = Math.Clamp(p.X, 0, MapWidth);
        p.Y = Math.Clamp(p.Y, 0, MapHeight);
    }

    void Glide(float dt)
    {
        Current.Rod = Approach(Target.Rod, Current.Rod, RodBase, dt, RodEps);
        Current.Pitch = Approach(Target.Pitch, Current.Pitch, AngleBase, dt, PitchEps);
        // Yaw turns the short way round (007cccb0 wraps the difference into ±π first).
        while (Current.Yaw - Target.Yaw > MathF.PI) Current.Yaw -= 2 * MathF.PI;
        while (Current.Yaw - Target.Yaw < -MathF.PI) Current.Yaw += 2 * MathF.PI;
        Current.Yaw = Approach(Target.Yaw, Current.Yaw, AngleBase, dt, YawEps);
        Current.X = Approach(Target.X, Current.X, AnchorBase, dt, AnchorEps);
        Current.Y = Approach(Target.Y, Current.Y, AnchorBase, dt, AnchorEps);
    }

    /// <summary>FUN_007ccc10: target + (current − target)·base^dt, snapping inside eps.</summary>
    public static float Approach(float target, float current, double basePerSecond, float dt, float eps)
    {
        if (MathF.Abs(target - current) < eps) return target;
        return (float)(target + (current - target) * Math.Pow(basePerSecond, dt));
    }

    /// <summary>
    /// FUN_007d4340: adds input to a value with hard limits; beyond the soft limit the input towards the
    /// hard limit fades linearly to zero, and with no input the value springs back inside the soft range.
    /// </summary>
    public static float SoftAdd(float v, float softMin, float softMax, float hardMin, float hardMax, float input, float dt, float eps)
    {
        if (v < softMin && input < 0 && softMin != hardMin) input *= (v - hardMin) / (softMin - hardMin);
        if (v > softMax && input > 0 && softMax != hardMax) input *= (v - hardMax) / (softMax - hardMax);
        v = Math.Clamp(v + input, hardMin, hardMax);
        if (input == 0)
        {
            if (v < softMin) v = Approach(softMin, v, SpringBase, dt, eps);
            else if (v > softMax) v = Approach(softMax, v, SpringBase, dt, eps);
        }
        return v;
    }
}
