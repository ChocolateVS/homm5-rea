using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Godot;
using H5.AdvNet;
using H5.Adventure.Map;
using H5.Core.Ui;
using H5.Net;
using H5Reborn.Ui;
using H5Reborn.Ui.Net;
using H5Reborn.Ui.Screens;

namespace H5Reborn.AdvNet;

/// <summary>
/// Registers the standard (adventure-map) multiplayer and hot-seat screens. Runs once when the
/// game assembly loads, so the main menu's stock "standart_hot_seat" command reaches us through
/// the global message bus without the menu knowing about these screens.
/// </summary>
public static class AdvNetScreens
{
    [ModuleInitializer]
    internal static void Init()
    {
        UiNavigator.Register("advcreate", () => new AdvSetupScreen(network: true));
        UiNavigator.Register("advwait", () => new AdvWaitScreen());
        UiNavigator.Register("hotseatcreate", () => new AdvSetupScreen(network: false));
        UiNavigator.Register("hotseatwait", () => new HotSeatWaitScreen());
        UiMessageBus.Global.On("standart_hot_seat", () => UiNavigator.Goto("hotseatcreate"));
    }

    /// <summary>Settings chosen on the create screen, carried to the hot-seat wait screen.</summary>
    internal static AdvNetSettings? PendingHotSeat;

    /// <summary>
    /// Set by the adventure-screen workstream: opens the adventure screen for a networked game
    /// (via a remote AdventureSession, see <see cref="RemoteAdventure"/>). Returns false if not available.
    /// </summary>
    public static Func<AdvNetGame, bool>? OpenNetworkGame;

    /// <summary>Set by the adventure-screen workstream: starts a local hot-seat game for the plan.</summary>
    public static Func<HotSeatPlan, bool>? OpenHotSeatGame;

    internal static string Text(string path) => UiResources.Instance.Text(path) ?? "";
}

/// <summary>One map entry of the create screens (stock, user or a generated random map).</summary>
sealed record MapEntry(string Id, string Name, int Humans, int Size, string Description, RmgSettings? Rmg);

/// <summary>
/// UI/MPCreate ("Create Standard game", network) and UI/HotSeat ("Hot-Seat: Create game"):
/// the map list, the map description, game options and "Generate a Map" (UI/RMGScreen).
/// </summary>
public partial class AdvSetupScreen : UiScreen
{
    readonly bool _network;
    readonly List<MapEntry> _maps = new();
    readonly ListRowsView _list;
    readonly AdvNetSettings _set = new();
    MapEntry? _selected;
    int _sort;
    readonly BitmapText _desc = new() { AnchorRight = 1, AnchorBottom = 1, Wrap = true };

    public AdvSetupScreen(bool network) : base(network ? "UI/MPCreate.(WindowScreen).xdb" : "UI/HotSeat.(WindowScreen).xdb")
    {
        _network = network;
        // Simultaneous turns (room key simultaneus_turns, docs/rules/SIMULTANEOUS.md): off by default.
        if (Find("simultaneus") is { } sim) { sim.SetState(1); sim.Clicked += _ => { Read(); ShowValues(); }; }
        if (Find("loadbutton") is { } load) load.Enabled = false;
        if (Find("load") is { } load2) load2.Enabled = false;

        var maps = Root.Find("maps")!;
        float ox = maps.Rect.X, oy = maps.Rect.Y;
        _list = new ListRowsView(new[]
        {
            new ListRowsView.Column(77 - ox + (network ? 0 : 1), 161, ""),
            new ListRowsView.Column(259 - ox + (network ? 0 : 1), 133, ""),
            new ListRowsView.Column(414 - ox + (network ? 0 : 1), 102, ""),
            new ListRowsView.Column(538 - ox + (network ? 0 : 1), 93, ""),
        }, 0, 0, 52, 20);
        Find("maps")?.AddChild(_list);
        // Map name and description go in the description panel's scroll area.
        foreach (var n in new[] { "mapname", "mapdescheader", "mapdesc" }) SetVisible(n, false);
        if (Find("mapinfo/container") is { } descBox) descBox.AddChild(_desc);
        _list.RowSelected += i => Select(i);
        _list.RowActivated += i => { Select(i); Create(); };

        if (Find("name") is UiEditLine name) name.Value = $"{LobbyService.Instance.PlayerName}'s game";
        if (Find("password") is UiEditLine pw) pw.Password = true;
        foreach (var t in new[] { "ghostmode", "quick_combat", "realtime" })
            if (Find(t) is { } box) { box.SetState(t == "realtime" ? 0 : 1); box.Clicked += _ => Read(); }
        // 0070862c: timelimitscroll 1..46 starting at 46 (above 45 = no limit) and difficultyscroll's 4 positions
        // starting at 1, as on the custom game screen (CustomGameOptions).
        Bar("timelimitscroll", CustomGameOptions.TimeLimitMax - CustomGameOptions.TimeLimitMin + 1, CustomGameOptions.TimeLimitDefault - CustomGameOptions.TimeLimitMin,
            p => _set.TimeLimitMinutes = CustomGameOptions.Minutes(p + CustomGameOptions.TimeLimitMin));
        Bar("difficultyscroll", CustomGameOptions.DifficultyCount, CustomGameOptions.DifficultyDefault, p => _set.Difficulty = p);
        Cycle("animationspeed_text", "animationspeedscroll", () => _set.AnimationSpeed = (_set.AnimationSpeed + 1) % 3);
        Cycle("combatturnspeed_text", "combatturnspeedscroll", () => _set.DynamicBattles = (_set.DynamicBattles + 1) % 4);

        Bus.On("create", Create);
        Bus.On("back", Back);
        Bus.On("sortbyname", () => Sort(0));
        Bus.On("sortbygoal", () => Sort(1));
        Bus.On("sortbyplayers", () => Sort(2));
        Bus.On("sortbymapsize", () => Sort(3));
        Bus.On("0 rmg_screen0 0", () => UiNavigator.ShowOverlay(new RmgDialog(Generated)));
        LoadMaps();
        ShowValues();
    }

