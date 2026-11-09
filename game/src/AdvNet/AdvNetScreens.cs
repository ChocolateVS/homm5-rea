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
        // Network: Load reopens a network save on this host (CMultiplayerHostLoadLogic); hot-seat: the hot-seat save list
        // (0077a7c0 → 007f9480 type 4, CLoadScreen<SHotSeatHeaderInfo>).
        foreach (var n in new[] { "loadbutton", "load" })
            if (Find(n) is { } load) load.Enabled = true;
        if (network) Bus.On("load", () => UiNavigator.ShowOverlay(H5Reborn.Save.NetLoadList.Open(LoadNetworkSave)));
        else Bus.On("load", () => UiNavigator.ShowOverlay(H5Reborn.Save.HotSeatLoadList.Open()));

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
        // The host's exe: H5_Game_PEST.exe on the 5.5 target keeps simultaneous turns on for good (SIMULTANEOUS.md §7);
        // on the stock 3.1 target H5Reborn does the same unless the player turned it off (user-requested, §7.4).
        _set.Pest = H5.Core.IO.TargetExe.Pest(Game.Instance.Install.Root, Options.GameOptionsScreen.StockUninterruptedSimultaneous);
        // The host's MMH 5.5 player settings (options screen) for the game's scripts.
        _set.ScriptSettings = Options.Mmh55Options.Overrides();
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
        // Network rooms use the UI/MPWait rows, kept by the host (NetGameSetup).
        _set.NetSetup = _network;
        if (!_network)
        {
            AdvNetScreens.PendingHotSeat = _set;
            UiNavigator.Goto("hotseatwait");
            return;
        }
        var lobby = LobbyService.Instance;
        EnableHostSaves();
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

    /// <summary>
    /// The host stores network saves in its own profile (CSaveScreen&lt;SLanSaveHeaderInfo&gt;): the session writes them
    /// from the server thread, so a plain directory store is captured here on the main thread.
    /// </summary>
    internal static void EnableHostSaves()
    {
        var dir = ProjectSettings.GlobalizePath(H5Reborn.Profiles.ProfileManager.SavesDir(H5Reborn.Profiles.ProfileManager.CurrentName));
        var storage = new H5.Save.DirectorySaveStorage(dir);
        AdventureNetSession.Fingerprint = Game.Instance.Install.DataFingerprint();
        AdventureNetSession.SaveSink = archive =>
        {
            try { H5.Save.SaveSlots.Write(storage, archive); return null; }
            catch (Exception e) { return e.Message; }
        };
    }

    /// <summary>
    /// "Load" on the network create screen (CMultiplayerHostLoadLogic): a network save from this profile is registered
    /// with the session factory and a room is created from its settings; the saved human slots wait for players.
    /// </summary>
    async void LoadNetworkSave(H5.Save.SaveEntry entry)
    {
        H5.AdvNet.NetSave save;
        try { save = H5.AdvNet.NetSave.FromArchive(H5.Save.SaveArchive.FromBytes(H5Reborn.Save.SaveManager.Storage.Read(entry.FileName))); }
        catch (Exception e) { UiNavigator.ShowOverlay(new MessageNotice(e.Message)); return; }
        var id = H5.AdvNet.NetSaveLoads.Register(save);
        var lobby = LobbyService.Instance;
        EnableHostSaves();
        if (!lobby.Connected)
        {
            try { await lobby.HostAsync(); }
            catch (Exception e) { UiNavigator.ShowOverlay(new MessageNotice(e.Message)); return; }
        }
        var name = (Find("name") as UiEditLine)?.Value ?? entry.Header.Name;
        var password = (Find("password") as UiEditLine)?.Value;
        lobby.Send(new CreateRoom(name, "adventure", Math.Max(2, save.Humans.Count), string.IsNullOrEmpty(password) ? null : password, H5.AdvNet.NetSaveLoads.RoomSettings(save, id)));
        UiNavigator.Goto("advwait");
    }
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
/// UI/MPWait ("Standard options", CMPWaitScreen vtable 00e3a74c over CMPUbiComWaitHostLogic / CMPUbiComWaitClientLogic):
/// one ID_MPWAIT_ITEM row per active map player (state, colour, faction, starting hero, starting bonus, team) from the
/// host's rows (<see cref="NetGameSetup"/> in the room settings), the map description / preferences panel with the
/// preview slideshow, the chat, Start Game and Cancel. A row arrow is a change request to the host (00711580 →
/// 0071f3d0); the arrows a player may use follow the host's lock bits (0071e7b0). The state arrows of your own row
/// toggle ready (0 ↔ 3); the host's state arrows on another player's row ask kick_header / kick_text. Start (0071d610)
/// acts only when 0071d590 allows it. Rooms reloaded from a network save keep the plain list of saved slots.
/// </summary>
public partial class AdvWaitScreen : UiScreen
{
    readonly ChatLogView _log = new();
    readonly ListRowsView _players;
    readonly List<UiNode> _rows = new();
    readonly H5Reborn.Adventure.ThumbnailSlideshow _slides;
    List<MapPlayer> _slots = new();
    string? _slotsFor, _mapFor;
    LoadedMap? _map;
    NetGameSetup? _setup;

