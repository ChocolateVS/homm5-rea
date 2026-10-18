using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace H5.Net;

/// <summary>
/// Lobby + game-routing server. Runs standalone (H5.Server) or inside a client
/// acting as host. All state is guarded by a single lock: message volume in a
/// turn-based game is tiny, and serialising keeps sessions deterministic.
/// </summary>
public sealed class LobbyServer : IAsyncDisposable
{
    sealed class Player(int id, Connection conn)
    {
        public readonly int Id = id;
        public readonly Connection Conn = conn;
        public string Name = "";
        public bool Authenticated;
        public Room? Room;
        public bool Ready;
        public JsonObject? Options;
    }

    sealed class Room(int id, string name, IGameSessionFactory factory, int maxPlayers, string? password, JsonObject? settings)
    {
        public readonly int Id = id;
        public readonly string Name = name;
        public readonly IGameSessionFactory Factory = factory;
        public readonly int MaxPlayers = maxPlayers;
        public readonly string? Password = password;
        public readonly JsonObject? Settings = settings;
        public readonly List<Player> Players = new();
        public Player? Host => Players.FirstOrDefault();
        public IGameSession? Session;
        public int Seq;
        public readonly Dictionary<int, int> SideOf = new();
    }

    readonly object _lock = new();
    readonly Dictionary<string, IGameSessionFactory> _factories;
    readonly Dictionary<int, Player> _players = new();
    readonly Dictionary<int, Room> _rooms = new();
    readonly string _serverName;
    readonly string? _requiredFingerprint;
    readonly Action<string> _log;
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    Task? _acceptLoop;
    int _nextPlayerId = 1, _nextRoomId = 1;

    public int Port { get; private set; }

    public LobbyServer(IEnumerable<IGameSessionFactory> factories, string serverName = "H5Reborn",
        string? requiredFingerprint = null, Action<string>? log = null)
    {
        _factories = factories.ToDictionary(f => f.Mode, StringComparer.OrdinalIgnoreCase);
        _serverName = serverName;
        _requiredFingerprint = requiredFingerprint;
        _log = log ?? (_ => { });
    }

    public void Start(int port = ProtocolInfo.DefaultPort, IPAddress? bind = null)
    {
        _listener = new TcpListener(bind ?? IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
        _log($"listening on {_listener.LocalEndpoint}");
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            var conn = new Connection(tcp);
            Player p;
            lock (_lock) { p = new Player(_nextPlayerId++, conn); _players[p.Id] = p; }
            conn.Received += (_, m) => OnMessage(p, m);
            conn.Closed += (_, e) => OnClosed(p, e);
            conn.Start();
            _log($"player {p.Id} connected from {conn.RemoteEndPoint}");
        }
    }

    void OnMessage(Player p, Message m)
    {
        lock (_lock)
        {
            try { Handle(p, m); }
            catch (Exception e)
            {
                _log($"error handling {m.GetType().Name} from {p.Id}: {e}");
                p.Conn.Send(new ErrorMsg("internal", "Server error"));
            }
        }
    }

    void Handle(Player p, Message m)
    {
        if (!p.Authenticated)
        {
            if (m is not Hello hello) { p.Conn.Send(new ErrorMsg("handshake", "Expected hello")); return; }
            if (hello.ProtocolVersion != ProtocolInfo.Version)
            { p.Conn.Send(new ErrorMsg("version", $"Server speaks protocol {ProtocolInfo.Version}")); return; }
            if (_requiredFingerprint != null && hello.DataFingerprint != _requiredFingerprint)
            { p.Conn.Send(new ErrorMsg("data_mismatch", "Your game data or mods differ from the server's")); return; }
            p.Name = string.IsNullOrWhiteSpace(hello.PlayerName) ? $"Player{p.Id}" : hello.PlayerName.Trim()[..Math.Min(hello.PlayerName.Trim().Length, 32)];
            p.Authenticated = true;
            p.Conn.Send(new Welcome(p.Id, _serverName));
            return;
        }

        switch (m)
        {
            case Ping ping: p.Conn.Send(new Pong(ping.Ticks)); break;
            case ListRooms: p.Conn.Send(BuildRoomList()); break;
            case CreateRoom cr: CreateRoomFor(p, cr); break;
            case JoinRoom jr: JoinRoomFor(p, jr); break;
            case LeaveRoom: LeaveCurrentRoom(p, "left"); break;
            case SetReady sr when p.Room is { Session: null } r:
                p.Ready = sr.Ready; BroadcastRoom(r); break;
            case SetPlayerOptions so when p.Room is { Session: null } r:
                if (r.Factory.ValidatePlayerOptions(so.Options) is { } err) { p.Conn.Send(new ErrorMsg("options", err)); break; }
                p.Options = so.Options; p.Ready = false; BroadcastRoom(r); break;
            case Chat chat when p.Room is { } r:
                var text = chat.Text.Length > 500 ? chat.Text[..500] : chat.Text;
                foreach (var q in r.Players) q.Conn.Send(new Chat(p.Id, text));
                break;
            case StartGame when p.Room is { Session: null } r && r.Host == p: StartRoomGame(r); break;
            case GameCommand gc when p.Room is { Session: not null } r: ApplyCommand(r, p, gc); break;
            default: p.Conn.Send(new ErrorMsg("unexpected", $"Not allowed now: {m.GetType().Name}")); break;
        }
    }