    public override void _Ready()
    {
        base._Ready();
        // Dev/test hook: H5_ADVNET_SIMUL=1 ticks Simultaneous Turns (screenshots of the option line).
        if (System.Environment.GetEnvironmentVariable("H5_ADVNET_SIMUL") == "1" && Find("simultaneus") is { } sim)
        { sim.SetState(0); Read(); ShowValues(); }
        // Dev/test hook: H5_ADVNET_RMG=1 opens the random map generator and generates a map.
        if (System.Environment.GetEnvironmentVariable("H5_ADVNET_RMG") == "1")
        {
            var dlg = UiNavigator.ShowOverlay(new RmgDialog(Generated));
            Callable.From(dlg.GenerateForTest).CallDeferred();
        }
    }

    void Bar(string bar, int count, int start, Action<int> changed)
    {
        if (Find(bar) is not { } node) return;
        var view = new H5Reborn.Options.UiScrollerView();
        node.AddChild(view);
        view.Setup(count, start);
        changed(start);
        view.PositionChanged += p => { changed(p); ShowValues(); };
    }

    void Cycle(string text, string bar, Action next)
    {
        foreach (var n in new[] { Find(text), Find(bar) })
            if (n != null) { n.MouseFilter = MouseFilterEnum.Stop; n.Clicked += _ => { next(); ShowValues(); }; }
    }

    void Read()
    {
        _set.GhostMode = Find("ghostmode")?.State == 0;
        _set.QuickCombat = Find("quick_combat")?.State == 0;
        _set.Autosave = Find("realtime")?.State == 0;
        _set.SimultaneousTurns = _network && Find("simultaneus")?.State == 0;
        // The host's exe: H5_Game_PEST.exe on the 5.5 target keeps simultaneous turns on for good (SIMULTANEOUS.md §7).
        _set.Pest = H5.Core.IO.TargetExe.Pest(Game.Instance.Install.Root);
    }

    void ShowValues()
    {
        string limit = _set.TimeLimitMinutes == 0 ? AdvNetScreens.Text("UI/HotSeat/Options/no_limit.txt") : $"{_set.TimeLimitMinutes} {AdvNetScreens.Text("UI/HotSeat/Options/min.txt")}";
        Find("timelimitvalue")?.SetValue("value", limit);
        Find("timelimitvalue")?.SetValue("measurement", "");
        Find("difficultyvalue")?.SetValue("value", H5Reborn.Adventure.CustomGameScreen.DifficultyName(_set.Difficulty));
        Find("animationspeed_text")?.SetValue("speed", new[] { "Slow", "Normal", "Fast" }[_set.AnimationSpeed]);
        Find("combatturnspeed_text")?.SetValue("speed", new[] { "Off", "Slow", "Normal", "Fast" }[_set.DynamicBattles]);
    }

