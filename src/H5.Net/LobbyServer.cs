using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using H5.Net.Transport;

namespace H5.Net;

/// <summary>
/// Lobby + game-routing server. Runs standalone (H5.Server) or inside a client
/// acting as host. Players arrive over any transport (TCP listener, or reliable
/// UDP channels that were direct, NAT-punched or relayed). All state is guarded by
/// a single lock: message volume in a turn-based game is tiny, and serialising
/// keeps sessions deterministic.
///
/// A player who drops out of a running game keeps their seat for
/// <see cref="ReconnectGrace"/>; reconnecting with the ResumeToken from Welcome
/// replays the missed updates (or the whole game) so their replica catches up.
/// </summary>
public sealed class LobbyServer : IAsyncDisposable
{
    sealed class Player(int id, string token)
    {
        public readonly int Id = id;
        public readonly string Token = token;
        public Connection? Conn;
        public string Name = "";
        public bool Authenticated;
        public Room? Room;
        public bool Ready;
        public JsonObject? Options;
        public DateTime? DroppedAt;
        public long LastSeenTicks = Environment.TickCount64;
        public void Send(Message m) => Conn?.Send(m);
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
        public Player? Host => Players.FirstOrDefault(p => p.DroppedAt == null) ?? Players.FirstOrDefault();
        public IGameSession? Session;
        public int Seq;
        public readonly Dictionary<int, int> SideOf = new();
        public IReadOnlyList<JsonNode>? Setups;
        public readonly List<GameUpdate> Log = new();
    }

    readonly object _lock = new();
    readonly Dictionary<string, IGameSessionFactory> _factories;
    readonly Dictionary<int, Player> _players = new();
    readonly Dictionary<string, Player> _byToken = new();
    readonly Dictionary<int, Room> _rooms = new();
    readonly string _requiredFingerprint;
    readonly Action<string> _log;
    readonly CancellationTokenSource _cts = new();
    readonly List<UdpHost> _udpHosts = new();
    TcpListener? _listener;
    Task? _acceptLoop, _sweepLoop;
    int _nextPlayerId = 1, _nextRoomId = 1;

    public int Port { get; private set; }
    public string ServerName { get; set; }
    /// <summary>How long a dropped player's seat in a running game is held.</summary>
    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>Connections silent for longer than this are dropped (clients ping every few seconds).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public LobbyServer(IEnumerable<IGameSessionFactory> factories, string serverName = "H5Reborn",
        string? requiredFingerprint = null, Action<string>? log = null)
    {
        _factories = factories.ToDictionary(f => f.Mode, StringComparer.OrdinalIgnoreCase);
        ServerName = serverName;
        _requiredFingerprint = requiredFingerprint ?? "";
        _log = log ?? (_ => { });
    }

    /// <summary>Starts the TCP listener (direct IP / LAN play).</summary>
    public void Start(int port = ProtocolInfo.DefaultPort, IPAddress? bind = null)
    {
        _listener = new TcpListener(bind ?? IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
        _sweepLoop ??= Task.Run(() => SweepLoop(_cts.Token));
        _log($"listening on tcp {_listener.LocalEndpoint}");
    }

    /// <summary>Also accepts reliable-UDP players (direct, hole-punched or relayed) arriving on this socket.</summary>
    public void AttachUdp(UdpHost host)
    {
        lock (_lock) _udpHosts.Add(host);
        host.Accepted += ch => AttachChannel(ch);
        host.AcceptConnections = true;
        _sweepLoop ??= Task.Run(() => SweepLoop(_cts.Token));
        _log($"accepting udp on {host.LocalEndPoint}");
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = AdoptTcp(tcp, ct);
        }
    }

    /// <summary>Native framed TCP and WebSocket (HTTP upgrade) share the listening port.</summary>
    async Task AdoptTcp(TcpClient tcp, CancellationToken ct)
    {
        try { AttachChannel(await PortSharing.AcceptAsync(tcp, ct)); }
        catch (Exception) { tcp.Dispose(); }
    }

