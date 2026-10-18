using Godot;
using H5.Net;
using H5.Net.Discovery;
using H5.Net.Master;
using H5Reborn.Ui.Net;

namespace H5Reborn.Ui.Screens;

/// <summary>
/// UI/MPDMLobby, "Duel Mode: Servers list" with the LAN menu (Join / Create /
/// Player Name / Standard Mode / Exit).
///
/// Not connected: the games list is a server browser — games hosted on the local
/// network (found automatically by LAN discovery) and public online games from the
/// configured master server. Join connects to the selected one; Create hosts a game
/// on this computer; the Standard Mode button (relabelled "Connect...") opens the
/// address / join-code dialog. Connected: the list shows that server's game rooms,
/// and joining or creating a room moves to the duel options screen (UI/MPDMWait).
/// </summary>
public partial class DuelLobbyScreen : UiScreen
{
    abstract record Row;
    sealed record RoomRow(RoomSummary Room) : Row;
    sealed record LanRow(LanGameInfo Game) : Row;
    sealed record OnlineRow(OnlineLobby Lobby) : Row;

    readonly ListRowsView _games;
    readonly ChatLogView _log = new();
    readonly BitmapText _players = new();
    List<Row> _shown = new();
    bool _showRunning = true, _showFull = true;
    double _refresh;
    string _lastKey = "";

    static string H(int i) => UiResources.Instance.Text($"UI/Lobby/Texts/Games/GameColumnHeader_{i}.txt") ?? "";

    public DuelLobbyScreen() : base("UI/MPDMLobby.(WindowScreen).xdb")
    {
        SetVisible("Menu", false);
        SetVisible("MenuLan", true);

        // Column cells of the stock Games panel background (local to the 'Games' window).
        _games = new ListRowsView(new ListRowsView.Column[]
        {
            new(12, 144, H(0)), new(158, 74, H(1)), new(234, 186, H(2)), new(422, 104, H(3)), new(528, 76, H(4)),
        }, headerY: 33, headerH: 22, rowsY: 57, rowH: 20);
        Find("Games")?.AddChild(_games);
        _games.RowActivated += _ => Join();
        Find("MPCommonChatLog")?.AddChild(_log);
        if (Find("PlayersList") is { } pl) { pl.AddChild(_players); _players.Position = new Vector2(4, 30); _players.Size = new Vector2(200, 280); }

        if (Find("EditLine") is UiEditLine chat)
            chat.Submitted += text =>
            {
                if (text.Length == 0) return;
                _log.AddSystem("Chat is available once you are in a game room.");
                chat.Value = "";
            };

        // Toggle state 0 enters "show", state 1 "hide"; start from the data's initial states.
        _showRunning = Find("ShowRunningBtn")?.State != 1;
        _showFull = Find("ShowObservableBtn")?.State != 1;

        // No "standard mode" exists here; its button opens the connect (address / join code) dialog.
        foreach (var w in Root.Descendants().Where(w => w.ButtonStates.Any(s => s.OnEnter.Any(c => c.Argument == "standardmode"))))
            Node(w)?.SetText("Connect...");

        Bus.On("join", Join);
        Bus.On("create", Create);
        Bus.On("changename", () => UiNavigator.ShowOverlay(new PlayerNameDialog()));
        Bus.On("standardmode", () => UiNavigator.ShowOverlay(new ConnectDialog()));
        Bus.On("disconnect", Exit);
        Bus.On("show_running", () => { _showRunning = true; Refresh(); });
        Bus.On("hide_running", () => { _showRunning = false; Refresh(); });
        Bus.On("show_observable", () => { _showFull = true; Refresh(); });
        Bus.On("hide_observable", () => { _showFull = false; Refresh(); });
    }

