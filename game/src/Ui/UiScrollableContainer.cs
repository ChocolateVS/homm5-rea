using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// The exe's CWindowScrollableContainer (vtable 00e94034, code 00886000..0088b000): a Border window (the
/// visible area, clipping), an internal content window inside it and a ScrollBar, both parts appended from the
/// Shared references (UiLoader.LoadParts).
/// <list type="bullet">
/// <item>Items (008888e0): windows added by code are stacked in order along the scroll bar's axis with the
/// shared Interval between them; their total is the content size.</item>
/// <item>Range (00887ba0): the content window takes the content size; the scroll bar gets
/// SetRange(0, content size, Border height) (width for a horizontal bar).</item>
/// <item>Scrolling (00887c80): the content window sits at −position inside the Border.</item>
/// </list>
/// Screens that draw their own rows put them in <see cref="Content"/> and call <see cref="SetContentSize"/>.
/// </summary>
public partial class UiScrollableContainer : UiNode
{
    /// <summary>The internal content window (+0x15c); child of the Border node, moved by the scroll position.</summary>
    public Control Content { get; } = new() { Name = "_Content", MouseFilter = MouseFilterEnum.Pass };
    public UiScrollBar? ScrollBar { get; private set; }
    public UiNode? Border { get; private set; }
    readonly int _interval;
    readonly List<UiNode> _items = new(), _pending = new();
    Rect2? _wantSel, _wantPre;
    float? _freeSize;

    public UiScrollableContainer(UiWindow window) : base(window)
    {
        _interval = (int?)window.RawShared?.Element("Interval") ?? 0;
        MouseFilter = MouseFilterEnum.Pass;
        Content.TextureFilter = TextureFilterEnum.ParentNode;
    }

    public override void OnBuilt()
    {
        foreach (var n in GetChildren())
        {
            if (n is not UiNode u) continue;
            if (u.Window.Part == "Border")
            {
                Border = u;
                u.ClipContents = true;
                u.AddChild(Content);
                var pending = _pending.ToList();
                _pending.Clear();
                foreach (var item in pending) AddItem(item);
            }
            else if (u.Window.Part == "ScrollBar" && u is UiScrollBar sb)
            {
                ScrollBar = sb;
                sb.ValueChanged += _ => PlaceContent();
            }
            else if (u.Window.Part is "Selection" or "PreSelection")
            {
                if (u.Window.Part == "Selection") _selection = u; else _preselection = u;
                u.Visible = false;
                u.MouseFilter = MouseFilterEnum.Ignore;
            }
        }
        if (_selection != null) AdoptMarker(_selection);
        if (_preselection != null) AdoptMarker(_preselection);
    }

    UiNode? _selection, _preselection;

    /// <summary>The Selection / PreSelection markers live in the content (00887850 places them in its coordinates).</summary>
    void AdoptMarker(UiNode marker)
    {
        if (Border == null) return;
        Window.Children.Remove(marker.Window);
        Border.Window.Children.Add(marker.Window);
        marker.GetParent()?.RemoveChild(marker);
        Content.AddChild(marker);
        Content.MoveChild(marker, 0);
        PlaceMarker(marker, marker == _selection ? _wantSel : _wantPre);
    }

    /// <summary>
    /// 00887850: the Selection marker takes the selected item's rectangle (content coordinates) and is shown;
    /// null hides it. <see cref="Preselect"/> does the same for the PreSelection (mouse-over) marker.
    /// </summary>
    public void Select(Rect2? itemRect) { _wantSel = itemRect; PlaceMarker(_selection, itemRect); }
    public void Preselect(Rect2? itemRect) { _wantPre = itemRect; PlaceMarker(_preselection, itemRect); }

    void PlaceMarker(UiNode? marker, Rect2? r)
    {
        if (marker == null || Border == null || marker.GetParent() != Content) return;
        marker.Visible = marker.Window.Visible = r != null;
        if (r is not { } rect) return;
        marker.Window.Placement = marker.Window.Placement.MergedWith(new UiPlacement
        {
            Position = (new Int2((int)rect.Position.X, (int)rect.Position.Y), true),
            Size = (new Int2((int)rect.Size.X, (int)rect.Size.Y), true),
            HorAlign = (UiAlign.LowEnd, true),
            VerAlign = (UiAlign.LowEnd, true),
        });
        var b = Border.Window.Rect;
        marker.Window.Rect = new UiRect(b.X + (int)rect.Position.X, b.Y + (int)rect.Position.Y, (int)rect.Size.X, (int)rect.Size.Y);
        marker.Position = rect.Position;
        marker.Size = rect.Size;
    }

