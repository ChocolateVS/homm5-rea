using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// The exe's CWindowScrollBar (vtable 00e8c764) with its CWindowSlider (vtable 00e13f2c), built from the data
/// for every WindowScrollBar of a screen: ButtonLower / ButtonGreater / Slider / Lever are the parts the loader
/// appends from the Shared references (UiLoader.LoadParts) and draw with their own button visuals.
/// <list type="bullet">
/// <item>Range model: <see cref="UiSliderModel"/> (SetRange 0048f730, positions clamped by 0048f440).</item>
/// <item>Arrows (00c411e0): a press steps by ClickScrollDist (instance value when its flag is set, else the
/// shared one, at least 1: 00c40ee0 / 00c40e80), ButtonGreater forward, ButtonLower back (00c40780); with
/// special positions one step. Held, after 500 ms the bar moves continuously by elapsed ms × Speed × 0.001
/// whenever more than 10 ms have passed (00c40e20).</item>
/// <item>Track (00490310 / 00490460): a press on the slider outside the lever moves one page (one special
/// position) towards the mouse; held, it repeats after 500 ms and then every 10 ms until the lever reaches
/// the mouse or the mouse is over the lever.</item>
/// <item>Lever drag (004905e0 → 0048f9b0): the position follows the lever's offset on the free track.</item>
/// <item>Mouse wheel (mouse_wheel_scroll, 00490460): position += wheel × page (one special position per
/// notch).</item>
/// <item>Enabling (00c40ab0): arrows and slider are enabled only while the range exceeds the page and the
/// bar is enabled; HideDisabled also hides them; the lever is shown only while scrollable (0048f730).</item>
/// </list>
/// </summary>
public partial class UiScrollBar : UiNode
{
    public UiSliderModel Model { get; } = new();
    readonly bool _horizontal, _hideDisabled;
    readonly float _speed;
    readonly int _clickDist, _minLever, _maxLever;

    /// <summary>The notify interface (ISliderNotify, slider +0x138): raised with the new position.</summary>
    public event Action<float>? ValueChanged;
    /// <summary>Raised with <see cref="Position"/> (the special-position index) when the position changes.</summary>
    public event Action<int>? PositionChanged;

    // Held arrow (00c411e0 / 00c40e20): +1 greater, −1 lower, 0 none; elapsed ms and first-repeat flag.
    int _arrow;
    double _arrowMs;
    bool _arrowFirst;
    // Held track (00490460): direction and elapsed ms; target is re-read from the mouse each repeat.
    int _track;
    double _trackMs;
    bool _trackFirst;
    // Lever drag: offset of the grab point inside the lever.
    float? _grab;

    /// <summary>Wheel input per notch. The exe's MOUSE_AXIS_Z scale is untraced (docs/rules/CAMERA.md); same value as the cameras.</summary>
    public const float WheelPerNotch = 0.75f;

    public UiScrollBar(UiWindow window) : base(window)
    {
        var shared = window.RawShared;
        _speed = ReadFloat(shared?.Element("Speed"), 100);
        // 00c40ee0: an instance ClickScrollDist whose flag is unset takes the shared value; 00c40e80: at least 1.
        var inst = window.RawInstance?.Element("ClickScrollDist");
        bool instSet = H5.Core.Data.XmlHelpers.BoolValue(inst?.Element("Second"));
        int dist = instSet ? (int?)inst!.Element("First") ?? 0 : (int?)shared?.Element("ClickScrollDist")?.Element("First") ?? 0;
        _clickDist = Math.Max(1, dist);
        _hideDisabled = H5.Core.Data.XmlHelpers.BoolValue(window.RawInstance?.Element("HideDisabled"));
        var slider = window.Children.FirstOrDefault(c => c.Part == "Slider");
        if (slider != null)
        {
            _horizontal = H5.Core.Data.XmlHelpers.BoolValue(slider.RawShared?.Element("Horisontal"));
            _minLever = (int?)slider.RawShared?.Element("MinLeverSize") ?? 0;
            _maxLever = (int?)slider.RawShared?.Element("MaxLeverSize") ?? 0;
            Model.SetSpecialPositions((int?)slider.RawInstance?.Element("SpecialPositions") ?? 0);
        }
        // The children are built by the screen; parts are looked up once they exist.
        MouseFilter = MouseFilterEnum.Pass;
        SetProcess(true);
    }

    static float ReadFloat(System.Xml.Linq.XElement? e, float fallback) =>
        e != null && float.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;

    UiNode? Lower, Greater, Slider, Lever;

    public override void OnBuilt()
    {
        foreach (var n in GetChildren()) Register(n);
        UpdateParts();
    }

