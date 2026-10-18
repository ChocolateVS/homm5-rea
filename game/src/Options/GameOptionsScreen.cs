using System.Globalization;
using Godot;
using H5.Core.Ui;
using H5Reborn.Settings;
using H5Reborn.Ui;

namespace H5Reborn.Options;

/// <summary>
/// UI/GameOptions driven by the original option definitions (<see cref="OptionsData"/>):
/// the menu panel lists one button per category (TemplateBtn clones, labelled with the
/// category's MenuButtonName) plus Replay and Back; choosing a category shows its two
/// pages side by side (PagePanel1_2 / PagePanel2_2) built from the page templates
/// (SectionPanelTemplate, CheckBoxPanelTemplate, ScrollBarPanelTemplate,
/// ComboBoxPanelTemplate), with Default, Apply and Back. Changes are pending until Apply;
/// options flagged Preview (volumes, gamma) take effect while editing and are reverted by
/// Back. Hidden options are skipped. This port's display extensions (window mode,
/// interface scale; user-requested, not in the original) follow the stock audio entries.
/// </summary>
public partial class GameOptionsScreen : UiScreen
{
    static readonly (string Label, float Value)[] Scales =
    {
        ("Auto", 0), ("100%", 1), ("125%", 1.25f), ("133%", 4f / 3f), ("150%", 1.5f), ("200%", 2), ("250%", 2.5f), ("300%", 3),
    };
    static readonly string[] Modes = { "Full screen", "Windowed", "Borderless window" };

    readonly Action _close;
    readonly OptionsData _data = OptionsData.Instance;
    readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly List<UiNode> _rows = new();
    readonly List<Action> _refreshers = new();
    OptionsData.Category? _category;

