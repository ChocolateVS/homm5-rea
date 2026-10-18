using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using H5.Net.Master;
using H5.Net.Transport;

namespace H5.Master;

/// <summary>
/// UDP side of the master (one socket, shared via <see cref="UdpHost"/>):
/// - learns each host's public endpoint from its periodic "register" datagrams,
///   which also keep the host's NAT mapping towards the master open;
/// - introduces joiners to hosts so both can punch towards each other;
/// - relays datagrams for pairs whose NATs refuse a direct path;
/// - opens reliable channels to hosts on behalf of WebSocket (browser) joiners,
///   which <see cref="WebSocketBridge"/> splices onto the browser's socket;
/// - forwards WebRTC signalling between WebSocket joiners and hosts.
/// </summary>
public sealed class RendezvousService : IHostedService, IAsyncDisposable
{
    sealed class RelaySession
    {
        public required ulong Id;
        public required string LobbyId;
        public IPEndPoint Host = null!;
        public required IPEndPoint Joiner;
        public long LastActivity = Environment.TickCount64;
        public long WindowStart = Environment.TickCount64;
        public long WindowBytes;
    }

    readonly MasterOptions _options;
    readonly LobbyRegistry _registry;
    readonly ILogger<RendezvousService> _log;
    readonly ConcurrentDictionary<ulong, RelaySession> _sessions = new();
    readonly ConcurrentDictionary<IPAddress, (long WindowStart, int Count)> _rate = new();
    readonly ConcurrentDictionary<ulong, Action<RvMessage>> _signalSinks = new();
    readonly CancellationTokenSource _cts = new();
    UdpHost? _udp;
    Task? _sweep;

    public int Port { get; private set; }
    public int RelaySessionCount => _sessions.Count;

    public RendezvousService(MasterOptions options, LobbyRegistry registry, ILogger<RendezvousService> log)
    {
        _options = options;
        _registry = registry;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _udp = UdpHost.Bind(new IPEndPoint(IPAddress.Parse(_options.UdpBind), _options.UdpPort));
        _udp.Intercept = Intercept;
        Port = _udp.LocalEndPoint.Port;
        _sweep = Task.Run(SweepLoop);
        _log.LogInformation("rendezvous/relay listening on udp {Endpoint}", _udp.LocalEndPoint);
        return Task.CompletedTask;
    }

