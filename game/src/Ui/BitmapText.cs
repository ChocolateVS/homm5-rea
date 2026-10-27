using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// Lays out and draws H5 markup with the original pre-rendered bitmap fonts through the exe's text layout
/// (<see cref="UiTextLayout"/>, H5_Game.exe CMLLayout): exe tokenization, font choice and glyph scaling, wrapping,
/// line alignment and vertical metrics, 8-way outlines. The layout runs in screen pixels at the UI scale (pt
/// font sizes pick their atlas for the scaled size, like the original's screen / virtual ratio) and is stored
/// in UI units relative to the block origin.
/// </summary>
public sealed class BitmapTextLayout
{
    public readonly record struct Glyph(Texture2D Atlas, Rect2 Source, Rect2 Dest, Color Color)
    {
        public Vector2 Pos => Dest.Position;
    }

    public List<Glyph> Glyphs { get; } = new();
    /// <summary>Block size in UI units as the exe reports it (rounded, CML GetSize).</summary>
    public Vector2 Size { get; private set; }
    /// <summary>Widest line in UI units (for windows that take their text's width).</summary>
    public float MaxLineWidth { get; private set; }
    /// <summary>Where the block was placed by <see cref="Build"/> (UI units); the glyphs already include it.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>Lays out <paramref name="markup"/> wrapped at <paramref name="width"/> UI units (no placement).</summary>
    public static BitmapTextLayout Block(string markup, IReadOnlyDictionary<string, string>? values, float width, bool wrap = true, float? scale = null)
    {
        var res = UiResources.Instance;
        float s = scale ?? Settings.VideoSettings.UiScale;
        if (s <= 0) s = 1;
        var stream = res.Markup.Tokenize(markup, null, values);
        var block = UiTextLayout.Layout(stream, res.Fonts, width * s, s, wrap);
        var layout = new BitmapTextLayout
        {
            // The exe converts the rounded screen-pixel size back to UI units (00b291b0).
            Size = new Vector2(Mathf.Round(block.RoundedWidth / s), Mathf.Round(block.RoundedHeight / s)),
            MaxLineWidth = Mathf.Ceil(block.MaxLineWidth / s),
        };
        foreach (var q in block.Quads)
        {
            var atlas = res.Texture(q.Font.DdsPath);
            if (atlas == null) continue;
            // Quads are snapped to whole screen pixels (the original's float positions go to the rasterizer).
            var dest = new Rect2(Mathf.Round(q.X) / s, Mathf.Round(q.Y) / s, q.W / s, q.H / s);
            // Glyphs drawn at another size than their atlas (pt sizes between atlases, UI scale) are filtered
            // bilinearly; nearest sampling would drop whole rows of the letters.
            bool exact = Mathf.IsEqualApprox(q.W, q.SrcW) && Mathf.IsEqualApprox(q.H, q.SrcH);
            layout.Glyphs.Add(new Glyph(exact ? atlas : Linear(atlas), new Rect2(q.SrcX, q.SrcY, q.SrcW, q.SrcH), dest, UiResources.ToColor(q.Color)));
        }
        return layout;
    }

    static readonly Dictionary<Texture2D, CanvasTexture> LinearAtlases = new();

    static Texture2D Linear(Texture2D atlas)
    {
        if (!LinearAtlases.TryGetValue(atlas, out var t))
            LinearAtlases[atlas] = t = new CanvasTexture { DiffuseTexture = atlas, TextureFilter = CanvasItem.TextureFilterEnum.Linear };
        return t;
    }

    /// <summary>
    /// Lays out <paramref name="markup"/> at the width of <paramref name="area"/> and places the block at its left
    /// edge, vertically by <paramref name="verticalAlign"/> (centre: round-half-even like the exe's placement).
    /// </summary>
    public static BitmapTextLayout Build(string markup, IReadOnlyDictionary<string, string>? values, Rect2 area,
        UiAlign verticalAlign = UiAlign.LowEnd, bool wrap = true, float? scale = null)
    {
        var layout = Block(markup, values, area.Size.X, wrap, scale);
        int h = (int)layout.Size.Y;
        float y = verticalAlign switch
        {
            UiAlign.Center => area.Position.Y + UiLayout.HalfEven((int)area.Size.Y - h),
            UiAlign.HighEnd => area.End.Y - h,
            _ => area.Position.Y,
        };
        return layout.At(new Vector2(area.Position.X, y));
    }

    /// <summary>Moves the block so its origin is at <paramref name="origin"/>.</summary>
    public BitmapTextLayout At(Vector2 origin)
    {
        var d = origin - Origin;
        for (int i = 0; i < Glyphs.Count; i++)
            Glyphs[i] = Glyphs[i] with { Dest = new Rect2(Glyphs[i].Dest.Position + d, Glyphs[i].Dest.Size) };
        Origin = origin;
        return this;
    }

    public void Draw(CanvasItem canvas, Vector2 offset = default)
    {
        foreach (var g in Glyphs)
            canvas.DrawTextureRectRegion(g.Atlas, new Rect2(g.Dest.Position + offset, g.Dest.Size), g.Source, g.Color);
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