    /// <summary>Adopts an already-established frame channel as a new client connection.</summary>
    public void AttachChannel(IFrameChannel channel)
    {
        var conn = new Connection(channel);
        Player p;
        lock (_lock)
        {
            p = new Player(_nextPlayerId++, Convert.ToHexString(RandomNumberGenerator.GetBytes(16))) { Conn = conn };
            _players[p.Id] = p;
        }
        Hook(p, conn);
        conn.Start();
        _log($"connection {p.Id} from {conn.RemoteEndPoint}");
    }

    void Hook(Player p, Connection conn)
    {
        conn.Received += (c, m) => OnMessage(p, c, m);
        conn.Closed += (c, e) => OnClosed(p, c, e);
    }

    void OnMessage(Player p, Connection from, Message m)
    {
        lock (_lock)
        {
            if (p.Conn != from) return; // stale connection after a resume
            p.LastSeenTicks = Environment.TickCount64;
            try { Handle(p, m); }
            catch (Exception e)
            {
                _log($"error handling {m.GetType().Name} from {p.Id}: {e}");
                p.Send(new ErrorMsg("internal", "Server error"));
            }
        }
    }

    void Handle(Player p, Message m)
    {
        if (!p.Authenticated)
        {
            if (m is not Hello hello) { p.Send(new ErrorMsg("handshake", "Expected hello")); return; }
            if (hello.ProtocolVersion != ProtocolInfo.Version)
            { p.Send(new ErrorMsg("version", $"This server runs H5Reborn network protocol {ProtocolInfo.Version}; you have {hello.ProtocolVersion}. Update both games to the same version.")); return; }
            if (_requiredFingerprint.Length > 0 && hello.DataFingerprint != _requiredFingerprint)
            { p.Send(new ErrorMsg("data_mismatch", "Your game data or mods differ from the host's. Both players need identical game files and mods.")); return; }

            if (hello.ResumeToken != null && _byToken.TryGetValue(hello.ResumeToken, out var seat) && seat != p)
            {
                Resume(seat, p, hello);
                return;
            }
            var name = string.IsNullOrWhiteSpace(hello.PlayerName) ? $"Player{p.Id}" : hello.PlayerName.Trim();
            p.Name = name[..Math.Min(name.Length, 32)];
            p.Authenticated = true;
            _byToken[p.Token] = p;
            p.Send(new Welcome(p.Id, ServerName, p.Token));
            return;
        }

        switch (m)
        {
            case Ping ping: p.Send(new Pong(ping.Ticks)); break;
            case Pong: break;
            case ListRooms: p.Send(BuildRoomList()); break;
            case CreateRoom cr: CreateRoomFor(p, cr); break;
            case JoinRoom jr: JoinRoomFor(p, jr); break;
            case LeaveRoom: LeaveCurrentRoom(p); break;
            case SetReady sr when p.Room is { Session: null } r:
                p.Ready = sr.Ready; BroadcastRoom(r); break;
            case SetPlayerOptions so when p.Room is { Session: null } r:
                if (r.Factory.ValidatePlayerOptions(so.Options) is { } err) { p.Send(new ErrorMsg("options", err)); break; }
                p.Options = so.Options; p.Ready = false; BroadcastRoom(r); break;
            case Chat chat when p.Room is { } r:
                var text = chat.Text.Length > 500 ? chat.Text[..500] : chat.Text;
                foreach (var q in r.Players) q.Send(new Chat(p.Id, text));
                break;
            case StartGame when p.Room is { Session: null } r && r.Host == p: StartRoomGame(r); break;
            case GameCommand gc when p.Room is { Session: not null } r: ApplyCommand(r, p, gc); break;
            default: p.Send(new ErrorMsg("unexpected", $"Not allowed now: {m.GetType().Name}")); break;
        }
    }