    bool Horizontal => ScrollBar?.Model is { } && ScrollBar.Window.Children.FirstOrDefault(c => c.Part == "Slider") is { } s
        && H5.Core.Data.XmlHelpers.BoolValue(s.RawShared?.Element("Horisontal"));

    public IReadOnlyList<UiNode> Items => _items;

    /// <summary>
    /// Adds a window of the screen as the next item (container slot 8): it moves under the Border window and
    /// into the content, and is stacked with the others on the next layout.
    /// </summary>
    public void AddItem(UiNode item)
    {
        if (Border == null || Screen == null) { _pending.Add(item); return; }
        foreach (var p in Screen.Root.Descendants().Prepend(Screen.Root)) p.Children.Remove(item.Window);
        Border.Window.Children.Add(item.Window);
        Border.Window.StackInterval = _interval;
        Border.Window.StackHorizontal = Horizontal;
        item.GetParent()?.RemoveChild(item);
        Content.AddChild(item);
        item.Visible = item.Window.Visible = true;
        _items.Add(item);
        _freeSize = null;
        Screen.QueueRelayout();
    }

    /// <summary>Removes every item (container slot 0x2c) and scrolls back to the start (008888e0 with no items).</summary>
    public void ClearItems()
    {
        foreach (var item in _items)
        {
            Border?.Window.Children.Remove(item.Window);
            Content.RemoveChild(item);
        }
        _items.Clear();
        ScrollBar?.SetValue(0);
        Screen?.QueueRelayout();
    }

    /// <summary>
    /// Container slot 14 (00888fa0): uses another window's scroll bar (a CWindowScrollBar elsewhere on the screen,
    /// e.g. the kingdom overview's scroll_of_views) instead of its own: the own bar is removed from the window
    /// (vfunc 0xe4), the container listens to the new one and sets its range again (00887ba0).
    /// </summary>
    public void SetScrollBar(UiScrollBar? bar)
    {
        if (bar == null || bar == ScrollBar) return;
        if (ScrollBar is { } own)
        {
            Window.Children.Remove(own.Window);
            own.QueueFree();
        }
        ScrollBar = bar;
        bar.ValueChanged += _ => PlaceContent();
        UpdateRange();
    }

    float? _restore;

    /// <summary>Scroll position to apply once the next layout has set the range (refills that keep their place).</summary>
    public void RestorePosition(float pos) { _restore = pos; Screen?.QueueRelayout(); }

    /// <summary>Content size for code-drawn content in <see cref="Content"/> (no items).</summary>
    public void SetContentSize(float size)
    {
        _freeSize = size;
        UpdateRange();
    }

    /// <summary>Scroll position in pixels (the scroll bar's value).</summary>
    public float ScrollPosition
    {
        get => ScrollBar?.Value ?? 0;
        set { ScrollBar?.SetValue(value); PlaceContent(); }
    }

    public override void OnPlaced() => UpdateRange();

    void UpdateRange()
    {
        if (Border == null) return;
        var b = Border.Window.Rect;
        float size = _freeSize ?? (_items.Count > 0 ? Border.Window.StackedSize : 0);
        bool h = Horizontal;
        Content.Size = h ? new Vector2(size, b.H) : new Vector2(b.W, size);
        ScrollBar?.SetRange(0, size, h ? b.W : b.H);
        if (_restore is float r && ScrollBar != null) { ScrollBar.SetValue(r); _restore = null; }
        PlaceContent();
    }

    void PlaceContent()
    {
        float pos = ScrollBar?.Value ?? 0;
        Content.Position = Horizontal ? new Vector2(-pos, 0) : new Vector2(0, -pos);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } mb && UiScrollBar.WheelNotches(mb) is float n && ScrollBar != null)
        {
            ScrollBar.Wheel(n);
            AcceptEvent();
            return;
        }
        base._GuiInput(e);
    }
}
