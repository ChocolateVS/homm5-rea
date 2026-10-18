using Godot;
using H5Reborn.Ui;

namespace H5Reborn.Options;

/// <summary>
/// The stock horizontal scroll bar (UI/Common/HorisontalScrollBar: lower/greater arrow
/// buttons and a lever on a slider) used by OPTION_SCROLLER entries, stepping through a
/// fixed number of positions. Drawn over the template's ScrollBar window; markers are
/// drawn by <see cref="MarkersView"/> in the template's MarkersBlock.
/// </summary>
public partial class UiScrollerView : Control
{
    const string Dir = "UI/Common/HorisontalScrollBar/";
    readonly Texture2D? _lower, _lowerPushed, _lowerDisabled, _greater, _greaterPushed, _greaterDisabled, _lever, _leverPushed, _leverDisabled;
    int _count = 1, _pos;
    bool _enabled = true, _dragging;
    int _pressed; // -1 lower, 1 greater, 0 none

    public event Action<int>? PositionChanged;
    public int Position => _pos;

    public UiScrollerView()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.ParentNode;
        var r = UiResources.Instance;
        _lower = r.Texture(Dir + "LowerNormal.(Texture).dds");
        _lowerPushed = r.Texture(Dir + "LowerPushed.(Texture).dds");
        _lowerDisabled = r.Texture(Dir + "LowerDisabled.(Texture).dds");
        _greater = r.Texture(Dir + "GreaterNormal.(Texture).dds");
        _greaterPushed = r.Texture(Dir + "GreaterPushed.(Texture).dds");
        _greaterDisabled = r.Texture(Dir + "GreaterDisabled.(Texture).dds");
        _lever = r.Texture(Dir + "LeverNormal.(Texture).dds");
        _leverPushed = r.Texture(Dir + "LeverPushed.(Texture).dds");
        _leverDisabled = r.Texture(Dir + "LeverDisabled.(Texture).dds");
    }

    public void Setup(int count, int position)
    {
        _count = Math.Max(1, count);
        _pos = Math.Clamp(position, 0, _count - 1);
        QueueRedraw();
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; QueueRedraw(); }
    }

    float ArrowW => Size.Y; // square arrow buttons at both ends (16x16 in the stock bar)
    float LeverW => _lever != null ? _lever.GetSize().X * (Size.Y / Math.Max(1, _lever.GetSize().Y)) : Size.Y;
    float TrackX0 => ArrowW;
    float TrackX1 => Size.X - ArrowW - LeverW;
    float LeverX => _count <= 1 ? TrackX0 : TrackX0 + (TrackX1 - TrackX0) * _pos / (_count - 1);

    public override void _Draw()
    {
        float h = Size.Y;
        Draw(_enabled ? (_pressed < 0 ? _lowerPushed : _lower) : _lowerDisabled, new Rect2(0, 0, ArrowW, h));
        Draw(_enabled ? (_pressed > 0 ? _greaterPushed : _greater) : _greaterDisabled, new Rect2(Size.X - ArrowW, 0, ArrowW, h));
        Draw(_enabled ? (_dragging ? _leverPushed : _lever) : _leverDisabled, new Rect2(LeverX, 0, LeverW, h));
    }

    void Draw(Texture2D? t, Rect2 r) { if (t != null) DrawTextureRect(t, r, false); }

    public override void _GuiInput(InputEvent e)
    {
        if (!_enabled) return;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                if (mb.Position.X < ArrowW) { _pressed = -1; Step(-1); }
                else if (mb.Position.X > Size.X - ArrowW) { _pressed = 1; Step(1); }
                else { _dragging = true; DragTo(mb.Position.X); }
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _pressed = 0;
                _dragging = false;
                QueueRedraw();
                break;
            case InputEventMouseMotion mm when _dragging:
                DragTo(mm.Position.X);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Step(1); AcceptEvent(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Step(-1); AcceptEvent(); break;
        }
    }

    void DragTo(float x)
    {
        if (_count <= 1) return;
        float t = (x - LeverW / 2 - TrackX0) / Math.Max(1, TrackX1 - TrackX0);
        SetPos((int)Math.Round(Math.Clamp(t, 0, 1) * (_count - 1)));
    }

    void Step(int d) => SetPos(_pos + d);

    void SetPos(int p)
    {
        p = Math.Clamp(p, 0, _count - 1);
        QueueRedraw();
        if (p == _pos) return;
        _pos = p;
        PositionChanged?.Invoke(p);
    }
}

/// <summary>Step markers of a scroller (the template's MarkerTemplate repeated across MarkersBlock).</summary>
public partial class MarkersView : Control
{
    readonly Texture2D? _on = UiResources.Instance.Texture("UI/GameOptions/Templates/ScrollBarPart/MarkerEnabled.(Texture).dds");
    readonly Texture2D? _off = UiResources.Instance.Texture("UI/GameOptions/Templates/ScrollBarPart/MarkerDisabled.(Texture).dds");
    int _count;
    bool _enabled = true;

    public MarkersView()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.ParentNode;
    }

    public void Setup(int count, bool enabled) { _count = count; _enabled = enabled; QueueRedraw(); }

    public override void _Draw()
    {
        var t = _enabled ? _on : _off;
        if (t == null || _count < 2) return;
        float w = t.GetSize().X, h = Size.Y;
        for (int i = 0; i < _count; i++)
        {
            float x = (Size.X - w) * i / (_count - 1);
            DrawTextureRect(t, new Rect2(x, 0, w, h), false);
        }
    }
}