    /// <summary>Moves a reconnecting player's identity onto the new connection and replays what it missed.</summary>
    void Resume(Player seat, Player fresh, Hello hello)
    {
        var old = seat.Conn;
        seat.Conn = fresh.Conn;
        seat.DroppedAt = null;
        seat.LastSeenTicks = Environment.TickCount64;
        _players.Remove(fresh.Id);
        // Rewire the fresh connection's events to the resumed identity.
        var conn = fresh.Conn!;
        fresh.Conn = null;
        Hook(seat, conn);
        if (old != null && old != conn) _ = old.DisposeAsync();

        seat.Send(new Welcome(seat.Id, ServerName, seat.Token));
        _log($"player {seat.Id} ({seat.Name}) resumed from {conn.RemoteEndPoint}");
        if (seat.Room is not { } room) return;
        if (room.Session != null && room.Setups != null && room.SideOf.TryGetValue(seat.Id, out var side))
        {
            int from = hello.LastSeq ?? -1;
            if (from < 0) seat.Send(new GameStarted(side, room.Setups[side]));
            foreach (var u in room.Log.Where(u => u.Seq > from))
                seat.Send(u with { ClientSeq = null });
        }
        BroadcastRoom(room);
        seat.Send(new Resumed(room.Id, room.Seq));
    }

    RoomList BuildRoomList() => new(_rooms.Values.Select(r =>
        new RoomSummary(r.Id, r.Name, r.Factory.Mode, r.Players.Count, r.MaxPlayers, r.Password != null, r.Session != null)).ToList());

    void CreateRoomFor(Player p, CreateRoom cr)
    {
        if (!_factories.TryGetValue(cr.Mode, out var factory)) { p.Send(new ErrorMsg("mode", $"Unknown mode {cr.Mode}")); return; }
        LeaveCurrentRoom(p);
        var max = Math.Clamp(cr.MaxPlayers, factory.MinPlayers, factory.MaxPlayers);
        var name = string.IsNullOrWhiteSpace(cr.Name) ? $"{p.Name}'s game" : cr.Name.Trim();
        var room = new Room(_nextRoomId++, name[..Math.Min(name.Length, 48)], factory, max,
            string.IsNullOrEmpty(cr.Password) ? null : cr.Password, cr.Settings);
        _rooms[room.Id] = room;
        AddToRoom(p, room);
        _log($"room {room.Id} '{room.Name}' ({factory.Mode}) created by {p.Id}");
    }

    void JoinRoomFor(Player p, JoinRoom jr)
    {
        if (!_rooms.TryGetValue(jr.RoomId, out var room)) { p.Send(new ErrorMsg("room", "That game no longer exists.")); return; }
        if (room.Session != null) { p.Send(new ErrorMsg("room", "That game has already started.")); return; }
        if (room.Players.Count >= room.MaxPlayers) { p.Send(new ErrorMsg("room", "That game is full.")); return; }
        if (room.Password != null && room.Password != jr.Password) { p.Send(new ErrorMsg("password", "Wrong password.")); return; }
        LeaveCurrentRoom(p);
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

    void LeaveCurrentRoom(Player p)
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
        if (room.Players.Count < room.Factory.MinPlayers) { host.Send(new ErrorMsg("start", "Not enough players.")); return; }
        if (room.Players.Any(q => q != host && !q.Ready)) { host.Send(new ErrorMsg("start", "Not everyone is ready.")); return; }
        if (room.Players.Any(q => q.DroppedAt != null)) { host.Send(new ErrorMsg("start", "A player is reconnecting.")); return; }

        var seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var bySide = room.Players.Select((q, i) => new RoomPlayer(q.Id, q.Name, i, q.Ready, q == host, q.Options)).ToList();
        room.Session = room.Factory.Create();
        room.Setups = room.Session.Start(bySide, room.Settings, seed);
        room.Log.Clear();
        room.Seq = 0;
        room.SideOf.Clear();
        for (int i = 0; i < room.Players.Count; i++)
        {
            room.SideOf[room.Players[i].Id] = i;
            room.Players[i].Send(new GameStarted(i, room.Setups[i]));
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
                p.Send(new CommandRejected(gc.ClientSeq, rej.Reason));
                break;
            case SessionResult.Accepted acc:
                var seq = ++room.Seq;
                var update = new GameUpdate(seq, null, side, acc.Events, acc.StateHash);
                room.Log.Add(update);
                foreach (var q in room.Players)
                    q.Send(q == p ? update with { ClientSeq = gc.ClientSeq } : update);
                if (acc.Ended != null) EndGame(room, acc.Ended);
                break;
        }
    }

