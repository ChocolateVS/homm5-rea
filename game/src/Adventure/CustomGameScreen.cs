using System.Text.RegularExpressions;
using Godot;
using H5.AdvNet;
using H5.Adventure.Map;
using H5Reborn.Options;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Single Player → Custom Game: the stock UI/CustomGameCreate screen (CCustomGameCreateScreen, H5_Game.exe
/// 00774f80). Lists Maps/SingleMissions, plus Maps/Multiplayer with "Show Multiplayer Maps"
/// (custom_game_show_mp_maps) and generated maps with "Show RMG Maps" (custom_game_show_rmg_maps), in Name /
/// Goal / Players / Map Size columns with the stock sort (00705250: clicking the sort column again flips its
/// direction); Time Limit (1..45 min, else unlimited) and Difficulty bars; Create opens the player setup
/// (UI/CustomGameWait), Load the load screen, Generate Map the random map generator.
/// </summary>
public partial class CustomGameScreen : UiScreen
{
    public sealed record MapEntry(MapCatalog.MapRef? Ref, RmgSettings? Rmg, string Name, string Description, int Size,
        List<int> Teams, string GoalKey, string? CustomGoal, int Folder);

    /// <summary>What Create hands to the player setup screen.</summary>
    public sealed record Choice(MapEntry Map, int TimeLimitMinutes, int Difficulty);
    internal static Choice? Pending;

    readonly List<MapEntry> _all;
    readonly Control _list;
    readonly CustomMapList.SortState _sort = new();
    readonly UiScrollerView _timeBar = new(), _diffBar = new();
    readonly BitmapText _desc = new() { AnchorRight = 1, AnchorBottom = 1, Wrap = true };
    List<MapEntry> _visible = new();
    int _selected, _scroll, _top;
    const int RowHeight = 21;

    static bool ShowMp => OptionsStore.GetBool("custom_game_show_mp_maps");
    static bool ShowRmg => OptionsStore.GetBool("custom_game_show_rmg_maps");

    public CustomGameScreen() : base("UI/CustomGameCreate.(WindowScreen).xdb")
    {
        _all = LoadEntries();
        _all.AddRange(GeneratedMaps.All().Select(Generated));
        foreach (var n in new[] { "screenshoot1", "screenshoot2" }) SetVisible(n, false);
        // The selected map's name and description go in the description panel's scroll area (as on UI/MPCreate).
        foreach (var n in new[] { "mapname", "mapdescheader", "mapdesc" }) SetVisible(n, false);
        if (Find("mapinfo/container") is { } descBox) descBox.AddChild(_desc);
        var container = Find("maps/container")!;
        _list = new Control { Name = "Rows", AnchorRight = 1, AnchorBottom = 1, ClipContents = true, MouseFilter = MouseFilterEnum.Stop };
        container.AddChild(_list);
        _list.GuiInput += OnListInput;
        // TimeLimitBar 1..46 starting at 46 (unlimited); DifficultyBar 4 positions at 1 (00774f80).
        Find("TimeLimitBar")?.AddChild(_timeBar);
        _timeBar.Setup(CustomGameOptions.TimeLimitMax - CustomGameOptions.TimeLimitMin + 1, CustomGameOptions.TimeLimitDefault - CustomGameOptions.TimeLimitMin);
        _timeBar.PositionChanged += _ => ShowOptions();
        Find("DifficultyBar")?.AddChild(_diffBar);
        _diffBar.Setup(CustomGameOptions.DifficultyCount, CustomGameOptions.DifficultyDefault);
        _diffBar.PositionChanged += _ => ShowOptions();
        Bus.On("back", () => UiNavigator.Goto("mainmenu"));
        Bus.On("create", Create);
        Bus.On("load", () => UiNavigator.Goto("customload"));
        Bus.On("rmg", () => UiNavigator.ShowOverlay(new AdvNet.RmgDialog(OnGenerated)));
        Bus.On("sortbyname", () => SortBy(0));
        Bus.On("sortbygoal", () => SortBy(1));
        Bus.On("sortbyplayers", () => SortBy(2));
        Bus.On("sortbymapsize", () => SortBy(3));
        // 00774de0 / 00774eb0: each check box flips its profile var and rebuilds the list.
        Bus.On("mp_filter_changed", () => { OptionsStore.Set("custom_game_show_mp_maps", ShowMp ? "0" : "1"); Rebuild(true); });
        Bus.On("rmg_filter_changed", () => { OptionsStore.Set("custom_game_show_rmg_maps", ShowRmg ? "0" : "1"); Rebuild(true); });
        Rebuild(true);
        ShowOptions();
    }

    public override void _Ready()
    {
        base._Ready();
        // Dev/test hooks for screenshots: H5_CUSTOM_FILTERS=mp,rmg ticks the filters; H5_CUSTOM_SORT=N clicks sort column N.
        if (System.Environment.GetEnvironmentVariable("H5_CUSTOM_SORT") is { } sort && int.TryParse(sort, out var col))
            Callable.From(() => SortBy(col)).CallDeferred();
        if (System.Environment.GetEnvironmentVariable("H5_CUSTOM_CREATE") == "1")
            ((SceneTree)Engine.GetMainLoop()).CreateTimer(2).Timeout += Create;
    }

    static MapEntry Generated(GeneratedMaps.Record r) =>
        new(null, r.Settings, r.Name, r.Settings.Template, RmgSizeTiles(r.Settings.Size), Enumerable.Range(0, r.Settings.Players).ToList(),
            "goal3", null, CustomMapList.Generated);

    static int RmgSizeTiles(int s) => new[] { 72, 96, 136, 176, 216, 256, 320 }[Math.Clamp(s, 0, 6)];

    static readonly Regex NameRef = new(@"<NameFileRef href=""([^""]*)""", RegexOptions.Compiled);
    static readonly Regex DescRef = new(@"<DescriptionFileRef href=""([^""]*)""", RegexOptions.Compiled);
    static readonly Regex GoalRef = new(@"<CustomGoal href=""([^""]+)""", RegexOptions.Compiled);
    static readonly Regex TileX = new(@"<TileX>(\d+)</TileX>", RegexOptions.Compiled);
    static readonly Regex Primary = new(@"<Primary>.*?<Kind>(OBJECTIVE_KIND_[A-Z0-9_]+)</Kind>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Descriptor scan of every map (name, description, size, teams, goal); full loading takes ~0.3 s per map.</summary>
    static List<MapEntry> LoadEntries()
    {
        var fs = Game.Instance.Fs;
        var list = new List<MapEntry>();
        foreach (var r in MapCatalog.All(fs, Game.Instance.Install))
        {
            try
            {
                using var files = r.Open(fs);
                var xml = System.Text.Encoding.UTF8.GetString(files.ReadAllBytes(files.MapXdbPath));
                string? Text(Regex re) => re.Match(xml) is { Success: true } m && m.Groups[1].Value.Length > 0 ? files.ReadText(m.Groups[1].Value, files.MapXdbPath)?.Trim() : null;
                int size = TileX.Match(xml) is { Success: true } tx ? int.Parse(tx.Groups[1].Value) : 0;
                var teams = new List<int>();
                var players = System.Xml.Linq.XDocument.Parse(xml).Root?.Element("players")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>();
                foreach (var p in players)
                    if ((string?)p.Element("ActivePlayer") == "true") teams.Add((int?)p.Element("Team") ?? teams.Count);
                var custom = Text(GoalRef);
                var kind = Primary.Match(xml) is { Success: true } pk ? pk.Groups[1].Value : null;
                list.Add(new MapEntry(r, null, Text(NameRef) ?? Path.GetFileName(r.Id), Text(DescRef) ?? "", size, teams,
                    CustomMapList.GoalKey(!string.IsNullOrEmpty(custom), kind), custom, CustomMapList.FolderOf(r.Id, false)));
            }
            catch (Exception e) { GD.PushWarning($"[H5] map list {r.Id}: {e.Message}"); }
        }
        return list;
    }

    /// <summary>Map size names by width (UIGameRoot MapSizeTable: S, M, L, XL).</summary>
    static string SizeName(int tiles) => tiles switch
    {
        <= 72 => "S", <= 96 => "M", <= 136 => "L", <= 176 => "XL", _ => "XXL",
    };

    static string GoalText(MapEntry m) => m.CustomGoal ?? Text($"UI/MPCreate/Maps/Goals/{m.GoalKey}.txt", m.GoalKey);

    static string PlayersText(MapEntry m) =>
        CustomMapList.PlayersText(CustomMapList.TeamSizes(m.Teams), Text("UI/Common/ffa_format.txt", "<value=count> (FFA)"), Text("UI/Common/players_separator.txt", "v"));

    static string Text(string path, string fallback) => UiResources.Instance.Text(path)?.Trim() is { Length: > 0 } t ? t : fallback;

    void SortBy(int column)
    {
        _sort.Click(column);
        Rebuild(false);
    }

    int Compare(MapEntry a, MapEntry b)
    {
        int c = _sort.Column switch
        {
            1 => string.CompareOrdinal(a.GoalKey, b.GoalKey),
            2 => CustomMapList.ComparePlayers(CustomMapList.TeamSizes(a.Teams), CustomMapList.TeamSizes(b.Teams)),
            3 => a.Size.CompareTo(b.Size),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        return _sort.Descending ? -c : c;
    }

    void Rebuild(bool keepSelection)
    {
        var sel = _visible.ElementAtOrDefault(_selected);
        int mask = CustomMapList.Mask(ShowMp, ShowRmg);
        _visible = _all.Where(m => CustomMapList.Shown(mask, m.Folder)).ToList();
        _visible.Sort(Compare);
        _selected = Math.Max(0, sel != null ? _visible.IndexOf(sel) : 0);
        if (Find("MPFilterBtn") is { } mp) mp.SetState(ShowMp ? 1 : 0);
        if (Find("RMGFilterBtn") is { } rmg) rmg.SetState(ShowRmg ? 1 : 0);
        var bars = new[] { "selectionname", "selectiongoal", "selectionplayers", "selectionmapsize" };
        var butts = new[] { "buttname", "buttgoal", "buttplayers", "buttmapsize" };
        for (int i = 0; i < 4; i++)
        {
            SetVisible(bars[i], i == _sort.Column);
            Find(butts[i])?.SetState(_sort.IsDescending(i) ? 1 : 0);
        }
        Redraw();
    }

    void Redraw()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _visible.Count - 1));
        // Rows start below the column header buttons where the list container reaches under them (MMH55 layout).
        _top = Find("buttname") is { } head && Find("maps/container") is { } cont ? Math.Max(0, head.Window.Rect.Y + head.Window.Rect.H - cont.Window.Rect.Y) : 0;
        int rows = Math.Max(1, (int)((_list.Size.Y > 0 ? _list.Size.Y : 264) - _top) / RowHeight);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _visible.Count - rows));
        for (int i = 0; i < rows && _scroll + i < _visible.Count; i++)
        {
            var m = _visible[_scroll + i];
            float y = _top + i * RowHeight;
            if (_scroll + i == _selected)
                _list.AddChild(new ColorRect { Color = new Color(1, 0.85f, 0.4f, 0.18f), Position = new Vector2(0, y), Size = new Vector2(580, RowHeight), MouseFilter = MouseFilterEnum.Ignore });
            // Column offsets from the stock row template UI/MPCreate/Maps/SingleMap: name 0, goal 182, players 337, size 461.
            AddCell(m.Name, 0, y, 156);
            AddCell(GoalText(m), 182, y, 142);
            AddCell(PlayersText(m), 337, y, 112);
            AddCell(SizeName(m.Size), 461, y, 102);
        }
        var sel = _visible.ElementAtOrDefault(_selected);
        _desc.Markup = sel == null ? "" : $"<h3_block>{Ui.Screens.ChatLogView.Escape(sel.Name)}<br><body><left>{Ui.Screens.ChatLogView.Escape(sel.Description)}";
        foreach (var n in FindAll("values"))
        {
            n.SetValue("players", sel != null ? PlayersText(sel) : "");
            n.SetValue("size", sel != null ? SizeName(sel.Size) : "");
            n.SetValue("goal", sel != null ? GoalText(sel) : "");
        }
        // 007747a0: Create is enabled only with a map selected.
        if (Find("CreateBtn") is { } create) create.Enabled = sel != null;
    }

    int TimeLimit => CustomGameOptions.Minutes(_timeBar.Position + CustomGameOptions.TimeLimitMin);

    void ShowOptions()
    {
        // 007747a0: below 46 the header shows the minutes (TimeLimitHeader "<value=time> min."), else T_UNLIMITED.
        if (Find("TimeLimitView") is { } tv)
        {
            if (TimeLimit > 0) { tv.SetText(null); tv.SetValue("time", TimeLimit.ToString()); }
            else tv.SetText(UiResources.Instance.Text("UI/CustomGameCreate/Unlimited.txt"));
        }
        Find("DifficultyView")?.SetValue("difficulty", DifficultyName(_diffBar.Position));
    }

    internal static string DifficultyName(int d)
    {
        var names = new[] { "Easy", "Normal", "Hard", "Heroic" };
        int i = Math.Clamp(d, 0, 3);
        return UiResources.Instance.Text($"GameMechanics/RefTables/UIDifficultyInfo/Difficulty{names[i]}Name.txt")?.Trim() ?? names[i];
    }

    void AddCell(string text, float x, float y, float w)
    {
        _list.AddChild(new BitmapText { Markup = $"<value_value>{text}", Position = new Vector2(x, y + 1), Size = new Vector2(w, RowHeight - 2), Wrap = false });
    }

    void OnListInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                if (mb.Position.Y < _top) break;
                _selected = _scroll + (int)((mb.Position.Y - _top) / RowHeight);
                Redraw();
                if (mb.DoubleClick) Create();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _scroll--; Redraw(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _scroll++; Redraw(); break;
        }
    }

    internal static void StartGeneratedForDev(RmgSettings rmg, string name) => StartGenerated(rmg, name);

    static void StartGenerated(RmgSettings rmg, string name)
    {
        var lm = AdvNet.AdvNetGame.Maps.Load(new AdvNetSettings { Rmg = rmg });
        var human = lm.Map.Players.Where(p => p.Active).OrderByDescending(p => p.CanBeHuman).First().Index;
        GD.Print($"[H5] generated map {name} ({lm.Map.TilesX}x{lm.Map.TilesY})");
        AdventureScreen.Open(AdventureSession.StartLocal(lm, new[] { human }, (ulong)rmg.Seed, 1, rmg));
    }

    /// <summary>The generator's map is kept (the exe writes Maps/RMG/*.h5m) and listed under "Show RMG Maps".</summary>
    void OnGenerated(RmgSettings rmg, string name)
    {
        var entry = Generated(GeneratedMaps.Add(name, rmg));
        _all.Add(entry);
        Rebuild(false);
        int i = _visible.IndexOf(entry);
        if (i >= 0) { _selected = i; Redraw(); }
    }

    void Create()
    {
        var sel = _visible.ElementAtOrDefault(_selected);
        if (sel == null) return;
        Pending = new Choice(sel, TimeLimit, _diffBar.Position);
        UiNavigator.Goto("customwait");
    }
}