    public AdvWaitScreen() : base("UI/MPWait.(WindowScreen).xdb")
    {
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
        Bus.On("switchtopref", () => H5Reborn.Adventure.MapDescriptionPanel.ShowPreferences(this, true));
        Bus.On("switchtodesc", () => H5Reborn.Adventure.MapDescriptionPanel.ShowPreferences(this, false));
        _slides = new H5Reborn.Adventure.ThumbnailSlideshow(Find("screenshoot1"), Find("screenshoot2"));
        AddChild(_slides);
    }

    public override void _Ready()
    {
        base._Ready();
        LobbyService.Instance.MessageReceived += OnMessage;
        Refresh();
        // Dev/test hook for screenshots: H5_MPWAIT_CLICKS="row:button,..." presses row buttons once the rows exist.
        if (System.Environment.GetEnvironmentVariable("H5_MPWAIT_CLICKS") is { Length: > 0 } clicks)
        {
            var t = ((SceneTree)Engine.GetMainLoop()).CreateTimer(4);
            t.Timeout += () =>
            {
                foreach (var c in clicks.Split(','))
                {
                    var p = c.Split(':');
                    if (p.Length == 2 && int.TryParse(p[0], out var row)) Press(row, p[1]);
                }
            };
        }
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
            case ErrorMsg { Code: "kicked" }:
                LobbyService.Instance.LeaveRoom();
                UiNavigator.Goto("lobby");
                break;
            case ErrorMsg { Code: "start" } when _setup != null:
                // 0071d910: a failed start shows GAME_START_FAILED_HEADER / _CONTENT (UI/Common/MULTIPLAYER_TEXTS).
                H5Reborn.Options.MessageBoxes.Message(this, MpText("GAME_START_FAILED_HEADER"), MpText("GAME_START_FAILED_CONTENT"));
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

    /// <summary>A UI/Common/MULTIPLAYER_TEXTS entry by its TextName.</summary>
    internal static string MpText(string name)
    {
        const string root = "UI/Common/MULTIPLAYER_TEXTS/texts.(WindowRelatedTexts).xdb";
        try
        {
            var item = Game.Instance.Xdb.Load(root).Root?.Element("texts")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("TextName") == name);
            var href = H5.Core.Xdb.XdbRef.Parse((string?)item?.Element("text")?.Attribute("href"), root);
            return href.IsEmpty ? name : (UiResources.Instance.Text(href.Path) ?? name).Trim();
        }
        catch (Exception) { return name; }
    }

    AdvNetSettings Settings => AdvNetSettings.From(LobbyService.Instance.Room?.Settings);

    void Refresh()
    {
        var room = LobbyService.Instance.Room;
        if (room == null) return;
        var s = Settings;
        if (s.NetSetup && s.Setup != null && s.Load == null && LoadMap(s))
        {
            _players.Visible = false;
            BuildRows(room, s);
            return;
        }
        _players.Visible = true;
        Find("gamename")?.SetValue("value", ChatLogView.Escape(room.Name));
        RefreshLegacy(room);
    }

    /// <summary>The room's map on this peer (rows, preview, description); false when it cannot be opened here.</summary>
    bool LoadMap(AdvNetSettings s)
    {
        var key = s.Map ?? $"rmg:{s.Rmg?.Template}:{s.Rmg?.Seed}";
        if (key == _mapFor) return _map != null;
        _mapFor = key;
        _map = null;
        try
        {
            _map = AdvNetGame.Maps.Load(s);
            H5Reborn.Adventure.CustomGameScreen.MapEntry? entry = s.Rmg != null
                ? H5Reborn.Adventure.CustomGameScreen.Generated(new H5Reborn.Adventure.GeneratedMaps.Record(_map.Map.Name, s.Rmg))
                : MapCatalog.Find(Game.Instance.Fs, Game.Instance.Install, s.Map!) is { } r ? H5Reborn.Adventure.CustomGameScreen.EntryOf(r, listedOnly: false) : null;
            if (entry != null)
            {
                // 007132d0: the description / preferences panel, as on UI/CustomGameWait; 00722140: the preview slideshow.
                H5Reborn.Adventure.MapDescriptionPanel.Fill(this, entry, s.Difficulty, s.TimeLimitMinutes);
                _slides.SetImages(H5Reborn.Adventure.CustomGameScreen.Thumbnails(entry));
            }
        }
        catch (Exception e) { _log.AddSystem(e.Message); _map = null; }
        return _map != null;
    }

