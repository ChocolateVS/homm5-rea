using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// WindowEditLine: single-line text input drawn with the original bitmap fonts
/// (style from the &lt;edit&gt; markup macro) and a blinking caret.
/// </summary>
public partial class UiEditLine : UiNode
{
    string _value = "";
    double _blink;
    public int MaxLength { get; set; } = 128;
    public bool Password { get; set; }
    public string Style { get; set; } = "<edit>";

    public event Action<string>? Submitted;
    public event Action<string>? Changed;

    public string Value
    {
        get => _value;
        set { _value = value ?? ""; Changed?.Invoke(_value); QueueRedraw(); }
    }

    public UiEditLine(UiWindow w) : base(w)
    {
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Ibeam;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        _blink += delta;
        if (HasFocus()) QueueRedraw();
    }

    public override void _Draw()
    {
        base._Draw();
        var shown = Password ? new string('*', _value.Length) : _value;
        var layout = BitmapTextLayout.Build(Style + Escape(shown), null, new Rect2(0, 0, 100000, Size.Y), UiAlign.Center, wrap: false);
        layout.Draw(this);
        if (HasFocus() && (int)(_blink * 2) % 2 == 0)
        {
            float x = layout.Glyphs.Count > 0 ? layout.Size.X + 1 : 1;
            DrawLine(new Vector2(x, 1), new Vector2(x, Math.Max(2, Size.Y - 1)), Colors.White, 1);
        }
    }

    // A literal '<' would start a tag in markup.
    static string Escape(string s) => s.Replace('<', '‹').Replace('>', '›');

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            GrabFocus();
            AcceptEvent();
            return;
        }
        if (e is not InputEventKey { Pressed: true } k || !HasFocus()) return;
        switch (k.Keycode)
        {
            case Key.Backspace:
                if (_value.Length > 0) Value = _value[..^1];
                break;
            case Key.Enter or Key.KpEnter:
                Submitted?.Invoke(_value);
                break;
            case Key.Escape:
                ReleaseFocus();
                break;
            default:
                if (k.Unicode >= 32 && _value.Length < MaxLength) Value = _value + (char)k.Unicode;
                else return;
                break;
        }
        _blink = 0;
        AcceptEvent();
    }
}