/// <summary>
/// Generated maps kept for the custom-game list and for reopening saves: the stock game writes each generated
/// map to Maps/RMG/&lt;name&gt;.h5m; H5Reborn has no .h5m writer, so it keeps the generator options (the generator
/// is deterministic) in user://maps/rmg/*.json.
/// </summary>
public static class GeneratedMaps
{
    public sealed record Record(string Name, RmgSettings Settings);
    const string Dir = "user://maps/rmg";

    public static List<Record> All()
    {
        var list = new List<Record>();
        var dir = ProjectSettings.GlobalizePath(Dir);
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))!.AsObject();
                var s = System.Text.Json.JsonSerializer.Deserialize<RmgSettings>(j["settings"]!.ToJsonString(), Json)!;
                list.Add(new Record((string?)j["name"] ?? Path.GetFileNameWithoutExtension(f), s));
            }
            catch (Exception e) { GD.PushWarning($"[H5] generated map {f}: {e.Message}"); }
        }
        return list;
    }

    static readonly System.Text.Json.JsonSerializerOptions Json = new() { IncludeFields = true };

    public static Record Add(string name, RmgSettings s)
    {
        var dir = ProjectSettings.GlobalizePath(Dir);
        Directory.CreateDirectory(dir);
        var j = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = name,
            ["settings"] = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(s, Json)),
        };
        File.WriteAllText(Path.Combine(dir, $"rmg_{s.Seed}.json"), j.ToJsonString());
        return new Record(name, s);
    }
}

