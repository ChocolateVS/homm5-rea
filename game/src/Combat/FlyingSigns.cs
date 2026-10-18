using Godot;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// The original combat "flying signs": short texts that rise from a stack and fade.
/// Behaviour from H5_Game.exe:
///  • casualty sign (FUN_0054a5c0): UI/CombatScreen-Heavy texts T_CREATURE_KILLED
///    ("&lt;flying_signs&gt;&lt;color_negative&gt;-&lt;value=delta&gt;") / T_CREATURE_RESURRECTED, glow
///    effect E_GOLD_OUTLINE, lifetime setvar combat_casuality_sign_duration (default 1600 ms);
///  • event sign (FUN_0054ad10): Text/Game/CombatStrings/Flying_Signs/* (Miss, Resisted,
///    Deadly strike, elemental damage, Unsummon), glow E_BLACK_OUTLINE, lifetime
///    combat_event_sign_duration (default 3000 ms);
///  • a stack's next sign never starts earlier than 500 ms after its previous one (DAT_00f31e14);
///  • the anchor is the stack's sign point with a zero offset (DAT_00fd64fc…, never written).
/// The original shows no damage numbers and no luck/morale signs (those are log lines + effects).
/// </summary>
public partial class FlyingSigns : Control
{
    public const double CasualtyDuration = 1.6, EventDuration = 3.0, MinSpacing = 0.5;
    /// <summary>World-space rise over a sign's lifetime (visual estimate; speed setvar ui_flying_signs_speed = 1).</summary>
    const float RiseMetres = 1.0f;

    // UIGameRoot outline effects: brushes stamped around the glyph shapes (BuildOutline).
    const string GoldGlow = "Textures/AdventureScreen/Light.xdb";
    const string BlackGlow = "Textures/Interface/Glow/BlackGlow.xdb";

    sealed class Sign
    {
        public required Func<Vector3> Anchor;
        public required BitmapTextLayout Layout;
        /// <summary>The outline effect rendered around the glyph shapes (see <see cref="BuildOutline"/>).</summary>
        public Texture2D? Glow;
        public Vector2 GlowOrigin;
        public bool GlowAdditive;
        public double Start, Duration;
    }

    readonly List<Sign> _signs = new();
    readonly Dictionary<object, double> _lastStart = new();
    Camera3D? _camera;

    public FlyingSigns()
    {
        Name = "FlyingSigns";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    Layer _glowLayer = null!, _textLayer = null!;

    /// <summary>A full-rect child canvas; the glow layer uses the additive UI material.</summary>
    sealed partial class Layer : Control
    {
        public Action<Control>? Paint;
        public Layer() { AnchorRight = 1; AnchorBottom = 1; MouseFilter = MouseFilterEnum.Ignore; TextureFilter = TextureFilterEnum.Nearest; }
        public override void _Draw() => Paint?.Invoke(this);
    }

    public override void _Ready()
    {
        _glowLayer = new Layer { Name = "Glow", Material = UiResources.AdditiveMaterial, Paint = c => PaintSigns(c, glow: true) };
        _textLayer = new Layer { Name = "Text", Paint = c => PaintSigns(c, glow: false) };
        AddChild(_glowLayer);
        AddChild(_textLayer);
    }

    public void Attach(Camera3D camera) => _camera = camera;

    static double Now => Time.GetTicksMsec() / 1000.0;

    int _casualties;

    public void Casualty(object key, Func<Vector3> anchor, int delta)
    {
        Add(key, anchor, delta < 0 ? "T_CREATURE_KILLED" : "T_CREATURE_RESURRECTED",
            new Dictionary<string, string> { ["delta"] = Math.Abs(delta).ToString() }, GoldGlow, CasualtyDuration, fromWindowTexts: true);
        // Dev capture 0.4 s after the sign becomes visible (signs on one stack are spaced 500 ms apart).
        if (CombatLauncher.SignScreenshot is { } shot && ++_casualties == shot.N && _signs.Count > 0)
            GetTree().CreateTimer(0.4 + Math.Max(0, _signs[^1].Start - Now)).Timeout += () =>
            {
                GetViewport().GetTexture().GetImage().SavePng(shot.Path);
                GD.Print($"[H5] sign screenshot {shot.Path}");
            };
    }

    /// <summary>Event sign from Text/Game/CombatStrings/Flying_Signs/&lt;name&gt;.txt.</summary>
    public void Event(object key, Func<Vector3> anchor, string name, Dictionary<string, string>? values = null) =>
        Add(key, anchor, $"Text/Game/CombatStrings/Flying_Signs/{name}.txt", values, BlackGlow, EventDuration, fromWindowTexts: false);

    void Add(object key, Func<Vector3> anchor, string textRef, Dictionary<string, string>? values, string glow, double duration, bool fromWindowTexts)
    {
        var markup = fromWindowTexts ? CombatTexts.Get(textRef) : UiResources.Instance.Text(textRef);
        if (string.IsNullOrEmpty(markup)) return;
        var layout = BitmapTextLayout.Build(markup, values, new Rect2(0, 0, 400, 64), wrap: false);
        double start = Now;
        if (_lastStart.TryGetValue(key, out var last) && start < last + MinSpacing) start = last + MinSpacing;
        _lastStart[key] = start;
        bool additive = UiResources.Instance.IsAdditive(glow);
        var (glowTex, glowOrigin) = BuildOutline(layout, UiResources.Instance.Texture(glow), additive);
        _signs.Add(new Sign
        {
            Anchor = anchor, Layout = layout, Start = start, Duration = duration,
            Glow = glowTex, GlowOrigin = glowOrigin, GlowAdditive = additive,
        });
    }

    // ---- outline effect ----------------------------------------------------------------------------

    static readonly Dictionary<Texture2D, Image> ImageCache = new();

    static Image? ImageOf(Texture2D? t)
    {
        if (t == null) return null;
        if (!ImageCache.TryGetValue(t, out var img))
        {
            img = t.GetImage();
            if (img == null) return null;
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.Rgba8);
            ImageCache[t] = img;
        }
        return img;
    }

