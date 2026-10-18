namespace H5.Core.Ui;

/// <summary>
/// Computes absolute window rectangles. Screens are authored for 1024×768
/// (WindowScreen Size); HorAlign/VerAlign = ALIGN_SCREEN screens stretch to the
/// actual resolution and children re-anchor via their alignments.
/// </summary>
public static class UiLayout
{
    public const int DesignWidth = 1024, DesignHeight = 768;

    public static void Apply(UiWindow root, int screenW = DesignWidth, int screenH = DesignHeight)
    {
        var screen = new UiRect(0, 0, screenW, screenH);
        // A WindowScreen with ALIGN_SCREEN fills the screen regardless of its authored size.
        bool fills = root.Type == "WindowScreen" &&
                     (string?)root.RawInstance?.Element("HorAlign") is null or "ALIGN_SCREEN";
        root.Rect = fills ? screen : Place(root.Placement, screen);
        foreach (var c in root.Children) Layout(c, root.Rect);
    }

    static void Layout(UiWindow w, UiRect parent)
    {
        w.Rect = Place(w.Placement, parent);
        foreach (var c in w.Children) Layout(c, w.Rect);
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

    static (int pos, int size) Axis(UiAlign a, int pos, int size, int lowMargin, int highMargin, int parentPos, int parentSize) => a switch
    {
        UiAlign.HighEnd => (parentPos + parentSize - size + pos, size),
        UiAlign.Center => (parentPos + (parentSize - size) / 2 + pos, size),
        UiAlign.Margin => (parentPos + lowMargin, Math.Max(0, parentSize - lowMargin - highMargin)),
        _ => (parentPos + pos, size),
    };
}