    bool Intercept(IPEndPoint from, byte[] d)
    {
        switch ((UdpKind)d[0])
        {
            case UdpKind.Relay when d.Length >= 9: Relay(from, d); return true;
            case UdpKind.Rendezvous:
                if (Allow(from.Address)) Rendezvous(from, RvMessage.Decode(d.AsSpan(1)));
                return true;
            case UdpKind.Punch when d.Length >= 9:
                // Lets clients verify the master is reachable over UDP.
                _udp!.SendRaw(from, UdpPacket.Token(UdpKind.PunchAck, BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(1))));
                return true;
            default:
                return false; // reliable-channel traffic of WebSocket bridges
        }
    }

    bool Allow(IPAddress a)
    {
        var now = Environment.TickCount64;
        var v = _rate.AddOrUpdate(a, _ => (now, 1), (_, old) => now - old.WindowStart > 1000 ? (now, 1) : (old.WindowStart, old.Count + 1));
        return v.Count <= 30;
    }

    void Rendezvous(IPEndPoint from, RvMessage? m)
    {
        if (m == null) return;
        switch (m.T)
        {
            case RvMessage.Register:
                {
                    var e = m.Lobby != null ? _registry.Authorize(m.Lobby, m.Token) : null;
                    if (e == null) { Reply(from, new RvMessage { T = RvMessage.Fail, Error = "unknown lobby" }); return; }
                    e.HostPublic = from;
                    e.HostLocal = (m.Local ?? Array.Empty<string>()).Take(8).ToArray();
                    foreach (var s in _sessions.Values.Where(s => s.LobbyId == e.Id)) s.Host = from;
                    Reply(from, new RvMessage { T = RvMessage.Registered, Public = from.ToString() });
                    return;
                }
            case RvMessage.Join:
                {
                    var e = Find(m);
                    if (e == null) { Reply(from, new RvMessage { T = RvMessage.Fail, Nonce = m.Nonce, Error = "No online game has that code." }); return; }
                    if (e.HostPublic == null) { Reply(from, new RvMessage { T = RvMessage.Fail, Nonce = m.Nonce, Error = "The host is not reachable yet; try again in a few seconds." }); return; }
                    var session = NewSession(e, from);
                    var joinerLocal = (m.Local ?? Array.Empty<string>()).Take(8).ToArray();
                    Reply(e.HostPublic, new RvMessage
                    {
                        T = RvMessage.Incoming, Lobby = e.Id, Session = session.Id, Nonce = m.Nonce,
                        Peer = new[] { from.ToString() }.Concat(joinerLocal).ToArray(),
                    });
                    Reply(from, new RvMessage
                    {
                        T = RvMessage.PeerInfo, Lobby = e.Id, Session = session.Id, Nonce = m.Nonce, Public = from.ToString(),
                        Peer = new[] { e.HostPublic.ToString() }.Concat(e.HostLocal).ToArray(),
                    });
                    _log.LogInformation("join {Lobby} from {Joiner}, relay session {Session:x}", e.Id, from, session.Id);
                    return;
                }
            case RvMessage.RtcAnswer or RvMessage.RtcIce or RvMessage.RtcUnsupported:
                // Host → WebSocket joiner signalling.
                if (_sessions.TryGetValue(m.Session, out var rs) && from.Equals(rs.Host) && _signalSinks.TryGetValue(m.Session, out var sink))
                    sink(m);
                return;
        }
    }

    LobbyRegistry.Entry? Find(RvMessage m) =>
        m.Code != null ? _registry.GetByCode(m.Code) : m.Lobby != null ? _registry.Get(m.Lobby) : null;

    RelaySession NewSession(LobbyRegistry.Entry e, IPEndPoint joiner)
    {
        if (_sessions.Count >= _options.MaxRelaySessions) Sweep(force: true);
        var s = new RelaySession
        {
            Id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1,
            LobbyId = e.Id,
            Host = e.HostPublic!,
            Joiner = joiner,
        };
        _sessions[s.Id] = s;
        return s;
    }

    void Relay(IPEndPoint from, byte[] d)
    {
        var id = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(1));
        if (!_sessions.TryGetValue(id, out var s)) return;
        IPEndPoint to;
        if (from.Equals(s.Host)) to = s.Joiner;
        else if (from.Equals(s.Joiner)) to = s.Host;
        else return;
        var now = Environment.TickCount64;
        s.LastActivity = now;
        if (now - s.WindowStart > 1000) { s.WindowStart = now; s.WindowBytes = 0; }
        s.WindowBytes += d.Length;
        if (s.WindowBytes > _options.RelayKilobytesPerSecond * 1024L) return; // over budget: drop, the reliable layer retries
        _udp!.SendRaw(to, d);
    }

    void Reply(IPEndPoint to, RvMessage m) => _udp!.SendRendezvous(to, m.Encode());

    /// <summary>
    /// Opens a reliable channel from the master to a lobby's host for a WebSocket joiner.
    /// The host already accepts packets from the master (it registers here), so no punching is needed.
    /// </summary>
    public async Task<ReliableUdpChannel> OpenBridgeAsync(string codeOrId, bool isCode, CancellationToken ct)
    {
        var e = (isCode ? _registry.GetByCode(codeOrId) : _registry.Get(codeOrId))
                ?? throw new KeyNotFoundException("No online game has that code.");
        if (e.HostPublic == null) throw new InvalidOperationException("The host is not reachable yet; try again in a few seconds.");
        return await _udp!.ConnectAsync(new UdpPath(e.HostPublic), TimeSpan.FromSeconds(6), ct);
    }

    /// <summary>Starts a WebRTC signalling session between a WebSocket joiner and a lobby host.</summary>
    public ulong OpenSignalling(string codeOrId, bool isCode, Action<RvMessage> toJoiner)
    {
        var e = (isCode ? _registry.GetByCode(codeOrId) : _registry.Get(codeOrId))
                ?? throw new KeyNotFoundException("No online game has that code.");
        if (e.HostPublic == null) throw new InvalidOperationException("The host is not reachable yet.");
        var s = NewSession(e, new IPEndPoint(IPAddress.None, 0)); // WebSocket joiner: no UDP endpoint
        _signalSinks[s.Id] = toJoiner;
        return s.Id;
    }

    /// <summary>Forwards a WebSocket joiner's signalling message to the host.</summary>
    public void Signal(ulong session, RvMessage m)
    {
        if (!_sessions.TryGetValue(session, out var s)) return;
        s.LastActivity = Environment.TickCount64;
        Reply(s.Host, m with { Session = session, Lobby = s.LobbyId });
    }

    public void CloseSignalling(ulong session)
    {
        _signalSinks.TryRemove(session, out _);
        _sessions.TryRemove(session, out _);
    }

    async Task SweepLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token); } catch (OperationCanceledException) { break; }
            Sweep(force: false);
            _registry.Sweep();
            foreach (var kv in _rate.Where(kv => Environment.TickCount64 - kv.Value.WindowStart > 10000).ToList())
                _rate.TryRemove(kv.Key, out _);
        }
    }

    void Sweep(bool force)
    {
        var now = Environment.TickCount64;
        var idle = (long)_options.RelayIdleTimeout.TotalMilliseconds;
        foreach (var s in _sessions.Values)
            if (!_signalSinks.ContainsKey(s.Id) && (now - s.LastActivity > (force ? idle / 10 : idle) || _registry.Get(s.LobbyId) == null))
                _sessions.TryRemove(s.Id, out _);
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        if (_udp != null) await _udp.DisposeAsync();
        if (_sweep != null) try { await _sweep; } catch { }
    }
}
