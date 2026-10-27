using System.Runtime.CompilerServices;
using Godot;
using H5.AdvNet;
using H5.Adventure.Map;
using H5.Core.Ui;
using H5.Core.Xdb;
using H5Reborn.Profiles;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;

namespace H5Reborn.Adventure;

/// <summary>Registers the custom-game screens (the main menu's CustomGame button goes to "customgame").</summary>
static class CustomGameScreens
{
    [ModuleInitializer]
    internal static void Init()
    {
        UiNavigator.Register("customgame", () => new CustomGameScreen());
        UiNavigator.Register("customwait", () => new CustomGameWaitScreen());
        // 00774a60: Load opens the load screen; leaving it returns to the custom game screen.
        UiNavigator.Register("customload", () => new Save.SaveLoadScreen(Save.SaveLoadScreen.Mode.Load, null, null, () => UiNavigator.Goto("customgame")));
    }
}

/// <summary>
/// UI/CustomGameWait ("Create Custom Game", CCustomGameWaitScreen 00777260): one stock MPWait player row per
/// active map player with the state, colour, faction, starting hero and starting bonus arrows, driven by
/// <see cref="CustomGameSetup"/>; the map description / preferences panel; Start launches the game when the
/// players form at least two teams (00776360), Back returns to the map list.
/// </summary>
public partial class CustomGameWaitScreen : UiScreen
{
    readonly CustomGameScreen.Choice? _choice;
    readonly CustomGameSetup? _setup;
    readonly AdvMap? _map;
    readonly MapFiles? _files;
    readonly H5.AdvNet.LoadedMap? _generated;
    readonly List<UiNode> _rows = new();

    static string Profile => ProfileManager.CurrentName;

    public CustomGameWaitScreen() : base("UI/CustomGameWait.(WindowScreen).xdb")
    {
        _choice = CustomGameScreen.Pending;
        Bus.On("back", () => UiNavigator.Goto("customgame"));
        Bus.On("start", Start);
        // 00711e70 / 00711e80 -> 00711d90(1 / 0).
        Bus.On("switchtopref", () => ShowPreferences(true));
        Bus.On("switchtodesc", () => ShowPreferences(false));
        // 007769d0 → 00722140: the map preview slideshow over screenshoot1 / screenshoot2 (3000 ms hold, 3000 ms fade).
        var slides = new ThumbnailSlideshow(Find("screenshoot1"), Find("screenshoot2"));
        AddChild(slides);
        if (_choice == null) return;
        try
        {
            var m = _choice.Map;
            if (m.Rmg != null)
            {
                _generated = AdvNet.AdvNetGame.Maps.Load(new AdvNetSettings { Rmg = m.Rmg });
                _map = _generated.Map;
            }
            else
            {
                _files = m.Ref!.Open(Game.Instance.Fs);
                _map = new MapLoader(Game.Instance.Db, Game.Instance.Fs).Load(_files);
            }
            // 007746c0: single missions (type 2) and generated maps (type 4) lock every row but the first.
            bool lockOthers = m.Folder is CustomMapList.SingleMissions or CustomMapList.Generated;
            _setup = new CustomGameSetup(_map, AdventureSession.SharedDb, Profile, lockOthers);
        }
        catch (Exception e) { GD.PushError($"[H5] custom game map: {e}"); return; }
        slides.SetImages(_choice.Map.Rmg != null ? GeneratedMaps.Thumbnails(_choice.Map.Rmg) : MapThumbnails.FromMap(_files!));
        FillDescription();
        BuildRows();
    }

    public override void _Ready()
    {
        base._Ready();
        // Dev/test hook for screenshots: H5_CUSTOM_WAIT_CLICKS="row:button,..." presses row buttons.
        if (System.Environment.GetEnvironmentVariable("H5_CUSTOM_WAIT_CLICKS") is { Length: > 0 } clicks && _setup != null)
            Callable.From(() =>
            {
                foreach (var c in clicks.Split(','))
                {
                    var p = c.Split(':');
                    if (p.Length == 2 && int.TryParse(p[0], out var row)) Press(row, p[1]);
                }
            }).CallDeferred();
        if (System.Environment.GetEnvironmentVariable("H5_CUSTOM_START") == "1")
            ((SceneTree)Engine.GetMainLoop()).CreateTimer(2).Timeout += Start;
    }