    void Register(Node n)
    {
        if (n is not UiNode u) return;
        switch (u.Window.Part)
        {
            case "ButtonLower": Lower = u; u.GuiInput += e => ArrowInput(e, -1); break;
            case "ButtonGreater": Greater = u; u.GuiInput += e => ArrowInput(e, 1); break;
            case "Slider":
                Slider = u;
                u.MouseFilter = MouseFilterEnum.Stop;
                u.GuiInput += TrackInput;
                foreach (var c in u.GetChildren())
                    if (c is UiNode l && l.Window.Part == "Lever") { Lever = l; l.GuiInput += LeverInput; }
                break;
        }
        UpdateParts();
    }

    // ---- Public API ----

    /// <summary>SetRange (slider interface slot 1, 0048f730), then the arrows' enabling (00c40bf0 → 00c40ab0).</summary>
    public void SetRange(float min, float max, float page)
    {
        Model.SetRange(min, max, page);
        UpdateParts();
    }

    /// <summary>SetPos without notification (slot 3).</summary>
    public void SetValue(float pos) { Model.SetPos(pos); UpdateParts(); }
    public float Value => Model.Pos;

    /// <summary>
    /// Discrete bars driven by code (value sliders such as the market amount or the custom-game time limit):
    /// <paramref name="count"/> special positions over a range with a page of one (0..count-1 scrollable).
    /// </summary>
    public void Setup(int count, int position)
    {
        count = Math.Max(1, count);
        Model.SetRange(0, count, 1);
        Model.SetSpecialPositions(count);
        if (count < 2) Model.SetPos(0); else Model.SetIndex(Math.Clamp(position, 0, count - 1));
        UpdateParts();
    }

    /// <summary>The special-position index (GetIndex, slot 11), or the rounded position without special positions.</summary>
    public int Position => Model.SpecialPositions > 1 ? Model.Index : (int)Math.Round(Model.Pos);

    protected override void OnEnabledChanged() => UpdateParts();

    /// <summary>Mouse wheel routed to this bar (by itself or by its container), in notches (positive = down/right).</summary>
    public void Wheel(float notches)
    {
        if (!Model.Scrollable || !Window.Enabled) return;
        if (Model.SpecialPositions > 1) { SetIndexNotify(Model.Index + Math.Sign(notches)); return; }
        SetPosNotify(Model.Pos + notches * WheelPerNotch * Model.Page);
    }

    // ---- Behaviour ----

    void SetPosNotify(float p)
    {
        bool changed = Model.SetPos(p);
        UpdateLever();
        if (changed) Notify();
    }

    void SetIndexNotify(int i)
    {
        if (Model.SpecialPositions < 2) return;
        bool changed = Model.SetIndex(Math.Clamp(i, 0, Model.SpecialPositions - 1));
        UpdateLever();
        if (changed) Notify();
    }

    void Notify()
    {
        ValueChanged?.Invoke(Model.Pos);
        PositionChanged?.Invoke(Position);
        SliderSound();
    }

    ulong _soundTick;

    /// <summary>
    /// 0048fd60, run after a notified move: at most every 400 ms (GetTickCount, slider +0x148) the slider sends the
    /// UIGameConsts command at +0xa4 (ScrollBarSound, UI/Sounds/ScrollBar: scroll_click).
    /// </summary>
    void SliderSound()
    {
        ulong now = Time.GetTicksMsec();
        if (_soundTick != 0 && now - _soundTick <= 400) return;
        _soundTick = now;
        UiAudio.Slider?.Invoke();
    }

    bool Usable => Model.Scrollable && Window.Enabled;

