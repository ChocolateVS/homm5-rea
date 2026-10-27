namespace H5.Core.Ui;

/// <summary>A glyph quad of a laid-out text: atlas rect, destination rect (float px) and colour.</summary>
public readonly record struct UiTextQuad(UiFont Font, int SrcX, int SrcY, int SrcW, int SrcH, float X, float Y, float W, float H, uint Color, bool Outline);

/// <summary>Result of <see cref="UiTextLayout.Layout"/>: quads relative to the block origin and the block size.</summary>
public sealed class UiTextBlock
{
    public readonly List<UiTextQuad> Quads = new();
    /// <summary>Unrounded block size (the layout's widest line end and its final pen y).</summary>
    public float Width, Height;
    /// <summary>Widest line's visual width (independent of the alignment offsets), for sizing a window to its text.</summary>
    public float MaxLineWidth;
    /// <summary>The size the exe reports for the block (CML GetSize 00cbe970: each axis rounded from value + 0.5).</summary>
    public int RoundedWidth => RoundSize(Width);
    public int RoundedHeight => RoundSize(Height);
    public int Lines;

    static int RoundSize(float v) => (int)Math.Round((float)(v + 0.5f), MidpointRounding.ToEven);
}

/// <summary>
/// The exe's markup text layout (H5_Game.exe CMLLayout 00cc0c20 + line finish 00cbea10 + newline 00cc06b0, word
/// measure 00cbf8b0, font choice 00cbf650). Everything is computed in screen pixels: <c>scale</c> is the ratio of
/// screen to UI pixels (the original's screen / virtual-screen ratio; our UI scale).
/// <list type="bullet">
/// <item>Font: pt sizes are multiplied by the scale (truncated), px/fx are not; then raised to the minfontsize.
/// The font table picks the atlas (<see cref="UiFontTable.Find"/>) and every glyph metric is multiplied by
/// size / nominal, so a 28 Header uses the 32 atlas at 0.875.</item>
/// <item>Word: pen starts at the outline width o; per glyph x = pen + (forcefontsize ? 0 : o) + A·s, the quad is
/// (w·s + o) × (h·s), the pen moves to x + B·s (+ o without forcefontsize). Height (cell − internal leading)·s
/// (+ 2o without forcefontsize), visual height cell·s + 2o. Outlines are 8 copies at (±o, 0), (0, ±o), (±o, ±o).
/// A tab character inside a word moves the pen to the next multiple of four average widths.</item>
/// <item>Lines: an element that would pass the width while the pen is past 0 starts a new line (space elements
/// at a line start are dropped; a line keeps its trailing space). The first element's mode aligns the line
/// (right: right − visual width; centre: round-half-even((width − visual width)/2); justify spreads the gaps,
/// except on a paragraph's last line). Each element sits in its line by its own vertical alignment (top / bottom /
/// middle, default middle).</item>
/// <item>Vertical: the first line advances by its visual (cell) height; later lines sit internal-leading higher
/// and advance by their height (minus the outline when forcefontsize); an empty line advances by the previous
/// line's height. The block height is the final pen y.</item>
/// </list>
/// </summary>
public static class UiTextLayout
{
    sealed class Elem
    {
        public UiMlKind Kind;
        public string Text = "";
        public UiMlState State = new();
        public float W, H, ExtW, ExtH, X, Y;
        public readonly List<UiTextQuad> Quads = new();
        public bool IsBreak => Kind == UiMlKind.Break;
    }

    sealed class LineState
    {
        public float MaxLine;
        public float Y, UsedW, Pen, LineH, PrevLineH, VisRight, ExtH, Left, LeftBottom = -1, Right, RightBottom = -1, Extra;
        public readonly List<Elem> Line = new(), FloatsLeft = new(), FloatsRight = new();
    }