    void LoadMaps()
    {
        _maps.Clear();
        foreach (var r in AdvNetGame.Maps.Catalog())
        {
            // Light descriptor read: name, size and human-capable players from map.xdb.
            try
            {
                using var files = r.Open(Game.Instance.Fs);
                var x = files.LoadXml(files.MapXdbPath).Root!;
                int size = (int?)x.Element("TileX") ?? 0;
                int humans = x.Element("players")?.Elements("Item").Count(p => (string?)p.Element("ActivePlayer") == "true" && (string?)p.Element("CanBeHumanPlayer") == "true") ?? 0;
                var name = files.ReadText((string?)x.Element("NameFileRef")?.Attribute("href"), files.MapXdbPath)?.Trim();
                var desc = files.ReadText((string?)x.Element("DescriptionFileRef")?.Attribute("href"), files.MapXdbPath)?.Trim() ?? "";
                if (_network && humans < 2) continue;
                if (!_network && humans < 2) continue;
                _maps.Add(new MapEntry(r.Id, string.IsNullOrEmpty(name) ? r.Id[(r.Id.LastIndexOfAny(new[] { '/', ':' }) + 1)..] : name, humans, size, desc, null));
            }
            catch (Exception e) { GD.Print($"[advnet] skipping map {r.Id}: {e.Message}"); }
        }
        Sort(_sort);
    }

    void Sort(int column)
    {
        _sort = column;
        var bars = new[] { "selectionname", "selectiongoal", "selectionplayers", "selectionmapsize" };
        for (int i = 0; i < bars.Length; i++) SetVisible(bars[i], i == column);
        _maps.Sort((a, b) => column switch
        {
            2 => a.Humans.CompareTo(b.Humans),
            3 => a.Size.CompareTo(b.Size),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        });
        _list.SetRows(_maps.Select(m => new[]
        {
            $"<body>{ChatLogView.Escape(m.Name)}", "<body>Standard", $"<body>{m.Humans}", $"<body>{SizeName(m.Size)}",
        }).ToList());
    }

    static string SizeName(int tiles) => tiles switch
    {
        <= 72 => "Tiny", <= 96 => "Small", <= 136 => "Medium", <= 176 => "Large", <= 216 => "XL", <= 256 => "Huge", _ => "Impossible",
    };

    void Select(int i)
    {
        if (i < 0 || i >= _maps.Count) return;
        _selected = _maps[i];
        _desc.Markup = $"<h3_block>{ChatLogView.Escape(_selected.Name)}<br><body><left>{ChatLogView.Escape(_selected.Description)}";
        foreach (var v in FindAll("values"))
        {
            v.SetValue("players", _selected.Humans.ToString());
            v.SetValue("size", SizeName(_selected.Size));
            v.SetValue("goal", "Standard");
            v.SetValue("underground", _selected.Rmg?.Underground == true ? "Yes" : "");
        }
    }

    void Generated(RmgSettings rmg, string name)
    {
        var entry = new MapEntry("rmg:" + rmg.Seed, name, rmg.Players, RmgSizeTiles(rmg.Size), $"{rmg.Template}", rmg);
        _maps.Insert(0, entry);
        Sort(_sort);
        Select(_maps.IndexOf(entry));
    }

    static int RmgSizeTiles(int s) => new[] { 72, 96, 136, 176, 216, 256, 320 }[Math.Clamp(s, 0, 6)];

    async void Create()
    {
        if (_selected == null) { UiNavigator.ShowOverlay(new MessageNotice(AdvNetScreens.Text("UI/HotSeat/error_select_map.txt"))); return; }
        Read();
        _set.Map = _selected.Rmg == null ? _selected.Id : null;
        _set.Rmg = _selected.Rmg;
        if (!_network)
        {
            AdvNetScreens.PendingHotSeat = _set;
            UiNavigator.Goto("hotseatwait");
            return;
        }
        var lobby = LobbyService.Instance;
        if (!lobby.Connected)
        {
            try { await lobby.HostAsync(); }
            catch (Exception e) { UiNavigator.ShowOverlay(new MessageNotice(e.Message)); return; }
        }
        var name = (Find("name") as UiEditLine)?.Value ?? "";
        var password = (Find("password") as UiEditLine)?.Value;
        lobby.Send(new CreateRoom(name, "adventure", Math.Max(2, _selected.Humans), string.IsNullOrEmpty(password) ? null : password, _set.ToJson()));
        UiNavigator.Goto("advwait");
    }

