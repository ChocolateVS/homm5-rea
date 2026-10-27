using System.Xml.Linq;
using Godot;
using H5.AdvNet;
using H5.Core.Ui;
using H5.Core.Xdb;
using H5.Rmg;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;

namespace H5Reborn.AdvNet;

/// <summary>
/// UI/RMGScreen, "Random Map Generator", built as the exe builds it (screen 0x00799b60): one row per entry of the
/// UIGameRoot <c>InfoRMGScreen</c> control table, cloned from the screen's templates by control type (ECT_COMBO 1 →
/// ComboTemplate 0x0079e350, ECT_SLIDER 2 → SliderTemplate, ECT_DIVIDER 3 → DividerTemplate 0x0079e2f0, ECT_CHECKBOX
/// 4 → CheckBoxTemplate 0x0079e6f0) and added to OptionsContainer. A combo row's CheckBox fixes its value: unchecked,
/// the combo is disabled (vf 0x2c 0x0079dcf0) and the option is random. Create generates the map with the ported
/// generator (src/H5.Rmg) and shows its minimap; Close hands the options back. All peers regenerate the same map from
/// these options and the seed. "Save Map" is disabled: there is no .h5m writer yet.
/// </summary>
public partial class RmgDialog : UiScreen
{
    /// <summary>EP_* values (0x00518630).</summary>
    static readonly string[] Params =
    {
        "EP_NONE", "EP_MAP_SIZE", "EP_AMOUNT_OF_WATER", "EP_MONSTER_LEVEL", "EP_NUMBER_OF_PLAYERS", "EP_NUMBER_OF_MAP_LEVELS",
        "EP_CUSTOM_TEMPLATE", "EP_GENERATE_MINIMAP", "EP_TRUE_RANDOM_TOWNS", "EP_RESOURCE_LEVEL", "EP_EXP_LEVEL", "EP_GRAAL_ON_MAP",
        "EP_RACE_PLAYER_1", "EP_RACE_PLAYER_2", "EP_RACE_PLAYER_3", "EP_RACE_PLAYER_4",
        "EP_RACE_PLAYER_5", "EP_RACE_PLAYER_6", "EP_RACE_PLAYER_7", "EP_RACE_PLAYER_8",
    };
    const int MapSize = 1, Water = 2, Monsters = 3, Players = 4, Levels = 5, Custom = 6, Minimap = 7, RandomTowns = 8,
        Resources = 9, Experience = 10, Grail = 11, Race1 = 12;
    const string RootPath = "UI/UIGameRoot.(UIGameRoot).xdb";
    const string TextsPath = "UI/RMGScreen/Texts.(WindowRelatedTexts).xdb";

    sealed class Option
    {
        public int Param, Type;
        public UiNode Row = null!;
        public UiNode? Check;
        public UiComboBox? Combo;
        public List<string> Items = new();
        public bool Checked => Check?.State > 0;          // 0x0079dca0 / 0x0079dd60: the button's state != 0
        public int Value => Combo?.Selected ?? 0;         // vf 0x28 0x0079dce0
        public bool Enabled = true;
    }

