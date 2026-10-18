using System.Net;
using System.Text.Json;
using Godot;
using FileAccess = Godot.FileAccess;
using H5.Net;
using H5.Net.Discovery;
using H5.Net.Hosting;
using H5.Net.Master;
using H5.Net.Transport;

namespace H5Reborn.Ui.Net;

/// <summary>
/// Client-side multiplayer session shared by the lobby screens. Owns the NetClient,
/// optionally an in-process <see cref="GameHost"/> (any player can host: LAN beacon,
/// TCP/WebSocket/UDP on one port, optional online listing with a join code), the LAN
/// browser and the online (master server) lobby list. Messages are polled on the main
/// thread every frame.
///
/// Ways to join: pick a LAN game (found automatically), type an IP[:port] or a
/// ws:// / wss:// URL, or enter an online join code (NAT hole punching, falling back
/// to the master's relay).
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
    GameHost? _host;
    UdpHost? _udp;
    LanBrowser? _lan;
    MasterClient? _master;
    double _onlineRefresh = 999;
    bool _onlineFetching;

    public bool Connected => _client?.Connected ?? false;
    public bool Reconnecting => _client?.Reconnecting ?? false;
    public bool IsHosting => _host != null;
    public int PlayerId => _client?.PlayerId ?? 0;
    public int PingMs => _client?.PingMs ?? -1;
    public string PlayerName { get; set; }
    public string? ServerAddress { get; private set; }
    public RoomList? Rooms { get; private set; }
    public RoomState? Room { get; private set; }
    public GameStarted? Started { get; private set; }
    /// <summary>The online join code while this client hosts a published game.</summary>
    public string? OnlineCode => _host?.Online?.Lobby.Code;

    /// <summary>Persistent network settings (user://net_settings.json).</summary>
    public NetSettings Settings { get; private set; }

    /// <summary>LAN games heard recently (empty until <see cref="StartLanBrowsing"/>).</summary>
    public IReadOnlyList<LanGameInfo> LanGames => _lan?.Games ?? Array.Empty<LanGameInfo>();
    /// <summary>Latest online lobby list from the master server (if one is configured).</summary>
    public IReadOnlyList<OnlineLobby> OnlineLobbies { get; private set; } = Array.Empty<OnlineLobby>();
    public string? OnlineError { get; private set; }

    /// <summary>The running networked combat, created as soon as GameStarted arrives so no update is missed.</summary>
    public Combat.NetCombatSession? Combat { get; private set; }

    public event Action<Message>? MessageReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Disconnected;
    /// <summary>Errors worth a message box (version/data mismatch, lost connection, failed join).</summary>
    public event Action<string, string>? ErrorRaised;

    public LobbyService()
    {
        DevArgs.Parse();
        Settings = NetSettings.Load();
        PlayerName = Settings.PlayerName ?? (System.Environment.UserName is { Length: > 0 } u ? u : "Player");
        if (DevArgs.Master != null) Settings = Settings with { MasterUrl = DevArgs.Master };
    }

    public override void _Process(double delta)
    {
        _client?.Poll();
        _onlineRefresh += delta;
        if (_onlineRefresh > 4 && _lan != null && !_onlineFetching && !string.IsNullOrWhiteSpace(Settings.MasterUrl))
        {
            _onlineRefresh = 0;
            _ = RefreshOnline();
        }
    }

    // ---- browsing ---------------------------------------------------------------------

    /// <summary>Starts LAN discovery and periodic online lobby refresh (while a browser screen is open).</summary>
    public void StartLanBrowsing()
    {
        if (_lan != null) return;
        try
        {
            _lan = DevArgs.NetLoopback
                ? new LanBrowser(new[] { new IPEndPoint(IPAddress.Loopback, LanDiscovery.DefaultPort) }, TimeSpan.FromSeconds(1))
                : new LanBrowser();
        }
        catch (Exception e) { GD.Print($"[net] LAN discovery unavailable: {e.Message}"); }
        _onlineRefresh = 999;
    }

    public async void StopLanBrowsing()
    {
        var lan = _lan;
        _lan = null;
        if (lan != null) await lan.DisposeAsync();
    }

    async Task RefreshOnline()
    {
        _onlineFetching = true;
        try
        {
            OnlineLobbies = await Master().ListAsync(Game.Instance.Install.DataFingerprint());
            OnlineError = null;
        }
        catch (Exception e)
        {
            OnlineLobbies = Array.Empty<OnlineLobby>();
            OnlineError = $"Online lobby list unavailable ({e.Message})";
        }
        finally { _onlineFetching = false; }
    }

    MasterClient Master()
    {
        var uri = MasterClient.ParseAddress(Settings.MasterUrl!);
        if (_master == null || _master.BaseUri != uri) { _master?.Dispose(); _master = new MasterClient(uri); }
        return _master;
    }

    // ---- hosting ----------------------------------------------------------------------

    /// <summary>Hosts a game on this computer (LAN-visible) and connects to it.</summary>
    public async Task HostAsync(int port = ProtocolInfo.DefaultPort, bool publishOnline = false)
    {
        await DisconnectAsync();
        var install = Game.Instance.Install;
        _host = GameHost.Start(new GameHostOptions
        {
            Name = $"{PlayerName}'s server",
            Port = port,
            Bind = DevArgs.NetLoopback ? IPAddress.Loopback : IPAddress.Any,
            Fingerprint = install.DataFingerprint(),
            LanBind = DevArgs.NetLoopback ? new IPEndPoint(IPAddress.Loopback, LanDiscovery.DefaultPort) : null,
        }, new IGameSessionFactory[] { new H5.Duel.DuelSessionFactory(Game.Instance.Db) }, s => GD.Print($"[server] {s}"));
        var hostPort = _host.Port;
        await ConnectChannelAsync($"127.0.0.1:{hostPort}",
            async ct => await TcpFrameChannel.ConnectAsync("127.0.0.1", hostPort, TimeSpan.FromSeconds(5), ct));
        if (publishOnline) await PublishOnlineAsync(false);
    }

    /// <summary>Lists the hosted game on the configured master server and returns the join code.</summary>
    public async Task<string> PublishOnlineAsync(bool hasPassword)
    {
        if (_host == null) throw new InvalidOperationException("Not hosting");
        if (string.IsNullOrWhiteSpace(Settings.MasterUrl)) throw new InvalidOperationException("No online server is configured. Set one under Online server.");
        var online = await _host.PublishOnlineAsync(MasterClient.ParseAddress(Settings.MasterUrl), "duel", 2, hasPassword,
            mapPorts: Settings.MapRouterPorts && !DevArgs.NetLoopback);
        StatusChanged?.Invoke($"Online game listed. Join code: {online.Lobby.Code}");
        GD.Print($"[net] online join code {online.Lobby.Code}");
        return online.Lobby.Code;
    }

    // ---- joining ----------------------------------------------------------------------

    /// <summary>
    /// Joins by address: "host", "host:port", "[ipv6]:port" (TCP, falling back to UDP),
    /// or a "ws://" / "wss://" URL (WebSocket, e.g. a server behind a TLS proxy).
    /// </summary>
    public async Task ConnectAsync(string host, int port = ProtocolInfo.DefaultPort)
    {
        await DisconnectAsync();
        if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(host);
            await ConnectChannelAsync(uri.Authority, async ct => await WebSocketFrameChannel.ConnectAsync(uri, TimeSpan.FromSeconds(8), ct));
            return;
        }
        var h = host.Trim('[', ']');
        await ConnectChannelAsync($"{host}:{port}", async ct =>
        {
            try { return await TcpFrameChannel.ConnectAsync(h, port, TimeSpan.FromSeconds(5), ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // TCP blocked (e.g. only UDP forwarded): try the host's reliable-UDP listener on the same port.
                var addr = (await Dns.GetHostAddressesAsync(h, ct)).First();
                return await Udp(addr.AddressFamily).ConnectAsync(new UdpPath(new IPEndPoint(addr, port)), TimeSpan.FromSeconds(4), ct);
            }
        });
    }

    /// <summary>Joins an online game by its join code through the master server.</summary>
    public async Task JoinOnlineAsync(string code)
    {
        await DisconnectAsync();
        if (string.IsNullOrWhiteSpace(Settings.MasterUrl)) throw new InvalidOperationException("No online server is configured. Set one under Online server.");
        var mc = Master();
        var normalized = JoinCodes.Normalize(code);
        bool relayed = false;
        await ConnectChannelAsync($"online game {normalized}", async ct =>
        {
            var r = await OnlineConnector.ConnectAsync(mc, Udp(System.Net.Sockets.AddressFamily.InterNetwork), normalized, ct: ct);
            relayed = r.Relayed;
            return r.Channel;
        });
        StatusChanged?.Invoke(relayed ? "Connected through the online relay." : "Connected directly (peer to peer).");
    }

    UdpHost Udp(System.Net.Sockets.AddressFamily family) =>
        _udp ??= UdpHost.Bind(new IPEndPoint(
            DevArgs.NetLoopback ? IPAddress.Loopback
            : family == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

    async Task ConnectChannelAsync(string label, Func<CancellationToken, Task<IFrameChannel>> connector)
    {
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
        var c = new NetClient();
        c.MessageReceived += OnMessage;
        c.Disconnected += reason =>
        {
            Room = null; Rooms = null; Started = null;
            Disconnected?.Invoke(reason);
        };
        c.ConnectionLost += reason => StatusChanged?.Invoke($"Connection lost ({reason}). Reconnecting...");
        c.ConnectionRestored += () => StatusChanged?.Invoke("Reconnected.");
        StatusChanged?.Invoke($"Connecting to {label}...");
        await c.ConnectAsync(connector, PlayerName, Game.Instance.Install.DataFingerprint());
        _client = c;
        ServerAddress = label;
    }

    public async Task DisconnectAsync()
    {
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
        if (_host != null) { await _host.DisposeAsync(); _host = null; }
        if (_udp != null) { await _udp.DisposeAsync(); _udp = null; }
        Room = null; Rooms = null; Started = null; ServerAddress = null;
    }

    void OnMessage(Message m)
    {
        switch (m)
        {
            case Welcome w: StatusChanged?.Invoke($"Connected to {w.ServerName}"); Send(new ListRooms()); break;
            case RoomList rl: Rooms = rl; break;
            case RoomState rs: Room = rs; break;
            case GameStarted gs:
                // After a reconnect GameStarted is resent only if the client lost its game state entirely.
                Started = gs;
                Combat = new Combat.NetCombatSession(Game.Instance.Db, gs,
                    cmd => _client?.SendCommand(cmd) ?? 0, () => _client?.PendingCommands ?? 0);
                break;
            case GameEnded: Started = null; break;
            case ErrorMsg e:
                StatusChanged?.Invoke(e.Text);
                if (e.Code is "version" or "data_mismatch") ErrorRaised?.Invoke("Cannot join", e.Text);
                break;
        }
        Combat?.OnMessage(m);
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
        Settings = Settings with { PlayerName = PlayerName };
        Settings.Save();
    }

    public void SetMasterUrl(string? url)
    {
        Settings = Settings with { MasterUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim() };
        Settings.Save();
        OnlineLobbies = Array.Empty<OnlineLobby>();
        _onlineRefresh = 999;
    }

    public override void _ExitTree()
    {
        _ = DisconnectAsync();
        StopLanBrowsing();
        _master?.Dispose();
    }
}

/// <summary>
/// Network settings saved in user://net_settings.json. MasterUrl is the online lobby
/// server ("host", "host:port" or a URL); the H5_MASTER environment variable or the
/// --master= dev switch override it. No master is configured by default: the user
/// runs their own (see docs/MULTIPLAYER.md).
/// </summary>
public sealed record NetSettings(string? PlayerName = null, string? MasterUrl = null, bool MapRouterPorts = true)
{
    const string FilePath = "user://net_settings.json";

    public static NetSettings Load()
    {
        NetSettings s = new();
        if (FileAccess.FileExists(FilePath))
        {
            using var f = FileAccess.Open(FilePath, FileAccess.ModeFlags.Read);
            try { s = JsonSerializer.Deserialize<NetSettings>(f?.GetAsText() ?? "") ?? new(); } catch (JsonException) { }
        }
        else if (FileAccess.FileExists("user://player_name.txt"))
        {
            using var f = FileAccess.Open("user://player_name.txt", FileAccess.ModeFlags.Read);
            s = s with { PlayerName = f?.GetAsText().Trim() };
        }
        var env = System.Environment.GetEnvironmentVariable("H5_MASTER");
        if (!string.IsNullOrWhiteSpace(env)) s = s with { MasterUrl = env };
        return s;
    }

    public void Save()
    {
        using var f = FileAccess.Open(FilePath, FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(this));
    }
}