    void Back() => UiNavigator.Goto(_network ? "lobby" : "mainmenu");
}

/// <summary>A plain modal notice using the original fonts (for setup errors).</summary>
public partial class MessageNotice : Control
{
    public MessageNotice(string text)
    {
        AnchorRight = 1; AnchorBottom = 1;
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.6f), AnchorRight = 1, AnchorBottom = 1 });
        AddChild(new BitmapText { Markup = $"<h3><center>{ChatLogView.Escape(text)}", AnchorLeft = 0.25f, AnchorRight = 0.75f, AnchorTop = 0.45f, AnchorBottom = 0.55f });
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } or InputEventKey { Pressed: true }) UiNavigator.CloseOverlay(this);
    }
}

/// <summary>
/// UI/MPWait ("Standard options"): players with their colour slot and ready state, map
/// description, chat, Start Game (host) / Ready (guest), Cancel. Clicking your own row picks
/// the next free colour slot of the map.
/// </summary>
public partial class AdvWaitScreen : UiScreen
{
    readonly ChatLogView _log = new();
    readonly ListRowsView _players;
    List<MapPlayer> _slots = new();
    string? _slotsFor;

    public AdvWaitScreen() : base("UI/MPWait.(WindowScreen).xdb")
    {
        var host = Root.Find("playerscontainer")!;
        _players = new ListRowsView(new[]
        {
            new ListRowsView.Column(8, 120, ""), new ListRowsView.Column(130, 280, ""), new ListRowsView.Column(412, 150, ""),
        }, 0, 0, 4, 24);
        Find("playerscontainer")?.AddChild(_players);
        _players.RowActivated += _ => NextSlot();
        _players.RowSelected += i => { if (MeIndex() == i) NextSlot(); };
        Find("log_client")?.AddChild(_log);
        if (Find("editchatmessage") is UiEditLine chat)
            chat.Submitted += text =>
            {
                if (text.Trim().Length == 0) return;
                LobbyService.Instance.Send(new Chat(0, text));
                chat.Value = "";
            };
        Bus.On("start", StartOrReady);
        Bus.On("back", () => { LobbyService.Instance.LeaveRoom(); UiNavigator.Goto("lobby"); });
        Bus.On("switchtopref", () => { SetVisible("container", false); SetVisible("preferences_container", true); });
        Bus.On("switchtodesc", () => { SetVisible("container", true); SetVisible("preferences_container", false); });
    }

    public override void _Ready()
    {
        base._Ready();
        LobbyService.Instance.MessageReceived += OnMessage;
        Refresh();
    }

    public override void _ExitTree() => LobbyService.Instance.MessageReceived -= OnMessage;

    int MeIndex() => LobbyService.Instance.Room?.Players.ToList().FindIndex(p => p.PlayerId == LobbyService.Instance.PlayerId) ?? -1;

    void OnMessage(Message m)
    {
        switch (m)
        {
            case RoomState: Refresh(); break;
            case Chat c:
                var from = LobbyService.Instance.Room?.Players.FirstOrDefault(p => p.PlayerId == c.FromPlayerId)?.Name ?? $"#{c.FromPlayerId}";
                _log.AddChat(from, c.Text);
                H5Reborn.Audio.AudioSystem.PlayNamed("CHAT_GET_MESSAGE_SOUND"); // new chat line (00726630)
                break;
            case ErrorMsg e: _log.AddSystem(e.Text); break;
            case GameStarted:
                if (LobbyService.Instance.Adventure is { } game)
                {
                    if (AdvNetScreens.OpenNetworkGame?.Invoke(game) == true) break;
                    _log.AddSystem($"Game started on {game.Replica.Map.Map.Name}; the adventure screen has no network mode yet.");
                }
                break;
            case H5.Net.GameEnded ge: _log.AddSystem($"Game ended: {ge.Reason}"); break;
        }
    }

    AdvNetSettings Settings => AdvNetSettings.From(LobbyService.Instance.Room?.Settings);

