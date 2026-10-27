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
        LayoutChildren(w);
    }

    /// <summary>
    /// A WindowGathering (tooltips, info lists) stacks its visible children top to bottom in
    /// list order, each in a slot as tall as the child, and takes the stacked height; other
    /// windows place children against their own rectangle.
    /// </summary>
    static void LayoutChildren(UiWindow w)
    {
        if (w.Type != "WindowGathering")
        {
            foreach (var c in w.Children) Layout(c, w.Rect);
            return;
        }
        int y = w.Rect.Y;
        foreach (var c in w.Children)
        {
            if (!c.Visible) { Layout(c, new UiRect(w.Rect.X, y, w.Rect.W, 0)); continue; }
            var slot = new UiRect(w.Rect.X, y, w.Rect.W, c.Placement.Size.Value.Y);
            Layout(c, slot);
            // The gathering positions its children itself: a child's own vertical Position offset
            // is ignored (CommonInfo's Damage row carries a stray y=52 in the stock data).
            if (c.Rect.Y != y) Shift(c, y - c.Rect.Y);
            if (c.Type == "WindowGathering") y += c.Rect.H;
            else y += Math.Max(c.Rect.H, c.Placement.Size.Value.Y);
        }
        w.Rect = new UiRect(w.Rect.X, w.Rect.Y, w.Rect.W, y - w.Rect.Y);
    }

    static void Shift(UiWindow w, int dy)
    {
        w.Rect = w.Rect with { Y = w.Rect.Y + dy };
        foreach (var c in w.Children) Shift(c, dy);
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