    public override void _Ready()
    {
        base._Ready();
        Audio.MenuAudio.Attach(this, "MP_MAIN_MENU_MUSIC_ID");
        var lobby = LobbyService.Instance;
        lobby.MessageReceived += OnMessage;
        lobby.StatusChanged += OnStatus;
        lobby.Disconnected += OnDisconnected;
        lobby.ErrorRaised += OnError;
        lobby.StartLanBrowsing();
        if (DevArgs.Name != null) lobby.SetPlayerName(DevArgs.Name);
        if (lobby.Connected) { lobby.Send(new ListRooms()); return; }
        if (DevArgs.Host) _ = Run(lobby.HostAsync(publishOnline: DevArgs.HostOnline));
        else if (DevArgs.JoinCode != null) _ = Run(lobby.JoinOnlineAsync(DevArgs.JoinCode));
        else if (DevArgs.Connect != null) { var (h, p) = ConnectDialog.ParseAddress(DevArgs.Connect); _ = Run(lobby.ConnectAsync(h, p)); }
        else _log.AddSystem("Games on your network appear below. Use Connect... to join by address or join code.");
        Refresh();
    }

    async Task Run(Task t)
    {
        try { await t; }
        catch (Exception e) { _log.AddSystem(e.Message); OnError("Network", e.Message); }
    }

    public override void _ExitTree()
    {
        var lobby = LobbyService.Instance;
        lobby.MessageReceived -= OnMessage;
        lobby.StatusChanged -= OnStatus;
        lobby.Disconnected -= OnDisconnected;
        lobby.ErrorRaised -= OnError;
        lobby.StopLanBrowsing();
    }

    public override void _Process(double delta)
    {
        var lobby = LobbyService.Instance;
        _refresh += delta;
        if (_refresh > 2 && lobby.Connected && lobby.Room == null)
        {
            _refresh = 0;
            lobby.Send(new ListRooms());
        }
        if (!lobby.Connected)
        {
            // Browser mode: refresh when the LAN/online lists change.
            var key = string.Join("|", lobby.LanGames.Select(g => g.Id + g.Players + g.OpenRooms))
                      + "#" + string.Join("|", lobby.OnlineLobbies.Select(l => l.Id + l.Players)) + lobby.OnlineError;
            if (key != _lastKey) { _lastKey = key; Refresh(); }
            if (DevArgs.JoinLanFirst && lobby.LanGames.Count > 0)
            {
                DevArgs.JoinLanFirst = false;
                var g = lobby.LanGames[0];
                _ = Run(lobby.ConnectAsync(g.Address, g.Port));
            }
        }
    }

    void OnStatus(string s) => _log.AddSystem(s);
    void OnDisconnected(string reason) { _log.AddSystem($"Disconnected: {reason}"); Refresh(); }

    void OnError(string title, string text)
    {
        if (UiNavigator.Current == this) Adventure.AdvDialogs.Message(this, title, text, null);
    }

    void OnMessage(Message m)
    {
        switch (m)
        {
            case Welcome:
                Refresh();
                if (DevArgs.CreateRoom is { } room) { DevArgs.CreateRoom = null; LobbyService.Instance.Send(new CreateRoom(room, "duel", 2, null, null)); }
                break;
            case RoomList:
                Refresh();
                if (DevArgs.JoinFirst && _shown.OfType<RoomRow>().FirstOrDefault(r => !r.Room.InProgress && r.Room.Players < r.Room.MaxPlayers) is { } first)
                {
                    DevArgs.JoinFirst = false;
                    LobbyService.Instance.Send(new JoinRoom(first.Room.RoomId, null));
                }
                break;
            case RoomState rs when UiNavigator.Current == this:
                UiNavigator.Goto(rs.Mode == "adventure" ? "advwait" : "wait");
                break;
        }
    }