    RoomList BuildRoomList() => new(_rooms.Values.Select(r =>
        new RoomSummary(r.Id, r.Name, r.Factory.Mode, r.Players.Count, r.MaxPlayers, r.Password != null, r.Session != null)).ToList());

    void CreateRoomFor(Player p, CreateRoom cr)
    {
        if (!_factories.TryGetValue(cr.Mode, out var factory)) { p.Conn.Send(new ErrorMsg("mode", $"Unknown mode {cr.Mode}")); return; }
        LeaveCurrentRoom(p, "left");
        var max = Math.Clamp(cr.MaxPlayers, factory.MinPlayers, factory.MaxPlayers);
        var name = string.IsNullOrWhiteSpace(cr.Name) ? $"{p.Name}'s game" : cr.Name.Trim();
        var room = new Room(_nextRoomId++, name, factory, max, string.IsNullOrEmpty(cr.Password) ? null : cr.Password, cr.Settings);
        _rooms[room.Id] = room;
        AddToRoom(p, room);
        _log($"room {room.Id} '{room.Name}' ({factory.Mode}) created by {p.Id}");
    }

    void JoinRoomFor(Player p, JoinRoom jr)
    {
        if (!_rooms.TryGetValue(jr.RoomId, out var room)) { p.Conn.Send(new ErrorMsg("room", "No such room")); return; }
        if (room.Session != null) { p.Conn.Send(new ErrorMsg("room", "Game already in progress")); return; }
        if (room.Players.Count >= room.MaxPlayers) { p.Conn.Send(new ErrorMsg("room", "Room is full")); return; }
        if (room.Password != null && room.Password != jr.Password) { p.Conn.Send(new ErrorMsg("password", "Wrong password")); return; }
        LeaveCurrentRoom(p, "left");
        AddToRoom(p, room);
    }

    void AddToRoom(Player p, Room room)
    {
        room.Players.Add(p);
        p.Room = room;
        p.Ready = false;
        p.Options = null;
        BroadcastRoom(room);
    }

    void LeaveCurrentRoom(Player p, string reason)
    {
        if (p.Room is not { } room) return;
        room.Players.Remove(p);
        p.Room = null;
        if (room.Session != null && room.SideOf.TryGetValue(p.Id, out var side))
        {
            var ended = room.Session.OnSideLeft(side);
            if (ended != null) EndGame(room, ended);
        }
        if (room.Players.Count == 0) { _rooms.Remove(room.Id); _log($"room {room.Id} closed"); }
        else BroadcastRoom(room);
    }

    void StartRoomGame(Room room)
    {
        var host = room.Host!;
        if (room.Players.Count < room.Factory.MinPlayers) { host.Conn.Send(new ErrorMsg("start", "Not enough players")); return; }
        if (room.Players.Any(q => q != host && !q.Ready)) { host.Conn.Send(new ErrorMsg("start", "Not everyone is ready")); return; }

        var seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var bySide = room.Players.Select((q, i) => new RoomPlayer(q.Id, q.Name, i, q.Ready, q == host, q.Options)).ToList();
        room.Session = room.Factory.Create();
        var setups = room.Session.Start(bySide, room.Settings, seed);
        room.SideOf.Clear();
        for (int i = 0; i < room.Players.Count; i++)
        {
            room.SideOf[room.Players[i].Id] = i;
            room.Players[i].Conn.Send(new GameStarted(i, setups[i]));
        }
        _log($"room {room.Id} started with seed {seed}");
        BroadcastRoom(room);
    }

    void ApplyCommand(Room room, Player p, GameCommand gc)
    {
        if (!room.SideOf.TryGetValue(p.Id, out var side)) return;
        switch (room.Session!.Apply(side, gc.Command))
        {
            case SessionResult.Rejected rej:
                p.Conn.Send(new CommandRejected(gc.ClientSeq, rej.Reason));
                break;
            case SessionResult.Accepted acc:
                var seq = ++room.Seq;
                foreach (var q in room.Players)
                    q.Conn.Send(new GameUpdate(seq, q == p ? gc.ClientSeq : null, side, acc.Events, acc.StateHash));
                if (acc.Ended != null) EndGame(room, acc.Ended);
                break;
        }
    }

    void EndGame(Room room, GameEnded ended)
    {
        foreach (var q in room.Players) { q.Conn.Send(ended); q.Ready = false; }
        room.Session = null;
        room.SideOf.Clear();
        room.Seq = 0;
        _log($"room {room.Id} game ended: winner side {ended.WinnerSide?.ToString() ?? "none"} ({ended.Reason})");
        if (room.Players.Count > 0) BroadcastRoom(room);
    }

    void BroadcastRoom(Room room)
    {
        var state = new RoomState(room.Id, room.Name, room.Factory.Mode, room.MaxPlayers,
            room.Players.Select((q, i) => new RoomPlayer(q.Id, q.Name, i, q.Ready, q == room.Host, q.Options)).ToList(),
            room.Settings, room.Session != null);
        foreach (var q in room.Players) q.Conn.Send(state);
    }

    void OnClosed(Player p, Exception? error)
    {
        lock (_lock)
        {
            LeaveCurrentRoom(p, "disconnected");
            _players.Remove(p.Id);
        }
        _log($"player {p.Id} disconnected{(error != null ? $": {error.Message}" : "")}");
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop != null) await _acceptLoop;
        List<Player> players;
        lock (_lock) players = _players.Values.ToList();
        foreach (var p in players) await p.Conn.DisposeAsync();
    }
}