    void LoadSlots()
    {
        var s = Settings;
        var key = s.Map ?? $"rmg:{s.Rmg?.Template}:{s.Rmg?.Seed}";
        if (key == _slotsFor) return;
        _slotsFor = key;
        try
        {
            if (s.Map != null && MapCatalog.Find(Game.Instance.Fs, Game.Instance.Install, s.Map) is { } r)
            {
                using var files = r.Open(Game.Instance.Fs);
                _slots = new MapLoader(Game.Instance.Db, Game.Instance.Fs).Load(files).Players.Where(p => p.Active && p.CanBeHuman).ToList();
            }
            else _slots = Enumerable.Range(0, Math.Max(2, s.Rmg?.Players ?? 2)).Select(i => new MapPlayer { Index = i, Active = true, CanBeHuman = true, Colour = "" }).ToList();
        }
        catch (Exception e) { _slots = new(); _log.AddSystem(e.Message); }
    }

    void Refresh()
    {
        var room = LobbyService.Instance.Room;
        if (room == null) return;
        LoadSlots();
        Find("gamename")?.SetValue("value", ChatLogView.Escape(room.Name));
        _players.SetRows(room.Players.Select(p =>
        {
            int slot = AdvPlayerOptions.From(p.Options).MapPlayer;
            var colour = _slots.FirstOrDefault(s => s.Index == slot)?.Colour.Replace("PCOLOR_", "") ?? "Random";
            string state = !p.Connected ? "Reconnecting" : p.IsHost ? "Host" : p.Ready ? "Ready" : "Not ready";
            return new[] { $"<body>{(string.IsNullOrEmpty(colour) ? $"Player {slot + 1}" : colour)}", $"<body_bright>{ChatLogView.Escape(p.Name)}", $"<body>{state}" };
        }).ToList());
        var me = room.Players.FirstOrDefault(p => p.PlayerId == LobbyService.Instance.PlayerId);
        if (Find("creategamebutton") is { } btn) btn.SetText(me?.IsHost == true ? null : me?.Ready == true ? "Not Ready" : "Ready");
    }

    void NextSlot()
    {
        var room = LobbyService.Instance.Room;
        if (room == null || _slots.Count == 0) return;
        var me = room.Players.First(p => p.PlayerId == LobbyService.Instance.PlayerId);
        var taken = room.Players.Where(p => p != me).Select(p => AdvPlayerOptions.From(p.Options).MapPlayer).ToHashSet();
        int cur = AdvPlayerOptions.From(me.Options).MapPlayer;
        var order = _slots.Select(s => s.Index).ToList();
        int start = Math.Max(0, order.IndexOf(cur) + 1);
        for (int k = 0; k < order.Count; k++)
        {
            int cand = order[(start + k) % order.Count];
            if (taken.Contains(cand)) continue;
            LobbyService.Instance.Send(new SetPlayerOptions(new AdvPlayerOptions { MapPlayer = cand }.ToJson()));
            return;
        }
    }

    void StartOrReady()
    {
        var room = LobbyService.Instance.Room;
        if (room == null) return;
        var me = room.Players.FirstOrDefault(p => p.PlayerId == LobbyService.Instance.PlayerId);
        if (me?.IsHost != true) { LobbyService.Instance.Send(new SetReady(!(me?.Ready ?? false))); return; }
        LobbyService.Instance.Send(new StartGame());
    }
}

/// <summary>Who plays which colour in a hot-seat game (local, several humans on one PC).</summary>
public sealed class HotSeatPlan
{
    public AdvNetSettings Settings { get; init; } = new();
    /// <summary>Map player index → player name for every human; other active players are computers.</summary>
    public Dictionary<int, string> Humans { get; } = new();
}

/// <summary>
/// UI/HotSeatWait ("Hot-Seat options"): each colour of the map as Human (with a name) or
/// Computer; clicking a row toggles it. Start hands the plan to the adventure screen.
/// </summary>
public partial class HotSeatWaitScreen : UiScreen
{
    readonly ListRowsView _rows;
    readonly HotSeatPlan _plan;
    readonly List<MapPlayer> _slots = new();