    static int LockBit(CustomGameSetup.Change kind) => kind switch
    {
        CustomGameSetup.Change.State => NetGameSetup.LockState, CustomGameSetup.Change.Colour => NetGameSetup.LockColour,
        CustomGameSetup.Change.Race => NetGameSetup.LockRace, CustomGameSetup.Change.Hero => NetGameSetup.LockHero, _ => NetGameSetup.LockBonus,
    };

    /// <summary>
    /// The state icon (States related textures; UNVERIFIED: the item's state → texture choice is not decoded, the textures'
    /// names are used: Open, Closed, Comp, HeroReady, SlotOpen).
    /// </summary>
    static string StateKey(NetSlotState st) => st switch
    {
        NetSlotState.Human => "state0", NetSlotState.Closed => "state1", NetSlotState.Computer => "state2",
        NetSlotState.Ready => "state3", _ => "state4",
    };

    static string StateTip(NetSlotState st) => st switch
    {
        NetSlotState.Human => "human_tooltip", NetSlotState.Ready => "human_ready_tooltip", NetSlotState.Closed => "closed_tooltip",
        NetSlotState.Computer => "computer_tooltip", _ => "open_tooltip",
    };

    void BuildRows(RoomState room, AdvNetSettings s)
    {
        if (_map == null || Find("playerscontainer") is not UiScrollableContainer box) return;
        _setup = NetGameSetup.FromRows(_map.Map, _map.Db, s.Setup!);
        int me = LobbyService.Instance.PlayerId, host = room.Players.FirstOrDefault(p => p.IsHost)?.PlayerId ?? -1;
        float pos = box.ScrollPosition;
        foreach (var r in _rows) RemoveInstance(r);
        _rows.Clear();
        box.ClearItems();
        for (int i = 0; i < _setup.Rows.Count; i++)
        {
            if (CreateRootWindow("ID_MPWAIT_ITEM", box.Window) is not { } node) continue;
            int row = i, locks = _setup.Locks(i, me, host);
            var slot = _setup.Rows[i];
            _rows.Add(node);
            H5Reborn.Adventure.SetupRowView.Fill(this, node, _setup.Model, row, b => Press(row, b),
                kind => (locks & LockBit(kind)) == 0, StateKey(slot.NetState), slot.NetState == NetSlotState.Open ? "" : null);
            if (H5Reborn.Adventure.SetupRowView.Child(this, node, "state") is { } state
                && state.Window.RelatedTexts.TryGetValue(StateTip(slot.NetState), out var tipPath))
                state.SetTooltip((UiResources.Instance.Text(tipPath) ?? "").Replace("<value=name>", ChatLogView.Escape(slot.Name)));
            box.AddItem(node);
        }
        box.RestorePosition(pos);
        // 00720080: the host's Start follows the start test (logic vf 0x18 = 0071d590).
        bool isHost = me == host;
        if (Find("creategamebutton") is { } btn) btn.Enabled = !isHost || _setup.CanStart();
    }

    void Press(int row, string button)
    {
        var room = LobbyService.Instance.Room;
        if (_setup == null || room == null || row < 0 || row >= _setup.Rows.Count) return;
        var b = H5Reborn.Adventure.SetupRowView.Buttons.FirstOrDefault(x => x.Button == button);
        if (b.Button == null) return;
        int me = LobbyService.Instance.PlayerId, host = room.Players.FirstOrDefault(p => p.IsHost)?.PlayerId ?? -1;
        var slot = _setup.Rows[row];
        if ((_setup.Locks(row, me, host) & LockBit(b.Kind)) != 0) return;
        // 0071f3d0 case 1: the host's state arrow on another player's row asks kick_header / kick_text (with the name).
        if (b.Kind == CustomGameSetup.Change.State && me == host && slot.Owner >= 0 && slot.Owner != me)
        {
            int target = slot.Owner;
            H5Reborn.Options.MessageBoxes.YesNo(this, MpText("kick_header"), MpText("kick_text").Replace("<value=name>", ChatLogView.Escape(slot.Name)),
                yes => { if (yes) LobbyService.Instance.Send(new KickPlayer(target)); });
            return;
        }
        LobbyService.Instance.Send(new SetupRequest(row, (int)b.Kind, b.Right));
    }