    /// <param name="wrap">false disables wrapping for our own single-line labels (the exe's equivalent is the
    /// &lt;nowrap&gt; mode, which also forces left alignment); stock texts always wrap.</param>
    public static UiTextBlock Layout(UiMlStream stream, UiFontTable fonts, float width, float scale = 1f, bool wrap = true)
    {
        var elems = stream.Items.Select(i => new Elem { Kind = i.Kind, Text = i.Text, State = i.State }).ToList();
        var block = new UiTextBlock();
        // 00cc1f90: a first pass; when a wrapdots line was cut the list was edited and is laid out again.
        if (!Pass(elems, stream, fonts, width, scale, false, block, wrap))
        {
            block = new UiTextBlock();
            Pass(elems, stream, fonts, width, scale, true, block, wrap);
        }
        return block;
    }

    static bool Pass(List<Elem> elems, UiMlStream stream, UiFontTable fonts, float width, float scale, bool second, UiTextBlock block, bool wrap)
    {
        var L = new LineState { Right = width };
        bool first = true;
        int fitMark = -1;
        for (int i = 0; i < elems.Count; i++)
        {
            var e = elems[i];
            if (e.IsBreak) { NewLine(L, width, true, first, scale); first = false; continue; }
            if (e.Kind == UiMlKind.Tab) MeasureTab(e, L, fonts, scale);
            else Measure(e, fonts, scale);
            bool space = e.Kind == UiMlKind.Space;
            float avail = L.Right - L.Left;
            switch (e.State.Mode)
            {
                case UiMlMode.NoWrap:
                    if (!(space && L.Line.Count == 0)) Add(L, e);
                    break;
                case UiMlMode.WrapLeft:
                    L.FloatsLeft.Add(e);
                    break;
                case UiMlMode.WrapRight:
                    L.FloatsRight.Add(e);
                    break;
                case UiMlMode.ForceWrap:
                    if (e.W + L.Pen > avail)
                    {
                        if (e.W > avail && e.Kind == UiMlKind.Word && e.Text.Length > 1)
                        {
                            // Split the word at the last character that fits (00cbff00), the rest goes on.
                            int n = FittingChars(e, fonts, scale, avail - L.Pen);
                            if (n == 0 && L.Pen <= 0) n = 1;
                            if (n > 0)
                            {
                                var rest = new Elem { Kind = e.Kind, Text = e.Text[n..], State = e.State };
                                e.Text = e.Text[..n];
                                Measure(e, fonts, scale);
                                elems.Insert(i + 1, rest);
                                Add(L, e);
                                NewLine(L, width, false, first, scale); first = false;
                                break;
                            }
                        }
                        if (L.Pen > 0) { NewLine(L, width, false, first, scale); first = false; }
                    }
                    if (!(space && L.Line.Count == 0)) Add(L, e);
                    break;
                case UiMlMode.WrapDots:
                {
                    if (space && L.Line.Count == 0) break;
                    bool empty = L.Pen <= 0;
                    Add(L, e);
                    if (second) break;
                    if (empty) { fitMark = i; break; }
                    if (space) break;
                    var dots = new Elem { Kind = UiMlKind.Word, Text = stream.Dots, State = e.State };
                    Measure(dots, fonts, scale);
                    if (dots.ExtW + L.VisRight <= avail) fitMark = i;
                    if (L.VisRight > avail)
                    {
                        // 00cc0f92..: keep the elements up to the last one after which the dots still fit,
                        // then the dots and a break; drop everything after.
                        // The walk back stops after the remembered element and never drops the first one.
                        int keep = Math.Max(fitMark + 1, 1);
                        elems.RemoveRange(keep, elems.Count - keep);
                        elems.Add(dots);
                        elems.Add(new Elem { Kind = UiMlKind.Break, State = e.State });
                        return false;
                    }
                    break;
                }
                default: // left, right, centre, justify
                    if (wrap && e.W + L.Pen > avail && L.Pen > 0) { NewLine(L, width, false, first, scale); first = false; }
                    if (!(space && L.Line.Count == 0)) Add(L, e);
                    break;
            }
        }
        foreach (var e in elems)
        {
            if (e.IsBreak) { block.Lines++; continue; }
            foreach (var q in e.Quads) block.Quads.Add(q with { X = q.X + e.X, Y = q.Y + e.Y });
        }
        block.Width = L.UsedW;
        block.MaxLineWidth = L.MaxLine;
        block.Height = L.Y;
        return true;
    }

