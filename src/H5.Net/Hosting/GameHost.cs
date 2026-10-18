using System.Net;
using H5.Net.Discovery;
using H5.Net.Master;
using H5.Net.Nat;
using H5.Net.Transport;

namespace H5.Net.Hosting;

public sealed record GameHostOptions
{
    public string Name { get; init; } = "H5Reborn";
    /// <summary>One port number serves TCP (native + WebSocket) and UDP (punched/relayed).</summary>
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    public IPAddress Bind { get; init; } = IPAddress.Any;
    public string Fingerprint { get; init; } = "";
    /// <summary>Answer LAN discovery probes.</summary>
    public bool LanDiscovery { get; init; } = true;
    /// <summary>Override the LAN beacon socket (tests use loopback); null = all interfaces on the default port.</summary>
    public IPEndPoint? LanBind { get; init; }
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Everything a player-hosted (or dedicated) game needs, in one place: the lobby
/// server listening on TCP (native frames and WebSocket upgrade) and UDP (reliable
/// channels), the LAN beacon, and optional online publishing through a master server
/// with router port mapping.
/// </summary>
public sealed class GameHost : IAsyncDisposable
{
    readonly GameHostOptions _options;
    LanBeacon? _beacon;
    OnlineHost? _online;
    MasterClient? _master;
    PortMapper? _mapper;
    readonly string _id = Guid.NewGuid().ToString("N");

    public LobbyServer Server { get; }
    public UdpHost Udp { get; }
    public int Port => Server.Port;
    public OnlineHost? Online => _online;
    public string? PortMappingMethod => _mapper?.Method;
    public event Action<string>? Log;

    GameHost(GameHostOptions options, LobbyServer server, UdpHost udp)
    {
        _options = options;
        Server = server;
        Udp = udp;
    }

    public static GameHost Start(GameHostOptions options, IEnumerable<IGameSessionFactory> factories, Action<string>? log = null)
    {
        var server = new LobbyServer(factories, options.Name, options.Fingerprint, log) { ReconnectGrace = options.ReconnectGrace };
        server.Start(options.Port, options.Bind);
        UdpHost udp;
        try { udp = UdpHost.Bind(new IPEndPoint(options.Bind, server.Port)); }
        catch (System.Net.Sockets.SocketException) { udp = UdpHost.Bind(new IPEndPoint(options.Bind, 0)); }
        server.AttachUdp(udp);
        var host = new GameHost(options, server, udp);
        if (log != null) host.Log += log;
        if (options.LanDiscovery)
        {
            try { host._beacon = new LanBeacon(host.LanInfo, options.LanBind); }
            catch (System.Net.Sockets.SocketException e) { host.Log?.Invoke($"LAN discovery unavailable: {e.Message}"); }
        }
        return host;
    }

    LanGameInfo LanInfo()
    {
        var (players, open, rooms) = Server.Stats();
        return new LanGameInfo(_id, Server.ServerName, Server.Port, ProtocolInfo.Version, _options.Fingerprint, players, open, rooms);
    }

    /// <summary>
    /// Lists this game on a master server so internet players can find it and join by code.
    /// <paramref name="mapPorts"/> asks the router (NAT-PMP/UPnP) to forward the port.
    /// </summary>
    public async Task<OnlineHost> PublishOnlineAsync(Uri masterUrl, string mode, int maxPlayers, bool hasPassword,
        bool mapPorts, CancellationToken ct = default)
    {
        await UnpublishAsync();
        if (mapPorts)
        {
            _mapper = new PortMapper();
            if (await _mapper.MapAsync(Server.Port, $"H5Reborn {Server.ServerName}", ct)) Log?.Invoke($"Router port {Server.Port} opened via {_mapper.Method}");
            else Log?.Invoke("Router port mapping unavailable; using NAT traversal");
        }
        _master = new MasterClient(masterUrl);
        var (players, open, _) = Server.Stats();
        _online = await OnlineHost.PublishAsync(_master, Udp,
            new LobbyAd(Server.ServerName, mode, maxPlayers, hasPassword, ProtocolInfo.Version, _options.Fingerprint, players, open),
            () => { var s = Server.Stats(); return new LobbyHeartbeat(s.Players, s.OpenRooms); }, ct: ct);
        _online.Log += s => Log?.Invoke(s);
        _online.RtcChannel = ch => Server.AttachChannel(ch);
        Log?.Invoke($"Published online with join code {_online.Lobby.Code}");
        return _online;
    }

    public async Task UnpublishAsync()
    {
        if (_online != null) { await _online.DisposeAsync(); _online = null; }
        _master?.Dispose(); _master = null;
        if (_mapper != null) { await _mapper.DisposeAsync(); _mapper = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await UnpublishAsync();
        if (_beacon != null) await _beacon.DisposeAsync();
        await Server.DisposeAsync();
        await Udp.DisposeAsync();
    }
}
