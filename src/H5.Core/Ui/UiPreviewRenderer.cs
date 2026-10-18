using H5.Core.IO;
using H5.Core.Ui.Imaging;
using H5.Core.Xdb;

namespace H5.Core.Ui;

/// <summary>
/// Software renderer for laid-out UI trees. It exists to validate the decoders against the
/// original game (and to document draw semantics for the Godot implementation), not for
/// runtime use: children are painted over their parent in <see cref="UiLayout.PaintOrder"/>, backgrounds then
/// foregrounds then text, buttons in their Normal visual state.
/// </summary>
public sealed class UiPreviewRenderer
{
    readonly PakFileSystem _fs;
    readonly XdbLoader _xdb;
    readonly UiMarkup _markup;
    readonly Lazy<UiFontTable> _fonts;
    readonly Dictionary<string, RgbaImage?> _textures = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowHidden { get; set; }
    public bool DrawOutlines { get; set; }
    /// <summary>Window names to skip (screens keep alternative panels visible and toggle them from code).</summary>
    public HashSet<string> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Missing { get; } = new();

    public UiPreviewRenderer(PakFileSystem fs, XdbLoader xdb)
    {
        _fs = fs; _xdb = xdb;
        _markup = new UiMarkup(xdb);
        _fonts = new Lazy<UiFontTable>(() => UiFontTable.Load(xdb));
    }

    public RgbaImage Render(UiWindow root, int width, int height, uint clear = 0xFF101010)
    {
        var img = new RgbaImage(width, height);
        img.Fill(clear);
        Draw(img, root);
        return img;
    }

    void Draw(RgbaImage img, UiWindow w)
    {
        if ((!w.Visible && !ShowHidden) || (w.Name.Length > 0 && Hidden.Contains(w.Name))) return;
        var normal = w.ButtonStates.Count > 0 && w.ButtonStates[0].Visuals.TryGetValue("Normal", out var v) ? v : null;
        DrawDrawable(img, normal?.Background ?? w.Background, w.Rect);
        DrawDrawable(img, normal?.Foreground ?? w.Foreground, w.Rect);
        if (w.Text != null) DrawText(img, w, normal?.TextFormatPath);
        if (DrawOutlines) img.StrokeRect(w.Rect.X, w.Rect.Y, w.Rect.W, w.Rect.H, 0xFF00FF00);
        foreach (var c in UiLayout.PaintOrder(w)) Draw(img, c);
    }

    RgbaImage? Texture(string? dds)
    {
        if (dds == null) return null;
        if (_textures.TryGetValue(dds, out var t)) return t;
        try { t = _fs.TryReadAllBytes(dds, out var b) ? DdsImage.Decode(b) : null; }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { t = null; }
        if (t == null) Missing.Add(dds);
        return _textures[dds] = t;
    }

    void DrawDrawable(RgbaImage img, UiDrawable? d, UiRect r)
    {
        if (d == null) return;
        var tex = Texture(d.DdsPath);
        if (tex == null) return;
        // Texture xdb Width/Height are the logical size; the DDS may be larger (padded).
        int tw = d.TextureSize.X > 0 ? d.TextureSize.X : tex.Width, th = d.TextureSize.Y > 0 ? d.TextureSize.Y : tex.Height;
        switch (d.Kind)
        {
            case UiDrawableKind.SimpleTexture:
            {
                int x = d.TextureAlignX switch { UiAlign.HighEnd => r.Right - tw, UiAlign.Center => r.X + (r.W - tw) / 2, _ => r.X };
                int y = d.TextureAlignY switch { UiAlign.HighEnd => r.Bottom - th, UiAlign.Center => r.Y + (r.H - th) / 2, _ => r.Y };
                // Clipped to the window rect, unscaled.
                int cw = Math.Min(tw, r.Right - x), ch = Math.Min(th, r.Bottom - y);
                img.Draw(tex, 0, 0, cw, ch, x, y, cw, ch, d.Color);
                break;
            }
            case UiDrawableKind.ScalingTexture:
            {
                int sw = d.ScaleSourceSize.X > 0 ? d.ScaleSourceSize.X : tw, sh = d.ScaleSourceSize.Y > 0 ? d.ScaleSourceSize.Y : th;
                img.Draw(tex, 0, 0, sw, sh, r.X, r.Y, r.W, r.H, d.Color);
                break;
            }
            case UiDrawableKind.TiledTexture:
                DrawNineSlice(img, tex, d, r);
                break;
            case UiDrawableKind.FrameSequence:
            {
                int fw = d.FrameSize.X > 0 ? d.FrameSize.X : tw, fh = d.FrameSize.Y > 0 ? d.FrameSize.Y : th;
                img.Draw(tex, 0, 0, fw, fh, r.X, r.Y, r.W > 0 ? r.W : fw, r.H > 0 ? r.H : fh, d.Color);
                break;
            }
        }
    }