    static void Add(LineState L, Elem e)
    {
        L.Line.Add(e);
        L.VisRight = L.Pen + e.ExtW;
        L.Pen += e.W;
        L.LineH = Math.Max(L.LineH, e.H);
        L.ExtH = Math.Max(L.ExtH, e.ExtH);
    }

    /// <summary>00cc06b0: finish the line, advance the pen y, place floats.</summary>
    static void NewLine(LineState L, float width, bool paragraphEnd, bool first, float scale)
    {
        FinishLine(L, paragraphEnd, first, scale);
        L.Line.Clear();
        if (first && L.ExtH > 0) { L.Y += L.ExtH; L.PrevLineH = L.LineH; }
        else if (L.LineH != 0) { L.Y += L.LineH - L.Extra; L.PrevLineH = L.LineH; }
        else L.Y += L.PrevLineH;
        L.Extra = 0; L.Pen = 0; L.LineH = 0; L.ExtH = 0;
        // 00cc03d0: floats (wrapleft / wrapright elements) stack along the margins from this line down.
        foreach (var f in L.FloatsLeft)
        {
            f.X = L.Left; f.Y = L.Y;
            L.LeftBottom = Math.Max(L.LeftBottom, f.H + L.Y);
            L.Left += f.W;
            L.UsedW = Math.Max(L.UsedW, L.Left);
        }
        foreach (var f in L.FloatsRight)
        {
            f.X = L.Right - f.W; f.Y = L.Y;
            L.UsedW = Math.Max(L.UsedW, L.Right);
            L.Right -= f.W;
            L.RightBottom = Math.Max(L.RightBottom, f.H + L.Y);
        }
        L.FloatsLeft.Clear(); L.FloatsRight.Clear();
        if (L.Y > L.LeftBottom) L.Left = 0;
        if (L.Y > L.RightBottom) L.Right = width;
    }

    /// <summary>00cbea10: horizontal alignment of the line and vertical placement of its elements.</summary>
    static void FinishLine(LineState L, bool paragraphEnd, bool first, float scale)
    {
        if (L.Line.Count == 0) return;
        L.MaxLine = Math.Max(L.MaxLine, L.VisRight);
        var mode = L.Line[0].State.Mode;
        if (paragraphEnd && mode == UiMlMode.Justify) mode = UiMlMode.Left;
        float x = L.Left, gap = 0;
        switch (mode)
        {
            case UiMlMode.Right: x = L.Right - L.Left + L.Left - L.VisRight; break;
            case UiMlMode.Center: x = (float)Math.Round((float)((L.Right - L.Left - L.VisRight) * 0.5f), MidpointRounding.ToEven) + L.Left; break;
            case UiMlMode.Justify: gap = (L.Right - L.Left - L.VisRight) / (L.Line.Count > 1 ? L.Line.Count - 1 : 1); break;
        }
        for (int k = 0; k < L.Line.Count; k++)
        {
            var e = L.Line[k];
            if (e.State.ForceFontSize && e.State.OutlineSize > 0)
                L.Extra = Math.Max(L.Extra, (float)Math.Ceiling(e.State.OutlineSize * scale));
            float y0 = first ? L.Y : L.Y - (e.ExtH - e.H);
            e.X = x;
            e.Y = e.State.VAlign switch
            {
                0 => y0,
                1 => y0 + L.LineH - e.H,
                _ => (L.LineH - e.H) * 0.5f + y0,
            };
            float end = x + (k == L.Line.Count - 1 ? e.ExtW : e.W);
            L.UsedW = Math.Max(L.UsedW, end);
            x = gap + end;
        }
    }

