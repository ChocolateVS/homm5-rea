namespace H5.Core.Ui;

/// <summary>
/// Computes absolute window rectangles. Screens are authored for 1024x768
/// (WindowScreen Size). Each WindowScreen positions that canvas on the real screen
/// per axis with HorAlign/VerAlign (set on most screens by the 3.1 patch archive):
/// ALIGN_SCREEN (or unset) stretches it to the screen so children re-anchor by their
/// own alignments; ALIGN_CENTER centres the authored canvas (modal dialogs, result
/// windows); ALIGN_LOW_END / ALIGN_HIGH_END pin it to the left/top or right/bottom.
/// </summary>
public static class UiLayout
{
    public const int DesignWidth = 1024, DesignHeight = 768;

    public static void Apply(UiWindow root, int screenW = DesignWidth, int screenH = DesignHeight)
    {
        var screen = new UiRect(0, 0, screenW, screenH);
        if (root.Type == "WindowScreen")
        {
            var size = root.Placement.Size.Value;
            var (x, w) = ScreenAxis((string?)root.RawInstance?.Element("HorAlign"), size.X > 0 ? size.X : DesignWidth, screenW);
            var (y, h) = ScreenAxis((string?)root.RawInstance?.Element("VerAlign"), size.Y > 0 ? size.Y : DesignHeight, screenH);
            root.Rect = new UiRect(x, y, w, h);
        }
        else root.Rect = Place(root.Placement, screen);
        LayoutChildren(root);
    }

    static (int pos, int size) ScreenAxis(string? align, int authored, int screen) => align switch
    {
        "ALIGN_CENTER" => ((screen - authored) / 2, authored),
        "ALIGN_LOW_END" => (0, authored),
        "ALIGN_HIGH_END" => (screen - authored, authored),
        _ => (0, screen), // ALIGN_SCREEN
    };

    static void Layout(UiWindow w, UiRect parent)
    {
        w.Rect = Place(w.Placement, parent);
        if (w.Type == "WindowGathering" && w.StackInterval is null) { Gather(w, parent); return; }
        LayoutChildren(w);
    }

    static void LayoutChildren(UiWindow w)
    {
        if (w.StackInterval is int gap) { Stack(w, gap); return; }
        if (w.Type == "WindowGathering") { Gather(w, null); return; }
        foreach (var c in w.Children) Layout(c, w.Rect);
    }

    /// <summary>
    /// CCommonGatheringWindow layout (00ac0690; instance fields sMargins Left/Right/Top/Bottom/Delta, Orientation,
    /// ChangePosSize at +0xf4..+0x10c of its data): the inner size is its own size less the margins, grown to the
    /// largest child across the stacking axis; the shown children are placed one after the other from the Left
    /// (ECHO_HORISONTAL) or Top (ECHO_VERTICAL) margin with Delta between them (added whenever the running offset
    /// is past the margin). With ChangePosSize each child takes the whole inner cross size and sits at the cross
    /// margin (flags 0xffff); without it only the stacking coordinate and size are set (flags 5 / 0xa).
    /// The gathering then takes the stacked size plus the margins (00a4f810 flags 0xc).
    /// </summary>
    static void Gather(UiWindow w, UiRect? parent)
    {
        var inst = w.RawInstance;
        var m = inst?.Element("sMargins");
        int left = (int?)m?.Element("Left") ?? 0, right = (int?)m?.Element("Right") ?? 0;
        int top = (int?)m?.Element("Top") ?? 0, bottom = (int?)m?.Element("Bottom") ?? 0, delta = (int?)m?.Element("Delta") ?? 0;
        bool horizontal = ((string?)inst?.Element("Orientation"))?.Trim() == "ECHO_HORISONTAL";
        bool fit = H5.Core.Data.XmlHelpers.BoolValue(inst?.Element("ChangePosSize"));
        int x0 = w.Rect.X, y0 = w.Rect.Y;
        int innerW = w.Rect.W - left - right, innerH = w.Rect.H - top - bottom;
        var shown = new List<UiWindow>();
        foreach (var c in w.Children)
        {
            Layout(c, w.Rect);
            if (!c.Visible || c.Part != null) continue;
            shown.Add(c);
            var (cw, ch) = Measure(c);
            if (horizontal) innerH = Math.Max(innerH, ch); else innerW = Math.Max(innerW, cw);
        }
        int start = horizontal ? left : top, at = start;
        foreach (var c in shown)
        {
            if (at != start) at += delta;
            if (horizontal)
            {
                if (fit) Resize(c, c.Rect with { H = innerH });
                int cw = Measure(c).W;
                Resize(c, fit ? new UiRect(x0 + at, y0 + top, cw, innerH) : c.Rect with { X = x0 + at, W = cw });
                at += cw;
            }
            else
            {
                if (fit) Resize(c, c.Rect with { W = innerW });
                int ch = Measure(c).H;
                Resize(c, fit ? new UiRect(x0 + left, y0 + at, innerW, ch) : c.Rect with { Y = y0 + at, H = ch });
                at += ch;
            }
        }
        int width = horizontal ? at + right : left + right + innerW;
        int height = horizontal ? top + bottom + innerH : at + bottom;
        var final = parent is { } p
            ? Place(w.Placement.MergedWith(new UiPlacement { Size = (new Int2(width, height), true) }), p)
            : w.Rect with { W = width, H = height };
        int dx = final.X - x0, dy = final.Y - y0;
        w.Rect = final;
        if (dx != 0 || dy != 0) foreach (var c in w.Children) Shift(c, dx, dy);
    }