    /// <summary>Flags read by DisableByCodeGlobalVar: set by the game while they apply.</summary>
    public static readonly HashSet<string> CodeFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "disable_by_non_collector_edition", // the Collector's Edition hero trail is not part of this release
    };

    public GameOptionsScreen(Action close) : base("UI/GameOptions.(WindowScreen).xdb")
    {
        _close = close;
        BuildMenu();
        Bus.On("back", () => _close());
        Bus.On("options_back", () => { RevertPreview(); ShowMenu(); });
        Bus.On("accept", Accept);
        Bus.On("default", AskDefaults);
        Bus.On("combat_replay", () => { });
        Bus.On("option_changed", () => { });
        // Category buttons are TemplateBtn clones: they send the template's "template" command.
        Bus.On("template", (_, node) =>
        {
            var id = node?.Name.ToString() is { } n && n.StartsWith("Category_") ? n["Category_".Length..] : null;
            if (_data.Categories.FirstOrDefault(c => c.Id == id) is { } cat) ShowCategory(cat);
        });
        ShowMenu();
        // Dev/test: `--options-category=game|video_audio` opens a category directly.
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--options-category=")) is { } arg
            && _data.Categories.FirstOrDefault(c => c.Id == arg["--options-category=".Length..]) is { } devCat)
            ShowCategory(devCat);
    }

    // ---------------------------------------------------------------- category menu

    void BuildMenu()
    {
        if (Root.Find("TemplateBtn") is not { } template) return;
        var panel = Root.Find("MenuPanel")!;
        int step = 31; // stock button pitch in the menu panel (TemplateBtn 575 → Replay 606)
        for (int i = 0; i < _data.Categories.Count; i++)
        {
            var c = _data.Categories[i];
            // Categories stack upwards from the template's slot, above Replay/Back.
            var at = new Int2(template.Rect.X - panel.Rect.X, template.Rect.Y - panel.Rect.Y - (_data.Categories.Count - 1 - i) * step);
            var btn = Instantiate(template, panel, at, "Category_" + c.Id);
            btn.SetText(c.MenuButtonName);
            btn.Visible = true;
            btn.TooltipText = c.Tooltip;

        }
        if (Find("TemplateBtn") is { } t) t.Visible = false;
        // Combat replays are not part of this port: the button stays in place, disabled.
        if (Find("CombatReplayBtn") is { } replay) replay.Enabled = false;
    }

    void ShowMenu()
    {
        _category = null;
        SetVisible("MenuPanel", true);
        SetVisible("OptionsMenuPanel", false);
        SetVisible("GameOptions", false);
    }

    void ShowCategory(OptionsData.Category c)
    {
        _category = c;
        _pending.Clear();
        SetVisible("MenuPanel", false);
        SetVisible("OptionsMenuPanel", true);
        SetVisible("GameOptions", true);
        SetVisible("PagePanel1_1", false);
        Find("CategoryHeaderView")?.SetText("<h1>" + c.Name);
        foreach (var r in _rows) r.QueueFree();
        _rows.Clear();
        _refreshers.Clear();
        var panels = new[] { "PagePanel1_2", "PagePanel2_2" };
        for (int i = 0; i < panels.Length; i++)
        {
            var panel = Root.Find(panels[i])!;
            SetVisible(panels[i], i < c.Pages.Count);
            if (i >= c.Pages.Count) continue;
            BuildPage(panel, c.Pages[i], c.Id == "video_audio" && c.Pages[i].Id == "page_audio");
        }
        RefreshAll();
    }

    // ---------------------------------------------------------------- page building

    sealed class PageCursor(GameOptionsScreen screen, UiWindow panel)
    {
        readonly UiWindow _cont = panel.Find("PageCont")!;
        int _y;

        public UiNode Add(string template)
        {
            var t = panel.Find(template)!;
            // Templates are authored at the page panel's origin; keep that geometry inside PageCont.
            var node = screen.Instantiate(t, _cont, new Int2(panel.Rect.X - _cont.Rect.X, _y));
            node.Visible = true;
            _y += t.Rect.H;
            screen._rows.Add(node);
            return node;
        }
    }

    static UiNode? Child(Node row, string name) => row.FindChild(name, recursive: true, owned: false) as UiNode;

    void BuildPage(UiWindow panel, OptionsData.Page page, bool withDisplayExtensions)
    {
        (Node(panel).FindChild("PageHeaderView", true, false) as UiNode)?.SetText("<h3_header>" + page.Name);
        var cursor = new PageCursor(this, panel);
        foreach (var o in page.Options)
        {
            if (o.Hide) continue;
            switch (o.Type)
            {
                case OptionsData.Kind.BlockHeader:
                    if (o.Name.Length > 0) Child(cursor.Add("SectionPanelTemplate"), "HeaderView")?.SetText("<h3><color_default>" + o.Name);
                    break;
                case OptionsData.Kind.CheckBox: AddCheck(cursor, o); break;
                case OptionsData.Kind.Scroller: AddScroller(cursor, o); break;
                case OptionsData.Kind.ComboBox: AddCombo(cursor, o); break;
            }
        }
        if (withDisplayExtensions) AddDisplayExtensions(cursor);
    }

    string Value(OptionsData.Option o) =>
        o.Var == null ? "0" : _pending.TryGetValue(o.Var, out var v) ? v : OptionsStore.Get(o.Var);

    static float ParseF(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;

    void Change(OptionsData.Option o, string value)
    {
        if (o.Var == null) return;
        _pending[o.Var] = value;
        // Preview options act at once (the stock screen sends options_preview_changed).
        if (o.Preview) { OptionsStore.Set(o.Var, value); OptionsStore.Apply(); }
        // An option controlling another forces it while disabled.
        if (o.DisablesId != null && _data.Find(o.DisablesId) is { Var: { } target } controlled && ParseF(value) != o.DisableByValue)
            _pending[target] = controlled.Type == OptionsData.Kind.CheckBox ? o.DisabledValue.ToString(CultureInfo.InvariantCulture) : Value(controlled);
        RefreshAll();
    }

    bool IsEnabled(OptionsData.Option o)
    {
        if (o.DisableByCode is { } flag)
        {
            bool negate = flag.StartsWith('~');
            var name = negate ? flag[1..] : flag;
            bool set = CodeFlags.Contains(name) || (name.StartsWith("gfx_") && OptionsStore.GetBool(name));
            if (negate ? !set : set) return false;
        }
        foreach (var other in _data.AllOptions)
            if (other.DisablesId == o.Id && other.DisablesId != null && ParseF(Value(other)) != other.DisableByValue)
                return false;
        return true;
    }

    void AddCheck(PageCursor cursor, OptionsData.Option o)
    {
        var row = cursor.Add("CheckBoxPanelTemplate");
        Child(row, "DescView")?.SetText("<body>" + o.Name);
        row.TooltipText = UiResources.StripMarkup(o.Tooltip);
        if (Child(row, "Button") is not { } box) return;
        int on = CheckedState(box);
        box.Clicked += _ => Change(o, ParseF(Value(o)) != 0 ? "0" : "1");
        _refreshers.Add(() =>
        {
            box.SetState(ParseF(Value(o)) != 0 ? on : 1 - on);
            box.Enabled = IsEnabled(o);
        });
    }

    void AddScroller(PageCursor cursor, OptionsData.Option o)
    {
        var row = cursor.Add("ScrollBarPanelTemplate");
        Child(row, "DescView")?.SetText("<body>" + o.Name);
        row.TooltipText = UiResources.StripMarkup(o.Tooltip);
        Child(row, "LowView")?.SetText("<value_value>" + (o.Labels.Count > 0 ? o.Labels[0] : ""));
        Child(row, "HighView")?.SetText("<value_value><right>" + (o.Labels.Count > 1 ? o.Labels[1] : ""));
        var bar = Child(row, "ScrollBar");
        var markers = Child(row, "MarkersBlock");
        if (Child(row, "MarkerTemplate") is { } mt) mt.Visible = false;
        var scroller = new UiScrollerView();
        bar?.AddChild(scroller);
        var marks = new MarkersView();
        markers?.AddChild(marks);
        scroller.PositionChanged += i => Change(o, o.Items[i].Value);
        _refreshers.Add(() =>
        {
            bool enabled = IsEnabled(o);
            scroller.Setup(o.Items.Count, NearestItem(o, Value(o)));
            scroller.Enabled = enabled;
            marks.Setup(o.Items.Count, enabled);
            if (Child(row, "ScrollBarBgNormal") is { } bg) bg.Visible = enabled;
            if (Child(row, "ScrollBarBgDisabled") is { } bgd) bgd.Visible = !enabled;
            row.TooltipText = UiResources.StripMarkup(o.Tooltip
                .Replace("<value=value>", o.Items.Count > 0 ? o.Items[NearestItem(o, Value(o))].Name : ""));
        });
    }

    void AddCombo(PageCursor cursor, OptionsData.Option o)
    {
        var row = cursor.Add("ComboBoxPanelTemplate");
        Child(row, "DescView")?.SetText("<body>" + o.Name);
        row.TooltipText = UiResources.StripMarkup(o.Tooltip);
        if (Child(row, "ComboBox") is not UiComboBox combo) return;
        var items = ItemsOf(o);
        combo.SelectionChanged += i => { if (i >= 0 && i < items.Count) Change(o, items[i].Value); };
        _refreshers.Add(() =>
        {
            combo.SetItems(items.Select(it => it.Name), NearestItem(o, Value(o), items));
            combo.Enabled = IsEnabled(o);
            if (Child(row, "ComboBoxDisabled") is { } d) d.Visible = !combo.Enabled;
        });
    }

    /// <summary>Items of an option; gfx_availible_resolutions is the monitor's resolution list.</summary>
    List<OptionsData.Item> ItemsOf(OptionsData.Option o)
    {
        if (o.ItemsGlobalVar == "gfx_availible_resolutions")
        {
            var list = VideoSettings.AvailableResolutions();
            if (!list.Contains(VideoSettings.Resolution)) list.Add(VideoSettings.Resolution);
            return list.Select(r => new OptionsData.Item($"{r.X} x {r.Y}", $"{r.X}x{r.Y}")).ToList();
        }
        return o.Items;
    }

    int NearestItem(OptionsData.Option o, string value, List<OptionsData.Item>? items = null)
    {
        items ??= o.Items;
        if (items.Count == 0) return -1;
        int exact = items.FindIndex(i => string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;
        if (o.UseItemNameAsValue) return items.Count - 1;
        float v = ParseF(value);
        int best = 0;
        for (int i = 1; i < items.Count; i++)
            if (Math.Abs(ParseF(items[i].Value) - v) < Math.Abs(ParseF(items[best].Value) - v)) best = i;
        return best;
    }

    static int CheckedState(UiNode box)
    {
        var states = box.Window.ButtonStates;
        for (int i = states.Count - 1; i >= 0; i--)
            if (states[i].Visuals.TryGetValue("Normal", out var v) && v.Background != null) return i;
        return 0;
    }

    // ---------------------------------------------------------------- display extensions

    void AddDisplayExtensions(PageCursor cursor)
    {
        Child(cursor.Add("SectionPanelTemplate"), "HeaderView")?.SetText("<h3><color_default>Display");
        var modeRow = cursor.Add("ComboBoxPanelTemplate");
        Child(modeRow, "DescView")?.SetText("<body>Window mode");
        var scaleRow = cursor.Add("ComboBoxPanelTemplate");
        Child(scaleRow, "DescView")?.SetText("<body>Interface scale");
        var mode = Child(modeRow, "ComboBox") as UiComboBox;
        var scale = Child(scaleRow, "ComboBox") as UiComboBox;
        if (mode != null)
            mode.SelectionChanged += i =>
            {
                VideoSettings.Mode = (VideoSettings.DisplayMode)i;
                _pending["gfx_fullscreen"] = i == 0 ? "1" : "0";
                VideoSettings.Apply(); VideoSettings.Save(); RefreshAll();
            };
        if (scale != null)
            scale.SelectionChanged += i => { VideoSettings.UiScaleSetting = Scales[i].Value; VideoSettings.Apply(); VideoSettings.Save(); RefreshAll(); };
        // Extended adventure zoom: a user-requested extension (not in the original) that lets the
        // adventure camera pull back much further; stored per profile as h5r_adv_extended_zoom.
        var zoomRow = cursor.Add("CheckBoxPanelTemplate");
        Child(zoomRow, "DescView")?.SetText("<body>Extended map zoom");
        zoomRow.TooltipText = "Lets the adventure map camera zoom out further than the original (H5Reborn extension).";
        if (Child(zoomRow, "Button") is { } zoomBox)
        {
            int on = CheckedState(zoomBox);
            zoomBox.Clicked += _ =>
            {
                bool now = !OptionsStore.GetBool(H5Reborn.Adventure.AdvCamera.ExtendedZoomVar, true);
                OptionsStore.Set(H5Reborn.Adventure.AdvCamera.ExtendedZoomVar, now ? "1" : "0");
                OptionsStore.Save();
                RefreshAll();
            };
            _refreshers.Add(() => zoomBox.SetState(OptionsStore.GetBool(H5Reborn.Adventure.AdvCamera.ExtendedZoomVar, true) ? on : 1 - on));
        }
        _refreshers.Add(() => mode?.SetItems(Modes, (int)VideoSettings.Mode));
        _refreshers.Add(() =>
        {
            int index = Array.FindIndex(Scales, s => Math.Abs(s.Value - VideoSettings.UiScaleSetting) < 0.01f);
            scale?.SetItems(Scales.Select(s => s.Value == 0 ? $"Auto ({VideoSettings.AutoUiScale(VideoSettings.RenderSize.Y) * 100:0}%)" : s.Label), Math.Max(0, index));
        });
    }

    // ---------------------------------------------------------------- commands

    void RefreshAll()
    {
        foreach (var r in _refreshers) r();
    }

    void Accept()
    {
        foreach (var (k, v) in _pending) OptionsStore.Set(k, v);
        _pending.Clear();
        OptionsStore.Save();
        RefreshAll();
    }

    void RevertPreview()
    {
        // Preview options were written to the store while editing; restore the saved values.
        if (_category != null && _pending.Count > 0) OptionsStore.Reload();
        _pending.Clear();
    }

    void AskDefaults()
    {
        if (_category is not { } c) return;
        var header = UiResources.Instance.Text("UI/GameOptions/ApplyDefaultQuestionHeader.txt") ?? "Default";
        var text = UiResources.Instance.Text("UI/GameOptions/ApplyDefaultQuestionText.txt") ?? "Restore default settings?";
        MessageBoxes.YesNo(this, header, text, yes =>
        {
            if (!yes) return;
            foreach (var o in c.Pages.SelectMany(p => p.Options))
            {
                if (o.Var == null || o.Type == OptionsData.Kind.BlockHeader || o.ItemsGlobalVar != null) continue;
                // DefaultValue1 is the value for check boxes and the item index for scrollers/combos.
                string v = o.Type == OptionsData.Kind.CheckBox || o.Items.Count == 0
                    ? o.Default.ToString(CultureInfo.InvariantCulture)
                    : o.Items[Math.Clamp((int)o.Default, 0, o.Items.Count - 1)].Value;
                Change(o, v);
            }
        });
    }
}