    void LoadSlots()
    {
        var s = Settings;
        var key = s.Map ?? $"rmg:{s.Rmg?.Template}:{s.Rmg?.Seed}";
        if (key == _slotsFor) return;
        _slotsFor = key;
        try
        {
            if (s.LoadSlots is { } saved && s.Map != null && MapCatalog.Find(Game.Instance.Fs, Game.Instance.Install, s.Map) is { } lr)
            {
                // Reloaded game (00754350): only the saved human slots can be taken.
                using var files = lr.Open(Game.Instance.Fs);
                var players = new MapLoader(Game.Instance.Db, Game.Instance.Fs).Load(files).Players;
                _slots = saved.Select(x => players[x.MapPlayer]).ToList();
            }
            else if (s.Map != null && MapCatalog.Find(Game.Instance.Fs, Game.Instance.Install, s.Map) is { } r)
            {
                using var files = r.Open(Game.Instance.Fs);
                _slots = new MapLoader(Game.Instance.Db, Game.Instance.Fs).Load(files).Players.Where(p => p.Active && p.CanBeHuman).ToList();
            }
            else _slots = Enumerable.Range(0, Math.Max(2, s.Rmg?.Players ?? 2)).Select(i => new MapPlayer { Index = i, Active = true, CanBeHuman = true, Colour = "" }).ToList();
        }
        catch (Exception e) { _slots = new(); _log.AddSystem(e.Message); }
    }

    /// <summary>Rooms without setup rows (reloaded network saves): the saved slots with their players and ready state.</summary>
    void RefreshLegacy(RoomState room)
    {
        _setup = null;
        LoadSlots();
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
        if (room == null || _slots.Count == 0 || _setup != null) return;
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

    /// <summary>
    /// Start Game: the host starts (0071d610); a client's button toggles its ready state (UNVERIFIED: the client logic's
    /// start handler is not traced — the same toggle as its own row's state arrows, 0071d3e0).
    /// </summary>
    void StartOrReady()
    {
        var room = LobbyService.Instance.Room;
        if (room == null) return;
        var me = room.Players.FirstOrDefault(p => p.PlayerId == LobbyService.Instance.PlayerId);
        if (me?.IsHost != true) { LobbyService.Instance.Send(new SetReady(!(me?.Ready ?? false))); return; }
        if (_setup != null && !_setup.CanStart()) return;
        LobbyService.Instance.Send(new StartGame());
    }
}

/// <summary>Who plays which colour in a hot-seat game (local, several humans on one PC).</summary>
public sealed class HotSeatPlan
{
    public AdvNetSettings Settings { get; init; } = new();
    /// <summary>Map player index → player name for every human; other active players are computers.</summary>
    public Dictionary<int, string> Humans { get; } = new();
    /// <summary>The players chosen on UI/HotSeatWait (closed slots left out); null = every active player, default bonus.</summary>
    public List<H5.Adventure.Engine.PlayerSetup>? Players { get; set; }
}

/// <summary>
/// UI/HotSeatWait ("Hot-Seat options", 0077d4d0): one ID_HOTSEAT_ITEM row (UI/MPWait ItemElements + a player-name edit
/// line) per model slot in "playerslist", over the game-setup model in hot-seat mode (CHotSeatLogic,
/// <see cref="CustomGameSetup.ForHotSeat"/>): the row arrows set a pending command that 0077bd40 hands to the model —
/// state 0077bc20, colour vf 0x30, race vf 0x38, hero vf 0x40, starting bonus vf 0x48 (00c9d860) — and 0077b760 enables
/// each pair (bonus: vf 0x44 = 00c9d810, the slot's CanChangeBonus). Start (0077c470) needs the two-team test and a
/// human row (0077baa0) and starts with the slots' choices (00c9fa80 applies the bonus).
/// </summary>
public partial class HotSeatWaitScreen : UiScreen
{
    readonly HotSeatPlan _plan;
    readonly CustomGameSetup? _setup;
    readonly List<UiNode> _rows = new();

