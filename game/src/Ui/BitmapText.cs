using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// Lays out and draws H5 markup with the original pre-rendered bitmap fonts
/// (UI/Fonts atlases + bin/fonts glyph metrics), matching the stock renderer:
/// glyph rect blitted at pen + A, pen advances A + B, greedy word wrap, per-line
/// alignment, 1px 4-way outline. Layout is cached until text, values or width change.
/// </summary>
public sealed class BitmapTextLayout
{
    public readonly record struct Glyph(Texture2D Atlas, Rect2 Source, Vector2 Pos, Color Color, bool Outline, Color OutlineColor);

    public List<Glyph> Glyphs { get; } = new();
    public Vector2 Size { get; private set; }

    public static BitmapTextLayout Build(string markup, IReadOnlyDictionary<string, string>? values, Rect2 area,
        UiAlign verticalAlign = UiAlign.LowEnd, bool wrap = true)
    {
        var res = UiResources.Instance;
        var layout = new BitmapTextLayout();
        var runs = res.Markup.Parse(markup, null, values);

        var lines = new List<List<(string s, UiTextStyle st, UiFont f)>> { new() };
        var widths = new List<float> { 0 };
        float width = area.Size.X;
        foreach (var run in runs)
        {
            if (run.LineBreak) { lines.Add(new()); widths.Add(0); continue; }
            var font = res.Fonts.Find(run.Style.Face, run.Style.Size);
            if (font == null) continue;
            foreach (var word in SplitWords(run.Text))
            {
                if (word == "\t")
                {
                    // Tab stops at half of the area, as used by two-column info texts.
                    float stop = widths[^1] < width / 2 ? width / 2 : widths[^1] + font.MeasureWidth("    ");
                    lines[^1].Add(("\t" + (stop - widths[^1]).ToString(System.Globalization.CultureInfo.InvariantCulture), run.Style, font));
                    widths[^1] = stop;
                    continue;
                }
                int ww = font.MeasureWidth(word);
                if (wrap && width > 0 && widths[^1] + ww > width && widths[^1] > 0 && word.Trim().Length > 0)
                {
                    lines.Add(new()); widths.Add(0);
                    if (word.Trim().Length == 0) continue;
                }
                lines[^1].Add((word, run.Style, font));
                widths[^1] += ww;
            }
        }

        // Line boxes: glyph rects are CellHeight tall; consecutive lines are LineAdvance apart,
        // so a block is (lines - 1) advances plus the last line's cell. Mixed fonts on a line
        // share a baseline (font ascent).
        static int Advance(List<(string s, UiTextStyle st, UiFont f)> l) => l.Count == 0 ? 16 : l.Max(p => p.f.LineAdvance);
        static int Cell(List<(string s, UiTextStyle st, UiFont f)> l) => l.Count == 0 ? 16 : l.Max(p => p.f.CellHeight);
        static int Ascent(List<(string s, UiTextStyle st, UiFont f)> l) => l.Count == 0 ? 0 : l.Max(p => p.f.Ascent);
        int total = 0;
        for (int i = 0; i < lines.Count; i++) total += i < lines.Count - 1 ? Advance(lines[i]) : Cell(lines[i]);

        // A <top>/<middle>/<bottom> tag overrides the text area's own vertical alignment.
        var tagged = runs.Select(r => r.Style.VAlign).FirstOrDefault(v => v != UiTextVAlign.Default);
        var v = tagged switch
        {
            UiTextVAlign.Top => UiAlign.LowEnd,
            UiTextVAlign.Middle => UiAlign.Center,
            UiTextVAlign.Bottom => UiAlign.HighEnd,
            _ => verticalAlign,
        };
        float y = v switch
        {
            UiAlign.Center => area.Position.Y + Mathf.Floor((area.Size.Y - total) / 2),
            UiAlign.HighEnd => area.End.Y - total,
            _ => area.Position.Y,
        };
        float maxW = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var align = line.Count > 0 ? line[0].st.Align : UiTextAlign.Left;
            float x = align switch
            {
                UiTextAlign.Center => area.Position.X + (area.Size.X - widths[i]) / 2,
                UiTextAlign.Right => area.End.X - widths[i],
                _ => area.Position.X,
            };
            maxW = Math.Max(maxW, widths[i]);
            int baseline = Ascent(line);
            foreach (var (s, st, f) in line)
            {
                if (s.StartsWith('\t')) { x += float.Parse(s[1..], System.Globalization.CultureInfo.InvariantCulture); continue; }
                var atlas = res.Texture(f.DdsPath);
                foreach (var ch in s)
                {
                    if (f.Glyph(ch) is not { } g) continue;
                    if (atlas != null && g.Width > 0)
                        layout.Glyphs.Add(new Glyph(atlas, new Rect2(g.X1, g.Y1, g.Width, g.Height),
                            new Vector2(Mathf.Round(x + g.A), Mathf.Round(y + baseline - f.Ascent)), UiResources.ToColor(st.Color),
                            st.Outline, UiResources.ToColor(st.OutlineColor)));
                    x += g.Advance;
                }
            }
            y += Advance(line);
        }
        layout.Size = new Vector2(maxW, total);
        return layout;
    }

    public void Draw(CanvasItem canvas, Vector2 offset = default)
    {
        foreach (var g in Glyphs)
        {
            if (!g.Outline) continue;
            foreach (var d in Outline)
                canvas.DrawTextureRectRegion(g.Atlas, new Rect2(g.Pos + offset + d, g.Source.Size), g.Source, g.OutlineColor);
        }
        foreach (var g in Glyphs)
            canvas.DrawTextureRectRegion(g.Atlas, new Rect2(g.Pos + offset, g.Source.Size), g.Source, g.Color);
    }

    static readonly Vector2[] Outline = { new(-1, 0), new(1, 0), new(0, -1), new(0, 1) };

    static IEnumerable<string> SplitWords(string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\t')
            {
                if (i > start) yield return s[start..i];
                yield return "\t";
                start = i + 1;
            }
            else if (s[i] == ' ') { yield return s[start..(i + 1)]; start = i + 1; }
        }
        if (start < s.Length) yield return s[start..];
    }
}

/// <summary>A standalone Control that draws markup text with the original fonts.</summary>
public partial class BitmapText : Control
{
    string _markup = "";
    BitmapTextLayout? _layout;
    Dictionary<string, string>? _values;

    public UiAlign VerticalAlign { get; set; } = UiAlign.LowEnd;
    public bool Wrap { get; set; } = true;

    public string Markup
    {
        get => _markup;
        set { if (_markup != value) { _markup = value; Invalidate(); } }
    }

    public void SetValue(string name, string value)
    {
        _values ??= new();
        _values[name] = value;
        Invalidate();
    }

    public BitmapText() { MouseFilter = MouseFilterEnum.Ignore; TextureFilter = TextureFilterEnum.ParentNode; }

    void Invalidate() { _layout = null; QueueRedraw(); }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Invalidate();
    }

    public override void _Draw()
    {
        _layout ??= BitmapTextLayout.Build(_markup, _values, new Rect2(Vector2.Zero, Size), VerticalAlign, Wrap);
        _layout.Draw(this);
    }
}