    /// <summary>
    /// The UIGameRoot outline effects (E_GOLD_OUTLINE = Textures/AdventureScreen/Light, E_BLACK_OUTLINE =
    /// Textures/Interface/Glow/BlackGlow) are brushes, not backdrops: BlackGlow is a 7×7 solid black
    /// square with a soft border, Light a 32×32 warm-white point with a faint gold falloff. Stamping the
    /// brush, centred and at 1:1 pixel scale, over every covered glyph pixel gives an outline that hugs
    /// the letters (a thick black rim for event signs, a gold rim for casualty numbers) while the text
    /// itself stays crisp on top. Stamps combine by maximum, so overlapping strokes never saturate.
    /// Built once per sign on the CPU.
    /// </summary>
    static (Texture2D? Tex, Vector2 Origin) BuildOutline(BitmapTextLayout layout, Texture2D? brushTex, bool additive)
    {
        var brush = ImageOf(brushTex);
        if (brush == null || layout.Glyphs.Count == 0) return (null, Vector2.Zero);
        int bw = brush.GetWidth(), bh = brush.GetHeight(), cx = bw / 2, cy = bh / 2;
        // Brush texels that contribute (colour for additive brushes, alpha for transparent ones).
        var taps = new List<(int dx, int dy, Color c)>();
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                var c = brush.GetPixel(x, y);
                if (additive ? (c.R + c.G + c.B) > 0 : c.A > 0) taps.Add((x - cx, y - cy, c));
            }
        if (taps.Count == 0) return (null, Vector2.Zero);

        var bounds = new Rect2(layout.Glyphs[0].Pos, layout.Glyphs[0].Source.Size);
        foreach (var g in layout.Glyphs) bounds = bounds.Merge(new Rect2(g.Pos, g.Source.Size));
        int padX = Math.Max(cx, bw - cx), padY = Math.Max(cy, bh - cy);
        int w = (int)Math.Ceiling(bounds.Size.X) + 2 * padX, h = (int)Math.Ceiling(bounds.Size.Y) + 2 * padY;
        var origin = bounds.Position - new Vector2(padX, padY);

