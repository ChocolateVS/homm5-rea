using System.Net;
using System.Text.Json.Nodes;
using Godot;
using FileAccess = Godot.FileAccess;
using H5.Net;

namespace H5Reborn.Ui.Net;

/// <summary>
/// Client-side multiplayer session shared by the lobby screens: owns the NetClient,
/// optionally an in-process LobbyServer (host mode), and the latest lobby/room
/// state. Messages are polled on the main thread every frame.
/// </summary>
public partial class LobbyService : Node
{
    static LobbyService? _instance;

    public static LobbyService Instance
    {
        get
        {
            if (_instance == null || !IsInstanceValid(_instance))
            {
                _instance = new LobbyService { Name = "LobbyService" };
                ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_instance);
            }
            return _instance;
        }
    }

    NetClient? _client;
    LobbyServer? _server;

    public bool Connected => _client?.Connected ?? false;
    public bool IsHosting => _server != null;
    public int PlayerId => _client?.PlayerId ?? 0;
    public string PlayerName { get; set; } = LoadPlayerName();
    public string? ServerAddress { get; private set; }
    public RoomList? Rooms { get; private set; }
    public RoomState? Room { get; private set; }
    public GameStarted? Started { get; private set; }

    public event Action<Message>? MessageReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Disconnected;

    public override void _Process(double delta) => _client?.Poll();

    /// <summary>Starts a server inside this client and connects to it.</summary>
    public async Task HostAsync(int port = ProtocolInfo.DefaultPort)
    {
        await DisconnectAsync();
        var install = Game.Instance.Install;
        _server = new LobbyServer(new IGameSessionFactory[] { new StubDuelSessionFactory() },
            $"{PlayerName}'s server", install.DataFingerprint(), s => GD.Print($"[server] {s}"));
        _server.Start(port, IPAddress.Any);
        await ConnectAsync("127.0.0.1", _server.Port);
    }

    public async Task ConnectAsync(string host, int port = ProtocolInfo.DefaultPort)
    {
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
        var c = new NetClient();
        c.MessageReceived += OnMessage;
        c.Disconnected += reason =>
        {
            Room = null; Rooms = null; Started = null;
            Disconnected?.Invoke(reason);
        };
        StatusChanged?.Invoke($"Connecting to {host}:{port}...");
        await c.ConnectAsync(host, port, PlayerName, Game.Instance.Install.DataFingerprint());
        _client = c;
        ServerAddress = $"{host}:{port}";
    }

    public async Task DisconnectAsync()
    {
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
        if (_server != null) { await _server.DisposeAsync(); _server = null; }
        Room = null; Rooms = null; Started = null; ServerAddress = null;
    }

    void OnMessage(Message m)
    {
        switch (m)
        {
            case Welcome w: StatusChanged?.Invoke($"Connected to {w.ServerName}"); Send(new ListRooms()); break;
            case RoomList rl: Rooms = rl; break;
            case RoomState rs: Room = rs; break;
            case GameStarted gs: Started = gs; break;
            case GameEnded: Started = null; break;
            case ErrorMsg e: StatusChanged?.Invoke(e.Text); break;
        }
        MessageReceived?.Invoke(m);
    }

    public void Send(Message m) => _client?.Send(m);

    public void LeaveRoom()
    {
        Send(new H5.Net.LeaveRoom());
        Room = null;
        Started = null;
        Send(new ListRooms());
    }

    public void SetPlayerName(string name)
    {
        PlayerName = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        using var f = FileAccess.Open("user://player_name.txt", FileAccess.ModeFlags.Write);
        f?.StoreString(PlayerName);
    }

    static string LoadPlayerName()
    {
        if (!FileAccess.FileExists("user://player_name.txt")) return System.Environment.UserName is { Length: > 0 } u ? u : "Player";
        using var f = FileAccess.Open("user://player_name.txt", FileAccess.ModeFlags.Read);
        var s = f?.GetAsText().Trim();
        return string.IsNullOrEmpty(s) ? "Player" : s;
    }

    public override void _ExitTree()
    {
        _ = DisconnectAsync();
    }
}

// TODO(duel-session): temporary stand-in until the rules workstream provides the authoritative
// combat session (H5.Combat). It accepts the lobby flow up to GameStarted, hands every side the
// seed and chosen presets, and rejects all gameplay commands.
sealed class StubDuelSessionFactory : IGameSessionFactory
{
    public string Mode => "duel";
    public int MinPlayers => 2;
    public int MaxPlayers => 2;

    public string? ValidatePlayerOptions(JsonObject? options)
    {
        if (options?["preset"] is not { } p) return null;
        return p.GetValueKind() == System.Text.Json.JsonValueKind.Number && p.GetValue<int>() is >= 0 and < 64
            ? null : "invalid preset";
    }

    public IGameSession Create() => new StubDuelSession();

    sealed class StubDuelSession : IGameSession
    {
        int _sides;
        public IReadOnlyList<JsonNode> Start(IReadOnlyList<RoomPlayer> playersBySide, JsonObject? roomSettings, ulong seed)
        {
            _sides = playersBySide.Count;
            var presets = new JsonArray(playersBySide.Select(p => (JsonNode?)JsonValue.Create(p.Options?["preset"]?.GetValue<int>() ?? 0)).ToArray());
            return playersBySide.Select((_, side) => (JsonNode)new JsonObject
            {
                ["mode"] = "duel",
                ["seed"] = seed.ToString(),
                ["side"] = side,
                ["presets"] = presets.DeepClone(),
            }).ToList();
        }

        public SessionResult Apply(int side, JsonNode command) =>
            new SessionResult.Rejected("Combat is not implemented yet");

        public GameEnded? OnSideLeft(int side) =>
            _sides == 2 ? new GameEnded(1 - side, "opponent left") : new GameEnded(null, "player left");
    }
}
