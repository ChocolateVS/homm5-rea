using System.Text.RegularExpressions;
using Godot;
using H5.Adventure.Map;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Single Player → Custom Game: the stock UI/CustomGameCreate screen listing every
/// playable map (stock archives and user .h5m maps) in its Name / Goal / Players /
/// Map Size columns, with the selected map's name and description; Create starts it.
/// Rows are drawn with the original fonts over the screen's scrollable container.
/// </summary>
public partial class CustomGameScreen : UiScreen
{
    public sealed record MapEntry(MapCatalog.MapRef Ref, string Name, string Description, int Size, int Players, bool Multiplayer);

    readonly List<MapEntry> _maps;
    readonly Control _list;
    int _selected, _scroll;
    bool _showMp = true;
    const int RowHeight = 21;

    public CustomGameScreen() : base("UI/CustomGameCreate.(WindowScreen).xdb")
    {
        _maps = LoadEntries();
        foreach (var n in new[] { "selectionname", "selectionplayers", "selectiongoal", "selectionmapsize", "RMGFilterBtn", "RMGFilterView", "screenshoot1", "screenshoot2", "LoadBtn" })
            SetVisible(n, false);
        var container = Find("maps/container")!;
        _list = new Control { Name = "Rows", AnchorRight = 1, AnchorBottom = 1, ClipContents = true, MouseFilter = MouseFilterEnum.Stop };
        container.AddChild(_list);
        _list.GuiInput += OnListInput;
        Bus.On("back", () => UiNavigator.Goto("mainmenu"));
        Bus.On("create", Create);
        // "Generate Map": the stock random map generator dialog (UI/RMGScreen, game/src/AdvNet); the
        // generated map starts at once with the first playable colour as the human player.
        Bus.On("rmg", () => UiNavigator.ShowOverlay(new AdvNet.RmgDialog(StartGenerated)));
        Bus.On("sortbyname", () => Sort(m => m.Name));
        Bus.On("sortbyplayers", () => Sort(m => m.Players.ToString("00")));
        Bus.On("sortbymapsize", () => Sort(m => m.Size.ToString("000")));
        Bus.On("sortbygoal", () => Sort(m => m.Name));
        Bus.On("mp_filter_changed", () => { _showMp = !_showMp; _scroll = 0; _selected = 0; Rebuild(); });
        Rebuild();
    }

    IEnumerable<MapEntry> VisibleMaps() => _maps.Where(m => _showMp || !m.Multiplayer);

    void Sort(Func<MapEntry, string> key)
    {
        var sel = VisibleMaps().ElementAtOrDefault(_selected);
        var sorted = _maps.OrderBy(key, StringComparer.OrdinalIgnoreCase).ToList();
        _maps.Clear();
        _maps.AddRange(sorted);
        _selected = Math.Max(0, VisibleMaps().ToList().IndexOf(sel!));
        Rebuild();
    }

    static readonly Regex NameRef = new(@"<NameFileRef href=""([^""]*)""", RegexOptions.Compiled);
    static readonly Regex DescRef = new(@"<DescriptionFileRef href=""([^""]*)""", RegexOptions.Compiled);
    static readonly Regex TileX = new(@"<TileX>(\d+)</TileX>", RegexOptions.Compiled);
    static readonly Regex Active = new(@"<ActivePlayer>true</ActivePlayer>", RegexOptions.Compiled);

    /// <summary>Header-only scan of every map descriptor (full loading takes ~0.3 s per map).</summary>
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
                list.Add(new MapEntry(r, Text(NameRef) ?? Path.GetFileName(r.Id), Text(DescRef) ?? "", size, Active.Matches(xml).Count,
                    r.Id.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception e) { GD.PushWarning($"[H5] map list {r.Id}: {e.Message}"); }
        }
        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Map size names by width (UIGameRoot MapSizeTable: S, M, L, XL).</summary>
    static string SizeName(int tiles) => tiles switch
    {
        <= 72 => "S", <= 96 => "M", <= 136 => "L", <= 176 => "XL", _ => "XXL",
    };

    void Rebuild()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        var maps = VisibleMaps().ToList();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, maps.Count - 1));
        int rows = Math.Max(1, (int)(_list.Size.Y > 0 ? _list.Size.Y : 264) / RowHeight);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, maps.Count - rows));
        for (int i = 0; i < rows && _scroll + i < maps.Count; i++)
        {
            var m = maps[_scroll + i];
            float y = i * RowHeight;
            if (_scroll + i == _selected)
                _list.AddChild(new ColorRect { Color = new Color(1, 0.85f, 0.4f, 0.18f), Position = new Vector2(0, y), Size = new Vector2(580, RowHeight), MouseFilter = MouseFilterEnum.Ignore });
            // Column offsets from the stock row template UI/MPCreate/Maps/SingleMap: name 0, goal 182, players 337, size 461.
            AddCell(m.Name, 0, y, 156);
            AddCell("Defeat all", 182, y, 142);
            AddCell(m.Players.ToString(), 337, y, 112);
            AddCell(SizeName(m.Size), 461, y, 102);
        }
        var sel = maps.ElementAtOrDefault(_selected);
        Find("mapinfo/mapname")?.SetValue("value", sel?.Name ?? "");
        Find("mapinfo/mapdesc")?.SetValue("value", sel?.Description ?? "");
        foreach (var n in FindAll("values"))
        {
            n.SetValue("players", sel?.Players.ToString() ?? "");
            n.SetValue("size", sel != null ? SizeName(sel.Size) : "");
        }
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
                _selected = _scroll + (int)(mb.Position.Y / RowHeight);
                Rebuild();
                if (mb.DoubleClick) Create();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _scroll--; Rebuild(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _scroll++; Rebuild(); break;
        }
    }

    internal static void StartGeneratedForDev(H5.AdvNet.RmgSettings rmg, string name) => StartGenerated(rmg, name);

    static void StartGenerated(H5.AdvNet.RmgSettings rmg, string name)
    {
        var lm = AdvNet.AdvNetGame.Maps.Load(new H5.AdvNet.AdvNetSettings { Rmg = rmg });
        var human = lm.Map.Players.Where(p => p.Active).OrderByDescending(p => p.CanBeHuman).First().Index;
        GD.Print($"[H5] generated map {name} ({lm.Map.TilesX}x{lm.Map.TilesY})");
        AdventureScreen.Open(AdventureSession.StartLocal(lm, new[] { human }, (ulong)rmg.Seed, 1));
    }

    void Create()
    {
        var sel = VisibleMaps().ElementAtOrDefault(_selected);
        if (sel == null) return;
        AdventureLauncher.Start(sel.Ref.Id, (ulong)Time.GetTicksUsec());
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
