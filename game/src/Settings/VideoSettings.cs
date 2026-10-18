using Godot;

namespace H5Reborn.Settings;

/// <summary>
/// Display settings: window mode, resolution and UI scale, persisted in
/// user://video.cfg and applied live.
///
/// Defaults: full screen at 1920x1080 (or the desktop size on smaller monitors);
/// the original also defaulted to full screen (gfx_fullscreen=1). Godot cannot
/// switch the monitor's video mode, so a full-screen resolution below the desktop
/// size renders the game at that size and upscales it, which is what a mode
/// switch looked like on LCDs.
///
/// UI scale is a deliberate, user-requested extension of the original: the stock
/// UI is laid out in real pixels (authored for 1024x768) and would shrink on
/// high-DPI screens. Layout keeps the original anchoring rules; the laid-out UI is
/// then scaled uniformly. "Auto" picks the largest step not above height/1080.
/// </summary>
public static class VideoSettings
{
    public enum DisplayMode { Fullscreen, Windowed, Borderless }

    const string File = "user://video.cfg";
    public static readonly float[] UiScaleSteps = { 1f, 1.25f, 4f / 3f, 1.5f, 1.75f, 2f, 2.5f, 3f, 4f };

    public static DisplayMode Mode { get; set; } = DisplayMode.Fullscreen;
    /// <summary>Requested resolution; (0,0) means the desktop size.</summary>
    public static Vector2I Resolution { get; set; } = new(1920, 1080);
    /// <summary>0 = automatic, otherwise a fixed UI scale factor.</summary>
    public static float UiScaleSetting { get; set; }

    /// <summary>Raised when the effective UI scale or render size may have changed.</summary>
    public static event Action? Changed;

    static bool _loaded, _cliWindow;
    static float? _cliUiScale;
    /// <summary>Dev/test `--window-size=WxH`: an exact borderless window size (not clamped to the monitor) for screenshots.</summary>
    static Vector2I? _cliWindowSize;

    static Window Root => ((SceneTree)Engine.GetMainLoop()).Root;

    public static Vector2I DesktopSize => DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());

    /// <summary>Size the 2D UI is laid out for (the render size, before the UI scale).</summary>
    public static Vector2 RenderSize => Root.ContentScaleMode == Window.ContentScaleModeEnum.Viewport
        ? Root.ContentScaleSize : Root.Size;

    public static float UiScale
    {
        get
        {
            if (_cliUiScale is { } cli) return cli;
            if (UiScaleSetting > 0) return UiScaleSetting;
            return AutoUiScale(RenderSize.Y);
        }
    }

    public static float AutoUiScale(float height)
    {
        float wanted = height / 1080f + 0.001f;
        return UiScaleSteps.LastOrDefault(s => s <= wanted, 1f);
    }

    /// <summary>Resolutions offered in the options: common modes up to the desktop size, plus the desktop size.</summary>
    public static List<Vector2I> AvailableResolutions()
    {
        var desktop = DesktopSize;
        var modes = new[]
        {
            new Vector2I(1024, 768), new(1280, 720), new(1280, 800), new(1280, 1024), new(1366, 768), new(1440, 900),
            new(1600, 900), new(1680, 1050), new(1920, 1080), new(1920, 1200), new(2560, 1080), new(2560, 1440),
            new(2560, 1600), new(3440, 1440), new(3840, 1600), new(3840, 2160), new(5120, 1440), new(5120, 2160),
        };
        return modes.Append(desktop).Where(m => m.X <= desktop.X && m.Y <= desktop.Y).Distinct()
            .OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
    }

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var cfg = new ConfigFile();
        if (cfg.Load(File) == Error.Ok)
        {
            Mode = Enum.TryParse<DisplayMode>((string)cfg.GetValue("video", "mode", Mode.ToString()), out var m) ? m : Mode;
            Resolution = new Vector2I((int)cfg.GetValue("video", "width", Resolution.X), (int)cfg.GetValue("video", "height", Resolution.Y));
            UiScaleSetting = (float)cfg.GetValue("video", "ui_scale", UiScaleSetting);
        }
        // Explicit window placement on the command line (tests, screenshots) keeps that window as is.
        var args = OS.GetCmdlineArgs();
        _cliWindow = args.Any(a => a is "--resolution" or "--position" or "-w" or "--windowed" || a.StartsWith("--resolution="));
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--ui-scale=") && float.TryParse(a["--ui-scale=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0)
                _cliUiScale = s;
            else if (a.StartsWith("--window-size=") && a["--window-size=".Length..].Split('x') is [var ws, var hs]
                     && int.TryParse(ws, out var wv) && int.TryParse(hs, out var hv))
                _cliWindowSize = new Vector2I(wv, hv);
        _cliWindow |= _cliWindowSize != null;
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("video", "mode", Mode.ToString());
        cfg.SetValue("video", "width", Resolution.X);
        cfg.SetValue("video", "height", Resolution.Y);
        cfg.SetValue("video", "ui_scale", UiScaleSetting);
        cfg.Save(File);
    }

    /// <summary>Applies the window mode/size (unless the command line fixed the window) and the render scaling.</summary>
    public static void Apply()
    {
        Load();
        var root = Root;
        if (_cliWindowSize is { } exact)
        {
            root.Mode = Window.ModeEnum.Windowed;
            root.Borderless = true;
            root.Size = exact;
        }
        else if (!_cliWindow && !OS.HasFeature("web"))
        {
            var desktop = DesktopSize;
            var res = Resolution.X <= 0 || Resolution.Y <= 0 || Resolution.X > desktop.X || Resolution.Y > desktop.Y ? desktop : Resolution;
            switch (Mode)
            {
                case DisplayMode.Fullscreen:
                    root.Borderless = false;
                    root.Mode = Window.ModeEnum.ExclusiveFullscreen;
                    break;
                case DisplayMode.Borderless:
                    root.Mode = Window.ModeEnum.Windowed;
                    root.Borderless = true;
                    root.Size = desktop;
                    root.Position = DisplayServer.ScreenGetPosition(DisplayServer.WindowGetCurrentScreen());
                    break;
                default:
                    root.Mode = Window.ModeEnum.Windowed;
                    root.Borderless = false;
                    root.Size = res;
                    root.Position = DisplayServer.ScreenGetPosition(DisplayServer.WindowGetCurrentScreen()) + (desktop - res) / 2;
                    break;
            }
        }
        ApplyScaling();
        Changed?.Invoke();
    }

    /// <summary>
    /// Real-pixel rendering (the original UI is pixel-exact), except a full-screen
    /// resolution below the desktop size, which renders at that size and upscales.
    /// Screens call this instead of disabling content scaling themselves.
    /// </summary>
    public static void ApplyScaling()
    {
        Load();
        var root = Root;
        var desktop = DesktopSize;
        bool lowRes = !_cliWindow && Mode == DisplayMode.Fullscreen && Resolution.X > 0 && Resolution.Y > 0
                      && (Resolution.X < desktop.X || Resolution.Y < desktop.Y);
        if (lowRes)
        {
            root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
            root.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
            root.ContentScaleSize = Resolution;
        }
        else root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
    }
}
