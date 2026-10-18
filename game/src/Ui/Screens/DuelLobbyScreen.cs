using Godot;
using H5.Net;
using H5Reborn.Ui.Net;

namespace H5Reborn.Ui.Screens;

/// <summary>
/// UI/MPDMLobby, "Duel Mode: Servers list" with the LAN menu (Join / Create /
/// Player Name / Standard Mode / Exit). The games list shows the rooms of the
/// connected H5Reborn server; joining or creating a room moves to the duel
/// options screen (UI/MPDMWait).
/// </summary>
public partial class DuelLobbyScreen : UiScreen
{
    readonly ListRowsView _games;
    readonly ChatLogView _log = new();
    readonly BitmapText _players = new();
    List<RoomSummary> _shown = new();
    bool _showRunning = true, _showFull = true;
    double _refresh;

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

        Bus.On("join", Join);
        Bus.On("create", () => UiNavigator.ShowOverlay(new CreateGameDialog()));
        Bus.On("changename", () => UiNavigator.ShowOverlay(new PlayerNameDialog()));
        Bus.On("standardmode", () => { });
        Bus.On("disconnect", Exit);
        Bus.On("show_running", () => { _showRunning = true; Refresh(); });
        Bus.On("hide_running", () => { _showRunning = false; Refresh(); });
        Bus.On("show_observable", () => { _showFull = true; Refresh(); });
        Bus.On("hide_observable", () => { _showFull = false; Refresh(); });
    }

    public override void _Ready()
    {
        base._Ready();
        var lobby = LobbyService.Instance;
        lobby.MessageReceived += OnMessage;
        lobby.StatusChanged += OnStatus;
        lobby.Disconnected += OnDisconnected;
        if (DevArgs.Name != null) lobby.SetPlayerName(DevArgs.Name);
        if (lobby.Connected) { lobby.Send(new ListRooms()); return; }
        if (DevArgs.Host) _ = Run(lobby.HostAsync());
        else if (DevArgs.Connect != null) { var (h, p) = ConnectDialog.ParseAddress(DevArgs.Connect); _ = Run(lobby.ConnectAsync(h, p)); }
        else UiNavigator.ShowOverlay(new ConnectDialog());
    }

    async Task Run(Task t)
    {
        try { await t; }
        catch (Exception e) { _log.AddSystem(e.Message); }
    }

    public override void _ExitTree()
    {
        var lobby = LobbyService.Instance;
        lobby.MessageReceived -= OnMessage;
        lobby.StatusChanged -= OnStatus;
        lobby.Disconnected -= OnDisconnected;
    }

    public override void _Process(double delta)
    {
        _refresh += delta;
        if (_refresh > 2 && LobbyService.Instance.Connected && LobbyService.Instance.Room == null)
        {
            _refresh = 0;
            LobbyService.Instance.Send(new ListRooms());
        }
    }

    void OnStatus(string s) => _log.AddSystem(s);
    void OnDisconnected(string reason) => _log.AddSystem($"Disconnected: {reason}");

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
                if (DevArgs.JoinFirst && _shown.FirstOrDefault(r => !r.InProgress && r.Players < r.MaxPlayers) is { } first)
                {
                    DevArgs.JoinFirst = false;
                    LobbyService.Instance.Send(new JoinRoom(first.RoomId, null));
                }
                break;
            case RoomState when UiNavigator.Current == this:
                UiNavigator.Goto("wait");
                break;
        }
    }

    void Refresh()
    {
        var lobby = LobbyService.Instance;
        _shown = (lobby.Rooms?.Rooms ?? Array.Empty<RoomSummary>())
            .Where(r => (_showRunning || !r.InProgress) && (_showFull || r.Players < r.MaxPlayers)).ToList();
        string Field(string s) => $"<dots><body_bright>{ChatLogView.Escape(s)}";
        _games.SetRows(_shown.Select(r => new[]
        {
            Field(r.Name + (r.HasPassword ? " *" : "")),
            $"<center><body_bright>{r.Players}<body>/<body_bright>{r.MaxPlayers}",
            $"<center>{Field(r.InProgress ? "Duel (running)" : "Duel")}",
            $"<center>{Field("-")}",
            $"<center>{Field("3.1")}",
        }).ToList());
        _players.Markup = lobby.Connected
            ? $"<body_bright>{ChatLogView.Escape(lobby.PlayerName)}<br><body>{ChatLogView.Escape(lobby.ServerAddress ?? "")}{(lobby.IsHosting ? " (host)" : "")}"
            : "";
    }

    void Join()
    {
        if (_games.Selected < 0 || _games.Selected >= _shown.Count) { _log.AddSystem("Select a game to join."); return; }
        LobbyService.Instance.Send(new JoinRoom(_shown[_games.Selected].RoomId, null));
    }

    async void Exit()
    {
        await LobbyService.Instance.DisconnectAsync();
        UiNavigator.Goto("multiplayer");
    }
}