    /// <summary>A child's current size (vfunc 0x50): a gathering's computed size, else at least its placement size.</summary>
    static (int W, int H) Measure(UiWindow c) => c.Type == "WindowGathering"
        ? (c.Rect.W, c.Rect.H)
        : (Math.Max(c.Rect.W, c.Placement.Size.Value.X), Math.Max(c.Rect.H, c.Placement.Size.Value.Y));

    /// <summary>Sets a child's rectangle (vfunc 0x58) and lays its own children out against it.</summary>
    static void Resize(UiWindow c, UiRect r)
    {
        if (r == c.Rect) return;
        if (r.W == c.Rect.W && r.H == c.Rect.H) { Shift(c, r.X - c.Rect.X, r.Y - c.Rect.Y); return; }
        c.Rect = r;
        LayoutChildren(c);
    }

    /// <summary>
    /// Scrollable-container items (008888e0): only the item list is stacked (other children are laid out as
    /// usual, whatever their visibility); each item is placed at the running offset (its own Position on
    /// the stacking axis is replaced), the offset advances by the item's size plus the container's Interval
    /// between items (shared +0xcc), and the total is the content size (+0x150).
    /// </summary>
    static void Stack(UiWindow w, int gap)
    {
        int at = 0, n = 0;
        foreach (var c in w.Children)
            if (!w.StackItems.Contains(c)) Layout(c, w.Rect);
        foreach (var c in w.StackItems)
        {
            if (!w.Children.Contains(c)) continue;
            if (n++ > 0) at += gap;
            var slot = w.StackHorizontal
                ? new UiRect(w.Rect.X + at, w.Rect.Y, c.Placement.Size.Value.X, w.Rect.H)
                : new UiRect(w.Rect.X, w.Rect.Y + at, w.Rect.W, c.Placement.Size.Value.Y);
            var p = c.Placement;
            c.Rect = Place(w.StackHorizontal
                ? p.MergedWith(new UiPlacement { Position = (new Int2(0, p.Position.Value.Y), true), HorAlign = (UiAlign.LowEnd, true) })
                : p.MergedWith(new UiPlacement { Position = (new Int2(p.Position.Value.X, 0), true), VerAlign = (UiAlign.LowEnd, true) }), slot);
            LayoutChildren(c);
            at += w.StackHorizontal ? c.Rect.W : c.Rect.H;
        }
        w.StackedSize = at;
    }

    static void Shift(UiWindow w, int dx, int dy)
    {
        w.Rect = w.Rect with { X = w.Rect.X + dx, Y = w.Rect.Y + dy };
        foreach (var c in w.Children) Shift(c, dx, dy);
    }

    /// <summary>
    /// Children back-to-front: higher <see cref="UiWindow.Priority"/> is drawn on top; among
    /// equal priorities the FIRST child in the list is topmost (verified on the combat jog
    /// dial and main menu panels). Hit-testing should use the reverse of this order.
    /// </summary>
    public static IEnumerable<UiWindow> PaintOrder(UiWindow w) =>
        w.Children.Select((c, i) => (c, i)).OrderBy(x => x.c.Priority).ThenByDescending(x => x.i).Select(x => x.c);

    public static UiRect Place(UiPlacement p, UiRect parent)
    {
        var (x, width) = Axis(p.HorAlign.Value, p.Position.Value.X, p.Size.Value.X, p.LowerMargin.Value.X, p.UpperMargin.Value.X, parent.X, parent.W);
        var (y, height) = Axis(p.VerAlign.Value, p.Position.Value.Y, p.Size.Value.Y, p.LowerMargin.Value.Y, p.UpperMargin.Value.Y, parent.Y, parent.H);
        return new UiRect(x, y, width, height);
    }

    /// <summary>
    /// One axis of the exe's placement (H5_Game.exe 00a4d630): low end = parent + position; centre =
    /// round-half-even((parent size − size) / 2) + parent + position; high end = parent + parent size − size +
    /// position; margin = parent + lower margin, size = parent size − both margins.
    /// </summary>
    public static (int pos, int size) Axis(UiAlign a, int pos, int size, int lowMargin, int highMargin, int parentPos, int parentSize) => a switch
    {
        UiAlign.HighEnd => (parentPos + parentSize - size + pos, size),
        UiAlign.Center => (parentPos + HalfEven(parentSize - size) + pos, size),
        UiAlign.Margin => (parentPos + lowMargin, Math.Max(0, parentSize - lowMargin - highMargin)),
        _ => (parentPos + pos, size),
    };

    /// <summary>x87 FISTP of d · 0.5 in the default round-to-nearest-even mode.</summary>
    public static int HalfEven(int d) => (int)Math.Round(d * 0.5, MidpointRounding.ToEven);

    /// <summary>
    /// Places a laid-out text block of <paramref name="w"/> × <paramref name="h"/> inside its window rectangle
    /// like the exe (00a4d7b0, used by ForegroundTextString 00b8fb40 and the text view 00b8f5e0): an axis is
    /// aligned by the text placement only when its HorAllign / VerAllign is set; otherwise the block starts at the
    /// window's left / top edge and the Position offset is ignored. The text wraps at the full window width.
    /// </summary>
    public static (int x, int y) PlaceText(UiPlacement? p, UiRect parent, int w, int h)
    {
        int x = parent.X, y = parent.Y;
        if (p == null) return (x, y);
        if (p.HorAlign.Set) x = Axis(p.HorAlign.Value, p.Position.Value.X, w, p.LowerMargin.Value.X, p.UpperMargin.Value.X, parent.X, parent.W).pos;
        if (p.VerAlign.Set) y = Axis(p.VerAlign.Value, p.Position.Value.Y, h, p.LowerMargin.Value.Y, p.UpperMargin.Value.Y, parent.Y, parent.H).pos;
        return (x, y);
    }
}
