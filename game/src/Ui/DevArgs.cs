using Godot;

namespace H5Reborn.Ui;

/// <summary>Command-line switches (after `--`) that drive the front end for automated tests.</summary>
public static class DevArgs
{
    public static string? Goto, Name, Connect, CreateRoom;
    public static bool Host, JoinFirst, Ready, AutoStart;
    /// <summary>Multiplayer test switches: `--master=URL`, `--host-online`, `--join-code=CODE`,
    /// `--join-lan-first`, `--net-loopback` (bind hosts/LAN discovery to 127.0.0.1 only, so automated
    /// tests never open the machine's network ports).</summary>
    public static string? Master, JoinCode;
    public static bool HostOnline, JoinLanFirst, NetLoopback;
    public static int? Preset;
    /// <summary>`--click=x,y@seconds` (repeatable): synthetic left clicks for UI tests.</summary>
    public static readonly List<(Vector2 Pos, double At)> Clicks = new();
    /// <summary>
    /// Further synthetic mouse input for UI tests (repeatable, same `@seconds` timing): `--wheel=x,y,notches`
    /// (positive = down), `--press=x,y` / `--release=x,y` (left button held between them, e.g. scroll-bar arrow or
    /// track repeat), `--move=x,y` (motion, with the left button down while pressed: lever drags), `--key=NAME@s`
    /// (a key press and release, Godot key name such as K or Escape).
    /// </summary>
    public static readonly List<(string Op, Vector2 Pos, float Arg, double At)> MouseOps = new();
    public static readonly List<(string Key, double At)> Keys = new();
    static bool _parsed;

    public static void Parse()
    {
        if (_parsed) return;
        _parsed = true;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            string V(string p) => a[p.Length..];
            if (a.StartsWith("--goto=")) Goto = V("--goto=");
            else if (a.StartsWith("--name=")) Name = V("--name=");
            else if (a.StartsWith("--connect=")) Connect = V("--connect=");
            else if (a.StartsWith("--create-room=")) CreateRoom = V("--create-room=");
            else if (a.StartsWith("--preset=") && int.TryParse(V("--preset="), out var p)) Preset = p;
            else if (a.StartsWith("--click="))
            {
                var parts = V("--click=").Split('@');
                var xy = parts[0].Split(',');
                Clicks.Add((new Vector2(float.Parse(xy[0]), float.Parse(xy[1])),
                    parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1));
            }
            else if (a.StartsWith("--wheel=") || a.StartsWith("--press=") || a.StartsWith("--release=") || a.StartsWith("--move="))
            {
                int eq = a.IndexOf('=');
                var parts = a[(eq + 1)..].Split('@');
                var v = parts[0].Split(',');
                MouseOps.Add((a[2..eq], new Vector2(float.Parse(v[0]), float.Parse(v[1])),
                    v.Length > 2 ? float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture) : 0,
                    parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1));
            }
            else if (a.StartsWith("--key="))
            {
                var parts = V("--key=").Split('@');
                Keys.Add((parts[0], parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1));
            }
            else if (a.StartsWith("--master=")) Master = V("--master=");
            else if (a.StartsWith("--join-code=")) JoinCode = V("--join-code=");
            else if (a == "--host-online") { Host = true; HostOnline = true; }
            else if (a == "--join-lan-first") JoinLanFirst = true;
            else if (a == "--net-loopback") NetLoopback = true;
            else if (a == "--host") Host = true;
            else if (a == "--join-first") JoinFirst = true;
            else if (a == "--ready") Ready = true;
            else if (a == "--autostart") AutoStart = true;
        }
    }

    static bool _scheduled, _held;

    /// <summary>
    /// Starts the `--click=` / `--wheel=` / `--press=` / `--release=` / `--move=` timers once, relative to now:
    /// from the main menu, or once the adventure map is up when the run starts there (`--adventure=`).
    /// </summary>
    public static void ScheduleInput(SceneTree tree, bool fromMenu = false)
    {
        Parse();
        if (_scheduled || fromMenu && OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--adventure="))) return;
        _scheduled = true;
        foreach (var (pos, at) in Clicks)
            tree.CreateTimer(at, processAlways: true, ignoreTimeScale: true).Timeout += () => MainMenu.Click(pos);
        foreach (var (op, pos, arg, at) in MouseOps)
            tree.CreateTimer(at, processAlways: true, ignoreTimeScale: true).Timeout += () => Mouse(op, pos, arg);
        foreach (var (key, at) in Keys)
            tree.CreateTimer(at, processAlways: true, ignoreTimeScale: true).Timeout += () =>
            {
                GD.Print($"[H5] dev input key {key}");
                var code = OS.FindKeycodeFromString(key);
                foreach (var pressed in new[] { true, false })
                    Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = code, Pressed = pressed });
            };
    }

    static void Mouse(string op, Vector2 pos, float arg)
    {
        GD.Print($"[H5] dev input {op} {pos} {arg}");
        var mask = _held ? MouseButtonMask.Left : 0;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos, ButtonMask = mask });
        switch (op)
        {
            case "wheel":
                var button = arg < 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
                for (int i = 0; i < Math.Max(1, (int)Math.Abs(arg)); i++)
                    foreach (var pressed in new[] { true, false })
                        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = button, Pressed = pressed, Factor = 1 });
                break;
            case "press":
                _held = true;
                Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
                break;
            case "release":
                _held = false;
                Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = false });
                break;
        }
    }
}