        // Glyph coverage mask.
        var mask = new float[w * h];
        foreach (var g in layout.Glyphs)
        {
            var atlas = ImageOf(g.Atlas);
            if (atlas == null) continue;
            int gx0 = (int)Math.Round(g.Pos.X - origin.X), gy0 = (int)Math.Round(g.Pos.Y - origin.Y);
            int sx0 = (int)g.Source.Position.X, sy0 = (int)g.Source.Position.Y;
            for (int y = 0; y < (int)g.Source.Size.Y; y++)
                for (int x = 0; x < (int)g.Source.Size.X; x++)
                {
                    int mx = gx0 + x, my = gy0 + y;
                    if (mx < 0 || my < 0 || mx >= w || my >= h) continue;
                    float a = atlas.GetPixel(sx0 + x, sy0 + y).A;
                    ref float m = ref mask[my * w + mx];
                    if (a > m) m = a;
                }
        }

        // Max-stamp the brush.
        var r = new float[w * h]; var gg = new float[w * h]; var b = new float[w * h]; var al = new float[w * h];
        for (int my = 0; my < h; my++)
            for (int mx = 0; mx < w; mx++)
            {
                float m = mask[my * w + mx];
                if (m <= 0) continue;
                foreach (var (dx, dy, c) in taps)
                {
                    int x = mx + dx, y = my + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    int i = y * w + x;
                    if (additive)
                    {
                        r[i] = Math.Max(r[i], c.R * m); gg[i] = Math.Max(gg[i], c.G * m); b[i] = Math.Max(b[i], c.B * m);
                    }
                    else al[i] = Math.Max(al[i], c.A * m);
                }
            }
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                img.SetPixel(x, y, additive ? new Color(r[i], gg[i], b[i], 1f) : new Color(0, 0, 0, al[i]));
            }
        return (ImageTexture.CreateFromImage(img), origin);
    }

    public override void _Process(double delta)
    {
        double now = Now;
        _signs.RemoveAll(s => now > s.Start + s.Duration);
        _glowLayer.QueueRedraw();
        _textLayer.QueueRedraw();
    }

    void PaintSigns(Control canvas, bool glow)
    {
        if (_camera == null) return;
        double now = Now;
        foreach (var s in _signs)
        {
            if (now < s.Start) continue;
            float k = (float)((now - s.Start) / s.Duration);
            var world = s.Anchor() + new Vector3(0, RiseMetres * k, 0);
            if (_camera.IsPositionBehind(world)) continue;
            var screen = _camera.UnprojectPosition(world);
            // Fade over the last third of the lifetime.
            float alpha = k < 0.66f ? 1f : Math.Max(0f, 1f - (k - 0.66f) / 0.34f);
            var offset = (screen - new Vector2(s.Layout.Size.X / 2, s.Layout.Size.Y)).Round();
            // Outline effect (BuildOutline): additive brushes (Light.xdb is CONVERT_TRANSPARENT_ADD) go
            // on the additive layer and fade by colour; alpha brushes (BlackGlow) are drawn under the
            // glyphs on the normal layer.
            if (s.Glow != null && s.GlowAdditive == glow)
            {
                var glowColor = glow ? new Color(alpha, alpha, alpha) : new Color(1, 1, 1, alpha);
                canvas.DrawTexture(s.Glow, s.GlowOrigin + offset, glowColor);
            }
            if (!glow)
                foreach (var g in s.Layout.Glyphs)
                    canvas.DrawTextureRectRegion(g.Atlas, new Rect2(g.Pos + offset, g.Source.Size), g.Source, g.Color with { A = g.Color.A * alpha });
        }
    }
}

/// <summary>Named texts of the combat window (UI/CombatScreen-Heavy/Texts.(WindowRelatedTexts).xdb).</summary>
public static class CombatTexts
{
    const string Path = "UI/CombatScreen-Heavy/Texts.(WindowRelatedTexts).xdb";
    static Dictionary<string, string>? _texts;

    public static string? Get(string name)
    {
        if (_texts == null)
        {
            _texts = new(StringComparer.Ordinal);
            var root = Game.Instance.Xdb.Load(Path).Root!;
            foreach (var item in root.Descendants("Item"))
            {
                var key = (string?)item.Element("TextName");
                var href = (string?)item.Element("text")?.Attribute("href");
                if (key == null || href == null) continue;
                var text = Game.Instance.Xdb.ReadText(href, Path);
                if (text != null) _texts[key] = text;
            }
        }
        return _texts.GetValueOrDefault(name);
    }
}