    void Refresh()
    {
        var lobby = LobbyService.Instance;
        string Field(string s) => $"<dots><body_bright>{ChatLogView.Escape(s)}";
        string Count(int n, int max) => $"<center><body_bright>{n}<body>/<body_bright>{max}";
        if (lobby.Connected)
        {
            _shown = (lobby.Rooms?.Rooms ?? Array.Empty<RoomSummary>())
                .Where(r => (_showRunning || !r.InProgress) && (_showFull || r.Players < r.MaxPlayers))
                .Select(r => (Row)new RoomRow(r)).ToList();
        }
        else
        {
            _shown = lobby.LanGames.Select(g => (Row)new LanRow(g))
                .Concat(lobby.OnlineLobbies.Where(l => _showFull || l.Players < l.MaxPlayers).Select(l => (Row)new OnlineRow(l)))
                .ToList();
        }
        _games.SetRows(_shown.Select(row => row switch
        {
            RoomRow { Room: var r } => new[]
            {
                Field(r.Name + (r.HasPassword ? " *" : "")), Count(r.Players, r.MaxPlayers),
                $"<center>{Field(r.InProgress ? "Duel (running)" : "Duel")}", $"<center>{Field("-")}", $"<center>{Field("3.1")}",
            },
            LanRow { Game: var g } => new[]
            {
                Field(g.Name), Count(g.Players, Math.Max(g.Players, 8)),
                $"<center>{Field($"LAN ({g.OpenRooms} open)")}", $"<center>{Field(g.Address)}", $"<center>{Field("3.1")}",
            },
            OnlineRow { Lobby: var l } => new[]
            {
                Field(l.Name + (l.HasPassword ? " *" : "")), Count(l.Players, l.MaxPlayers),
                $"<center>{Field("Online duel")}", $"<center>{Field(l.Code)}", $"<center>{Field("3.1")}",
            },
            _ => Array.Empty<string>(),
        }).ToList());

        string info;
        if (lobby.Connected)
        {
            var ping = lobby.PingMs >= 0 ? $"<br><body>Ping: <body_bright>{lobby.PingMs} ms" : "";
            var code = lobby.OnlineCode is { } c ? $"<br><body>Join code: <body_bright>{c}" : "";
            info = $"<body_bright>{ChatLogView.Escape(lobby.PlayerName)}<br><body>{ChatLogView.Escape(lobby.ServerAddress ?? "")}{(lobby.IsHosting ? " (host)" : "")}{ping}{code}";
        }
        else
        {
            var server = string.IsNullOrWhiteSpace(lobby.Settings.MasterUrl) ? "none (set one in Connect...)" : lobby.Settings.MasterUrl!;
            info = $"<body_bright>{ChatLogView.Escape(lobby.PlayerName)}<br><body>Online server: {ChatLogView.Escape(server)}"
                   + (lobby.OnlineError != null ? $"<br><color=FFFF6040>{ChatLogView.Escape(lobby.OnlineError)}" : "");
        }
        _players.Markup = info;
    }

    async void Join()
    {
        var lobby = LobbyService.Instance;
        if (_games.Selected < 0 || _games.Selected >= _shown.Count)
        {
            if (lobby.Connected) _log.AddSystem("Select a game to join.");
            else UiNavigator.ShowOverlay(new ConnectDialog());
            return;
        }
        try
        {
            switch (_shown[_games.Selected])
            {
                case RoomRow { Room: var r }:
                    if (r.HasPassword) UiNavigator.ShowOverlay(new PasswordDialog(r.RoomId));
                    else lobby.Send(new JoinRoom(r.RoomId, null));
                    break;
                case LanRow { Game: var g }:
                    await lobby.ConnectAsync(g.Address, g.Port);
                    break;
                case OnlineRow { Lobby: var l }:
                    await lobby.JoinOnlineAsync(l.Code);
                    break;
            }
        }
        catch (Exception e) { _log.AddSystem(e.Message); OnError("Network", e.Message); }
    }

    async void Create()
    {
        var lobby = LobbyService.Instance;
        if (!lobby.Connected)
        {
            try { await lobby.HostAsync(); }
            catch (Exception e) { OnError("Network", e.Message); return; }
        }
        // Standard (adventure map) game or duel, like the original lobby's two game types.
        UiNavigator.ShowOverlay(new AdvNet.GameTypeDialog());
    }

    async void Exit()
    {
        await LobbyService.Instance.DisconnectAsync();
        UiNavigator.Goto("multiplayer");
    }
}