    void ArrowInput(InputEvent e, int dir)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed && Usable)
            {
                if (Model.SpecialPositions > 1) { SetIndexNotify(Model.Index + dir); return; }
                _arrow = dir; _arrowMs = 0; _arrowFirst = true;
                SetPosNotify(Model.Pos + dir * _clickDist);
            }
            else if (!mb.Pressed) _arrow = 0;
        }
        else if (e is InputEventMouseButton { Pressed: true } w && WheelNotches(w) is float n) Wheel(n);
    }

    void TrackInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb when Usable:
                _track = 0;
                TrackStep(mb.Position);
                _trackMs = 0; _trackFirst = true;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _track = 0;
                break;
            case InputEventMouseButton { Pressed: true } w when WheelNotches(w) is float n:
                Wheel(n);
                break;
        }
    }

    /// <summary>One track step towards the mouse (00490310 with jump = 0); false once the lever has reached it.</summary>
    bool TrackStep(Vector2 local)
    {
        if (Slider == null) return false;
        var (len, lever) = Lengths();
        float along = _horizontal ? local.X : local.Y;
        float target = Model.ValueAt(along - lever / 2f, len, lever);
        int dir = target > Model.Pos ? 1 : -1;
        if (_track != 0 && dir != _track) return false;
        if (target == Model.Pos) return false;
        _track = dir;
        if (Model.SpecialPositions > 1) SetIndexNotify(Model.Index + dir);
        else SetPosNotify(Model.Pos + dir * Model.Page);
        return true;
    }

    void LeverInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb when Usable:
                _grab = _horizontal ? mb.Position.X : mb.Position.Y;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _grab = null;
                break;
            case InputEventMouseMotion mm when _grab is float g && Lever != null && Slider != null:
            {
                var (len, lever) = Lengths();
                var inSlider = Lever.Position + mm.Position;
                float off = (_horizontal ? inSlider.X : inSlider.Y) - g;
                SetPosNotify(Model.ValueAt(off, len, lever));
                break;
            }
            case InputEventMouseButton { Pressed: true } w when WheelNotches(w) is float n:
                Wheel(n);
                break;
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } w && WheelNotches(w) is float n) { Wheel(n); AcceptEvent(); return; }
        base._GuiInput(e);
    }

    internal static float? WheelNotches(InputEventMouseButton mb) => mb.ButtonIndex switch
    {
        MouseButton.WheelDown => mb.Factor > 0 ? mb.Factor : 1,
        MouseButton.WheelUp => -(mb.Factor > 0 ? mb.Factor : 1),
        MouseButton.WheelRight => mb.Factor > 0 ? mb.Factor : 1,
        MouseButton.WheelLeft => -(mb.Factor > 0 ? mb.Factor : 1),
        _ => null,
    };

    public override void _Process(double delta)
    {
        double ms = delta * 1000;
        if (_arrow != 0)
        {
            if (!Usable || !Input.IsMouseButtonPressed(MouseButton.Left)) _arrow = 0;
            else
            {
                _arrowMs += ms;
                if (_arrowMs > (_arrowFirst ? 500 : 10))
                {
                    _arrowFirst = false;
                    SetPosNotify(Model.Pos + _arrow * (float)(_arrowMs * _speed * 0.001));
                    _arrowMs = 0;
                }
            }
        }
        if (_track != 0 && Slider != null)
        {
            if (!Usable || !Input.IsMouseButtonPressed(MouseButton.Left)) _track = 0;
            else
            {
                _trackMs += ms;
                var mouse = Slider.GetLocalMousePosition();
                bool overLever = Lever != null && new Rect2(Lever.Position, Lever.Size).HasPoint(mouse);
                if (overLever) _trackMs = 0;
                else if (_trackMs > (_trackFirst ? 500 : 10))
                {
                    _trackFirst = false;
                    _trackMs = 0;
                    if (!TrackStep(mouse)) _track = 0;
                }
            }
        }
    }

    (int len, int lever) Lengths()
    {
        if (Slider == null) return (0, 0);
        var r = Slider.Window.Rect;
        int len = _horizontal ? r.W : r.H;
        return (len, Model.LeverLength(len, _minLever, _maxLever));
    }

    public override void OnPlaced() => UpdateLever();

    /// <summary>00c40ab0: enabling / hiding of the parts; then the lever.</summary>
    void UpdateParts()
    {
        bool on = Usable;
        foreach (var p in new[] { Lower, Greater, Slider })
        {
            if (p == null) continue;
            p.Enabled = on;
            if (_hideDisabled) p.Visible = on;
        }
        if (Lever != null) { Lever.Visible = Model.Scrollable; Lever.Enabled = on; }
        UpdateLever();
    }

    /// <summary>Lever placement (0048f440): offset and length along the slider; the other axis is the slider's.</summary>
    void UpdateLever()
    {
        if (Slider == null || Lever == null) return;
        var (len, lever) = Lengths();
        int off = Model.LeverOffset(len, lever);
        var p = Lever.Window.Placement;
        Lever.Window.Placement = p.MergedWith(new UiPlacement
        {
            Position = (_horizontal ? new Int2(off, p.Position.Value.Y) : new Int2(p.Position.Value.X, off), true),
            Size = (_horizontal ? new Int2(lever, p.Size.Value.Y) : new Int2(p.Size.Value.X, lever), true),
        });
        var sr = Slider.Window.Rect;
        Lever.Window.Rect = UiLayout.Place(Lever.Window.Placement, sr);
        Lever.Position = new Vector2(Lever.Window.Rect.X - sr.X, Lever.Window.Rect.Y - sr.Y);
        Lever.Size = new Vector2(Lever.Window.Rect.W, Lever.Window.Rect.H);
    }
}