    public HotSeatWaitScreen() : base("UI/HotSeatWait.(WindowScreen).xdb")
    {
        _plan = new HotSeatPlan { Settings = AdvNetScreens.PendingHotSeat ?? new AdvNetSettings() };
        var list = Root.Find("playerslist")!;
        _rows = new ListRowsView(new[] { new ListRowsView.Column(10, 150, ""), new ListRowsView.Column(165, 250, ""), new ListRowsView.Column(420, 150, "") }, 0, 0, 34, 26);
        Find("playerslist")?.AddChild(_rows);
        _rows.RowSelected += Toggle;
        Bus.On("start", Start);
        Bus.On("back", () => UiNavigator.Goto("hotseatcreate"));
        try
        {
            var s = _plan.Settings;
            if (s.Map != null && MapCatalog.Find(Game.Instance.Fs, Game.Instance.Install, s.Map) is { } r)
            {
                using var files = r.Open(Game.Instance.Fs);
                _slots.AddRange(new MapLoader(Game.Instance.Db, Game.Instance.Fs).Load(files).Players.Where(p => p.Active));
            }
            else
                _slots.AddRange(Enumerable.Range(0, s.Rmg?.Players ?? 2).Select(i => new MapPlayer { Index = i, Active = true, CanBeHuman = true }));
        }
        catch (Exception e) { GD.Print($"[advnet] hot-seat map: {e.Message}"); }
        foreach (var p in _slots.Where(p => p.CanBeHuman).Take(2)) _plan.Humans[p.Index] = $"Player {p.Index + 1}";
        Find("gamename")?.SetValue("value", ChatLogView.Escape(_plan.Settings.Map ?? "Random map"));
        Refresh();
    }

    void Toggle(int i)
    {
        if (i < 0 || i >= _slots.Count || !_slots[i].CanBeHuman) return;
        var idx = _slots[i].Index;
        if (!_plan.Humans.Remove(idx)) _plan.Humans[idx] = $"Player {idx + 1}";
        Refresh();
    }

    void Refresh() => _rows.SetRows(_slots.Select(p => new[]
    {
        $"<body>{(p.Colour.Length > 0 ? p.Colour.Replace("PCOLOR_", "") : $"Player {p.Index + 1}")}",
        $"<body_bright>{(_plan.Humans.TryGetValue(p.Index, out var n) ? ChatLogView.Escape(n) : "Computer")}",
        $"<body>{(_plan.Humans.ContainsKey(p.Index) ? "Human" : "Computer")}",
    }).ToList());

    void Start()
    {
        if (_plan.Humans.Count < 2) { UiNavigator.ShowOverlay(new MessageNotice(AdvNetScreens.Text("UI/HotSeatWait/WrongNameText.txt") is { Length: > 0 } t ? t : "Choose at least two human players.")); return; }
        if (AdvNetScreens.OpenHotSeatGame?.Invoke(_plan) != true)
            UiNavigator.ShowOverlay(new MessageNotice("Hot-seat games need the adventure screen's hot-seat mode (not available yet)."));
    }
}

/// <summary>
/// Hot-seat turn hand-over rules for the adventure screen: the map is shown from the side of
/// the human whose turn it is; between two humans the original covers the screen with its
/// "HotSeatBlackBg" panel (UI/AdventureScreen) and the turn announcement until the next player
/// confirms, so nobody sees the other's view. Computer turns are shown from the last human's side.
/// </summary>
public sealed class HotSeatController(IReadOnlySet<int> humans)
{
    int _viewer = -1;
    public int Viewer => _viewer;
    public bool HandOverPending { get; private set; }

    /// <summary>Call when the current player changes; returns true when the hand-over screen must be shown.</summary>
    public bool OnTurn(int currentPlayer)
    {
        if (!humans.Contains(currentPlayer)) return false;
        if (currentPlayer == _viewer) return false;
        bool cover = _viewer >= 0;
        _viewer = currentPlayer;
        HandOverPending = cover;
        return cover;
    }

    /// <summary>The next player clicked through the hand-over screen.</summary>
    public void Confirm() => HandOverPending = false;
}

/// <summary>
/// Original lobby flow choice: a "Standard" game (adventure map, UI/MPCreate) or a "Duel"
/// (UI/MPDMCreate). Shown by the lobby's Create button.
/// </summary>
public partial class GameTypeDialog : MpdmDialog
{
    public GameTypeDialog()
    {
        Header?.SetText("<h3_header>Create game");
        Label?.SetText("<body_bright>Standard game (adventure map)");
        if (NameEdit != null) NameEdit.Visible = false;
        if (PasswordEdit != null) PasswordEdit.Visible = false;
        Check?.SetState(0);
        CreateButton?.SetText("OK");
    }

    protected override void Confirm()
    {
        bool standard = Check?.State == 0;
        Close();
        if (standard) UiNavigator.Goto("advcreate");
        else UiNavigator.ShowOverlay(new CreateGameDialog());
    }
}
