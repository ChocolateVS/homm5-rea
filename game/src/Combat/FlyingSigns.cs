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

    // UIGameRoot outline effects: glow textures drawn additively behind the glyphs.
    const string GoldGlow = "Textures/AdventureScreen/Light.xdb";
    const string BlackGlow = "Textures/Interface/Glow/BlackGlow.xdb";

    sealed class Sign
    {
        public required Func<Vector3> Anchor;
        public required BitmapTextLayout Layout;
        public Texture2D? Glow;
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
        if (CombatLauncher.SignScreenshot is { } shot && ++_casualties == shot.N)
            GetTree().CreateTimer(0.4).Timeout += () =>
            {
                GetViewport().GetTexture().GetImage().SavePng(shot.Path);
                GD.Print($"[H5] sign screenshot {shot.Path}");
            };
        Add(key, anchor, delta < 0 ? "T_CREATURE_KILLED" : "T_CREATURE_RESURRECTED",
            new Dictionary<string, string> { ["delta"] = Math.Abs(delta).ToString() }, GoldGlow, CasualtyDuration, fromWindowTexts: true);
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
        _signs.Add(new Sign
        {
            Anchor = anchor, Layout = layout, Start = start, Duration = duration,
            Glow = UiResources.Instance.Texture(glow), GlowAdditive = UiResources.Instance.IsAdditive(glow),
        });
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
            // Outline effect: the effect texture stretched around each glyph. Additive textures
            // (Light.xdb is CONVERT_TRANSPARENT_ADD) go on the additive layer and fade by colour;
            // alpha-blended ones (BlackGlow) are drawn under the glyphs on the normal layer.
            if (s.Glow != null && s.GlowAdditive == glow)
            {
                var glowColor = glow ? new Color(alpha, alpha, alpha) : new Color(1, 1, 1, alpha);
                foreach (var g in s.Layout.Glyphs)
                    canvas.DrawTextureRect(s.Glow, new Rect2(g.Pos + offset - new Vector2(3, 3), g.Source.Size + new Vector2(6, 6)), false, glowColor);
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