    /// <summary>
    /// The map description / preferences panel (MPWait/MapDescription "definition", 007132d0 → 00712470 / 00713180 →
    /// 007129e0): items from UIGameRoot templates added to the panel's two scrollable containers, then 00711d90(1).
    /// </summary>
    void FillDescription()
    {
        var m = _choice!.Map;
        string name = ChatLogView.Escape(m.Name);
        // 007128f0: the description container is cleared (slot 0x2c), then the map name item (00712800,
        // map_name_definition_item) and ID_MAP_DESCRIPTION_TEXT with the description are added (slot 8).
        if (Find("container") is UiScrollableContainer desc)
        {
            desc.ClearItems();
            AddTextItem(desc, "map_name_definition_item", name);
            AddTextItem(desc, "ID_MAP_DESCRIPTION_TEXT", ChatLogView.Escape(m.Description));
        }
        // 007129e0: the preferences container gets the map name item, then one game_definition_item per row
        // (007121b0: name = the panel's related text, value, addition = related text or empty): players (007123c0,
        // 00707330 text), goal, size (007f6410: UIGameRoot map_sizes "size%d"), difficulty (00a33740), time limit.
        if (Find("preferences_container") is UiScrollableContainer prefs)
        {
            AddTextItem(prefs, "map_name_definition_item", name);
            AddPreference(prefs, "players", CustomGameScreen.PlayersText(m), null);
            AddPreference(prefs, "goal", CustomGameScreen.GoalText(m), null);
            AddPreference(prefs, "size", MapSizeText(m.Size), null);
            AddPreference(prefs, "difficulty", CustomGameScreen.DifficultyName(_choice.Difficulty), null);
            // 007122b0 with no limit: the value is the related text no_limit; else minutes + related "measurement".
            if (_choice.TimeLimitMinutes < 1) AddPreference(prefs, "time_limit", Related("no_limit"), null);
            else AddPreference(prefs, "time_limit", _choice.TimeLimitMinutes.ToString(), "measurement");
        }
        // 007129e0: gamename (+0x24) "value" = the map name.
        Find("gamename")?.SetValue("value", name);
        ShowPreferences(true); // 00713180 ends with 00711d90(1)
    }

    string Related(string key) =>
        Find("definition") is { } panel && panel.Window.RelatedTexts.TryGetValue(key, out var path)
            ? (UiResources.Instance.Text(path) ?? "").Trim() : "";

    void AddTextItem(UiScrollableContainer box, string id, string value)
    {
        if (CreateRootWindow(id, box.Window) is not { } item) return;
        item.SetValue("value", value);
        box.AddItem(item);
    }