    readonly Action<RmgSettings, string> _done;
    readonly List<Option> _options = new();
    readonly TextureRect _minimap = new() { AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
    List<string> _templates = new();
    RmgSettings? _generated;

    public RmgDialog(Action<RmgSettings, string> done) : base("UI/RMGScreen/RMGScreen.(WindowScreen).xdb")
    {
        _done = done;
        BuildOptions();
        // 0x00799b60: the four templates are hidden (vf 0x44(0)) once looked up; rows are their clones, the container's
        // items. Our container stacks every child of its border, so the templates leave the screen instead.
        foreach (var t in new[] { "SliderTemplate", "ComboTemplate", "DividerTemplate", "CheckBoxTemplate" })
            if (Find(t) is { } node) RemoveInstance(node);
        Find("Minimap")?.AddChild(_minimap);
        SetVisible("ProgressPanel", false);     // 0x00799b60 hides ProgressBarBox (vf 0x44(0)); the panel shows while generating
        SetVisible("MinimapUnderground", false);
        if (Find("SaveBtn") is { } save) save.Enabled = false;
        if (Find("MapNameEdit") is UiEditLine name) name.Value = "RMG Map";
        Bus.On("create", Generate);
        Bus.On("back", Close);
        Update();
    }

    static string Text(string? href) =>
        string.IsNullOrEmpty(href) ? "" : AdvNetScreens.Text(XdbRef.Parse(href, RootPath).Path).Trim();

    void BuildOptions()
    {
        if (Find("OptionsContainer") is not UiScrollableContainer box) return;
        var table = Game.Instance.Xdb.Load(RootPath).Root?.Element("InfoRMGScreen")?.Element("Controls");
        if (table == null) return;
        var templates = new Dictionary<int, string> { [1] = "ComboTemplate", [2] = "SliderTemplate", [3] = "DividerTemplate", [4] = "CheckBoxTemplate" };
        int n = 0;
        foreach (var it in table.Elements("Item"))
        {
            int param = Array.IndexOf(Params, (string?)it.Element("Param") ?? "");
            int type = ((string?)it.Element("ControlType")) switch { "ECT_COMBO" => 1, "ECT_SLIDER" => 2, "ECT_DIVIDER" => 3, "ECT_CHECKBOX" => 4, _ => 0 };
            // 0x00799b60: an unknown type gets no row; ECT_SLIDER (0x0079b710) clones its template but no table entry uses it.
            if (type == 0 || type == 2 || !templates.TryGetValue(type, out var tname) || Find(tname) is not { } template) continue;
            var row = Instantiate(template.Window, box.Window, new Int2(0, 0), $"option{n++}");
            row.Visible = true;
            var o = new Option { Param = Math.Max(param, 0), Type = type, Row = row };
            string Href(string el) => (string?)it.Element(el)?.Attribute("href") ?? "";
            if (type != 3)
            {
                o.Check = Child(row, "CheckBox");
                o.Check?.SetTooltip(Text(Href("CheckBoxTooltipFileRef")));
                Child(row, type == 1 ? "ComboAndView" : "CheckBoxView")?.SetTooltip(Text(Href("MainTooltipFileRef")));
                Child(row, "NameView")?.SetValue("name", Text(Href("NameFileRef")));
                if (o.Check != null)
                {
                    var opt = o;
                    o.Check.Clicked += _ => CheckClicked(opt);
                }
            }
            if (type == 1)
            {
                o.Combo = Child(row, "Combo") as UiComboBox;
                o.Items = it.Element("Items")?.Elements("Item").Select(i => Text((string?)i.Element("TextFileRef")?.Attribute("href"))).ToList() ?? new();
                o.Combo?.SetItems(o.Items, o.Items.Count > 0 ? 0 : -1);
                if (o.Combo != null) o.Combo.SelectionChanged += _ => { _generated = null; Update(); };
            }
            box.AddItem(row);
            _options.Add(o);
        }
    }

    UiNode? Child(UiNode row, string name)
    {
        var w = row.Window.Descendants().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        return w == null ? null : NodeOf(w);
    }

    Option? Get(int param) => _options.FirstOrDefault(o => o.Param == param);

    /// <summary>
    /// change_option_check (0x007995b0): the custom-template row, unchecked, first asks T_USE_CUSTOM_TEMPLATE_HEADER /
    /// _QUESTION (0x00679150); any other row toggles its check (vf 0x24). Then 0x00799500.
    /// </summary>
    void CheckClicked(Option o)
    {
        if (o.Check == null) return;
        // Our button may already have changed its own state on the click; the exe's handler sets it from the old one.
        bool wasChecked = o.Check.Window.AutoChangeState ? !o.Checked : o.Checked;
        if (o.Param == Custom && !wasChecked)
        {
            o.Check.SetState(0);
            string head = UiGameRoot.RelatedTextAt(TextsPath, "T_USE_CUSTOM_TEMPLATE_HEADER") ?? "";
            string q = UiGameRoot.RelatedTextAt(TextsPath, "T_USE_CUSTOM_TEMPLATE_QUESTION") ?? "";
            // TODO(exe): what 0x00679150 (1, 1, header, question) answers is not traced; ours checks the row on Yes.
            H5Reborn.Adventure.AdvDialogs.YesNo(this, head, q, null, yes => { if (yes) { o.Check.SetState(1); Changed(); } });
            return;
        }
        o.Check.SetState(wasChecked ? 0 : 1);
        Changed();
    }

    void Changed() { _generated = null; Update(); }

    /// <summary>
    /// 0x00799500: the state is read back (0x00797520: the race rows are enabled only with a fixed number of players,
    /// True Random Towns off and the player index below that number, and unchecked when disabled); with the
    /// custom-template row checked its combo gets the templates 0x0090c880 lists for the options (0x00798160,
    /// combo vf 0x30 0x0079e900: same names keep the selection, else item 0); then every row's vf 0x2c.
    /// </summary>
    void Update()
    {
        var players = Get(Players);
        bool randomTowns = Get(RandomTowns)?.Checked == true;
        for (int i = 0; i < 8; i++)
            if (Get(Race1 + i) is { } race)
            {
                // vf 0x34 (0x0079dd20) enables the check box and the combo; a disabled row is unchecked (vf 0x24(0)).
                race.Enabled = players != null && players.Checked && !randomTowns && i < players.Value + 2;
                if (!race.Enabled) race.Check?.SetState(0);
                if (race.Check != null) race.Check.Enabled = race.Enabled;
            }
        if (Get(Custom) is { Checked: true } custom && custom.Combo != null)
        {
            var o = InitialParams();
            var list = AdvNetGame.Maps.Rmg.Templates.Values
                .Where(t => t.ListedFor(o.RandomSize ? null : o.Size, o.RandomUnderground ? null : o.Underground, o.WaterAmount != 0, o.RandomPlayers ? null : o.Players))
                .Select(t => t.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (!list.SequenceEqual(_templates))
            {
                _templates = list;
                custom.Combo.SetItems(list, list.Count > 0 ? 0 : -1);
            }
        }
        foreach (var o in _options)
            if (o.Combo != null) o.Combo.Enabled = o.Checked && o.Enabled;    // 0x0079dcf0
    }

    /// <summary>
    /// 0x00799230: the screen state as the generator's InitialParams. Unchecked rows are random (size, underground,
    /// players, water, monsters); a value outside the list falls back to 1 (size, monsters), 0 (water: item 1 is 2),
    /// 2 (resources, experience); players = item + 2; PlayersInfo only with fixed players and True Random Towns off,
    /// race = the row's item + 2 whether or not it is checked (3..10, else 1 random).
    /// </summary>
    RmgOptions InitialParams()
    {
        int V(int p, int lo, int hi, int fallback) { int v = Get(p)?.Value ?? fallback; return v < lo || v > hi ? fallback : v; }
        bool Fixed(int p) => Get(p)?.Checked == true;
        int water = Get(Water)?.Value ?? 0;
        water = water == 1 ? 2 : water is < 0 or > 2 ? 0 : water;
        var o = new RmgOptions
        {
            Size = V(MapSize, 0, 6, 1), RandomSize = !Fixed(MapSize),
            Underground = (Get(Levels)?.Value ?? 0) > 0, RandomUnderground = !Fixed(Levels),
            Players = (Get(Players)?.Value ?? 0) + 2, RandomPlayers = !Fixed(Players),
            WaterAmount = water, RandomWater = !Fixed(Water),
            MonsterLevel = V(Monsters, 0, 4, 1), RandomMonsterLevel = !Fixed(Monsters),
            ResourceLevel = V(Resources, 0, 4, 2), ExpLevel = V(Experience, 0, 4, 2),
            Grail = Fixed(Grail), RandomTowns = Fixed(RandomTowns),
        };
        if (!o.RandomPlayers && !o.RandomTowns)
            for (int i = 0; i < o.Players; i++)
            {
                int race = (Get(Race1 + i)?.Value ?? 0) + 2;
                o.PlayerRaces.Add(race is < 3 or > 10 ? 1 : race);
            }
        return o;
    }

    internal void GenerateForTest()
    {
        // Dev/test hook: H5_RMG_CHECKALL=1 checks every row (fixed values) before generating.
        if (System.Environment.GetEnvironmentVariable("H5_RMG_CHECKALL") == "1") CheckAllForTest();
        Generate();
    }

    internal void CheckAllForTest()
    {
        foreach (var o in _options) if (o.Check != null && o.Param != Custom) o.Check.SetState(1);
        Changed();
    }

    /// <summary>
    /// Create (0x007993a0): the template is the custom combo's item when that row is checked, else a random one of
    /// those 0x0090c880 lists for the options (FUN_00b25910() % n); the seed is time(); EP_GENERATE_MINIMAP decides
    /// whether the minimap is made (InitialParams +0x94 = not checked).
    /// </summary>
    void Generate()
    {
        var o = InitialParams();
        var listed = AdvNetGame.Maps.Rmg.Templates.Values
            .Where(t => t.ListedFor(o.RandomSize ? null : o.Size, o.RandomUnderground ? null : o.Underground, o.WaterAmount != 0, o.RandomPlayers ? null : o.Players))
            .Select(t => t.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var custom = Get(Custom);
        o.Template = custom is { Checked: true, Combo: { Selected: >= 0 } c } && c.Selected < _templates.Count ? _templates[c.Selected]
            : listed.Count > 0 ? listed[(int)(GD.Randi() % (uint)listed.Count)] : "";
        if (o.Template.Length == 0)
        {
            UiNavigator.ShowOverlay(new MessageNotice(AdvNetScreens.Text("UI/RMGScreen/NoTemplatesText.txt")));
            return;
        }
        o.Seed = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & int.MaxValue) | 1;
        bool minimap = Get(Minimap)?.Checked == true;
        try
        {
            var res = new RmgGenerator(AdvNetGame.Maps.Rmg, o).Generate();
            var map = res.ToAdvMap(new H5.Adventure.Map.SharedCatalog(new XdbLoader(Game.Instance.Fs)));
            _minimap.Texture = minimap ? MinimapImage(map, 0) : null;
            _generated = RmgSettings.From(o);
        }
        catch (Exception e) { UiNavigator.ShowOverlay(new MessageNotice(e.Message)); }
    }

    /// <summary>Top-down terrain/blocking preview of a floor. TODO(exe): the exe's minimap object 0x0079d920 is not ported.</summary>
    static ImageTexture? MinimapImage(H5.Adventure.Map.AdvMap map, int floor)
    {
        var f = map.Floor(floor);
        if (f == null) return null;
        var img = Image.CreateEmpty(f.Width, f.Height, false, Image.Format.Rgb8);
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
            {
                var c = f.At(x, y).ToString() switch
                {
                    "Water" => new Color(0.15f, 0.3f, 0.55f),
                    "Road" => new Color(0.75f, 0.68f, 0.5f),
                    "Sand" => new Color(0.8f, 0.72f, 0.45f),
                    "Snow" => new Color(0.9f, 0.92f, 0.95f),
                    "Lava" => new Color(0.4f, 0.15f, 0.1f),
                    "Subterranean" => new Color(0.35f, 0.3f, 0.28f),
                    _ => new Color(0.3f, 0.5f, 0.25f),
                };
                if (f.IsBlocked(x, y)) c = c.Darkened(0.55f);
                img.SetPixel(x, y, c);
            }
        foreach (var o in map.Objects.Where(o => o.Pos.Floor == floor && o.Kind == H5.Adventure.Map.ObjectKind.Town))
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (f.InBounds(o.Pos.X + dx, o.Pos.Y + dy)) img.SetPixel(o.Pos.X + dx, o.Pos.Y + dy, Colors.White);
        return ImageTexture.CreateFromImage(img);
    }

    void Close()
    {
        UiNavigator.CloseOverlay(this);
        if (_generated != null)
        {
            var name = (Find("MapNameEdit") as UiEditLine)?.Value ?? "RMG Map";
            _done(_generated, $"{name} ({_generated.Template})");
        }
    }
}
