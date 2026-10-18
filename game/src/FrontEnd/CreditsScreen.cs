using Godot;
using H5.Core.Ui;
using H5Reborn.Effects;
using H5Reborn.Ui;

namespace H5Reborn.FrontEnd;

/// <summary>
/// The stock credits roll (NUI::CCreditsScreen, H5_Game.exe 00793d20 / 007942a0 / 007936b0),
/// opened by the main menu's "credits" message and closed by Esc (UI/Credits esc_pressed → close).
/// <list type="bullet">
/// <item>Backdrop: its own 3D view of Arenas/Interface/Magic_Guild (static resource at 0x00fe34d8,
/// initialised at 00dbbf20), with the guild model "mgi" switched to upgrade level 5 and the scene
/// clock pre-rolled 10 s (updates to 10000 / 10001 ms) like every interface arena.</item>
/// <item>Text: T_CREDITS (UI/Credits/Credits.txt) then T_NIVAL_CREDITS (Nival.txt), split at "\n";
/// each line is a copy of the TextItem view (347 px wide, height = its text) placed in TextPanel.</item>
/// <item>Scrolling: lines are created at the panel's bottom while the last line's bottom edge is
/// above it, move up by credits_move_speed px/s (setvar; code default 30, stock profile 40), and
/// are destroyed once fully above the panel. A ×300 fast mode exists in the code (flag at +0x1c0,
/// cleared after the Ubisoft block) but the screen constructor starts it off.</item>
/// <item>Fade (007936b0): with fade zone z = credits_fade_zone (default 1/6) and panel height H,
/// a line of height h at y has d = |y + h/2 − H/2| / max(1, (H − h)/2); alpha = 1 while
/// d &lt; 1 − 2z, else clamp(1 − (d − (1 − 2z)) / (2z), 0, 1).</item>
/// </list>
/// The roll does not loop or close by itself: once the last line has left the panel the screen
/// stays on the backdrop until Esc.
/// </summary>
public partial class CreditsScreen : UiScreen
{
    public const string ScreenXdb = "UI/Credits/CreditsScreen.(WindowScreen).xdb";
    const string ArenaPath = "Arenas/Interface/Magic_Guild/Magic_Guild.(ArenaDesc).xdb";
    const float PreRoll = 10f;
    /// <summary>Magic_Guild initialCamPos FOV.</summary>
    const float ArenaFov = 54.4322f;
    const int ItemWidth = 347, EmptyLineHeight = 16;

    /// <summary>setvar credits_move_speed (px/s). Stock profiles/autoexec_a2.cfg sets 40.</summary>
    public static float MoveSpeed = 40f;
    /// <summary>setvar credits_fade_zone (fraction of the panel height). Code default 1/6.</summary>
    public static float FadeZone = 1f / 6f;

    Town.TownScene3D? _scene;
    SubViewport? _viewport;
    readonly List<string> _lines = new();
    readonly List<(BitmapText Text, float Y, float H)> _items = new();
    int _next;
    Control? _panel;

    public CreditsScreen() : base(ScreenXdb)
    {
        Bus.On("close", Close);
        Bus.On("esc_pressed", Close);
        foreach (var file in new[] { "UI/Credits/Credits.txt", "UI/Credits/Nival.txt" })
            if (UiResources.Instance.Text(file) is { } text)
                foreach (var line in text.Split('\n'))
                    _lines.Add(line.TrimEnd('\r'));
    }

    public override void _Ready()
    {
        base._Ready();
        // The 3D view sits behind the credits window.
        var container = new SubViewportContainer
        {
            Name = "Credits3DContainer", AnchorRight = 1, AnchorBottom = 1, Stretch = true,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var viewport = new SubViewport { Name = "Credits3D", HandleInputLocally = false };
        var scene = new Town.TownScene3D(ArenaPath);
        viewport.AddChild(scene);
        container.AddChild(viewport);
        AddChild(container);
        MoveChild(container, 0);
        scene.ShowLevel("mgi", 5);
        _scene = scene;
        _viewport = viewport;
        foreach (var fx in scene.FindChildren("*", nameof(EffectPlayer), recursive: true, owned: false).OfType<EffectPlayer>())
            fx.Seek(PreRoll);

        _panel = Find("Main/TextPanel") ?? Find("TextPanel");
        if (Find("Main/TextPanel/TextItem") is { } template) template.Visible = false;
        else if (Find("TextItem") is { } t) t.Visible = false;
    }

    public override void _Process(double delta)
    {
        // Interface arenas use the exe projection (FOV horizontal up to 16:9) with initialCamPos FOV 54.43°.
        if (_scene?.Camera is { } cam && _viewport != null)
            ExeProjection.Apply(cam, ArenaFov, _viewport.Size);
        if (_panel == null) return;
        // Panel sizes are in UI units (the screen's UI scale is a transform on the root).
        float panelH = Math.Max(1f, _panel.Size.Y);
        float step = MoveSpeed * (float)delta;

        // Move up; drop lines whose bottom edge has passed above the panel (007942a0).
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var (text, y, h) = _items[i];
            y -= step;
            if (y + h < -1f) { text.QueueFree(); _items.RemoveAt(i); continue; }
            _items[i] = (text, y, h);
        }

        // Spawn at the bottom while the last line's bottom edge is inside the panel.
        float nextY = _items.Count == 0 ? panelH : _items[^1].Y + _items[^1].H;
        while (_next < _lines.Count && nextY <= panelH)
        {
            var markup = _lines[_next++];
            var layout = BitmapTextLayout.Build(markup, null, new Rect2(0, 0, ItemWidth, 4096));
            float h = layout.Size.Y < 1f ? EmptyLineHeight : layout.Size.Y;
            var text = new BitmapText { Markup = markup, Wrap = true, Size = new Vector2(ItemWidth, h) };
            _panel.AddChild(text);
            _items.Add((text, nextY, h));
            nextY += h;
        }

        foreach (var (text, y, h) in _items)
        {
            text.Position = new Vector2(0, y);
            text.Modulate = new Color(1, 1, 1, Alpha(y, h, panelH));
        }
    }

    /// <summary>007936b0: per-line opacity from its distance to the panel centre.</summary>
    public static float Alpha(float y, float h, float panelH)
    {
        float inner = 1f - 2f * FadeZone;
        float d = Math.Abs(y + h * 0.5f - panelH * 0.5f) / Math.Max(1f, (panelH - h) * 0.5f);
        if (d < inner) return 1f;
        float a = 1f - (d - inner) / Math.Max(1.1920929e-7f, 1f - inner);
        return Math.Clamp(a, 0f, 1f);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            Bus.Send("esc_pressed");
        }
    }

    void Close() => UiNavigator.Goto("mainmenu");
}