    /// <summary>00711e90: a game_definition_item with its name view's "value" and the value view's "value" / "addition".</summary>
    void AddPreference(UiScrollableContainer box, string nameKey, string value, string? additionKey)
    {
        if (CreateRootWindow("game_definition_item", box.Window) is not { } item) return;
        UiNode? Part(string n) => item.Window.Descendants().FirstOrDefault(d => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase)) is { } w ? NodeOf(w) : null;
        Part("name")?.SetValue("value", Related(nameKey));
        if (Part("value") is { } v)
        {
            v.SetValue("value", ChatLogView.Escape(value));
            v.SetValue("addition", additionKey == null ? "" : Related(additionKey));
        }
        box.AddItem(item);
    }

    /// <summary>
    /// 007f6410: UIGameRoot related texts map_sizes, "size%d". The index is taken as the MapSizeTable band of the map
    /// width (first MaxSize ≥ width); the caller's source of the index is untraced.
    /// </summary>
    static string MapSizeText(int tiles)
    {
        const string root = "UI/UIGameRoot.(UIGameRoot).xdb";
        var r = Game.Instance.Xdb.Load(root).Root;
        var bands = r?.Element("MapSizeTable")?.Element("Items")?.Elements("Item").Select(i => (int?)i.Element("MaxSize") ?? 0).ToList() ?? new();
        int index = bands.FindIndex(b => tiles <= b);
        if (index < 0) index = Math.Max(0, bands.Count - 1);
        var item = r?.Element("relatedTexts")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == "map_sizes");
        var href = XdbRef.Parse((string?)item?.Element("RelatedTexts")?.Attribute("href"), root);
        if (href.IsEmpty || !Game.Instance.Fs.Exists(href.Path)) return "";
        var entry = Game.Instance.Xdb.Load(href.Path).Root?.Element("texts")?.Elements("Item")
            .FirstOrDefault(i => (string?)i.Element("TextName") == $"size{index}");
        var text = XdbRef.Parse((string?)entry?.Element("text")?.Attribute("href"), href.Path);
        return text.IsEmpty ? "" : (UiResources.Instance.Text(text.Path) ?? "").Trim();
    }

    /// <summary>
    /// 00711d90: buttonslayer takes its related texture texture0 (preferences) / texture1 (description); buttontopref
    /// is enabled while the description shows, buttontodesc while the preferences show; container /
    /// preferences_container visible accordingly.
    /// </summary>
    void ShowPreferences(bool show)
    {
        if (Find("buttonslayer") is { } layer && layer.Window.RelatedTextures.TryGetValue(show ? "texture0" : "texture1", out var tex)
            && layer.Window.Background is { } bg)
        {
            bg.DdsPath = bg.TexturePath = tex;
            layer.QueueRedraw();
        }
        if (Find("buttontopref") is { } toPref) toPref.Enabled = !show;
        if (Find("buttontodesc") is { } toDesc) toDesc.Enabled = show;
        SetVisible("container", !show);
        SetVisible("preferences_container", show);
    }

    static readonly (string Button, CustomGameSetup.Change Kind, bool Right)[] Buttons =
    {
        ("open_close_left", CustomGameSetup.Change.State, false), ("open_close_right", CustomGameSetup.Change.State, true),
        ("team_color_left", CustomGameSetup.Change.Colour, false), ("team_color_right", CustomGameSetup.Change.Colour, true),
        ("race_change_left", CustomGameSetup.Change.Race, false), ("race_change_right", CustomGameSetup.Change.Race, true),
        ("hero_change_left", CustomGameSetup.Change.Hero, false), ("hero_change_right", CustomGameSetup.Change.Hero, true),
        ("bonus_change_left", CustomGameSetup.Change.Bonus, false), ("bonus_change_right", CustomGameSetup.Change.Bonus, true),
    };

    void Press(int row, string button)
    {
        if (_setup == null) return;
        var b = Buttons.FirstOrDefault(x => x.Button == button);
        if (b.Button == null) return;
        _setup.Apply(row, b.Kind, b.Right, Profile);
        BuildRows();
    }

    void BuildRows()
    {
        // 007769d0: one ListItem clone per player row, shown and added to playerscontainer (slot 8), scrolled by
        // its bar. Our rows are rebuilt after each change; the scroll position is kept.
        if (_setup == null || Find("ListItem") is not { } template || Find("playerscontainer") is not UiScrollableContainer box) return;
        float pos = box.ScrollPosition;
        foreach (var r in _rows) RemoveInstance(r);
        _rows.Clear();
        box.ClearItems();
        for (int i = 0; i < _setup.Slots.Count; i++)
        {
            var node = Instantiate(template.Window, box.Window, new Int2(0, 0), $"row{i}");
            _rows.Add(node);
            FillRow(node, i);
            box.AddItem(node);
        }
        box.RestorePosition(pos);
        // 00776360 starts only with two teams; the Start button follows that check.
        foreach (var n in new[] { "start", "StartBtn" }) if (Find(n) is { } start) start.Enabled = _setup.CanStart();
    }

    UiNode? Child(UiNode row, string name)
    {
        var w = row.Window.Descendants().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        return w == null ? null : NodeOf(w);
    }

    void FillRow(UiNode node, int row)
    {
        var s = _setup!.Slots[row];
        foreach (var (button, kind, right) in Buttons)
            if (Child(node, button) is { } b)
            {
                b.Enabled = _setup.CanChange(row, kind);
                b.Clicked += _ => Press(row, button);
            }
        string colour = s.Colour.Replace("PCOLOR_", "").ToLowerInvariant();
        string colourName = UiResources.Instance.Text($"UI/MPWait/PlayersList/Item/player_{colour}.txt")?.Trim() ?? colour;
        // 00778000: human = profile name, computer = the row's colour name, closed = empty.
        string name = s.State switch
        {
            SetupSlot.SlotState.Human => s.Name,
            SetupSlot.SlotState.Closed => "",
            _ => colourName,
        };
        Child(node, "name")?.SetValue("value", ChatLogView.Escape(name));
        Child(node, "teamnumber")?.SetValue("value", (s.Team + 1).ToString());
        Child(node, "bonus")?.SetValue("value", "");
        Child(node, "race")?.SetValue("value", "");
        Child(node, "state")?.SetValue("value", "");
        // Icons from the row's related textures (States/TeamColors/Races/Heroes/Bonuses).
        Icon(Child(node, "state"), s.State switch { SetupSlot.SlotState.Human => "state3", SetupSlot.SlotState.Closed => "state1", _ => "state2" });
        Icon(Child(node, "team"), "team_" + (colour == "brown" ? "tan" : colour));
        Icon(Child(node, "race"), RaceKey(s.Race));
        Icon(Child(node, "bonus"), $"bonus{s.Bonus}");
        if (Child(node, "heroicon") is { } hero)
        {
            if (s.Hero != null && Face(s.Hero) is { } face) SetIcon(hero, face);
            else Icon(hero, "random");
        }
        Child(node, "race")?.SetTooltip(RaceTooltip(s.Race));
        Child(node, "bonus")?.SetTooltip(BonusTooltip(s.Bonus));
        if (s.Hero != null && Game.Instance.Db.Heroes.TryGetValue(s.Hero, out var def))
            Child(node, "heroicon")?.SetTooltip((UiResources.Instance.Text("UI/MPWait/PlayersList/Item/HeroToolTipFormat.txt") ?? "")
                .Replace("<value=heroname>", def.Name).Replace("<value=herospecialisation>", def.SpecializationName));
    }

    static readonly Dictionary<string, (string Tex, string Tip)> Races = new()
    {
        ["TOWN_HEAVEN"] = ("race_haven", "haven"), ["TOWN_PRESERVE"] = ("race_rampart", "preserve"),
        ["TOWN_ACADEMY"] = ("race_academy", "academy"), ["TOWN_DUNGEON"] = ("race_dungeon", "dungeon"),
        ["TOWN_NECROMANCY"] = ("rece_necropolis", "necromancy"), ["TOWN_INFERNO"] = ("race_inferno", "inferno"),
        ["TOWN_FORTRESS"] = ("rece_fortress", "fortress"), ["TOWN_STRONGHOLD"] = ("race_stronghold", "stronghold"),
    };

    static string RaceKey(string? race) => race != null && Races.TryGetValue(race, out var r) ? r.Tex : "race_random";

    static string RaceTooltip(string? race)
    {
        const string dir = "UI/MPWait/PlayersList/Item/";
        var name = UiResources.Instance.Text(dir + $"race_tooltip_{(race != null && Races.TryGetValue(race, out var r) ? r.Tip : "random")}.txt")?.Trim() ?? "";
        return (UiResources.Instance.Text(dir + "race_tooltip_format.txt") ?? "<value=race>").Replace("<value=race>", name);
    }

    static string BonusTooltip(int bonus) =>
        UiResources.Instance.Text("UI/MPWait/PlayersList/Item/" + bonus switch
        {
            1 => "bonus_tooltip_random_minor_artifact.txt", 2 => "bonus_tooltip_gold.txt", 3 => "bonus_tooltip_resource.txt", _ => "bonus_tooltip_random.txt",
        })?.Trim() ?? "";

    void Icon(UiNode? node, string key)
    {
        if (node == null || !node.Window.RelatedTextures.TryGetValue(key, out var path)) return;
        if (UiResources.Instance.Texture(path) is { } t) SetIcon(node, t);
    }

    static void SetIcon(UiNode node, Texture2D t)
    {
        node.AddChild(new TextureRect
        {
            Texture = t, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        });
    }

    static Texture2D? Face(string heroId)
    {
        if (!Game.Instance.Db.Heroes.TryGetValue(heroId, out var def) || !Game.Instance.Fs.Exists(def.SourcePath)) return null;
        var root = Game.Instance.Xdb.Load(def.SourcePath).Root!;
        var href = (string?)root.Element("FaceTexture")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, def.SourcePath).Path);
    }

    void Start()
    {
        // 00776360: nothing happens until the players form at least two teams (model vf 0x4c, 00c9d940).
        if (_setup == null || _map == null || _choice == null || !_setup.CanStart()) return;
        var setup = _setup.ToGameSetup((ulong)Time.GetTicksUsec(), _choice.Difficulty);
        var db = AdventureSession.SharedDb;
        var (game, opening) = H5.Adventure.Engine.Adventure.NewGame(db, _map, setup);
        GD.Print($"[H5] custom game {_map.Id}: humans {string.Join(",", setup.Players.Where(p => p.Human).Select(p => p.MapPlayer + 1))}, difficulty {setup.Difficulty}, time limit {_choice.TimeLimitMinutes}");
        AdventureScreen.Open(AdventureSession.FromNewGame(_files ?? _generated?.Files ?? MapFiles.FromPak(Game.Instance.Fs, "Maps/Generated/map.xdb"), game, opening, setup.Seed, _choice.Map.Rmg, _choice.TimeLimitMinutes));
    }
}