    static void DrawNineSlice(RgbaImage img, RgbaImage tex, UiDrawable d, UiRect r)
    {
        int Wd(string k) => d.Tiles.TryGetValue(k, out var t) ? t.Size.X : 0;
        int Ht(string k) => d.Tiles.TryGetValue(k, out var t) ? t.Size.Y : 0;
        int left = Math.Max(Wd("LT"), Wd("L")), right = Math.Max(Wd("RT"), Wd("R"));
        int top = Math.Max(Ht("LT"), Ht("T")), bottom = Math.Max(Ht("LB"), Ht("B"));
        var cells = new Dictionary<string, UiRect>
        {
            ["F"] = new(r.X + left, r.Y + top, r.W - left - right, r.H - top - bottom),
            ["T"] = new(r.X + left, r.Y, r.W - left - right, top),
            ["B"] = new(r.X + left, r.Bottom - bottom, r.W - left - right, bottom),
            ["L"] = new(r.X, r.Y + top, left, r.H - top - bottom),
            ["R"] = new(r.Right - right, r.Y + top, right, r.H - top - bottom),
            ["LT"] = new(r.X, r.Y, left, top),
            ["RT"] = new(r.Right - right, r.Y, right, top),
            ["LB"] = new(r.X, r.Bottom - bottom, left, bottom),
            ["RB"] = new(r.Right - right, r.Bottom - bottom, right, bottom),
        };
        foreach (var (k, cell) in cells)
        {
            if (!d.Tiles.TryGetValue(k, out var part) || cell.W <= 0 || cell.H <= 0) continue;
            var s = part.Source;
            if (s.W <= 0 || s.H <= 0) continue;
            // Edges and fill repeat the source region (tiling); corners are drawn once.
            for (int y = cell.Y; y < cell.Bottom; y += s.H)
            for (int x = cell.X; x < cell.Right; x += s.W)
            {
                int w = Math.Min(s.W, cell.Right - x), h = Math.Min(s.H, cell.Bottom - y);
                img.Draw(tex, s.X, s.Y, w, h, x, y, w, h, d.Color);
            }
        }
    }

    void DrawText(RgbaImage img, UiWindow w, string? stateFormatPath)
    {
        var t = w.Text!;
        string format = t.Format ?? (stateFormatPath != null ? _xdb.ReadText("/" + stateFormatPath) ?? "" : "");
        string text = format + (t.Raw ?? "");
        if (text.Length == 0) return;
        var runs = _markup.Parse(text);
        var area = t.Placement != null ? UiLayout.Place(t.Placement, w.Rect) : w.Rect;
        if (area.W <= 0 || area.H <= 0) area = w.Rect;
        var vAlign = t.Placement?.VerAlign.Value ?? UiAlign.LowEnd;

        // Greedy line layout with word wrap.
        var lines = new List<List<(string s, UiTextStyle st, UiFont f)>> { new() };
        var widths = new List<int> { 0 };
        foreach (var run in runs)
        {
            if (run.LineBreak) { lines.Add(new()); widths.Add(0); continue; }
            var font = _fonts.Value.Find(run.Style.Face, run.Style.Size);
            if (font == null) continue;
            foreach (var word in SplitKeepSpaces(run.Text))
            {
                int ww = font.MeasureWidth(word);
                if (widths[^1] + ww > area.W && widths[^1] > 0 && word.Trim().Length > 0) { lines.Add(new()); widths.Add(0); }
                lines[^1].Add((word, run.Style, font));
                widths[^1] += ww;
            }
        }
        int LineH(List<(string s, UiTextStyle st, UiFont f)> l) => l.Count == 0 ? 16 : l.Max(p => Math.Max(p.f.LineHeight, p.f.CellHeight));
        int total = lines.Sum(LineH);
        int y = vAlign switch { UiAlign.Center => area.Y + (area.H - total) / 2, UiAlign.HighEnd => area.Bottom - total, _ => area.Y };
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var align = line.Count > 0 ? line[0].st.Align : UiTextAlign.Left;
            int x = align switch
            {
                UiTextAlign.Center => area.X + (area.W - widths[i]) / 2,
                UiTextAlign.Right => area.Right - widths[i],
                _ => area.X,
            };
            foreach (var (s, st, f) in line)
            {
                var atlas = Texture(f.DdsPath);
                foreach (var ch in s)
                {
                    var g = f.Glyph(ch);
                    if (g is not { } gl) continue;
                    if (atlas != null && gl.Width > 0)
                    {
                        if (st.Outline)
                            foreach (var (ox, oy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                                img.Draw(atlas, gl.X1, gl.Y1, gl.Width, gl.Height, x + gl.A + ox, y + oy, gl.Width, gl.Height, st.OutlineColor);
                        img.Draw(atlas, gl.X1, gl.Y1, gl.Width, gl.Height, x + gl.A, y, gl.Width, gl.Height, st.Color);
                    }
                    x += gl.Advance;
                }
            }
            y += LineH(line);
        }
    }

    static IEnumerable<string> SplitKeepSpaces(string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == ' ') { yield return s[start..(i + 1)]; start = i + 1; }
        if (start < s.Length) yield return s[start..];
    }
}