    /// <summary>00cbf650: atlas and glyph scale for an element's font state.</summary>
    static (UiFont? font, float s) FontOf(UiMlState st, UiFontTable fonts, float scale)
    {
        int size = st.SizeUnit == UiMlSizeUnit.Pt ? (int)(st.Size * scale) : st.Size;
        if (size <= st.MinFontSize) size = st.MinFontSize;
        var f = fonts.Find(st.Face, size);
        if (f == null || f.Nominal <= 0) return (f, 1f);
        return (f, (float)size / f.Nominal);
    }

    /// <summary>00cbf8b0: glyph quads, advance width and heights of a word or space run.</summary>
    static void Measure(Elem e, UiFontTable fonts, float scale)
    {
        e.Quads.Clear();
        var st = e.State;
        var (f, s) = FontOf(st, fonts, scale);
        if (f == null) { e.W = e.H = e.ExtW = e.ExtH = 0; return; }
        float o = st.OutlineSize * scale;
        bool force = st.ForceFontSize;
        float twoO = o + o;
        e.H = f.Nominal * s + (force ? 0 : twoO);
        e.ExtH = f.CellHeight * s + twoO;
        float pen = o, lastExtra = 0;
        var text = e.Text;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            bool last = i == text.Length - 1;
            if (ch == '\t')
            {
                float unit = (force ? 0 : twoO) + f.AvgCharWidth * s;
                if (unit != 0)
                {
                    int n = (int)Math.Round(pen / unit, MidpointRounding.ToEven);
                    pen = (n / 4 * 4 + 4) * unit;
                }
                continue;
            }
            if (f.Glyph(ch) is not { } g) continue;
            float x = (force ? 0 : o) + pen + g.A * s;
            float w = g.Width * s + o, h = g.Height * s;
            if (g.Width > 0)
            {
                if (st.OutlineSize != 0)
                    foreach (var (dx, dy) in OutlineOffsets)
                        e.Quads.Add(new UiTextQuad(f, g.X1, g.Y1, g.Width, g.Height, x + dx * o, dy * o, w, h, st.OutlineColor, true));
                e.Quads.Add(new UiTextQuad(f, g.X1, g.Y1, g.Width, g.Height, x, 0, w, h, st.Color, false));
            }
            float adv = g.B * s + (force ? 0 : o);
            if (last) lastExtra = w - adv;
            pen = x + adv;
        }
        e.W = pen - (force ? twoO : 0);
        e.ExtW = lastExtra + pen;
        // Outline quads are drawn under the glyphs (00cbe8e0 draws the outline list first).
        e.Quads.Sort((a, b) => b.Outline.CompareTo(a.Outline));
    }

    /// <summary>Order of the outline copies in 00cbf8b0.</summary>
    static readonly (int dx, int dy)[] OutlineOffsets = { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    /// <summary>00cc0130: a &lt;tab&gt; element reaches the next multiple of four average widths (rounded stop + 1).</summary>
    static void MeasureTab(Elem e, LineState L, UiFontTable fonts, float scale)
    {
        e.Quads.Clear();
        var (f, s) = FontOf(e.State, fonts, scale);
        float unit = (f?.AvgCharWidth ?? 0) * 4 * s;
        e.W = e.ExtW = unit > 0 ? ((int)Math.Round(L.Pen / unit, MidpointRounding.ToEven) + 1) * unit - L.Pen : 0;
        e.H = e.ExtH = L.PrevLineH;
    }

    /// <summary>Number of leading characters of a word whose pen stays within <paramref name="room"/> (00cbf8b0's count).</summary>
    static int FittingChars(Elem e, UiFontTable fonts, float scale, float room)
    {
        var probe = new Elem { Kind = e.Kind, State = e.State };
        int n = 0;
        for (int k = 1; k <= e.Text.Length; k++)
        {
            probe.Text = e.Text[..k];
            Measure(probe, fonts, scale);
            if (probe.W > room) break;
            n = k;
        }
        return n;
    }
}