    void EndGame(Room room, GameEnded ended)
    {
        foreach (var q in room.Players) { q.Send(ended); q.Ready = false; }
        room.Session = null;
        room.Setups = null;
        room.Log.Clear();
        room.SideOf.Clear();
        room.Seq = 0;
        _log($"room {room.Id} game ended: winner side {ended.WinnerSide?.ToString() ?? "none"} ({ended.Reason})");
        // Seats of players who never came back are released now that the game is over.
        foreach (var q in room.Players.Where(q => q.DroppedAt != null).ToList()) Forget(q);
        if (room.Players.Count > 0) BroadcastRoom(room);
        else _rooms.Remove(room.Id);
    }

    void BroadcastRoom(Room room)
    {
        var state = new RoomState(room.Id, room.Name, room.Factory.Mode, room.MaxPlayers,
            room.Players.Select((q, i) => new RoomPlayer(q.Id, q.Name, i, q.Ready, q == room.Host, q.Options, q.DroppedAt == null)).ToList(),
            room.Settings, room.Session != null);
        foreach (var q in room.Players) q.Send(state);
    }

    void OnClosed(Player p, Connection c, Exception? error)
    {
        lock (_lock)
        {
            if (p.Conn != c) return; // superseded by a resumed connection
            p.Conn = null;
            if (p.Room is { Session: not null } room && room.SideOf.ContainsKey(p.Id))
            {
                // Hold the seat so the player can reconnect into the running game.
                p.DroppedAt = DateTime.UtcNow;
                _log($"player {p.Id} dropped from running game in room {room.Id}; holding seat for {ReconnectGrace.TotalSeconds:0}s");
                BroadcastRoom(room);
            }
            else Forget(p);
        }
        _log($"connection {p.Id} closed{(error != null ? $": {error.Message}" : "")}");
    }

    void Forget(Player p)
    {
        LeaveCurrentRoom(p);
        _players.Remove(p.Id);
        _byToken.Remove(p.Token);
    }

    async Task SweepLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            List<Connection> idle = new();
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                foreach (var p in _players.Values.ToList())
                {
                    if (p.DroppedAt is { } t && now - t > ReconnectGrace)
                    {
                        _log($"player {p.Id} did not reconnect; releasing seat");
                        p.DroppedAt = null;
                        Forget(p);
                    }
                    else if (p.Conn != null && Environment.TickCount64 - p.LastSeenTicks > IdleTimeout.TotalMilliseconds)
                        idle.Add(p.Conn);
                }
            }
            foreach (var c in idle) await c.DisposeAsync();
        }
    }

    /// <summary>Snapshot for LAN beacons and master-server heartbeats.</summary>
    public (int Players, int OpenRooms, int Rooms) Stats()
    {
        lock (_lock)
            return (_players.Values.Count(p => p.Authenticated), _rooms.Values.Count(r => r.Session == null && r.Players.Count < r.MaxPlayers), _rooms.Count);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (var t in new[] { _acceptLoop, _sweepLoop })
            if (t != null) try { await t; } catch { }
        List<Connection> conns;
        lock (_lock) conns = _players.Values.Select(p => p.Conn).OfType<Connection>().ToList();
        foreach (var c in conns) await c.DisposeAsync();
        lock (_lock) foreach (var h in _udpHosts) h.AcceptConnections = false;
    }
}
