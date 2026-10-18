using Godot;
using H5Reborn.Adventure;

namespace H5Reborn.Interaction;

/// <summary>
/// Dev/test: `-- --input-test=STEP;STEP;…` replays real input events on the adventure map and saves
/// screenshots, to verify interactions off-screen. Each STEP is `seconds:op[:arg]`, where op is
/// click|dbl|rclick (arg = "x,y", "hero", "town", "herolist0", "townlist0") — dbl sends a press,
/// release, then a press flagged as double click like the OS does — key (arg such as "ctrl+1", "t",
/// "escape", "f4"), or shot (arg = PNG path). Times are seconds after the map opened.
/// </summary>
public partial class InputTestDriver : Node
{
    readonly AdventureScreen _adv;
    readonly List<(double At, string Op, string Arg)> _steps = new();
    double _t;
    int _next;

    public static void AttachIfRequested(AdventureScreen adv)
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--input-test="))
            {
                var d = new InputTestDriver(adv, a["--input-test=".Length..]);
                adv.GetTree().Root.CallDeferred(Node.MethodName.AddChild, d);
                return;
            }
    }

    InputTestDriver(AdventureScreen adv, string script)
    {
        _adv = adv;
        ProcessMode = ProcessModeEnum.Always;
        foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = step.Split(':', 3);
            _steps.Add((double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p[1], p.Length > 2 ? p[2] : ""));
        }
    }

    public override void _Process(double delta)
    {
        _t += delta;
        while (_next < _steps.Count && _steps[_next].At <= _t)
        {
            var (_, op, arg) = _steps[_next++];
            try { Run(op, arg); }
            catch (Exception e) { GD.PushError($"[input-test] {op}:{arg} failed: {e.Message}"); }
        }
    }

    void Run(string op, string arg)
    {
        GD.Print($"[input-test] t={_t:0.0} {op} {arg}");
        switch (op)
        {
            case "click": Mouse(Pos(arg), MouseButton.Left, dbl: false); break;
            case "dbl":
                Mouse(Pos(arg), MouseButton.Left, dbl: false);
                Mouse(Pos(arg), MouseButton.Left, dbl: true);
                break;
            case "rclick": Mouse(Pos(arg), MouseButton.Right, dbl: false); break;
            case "move":
                var mp = Pos(arg);
                Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = mp, GlobalPosition = mp });
                GetViewport().WarpMouse(mp);
                break;
            case "rdown":
            case "rup":
                var rp = Pos(arg);
                Godot.Input.ParseInputEvent(new InputEventMouseButton { Position = rp, GlobalPosition = rp, ButtonIndex = MouseButton.Right, Pressed = op == "rdown", ButtonMask = op == "rdown" ? MouseButtonMask.Right : 0 });
                break;
            case "key": Key(arg); break;
            case "shot":
                var img = GetViewport().GetTexture().GetImage();
                img.SavePng(arg);
                GD.Print($"[input-test] screenshot {arg}");
                break;
            case "log":
                GD.Print($"[input-test] dialogs={_adv.DialogLayer.GetChildCount()} town={(Town.TownScreen.Current != null)} selected={_adv.SelectedHeroId}");
                break;
            case "quit": GetTree().Quit(); break;
        }
    }

    Vector2 Pos(string arg)
    {
        var cam = _adv.World.Camera;
        switch (arg)
        {
            case "hero":
                var h = _adv.Session.S.Hero(_adv.SelectedHeroId) ?? _adv.MyHeroes().First();
                return cam.UnprojectPosition(_adv.World.TileCenter(h.Pos) + Vector3.Up * 0.8f);
            case "town":
                var t = _adv.MyTowns().First();
                return cam.UnprojectPosition(_adv.World.TileCenter(t.Pos) + Vector3.Up * 1.5f);
            case "monster":
                var hh = _adv.Session.S.Hero(_adv.SelectedHeroId) ?? _adv.MyHeroes().First();
                var mon = _adv.Session.Map.Objects.Where(o => o.Kind == H5.Adventure.Map.ObjectKind.Monster && o.Pos.Floor == hh.Pos.Floor
                        && !(_adv.Session.S.Objects.TryGetValue(o.Index, out var os) && os.Removed))
                    .OrderBy(o => Math.Abs(o.Pos.X - hh.Pos.X) + Math.Abs(o.Pos.Y - hh.Pos.Y)).First();
                _adv.World.Camera.CenterOn(mon.Pos);
                return cam.UnprojectPosition(_adv.World.TileCenter(mon.Pos) + Vector3.Up * 0.8f);
            case var s when s.StartsWith("herolist") || s.StartsWith("townlist"):
                int i = int.Parse(s[8..]);
                var col = _adv.Hud.Screen.Find($"TownHeroPanel/Item_{i + 1:00}");
                var btn = col?.FindChild(s.StartsWith("hero") ? "HeroBtn" : "TownBtn", true, false) as Control;
                return btn != null ? btn.GetGlobalRect().GetCenter() : Vector2.Zero;
            default:
                var xy = arg.Split(',');
                return new Vector2(float.Parse(xy[0]), float.Parse(xy[1]));
        }
    }

    void Mouse(Vector2 pos, MouseButton button, bool dbl)
    {
        var motion = new InputEventMouseMotion { Position = pos, GlobalPosition = pos };
        Godot.Input.ParseInputEvent(motion);
        Godot.Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = button, Pressed = true, DoubleClick = dbl, ButtonMask = button == MouseButton.Left ? MouseButtonMask.Left : MouseButtonMask.Right });
        Godot.Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = button, Pressed = false });
    }

    void Key(string arg)
    {
        var parts = arg.ToLowerInvariant().Split('+');
        var ev = new InputEventKey { Pressed = true };
        foreach (var p in parts)
        {
            switch (p)
            {
                case "ctrl": ev.CtrlPressed = true; break;
                case "shift": ev.ShiftPressed = true; break;
                case "alt": ev.AltPressed = true; break;
                default: ev.Keycode = OS.FindKeycodeFromString(p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]); break;
            }
        }
        Godot.Input.ParseInputEvent(ev);
        var up = (InputEventKey)ev.Duplicate();
        up.Pressed = false;
        Godot.Input.ParseInputEvent(up);
    }
}
