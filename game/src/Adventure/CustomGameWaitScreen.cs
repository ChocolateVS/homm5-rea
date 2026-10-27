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
    readonly BitmapText _desc = new() { AnchorRight = 1, AnchorBottom = 1, Wrap = true };
    readonly BitmapText _prefs = new() { AnchorRight = 1, AnchorBottom = 1, Wrap = true };
    int _scroll;
    const int RowHeight = 107; // UI/CustomGameWait/Item (ListItem) height

    static string Profile => ProfileManager.CurrentName;

    public CustomGameWaitScreen() : base("UI/CustomGameWait.(WindowScreen).xdb")
    {
        _choice = CustomGameScreen.Pending;
        Bus.On("back", () => UiNavigator.Goto("customgame"));
        Bus.On("start", Start);
        Bus.On("switchtopref", () => { SetVisible("container", false); SetVisible("preferences_container", true); });
        Bus.On("switchtodesc", () => { SetVisible("container", true); SetVisible("preferences_container", false); });
        foreach (var n in new[] { "screenshoot1", "screenshoot2" }) SetVisible(n, false);
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
        Find("container")?.AddChild(_desc);
        Find("preferences_container")?.AddChild(_prefs);
        SetVisible("preferences_container", false);
        _desc.Markup = $"<h3_block>{ChatLogView.Escape(_choice.Map.Name)}<br><body><left>{ChatLogView.Escape(_choice.Map.Description)}";
        _prefs.Markup = (UiResources.Instance.Text("UI/CustomGameWait/Preferences.txt") ?? "")
            .Replace("<value=players>", _setup.Slots.Count.ToString())
            .Replace("<value=size>", $"{_map.TilesX}x{_map.TilesY}")
            .Replace("<value=goal>", _choice.Map.CustomGoal ?? UiResources.Instance.Text($"UI/MPCreate/Maps/Goals/{_choice.Map.GoalKey}.txt")?.Trim() ?? "")
            .Replace("<value=difficulty>", CustomGameScreen.DifficultyName(_choice.Difficulty));
        if (Find("playerscontainer") is { } box)
        {
            box.ClipContents = true;
            box.MouseFilter = MouseFilterEnum.Pass;
            box.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }) { _scroll--; BuildRows(); }
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }) { _scroll++; BuildRows(); }
            };
        }
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
        if (_setup == null || Find("ListItem") is not { } template || Find("playerscontainer") is not { } box) return;
        foreach (var r in _rows) RemoveInstance(r);
        _rows.Clear();
        int visible = Math.Max(1, (int)(box.Window.Rect.H / RowHeight));
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _setup.Slots.Count - visible));
        for (int i = _scroll; i < _setup.Slots.Count && i < _scroll + visible + 1; i++)
        {
            int row = i;
            var node = Instantiate(template.Window, box.Window, new Int2(0, (i - _scroll) * RowHeight), $"row{i}");
            _rows.Add(node);
            FillRow(node, row);
        }
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