/// <summary>
/// Entry points into the adventure map. Dev args (after `--`): `--adventure=&lt;map&gt;`
/// (catalogue id, short name such as A2M1, or .h5m path), `--adventure-seed=N`,
/// `--adventure-autoplay` (the bot plays the human side too, battles are quick combat),
/// `--adventure-days=N` (autoplay length), `--adventure-quit-when-done`,
/// `--adventure-floor=1` (view the underground), `--adventure-look=x,y` (camera target tile),
/// `--adventure-yaw=deg`, `--adventure-click=x,y` (simulated click), `--adventure-fake-mouse=x,y`,
/// `--adventure-shots=DIR:COUNT:INTERVAL`, `--adventure-test-attack`, `--adventure-verbose`.
/// </summary>
public static partial class AdventureLauncher
{
    static ulong? _seed;
    static bool _quitWhenDone;
    static (string Dir, int Count, double Interval)? _shots;
    static bool _testAttack;
    static Vector2? _click;
    static float? _yaw;
    static bool _go, _endTurn, _meetTest, _spellbook;
    static string? _visitTest, _open;
    static string? _cast;
    static int? _floor;
    static Vector2I? _look;

    public static bool HandleArg(string arg)
    {
        if (arg.StartsWith("--adventure="))
        {
            var map = arg["--adventure=".Length..];
            Callable.From(() => Start(map, _seed ?? 1)).CallDeferred();
            return true;
        }
        if (arg.StartsWith("--adventure-seed=")) { _seed = ulong.Parse(arg["--adventure-seed=".Length..]); return true; }
        if (arg == "--adventure-autoplay") { AdventureScreen.Autoplay = true; return true; }
        if (arg == "--adventure-real-combat") { AdventureScreen.AutoplayRealCombat = true; return true; }
        if (arg == "--adventure-test-attack") { _testAttack = true; return true; }
        if (arg == "--adventure-end-turn") { _endTurn = true; return true; }
        if (arg == "--adventure-meet-test") { _meetTest = true; return true; }
        if (arg.StartsWith("--adventure-visit-test=")) { _visitTest = arg["--adventure-visit-test=".Length..]; AdventureScreen.DevAutoCloseBoxes = true; return true; }
        if (arg.StartsWith("--adventure-sphinx-page=")) { SphinxScreen.DevPage = int.Parse(arg["--adventure-sphinx-page=".Length..]); return true; }
        if (arg.StartsWith("--adventure-open=")) { _open = arg["--adventure-open=".Length..]; return true; }
        if (arg == "--adventure-spellbook") { _spellbook = true; return true; }
        if (arg == "--adventure-rmg-test")
        {
            // Dev: generate a random map with the dialog's defaults and start it.
            // (after the intro movies, once the main menu hosts overlays)
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.CreateTimer(15).Timeout += () =>
            {
                var dlg = UiNavigator.ShowOverlay(new AdvNet.RmgDialog(CustomGameScreen.StartGeneratedForDev));
                dlg.GenerateForTest();
                tree.CreateTimer(2).Timeout += () => dlg.Bus.Send("back"); // closing hands the generated map back
            };
            return true;
        }
        if (arg.StartsWith("--adventure-cast=")) { _cast = arg["--adventure-cast=".Length..]; return true; }
        if (arg == "--adventure-reveal") { AdvWorld.RevealAll = true; return true; }
        if (arg == "--adventure-go") { _go = true; return true; }
        if (arg == "--adventure-verbose") { AdventureScreen.Verbose = true; return true; }
        if (arg.StartsWith("--adventure-hover=")) { AdventureScreen.DevHover = arg["--adventure-hover=".Length..]; return true; }
        if (arg.StartsWith("--adventure-fake-mouse="))
        {
            var p = arg["--adventure-fake-mouse=".Length..].Split(',');
            AdventureScreen.FakeMouse = new Vector2(float.Parse(p[0]), float.Parse(p[1]));
            return true;
        }
        if (arg.StartsWith("--adventure-click="))
        {
            var p = arg["--adventure-click=".Length..].Split(',');
            _click = new Vector2(float.Parse(p[0]), float.Parse(p[1]));
            return true;
        }
        if (arg.StartsWith("--adventure-shots="))
        {
            // --adventure-shots=DIR:COUNT:INTERVAL — a screenshot every INTERVAL seconds (after the map is up).
            var parts = arg["--adventure-shots=".Length..].Split('|');
            var spec = parts[0];
            int c1 = spec.LastIndexOf(':'), c0 = spec.LastIndexOf(':', c1 - 1);
            _shots = (spec[..c0], int.Parse(spec[(c0 + 1)..c1]), double.Parse(spec[(c1 + 1)..], System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        if (arg.StartsWith("--adventure-days=")) { AdventureScreen.AutoplayDays = int.Parse(arg["--adventure-days=".Length..]); return true; }
        if (arg == "--adventure-quit-when-done")
        {
            _quitWhenDone = true;
            AdventureScreen.AutoplayFinished += () => ((SceneTree)Engine.GetMainLoop()).CreateTimer(1.0).Timeout += () => ((SceneTree)Engine.GetMainLoop()).Quit();
            return true;
        }
        if (arg.StartsWith("--adventure-yaw=")) { _yaw = float.Parse(arg["--adventure-yaw=".Length..], System.Globalization.CultureInfo.InvariantCulture); return true; }
        if (arg.StartsWith("--adventure-floor=")) { _floor = int.Parse(arg["--adventure-floor=".Length..]); return true; }
        if (arg.StartsWith("--adventure-look="))
        {
            var p = arg["--adventure-look=".Length..].Split(',');
            _look = new Vector2I(int.Parse(p[0]), int.Parse(p[1]));
            return true;
        }
        if (arg.StartsWith("--adventure-zoom=")) { _zoom = float.Parse(arg["--adventure-zoom=".Length..], System.Globalization.CultureInfo.InvariantCulture); return true; }
        return false;
    }

    static float? _zoom;

    static void ScheduleTests(AdventureScreen screen)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        SceneTreeTimer After(double s) => tree.CreateTimer(s, processAlways: true, ignoreTimeScale: true);
        if (_floor != null || _look != null || _yaw != null)
        {
            if (_floor is int f) screen.World.SetFloor(f);
            if (_look is { } l) screen.World.Camera.CenterOn(new Tile(l.X, l.Y, screen.World.Floor));
            if (_yaw is float y) screen.World.Camera.SetYaw(y);
        }
        if (_zoom is float z) screen.World.Camera.SetDistance(z);
        if (_testAttack) After(0.5).Timeout += screen.TestAttackNearest;
        if (_endTurn) After(0.5).Timeout += screen.EndTurn;
        if (_meetTest) After(0.5).Timeout += screen.MeetTest;
        if (_visitTest is { } visit) After(0.5).Timeout += () => screen.DevVisit(visit);
        // `--adventure-open=objectives|kingdom|heroes|puzzle`: opens that screen (screenshots).
        if (_open is { } open) After(0.5).Timeout += () =>
        {
            if (open == "puzzle") screen.ShowPuzzleMap();
            else screen.ShowMissionBox(open == "objectives" ? "TabObjectives" : "TabKingdom");
            if (open == "heroes") screen.GetNode("MissionBox").Call("ShowHeroesForDev");
        };
        if (_spellbook) After(0.5).Timeout += () => screen.DevSpellbook(null);
        if (_cast is { } cast) After(0.5).Timeout += () => screen.DevSpellbook(cast);
        if (_click is { } click)
        {
            After(0.3).Timeout += () => screen.SimulateClick(click);
            // A second click on the plotted destination sends the hero (`--adventure-go`).
            if (_go) After(1.3).Timeout += () => screen.SimulateClick(click);
        }
        if (_shots is { } shots)
        {
            Directory.CreateDirectory(shots.Dir);
            for (int i = 0; i < shots.Count; i++)
            {
                int n = i;
                After(1.0 + shots.Interval * i).Timeout += () =>
                {
                    var path = Path.Combine(shots.Dir, $"shot_{n:00}.png");
                    tree.Root.GetViewport().GetTexture().GetImage().SavePng(path);
                    GD.Print($"[H5] adventure shot {path}");
                    if (n == shots.Count - 1 && _quitWhenDone) tree.CreateTimer(0.5).Timeout += () => tree.Quit();
                };
            }
        }
    }

    /// <summary>Runs an action after the given number of rendered frames.</summary>
    sealed partial class AfterFrames(int frames, Action action) : Node
    {
        public override void _Process(double delta)
        {
            if (--frames > 0) return;
            action();
            QueueFree();
        }
    }

    public static void Start(string map, ulong seed)
    {
        try
        {
            var session = AdventureSession.Start(map, seed);
            var screen = AdventureScreen.Open(session);
            // The first frames after loading take seconds (GPU uploads); start test timers only
            // once rendering has settled so they fire in the intended order.
            screen.AddChild(new AfterFrames(10, () => ScheduleTests(screen)) { Name = "TestScheduler" });
        }
        catch (Exception e)
        {
            GD.PushError($"[H5] could not start adventure map '{map}': {e}");
            if (_quitWhenDone) ((SceneTree)Engine.GetMainLoop()).Quit(1);
        }
    }
}