    public HotSeatWaitScreen() : base("UI/HotSeatWait.(WindowScreen).xdb")
    {
        // Dev/test: H5_HOTSEAT_MAP names the map when the screen is opened directly (--goto=hotseatwait).
        _plan = new HotSeatPlan { Settings = AdvNetScreens.PendingHotSeat ?? new AdvNetSettings { Map = System.Environment.GetEnvironmentVariable("H5_HOTSEAT_MAP") } };
        Bus.On("start", Start);
        Bus.On("back", () => UiNavigator.Goto("hotseatcreate"));
        try { _setup = CustomGameSetup.ForHotSeat(AdvNetGame.Maps.Load(_plan.Settings).Map, H5Reborn.Adventure.AdventureSession.SharedDb); }
        catch (Exception e) { GD.Print($"[advnet] hot-seat map: {e.Message}"); }
        if (_setup != null) HotSeatNames.Init(_setup.Slots, ColourText);
        Find("gamename")?.SetValue("value", ChatLogView.Escape(_plan.Settings.Map ?? "Random map"));
        BuildRows();
    }

    public override void _Ready()
    {
        base._Ready();
        // Dev/test hook for screenshots: H5_HOTSEAT_CLICKS="row:button,..." presses row buttons.
        if (System.Environment.GetEnvironmentVariable("H5_HOTSEAT_CLICKS") is { Length: > 0 } clicks && _setup != null)
            Callable.From(() =>
            {
                foreach (var c in clicks.Split(','))
                {
                    var p = c.Split(':');
                    if (p.Length == 2 && int.TryParse(p[0], out var row)) Press(row, p[1]);
                }
            }).CallDeferred();
    }

    void Press(int row, string button)
    {
        if (_setup == null) return;
        var b = H5Reborn.Adventure.SetupRowView.Buttons.FirstOrDefault(x => x.Button == button);
        if (b.Button == null) return;
        _setup.Apply(row, b.Kind, b.Right, "");
        FixNames();   // 0077bd40: after a row command, logic vf 0x1c = 0077be40
        BuildRows();
    }

    void BuildRows()
    {
        if (_setup == null || Find("playerslist") is not UiScrollableContainer box) return;
        float pos = box.ScrollPosition;
        foreach (var r in _rows) RemoveInstance(r);
        _rows.Clear();
        box.ClearItems();
        for (int i = 0; i < _setup.Slots.Count; i++)
        {
            if (CreateRootWindow("ID_HOTSEAT_ITEM", box.Window) is not { } node) continue;
            int row = i;
            _rows.Add(node);
            H5Reborn.Adventure.SetupRowView.Fill(this, node, _setup, row, b => Press(row, b));
            // The name edit line: the row's name (editable for humans); defaults and fixes: HotSeatNames.
            if (H5Reborn.Adventure.SetupRowView.Child(this, node, "playername") is UiEditLine edit)
            {
                var s = _setup.Slots[row];
                // The edit line covers the item's name view: the name is shown in it (computer rows: the colour name).
                H5Reborn.Adventure.SetupRowView.Child(this, node, "name")?.SetValue("value", "");
                edit.Value = H5Reborn.Adventure.SetupRowView.DisplayName(s);
                edit.Enabled = s.State == SetupSlot.SlotState.Human;
                edit.Changed += t => s.Name = t;
            }
            box.AddItem(node);
        }
        box.RestorePosition(pos);
        foreach (var n in new[] { "start", "StartBtn" }) if (Find(n) is { } start) start.Enabled = _setup.CanStartHotSeat();
    }

    /// <summary>The name table text of a colour key (UI/MPWait/PlayersList/Item related texts, see HotSeatNames).</summary>
    static string ColourText(string key) => UiResources.Instance.Text($"UI/MPWait/PlayersList/Item/player_{key}.txt")?.Trim() ?? key;

    void FixNames() { if (_setup != null) HotSeatNames.Fix(_setup.Slots, ColourText); }

    void Start()
    {
        if (_setup == null || !_setup.CanStartHotSeat()) return;
        // 0077c470: unless every row's name is valid (logic vf 0x18 = 0077bbe0 → 0077bb50), vf 0x1c = 0077be40 fixes them.
        if (!_setup.Slots.All(s => HotSeatNames.Valid(s, ColourText))) FixNames();
        _plan.Humans.Clear();
        foreach (var s in _setup.Slots.Where(s => s.State == SetupSlot.SlotState.Human)) _plan.Humans[s.MapPlayer] = s.Name;
        _plan.Players = _setup.ToGameSetup(0, _plan.Settings.Difficulty).Players;
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
