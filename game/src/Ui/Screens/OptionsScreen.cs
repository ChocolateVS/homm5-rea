using Godot;
using H5.Core.Ui;
using H5Reborn.Settings;

namespace H5Reborn.Ui.Screens;

/// <summary>
/// UI/GameOptions, "Video &amp; Audio Options" category. Entries are instances of the
/// screen's own templates (SectionPanelTemplate, CheckBoxPanelTemplate,
/// ComboBoxPanelTemplate) stacked in a page's PageCont, as the original code does.
/// The left page holds the stock video entries (Screen resolution, Full Screen,
/// texts from UI/Consts/GameOptions); the right page holds this port's display
/// extensions (window mode, interface scale). Changes apply immediately and are saved.
/// </summary>
public partial class OptionsScreen : UiScreen
{
    const string Texts = "UI/Consts/GameOptions/";
    static readonly (string Label, float Value)[] Scales =
    {
        ("Auto", 0), ("100%", 1), ("125%", 1.25f), ("133%", 4f / 3f), ("150%", 1.5f), ("200%", 2), ("250%", 2.5f), ("300%", 3),
    };
    static readonly string[] Modes = { "Full screen", "Windowed", "Borderless window" };

    readonly string _returnTo;
    List<Vector2I> _resolutions = new();
    UiComboBox? _resolution, _mode, _scale;
    UiNode? _fullScreen;

    static string T(string name) => UiResources.Instance.Text(Texts + name) ?? name;

    public OptionsScreen(string returnTo = "mainmenu") : base("UI/GameOptions.(WindowScreen).xdb")
    {
        _returnTo = returnTo;
        SetVisible("MenuPanel", false);
        SetVisible("OptionsMenuPanel", true);
        SetVisible("PagePanel1_1", false);
        Find("CategoryHeaderView")?.SetText("<h1>" + T("CategoryVideoAudio.txt"));

        BuildVideoPage(Root.Find("PagePanel1_2")!);
        BuildDisplayPage(Root.Find("PagePanel2_2")!);

        Bus.On("options_back", () => UiNavigator.Goto(_returnTo));
        Bus.On("back", () => UiNavigator.Goto(_returnTo));
        Bus.On("accept", () => { VideoSettings.Save(); UiNavigator.Goto(_returnTo); });
        Bus.On("default", Defaults);
        Bus.On("option_changed", () => { });
        Refresh();
    }

    sealed class Page(OptionsScreen screen, UiWindow panel)
    {
        readonly UiWindow _cont = panel.Find("PageCont")!;
        int _y;

        public UiNode Add(string template)
        {
            var t = panel.Find(template)!;
            // Templates are authored at the page panel's origin; keep that geometry inside PageCont.
            var node = screen.Instantiate(t, _cont, new Int2(panel.Rect.X - _cont.Rect.X, _y));
            _y += t.Rect.H;
            return node;
        }

        public void Section(string title) => Child(Add("SectionPanelTemplate"), "HeaderView")?.SetText("<h3><color_default>" + title);

        public UiNode? Check(string label)
        {
            var row = Add("CheckBoxPanelTemplate");
            Child(row, "DescView")?.SetText("<body>" + label);
            return Child(row, "Button");
        }

        public UiComboBox? Combo(string label)
        {
            var row = Add("ComboBoxPanelTemplate");
            Child(row, "DescView")?.SetText("<body>" + label);
            return Child(row, "ComboBox") as UiComboBox;
        }

        static UiNode? Child(UiNode row, string name) =>
            row.FindChild(name, recursive: true, owned: false) as UiNode;
    }

    void BuildVideoPage(UiWindow panel)
    {
        (Node(panel).FindChild("PageHeaderView", true, false) as UiNode)?.SetText("<h3_header>" + T("PageVideo.txt"));
        var page = new Page(this, panel);
        _resolution = page.Combo(T("VideoScreenResolution/Name.txt"));
        _fullScreen = page.Check(T("VideoFullScreen/Name.txt"));

        _resolutions = VideoSettings.AvailableResolutions();
        if (_resolution != null)
        {
            _resolution.SelectionChanged += i =>
            {
                VideoSettings.Resolution = _resolutions[i];
                ApplyAndSave();
            };
        }
        if (_fullScreen != null)
            _fullScreen.Clicked += _ =>
            {
                VideoSettings.Mode = _fullScreen.State == CheckedState(_fullScreen) ? VideoSettings.DisplayMode.Fullscreen : VideoSettings.DisplayMode.Windowed;
                ApplyAndSave();
            };
    }

    void BuildDisplayPage(UiWindow panel)
    {
        (Node(panel).FindChild("PageHeaderView", true, false) as UiNode)?.SetText("<h3_header>Display");
        var page = new Page(this, panel);
        _mode = page.Combo("Window mode");
        _scale = page.Combo("Interface scale");
        if (_mode != null)
            _mode.SelectionChanged += i => { VideoSettings.Mode = (VideoSettings.DisplayMode)i; ApplyAndSave(); };
        if (_scale != null)
            _scale.SelectionChanged += i => { VideoSettings.UiScaleSetting = Scales[i].Value; ApplyAndSave(); };
    }

    /// <summary>
    /// Which of a two-state check box's states shows the tick: the one whose Normal visual
    /// draws a texture (stock check boxes differ: MPDMCreate ticks in state 0, GameOptions in 1).
    /// </summary>
    static int CheckedState(UiNode box)
    {
        var states = box.Window.ButtonStates;
        for (int i = states.Count - 1; i >= 0; i--)
            if (states[i].Visuals.TryGetValue("Normal", out var v) && v.Background != null) return i;
        return 0;
    }

    void ApplyAndSave()
    {
        VideoSettings.Apply();
        VideoSettings.Save();
        Refresh();
    }

    void Defaults()
    {
        VideoSettings.Mode = VideoSettings.DisplayMode.Fullscreen;
        VideoSettings.Resolution = new Vector2I(1920, 1080);
        VideoSettings.UiScaleSetting = 0;
        ApplyAndSave();
    }

    void Refresh()
    {
        var current = VideoSettings.Resolution;
        int index = _resolutions.FindIndex(r => r == current);
        if (index < 0) index = _resolutions.Count - 1; // larger than the desktop: shown as the desktop size
        _resolution?.SetItems(_resolutions.Select(r => $"{r.X} x {r.Y}"), index);
        if (_fullScreen != null)
            _fullScreen.SetState(VideoSettings.Mode == VideoSettings.DisplayMode.Fullscreen ? CheckedState(_fullScreen) : 1 - CheckedState(_fullScreen));
        _mode?.SetItems(Modes, (int)VideoSettings.Mode);
        int scaleIndex = Array.FindIndex(Scales, s => Math.Abs(s.Value - VideoSettings.UiScaleSetting) < 0.01f);
        _scale?.SetItems(Scales.Select(s => s.Value == 0 ? $"Auto ({VideoSettings.AutoUiScale(VideoSettings.RenderSize.Y) * 100:0}%)" : s.Label),
            Math.Max(0, scaleIndex));
    }
}
