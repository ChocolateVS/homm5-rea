using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using H5.Net.Transport;

namespace H5.Net.Master;

/// <summary>HTTP client for the master server's lobby list.</summary>
public sealed class MasterClient : IDisposable
{
    readonly HttpClient _http;
    MasterInfo? _info;

    public Uri BaseUri { get; }

    public MasterClient(Uri baseUri, HttpClient? http = null)
    {
        BaseUri = baseUri;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>Accepts "host", "host:port" or a full URL; the default HTTP port is 27620.</summary>
    public static Uri ParseAddress(string s)
    {
        s = s.Trim();
        if (!s.Contains("://")) s = "http://" + s;
        var u = new UriBuilder(s);
        if (u.Uri.IsDefaultPort && !s.Contains($":{u.Port}")) u.Port = 27620;
        return u.Uri;
    }

    public async Task<MasterInfo> InfoAsync(CancellationToken ct = default) =>
        _info ??= await _http.GetFromJsonAsync<MasterInfo>(new Uri(BaseUri, "/api/info"), RvMessage.Json, ct)
                  ?? throw new InvalidDataException("bad master info");

    /// <summary>The master's UDP rendezvous/relay endpoint (same host as the HTTP API).</summary>
    public async Task<IPEndPoint> UdpEndPointAsync(CancellationToken ct = default)
    {
        var info = await InfoAsync(ct);
        var addrs = await Dns.GetHostAddressesAsync(BaseUri.DnsSafeHost, ct);
        var a = addrs.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addrs.First();
        return new IPEndPoint(a, info.UdpPort);
    }

    public async Task<IReadOnlyList<OnlineLobby>> ListAsync(string? fingerprint, CancellationToken ct = default)
    {
        var q = $"/api/lobbies?protocol={ProtocolInfo.Version}" + (fingerprint != null ? $"&fingerprint={Uri.EscapeDataString(fingerprint)}" : "");
        return await _http.GetFromJsonAsync<List<OnlineLobby>>(new Uri(BaseUri, q), RvMessage.Json, ct) ?? new();
    }

    public async Task<OnlineLobby?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        var r = await _http.GetAsync(new Uri(BaseUri, $"/api/lobbies/code/{Uri.EscapeDataString(JoinCodes.Normalize(code))}"), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<OnlineLobby>(RvMessage.Json, ct);
    }

    internal async Task<LobbyCreated> CreateAsync(LobbyAd ad, CancellationToken ct)
    {
        var r = await _http.PostAsJsonAsync(new Uri(BaseUri, "/api/lobbies"), ad, RvMessage.Json, ct);
        if (r.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("The master server is rate-limiting this address; try again in a minute.");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"The master server refused the game ({(int)r.StatusCode}).");
        return await r.Content.ReadFromJsonAsync<LobbyCreated>(RvMessage.Json, ct) ?? throw new InvalidDataException("bad response");
    }

    internal async Task<bool> HeartbeatAsync(LobbyCreated lobby, LobbyHeartbeat hb, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, new Uri(BaseUri, $"/api/lobbies/{lobby.Id}"))
        { Content = JsonContent.Create(hb, options: RvMessage.Json) };
        req.Headers.Add("X-Host-Token", lobby.HostToken);
        var r = await _http.SendAsync(req, ct);
        return r.IsSuccessStatusCode;
    }

    internal async Task DeleteAsync(LobbyCreated lobby)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, new Uri(BaseUri, $"/api/lobbies/{lobby.Id}"));
        req.Headers.Add("X-Host-Token", lobby.HostToken);
        try { await _http.SendAsync(req); } catch { }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>LAN unicast addresses of this machine, advertised so peers behind the same NAT connect locally.</summary>
    public static string[] LocalEndpoints(int port) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new IPEndPoint(a.Address, port).ToString())
            .Take(6).ToArray();
}

/// <summary>
/// Publishes a hosted game on the master and keeps it alive: HTTP heartbeats for the
/// listing, UDP registrations that keep the host's NAT mapping open, and outgoing
/// punches towards each announced joiner. Joiners then connect to the host's UdpHost
/// (attached to its LobbyServer) directly or through the relay.
/// </summary>
public sealed class OnlineHost : IAsyncDisposable
{
    readonly MasterClient _master;
    readonly UdpHost _udp;
    readonly Func<LobbyHeartbeat> _status;
    readonly CancellationTokenSource _cts = new();
    readonly IPEndPoint _masterUdp;
    readonly string[] _local;
    Task? _loop;

    public LobbyCreated Lobby { get; }
    public string? PublicEndPoint { get; private set; }
    public event Action<string>? Log;
    /// <summary>Answers WebRTC offers from browser joiners (default: "unsupported", so they use the WebSocket bridge).</summary>
    public IRtcAnswerer Rtc { get; set; } = new NoRtcAnswerer();
    /// <summary>Receives DataChannels opened by <see cref="Rtc"/> (wire to LobbyServer.AttachChannel).</summary>
    public Action<IFrameChannel>? RtcChannel { get; set; }

    OnlineHost(MasterClient master, UdpHost udp, LobbyCreated lobby, IPEndPoint masterUdp, Func<LobbyHeartbeat> status, string[] local)
    {
        _master = master; _udp = udp; Lobby = lobby; _masterUdp = masterUdp; _status = status; _local = local;
        _udp.RendezvousReceived += OnRendezvous;
    }

    public static async Task<OnlineHost> PublishAsync(MasterClient master, UdpHost udp, LobbyAd ad, Func<LobbyHeartbeat> status,
        string[]? localEndpoints = null, CancellationToken ct = default)
    {
        var masterUdp = await master.UdpEndPointAsync(ct);
        var lobby = await master.CreateAsync(ad, ct);
        var host = new OnlineHost(master, udp, lobby, masterUdp, status, localEndpoints ?? MasterClient.LocalEndpoints(udp.LocalEndPoint.Port));
        host.Register();
        host._loop = Task.Run(host.Loop);
        return host;
    }

    void Register() => _udp.SendRendezvous(_masterUdp,
        new RvMessage { T = RvMessage.Register, Lobby = Lobby.Id, Token = Lobby.HostToken, Local = _local }.Encode());

    async Task Loop()
    {
        int tick = 0;
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2.5), _cts.Token); } catch (OperationCanceledException) { break; }
            tick++;
            Register(); // keeps the NAT mapping towards the master alive
            if (tick % 4 == 0)
            {
                try
                {
                    if (!await _master.HeartbeatAsync(Lobby, _status(), _cts.Token))
                        Log?.Invoke("The master server no longer lists this game.");
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Log?.Invoke($"Master heartbeat failed: {e.Message}"); }
            }
        }
    }

    void OnRendezvous(IPEndPoint from, byte[] json)
    {
        var m = RvMessage.Decode(json);
        if (m == null) return;
        switch (m.T)
        {
            case RvMessage.Registered: PublicEndPoint = m.Public; break;
            case RvMessage.Incoming when m.Peer != null:
                // Punch towards the joiner so our NAT accepts its packets; the joiner punches back.
                var targets = m.Peer.Select(p => IPEndPoint.TryParse(p, out var ep) ? ep : null).OfType<IPEndPoint>().ToList();
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 12; i++)
                    {
                        foreach (var t in targets) _udp.SendPunch(t, m.Session);
                        await Task.Delay(150);
                    }
                });
                Log?.Invoke($"Player joining from {m.Peer[0]}");
                break;
            case RvMessage.RtcOffer or RvMessage.RtcIce:
                foreach (var reply in Rtc.OnSignal(m.Session, m, ch => RtcChannel?.Invoke(ch)))
                    _udp.SendRendezvous(_masterUdp, (reply with { Session = m.Session, Lobby = Lobby.Id }).Encode());
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _udp.RendezvousReceived -= OnRendezvous;
        _cts.Cancel();
        if (_loop != null) try { await _loop; } catch { }
        await _master.DeleteAsync(Lobby);
    }
}

/// <summary>Joiner side: rendezvous via the master, punch, and fall back to the relay.</summary>
public static class OnlineConnector
{
    /// <summary>Result of a connection attempt; Relayed tells the UI whether traffic goes via the master.</summary>
    public sealed record Result(ReliableUdpChannel Channel, bool Relayed);

    /// <summary>
    /// Connects to an online lobby (by join code or id). Tries a direct NAT-punched path
    /// to the host's public and LAN endpoints first; if nothing answers within
    /// <paramref name="punchTimeout"/>, uses the master's relay.
    /// </summary>
    public static async Task<Result> ConnectAsync(MasterClient master, UdpHost udp, string codeOrId, bool isCode = true,
        TimeSpan? punchTimeout = null, CancellationToken ct = default)
    {
        var masterUdp = await master.UdpEndPointAsync(ct);
        var nonce = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        var peerTcs = new TaskCompletionSource<RvMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var direct = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong session = 0;

        void OnRv(IPEndPoint from, byte[] json)
        {
            var m = RvMessage.Decode(json);
            if (m == null || m.Nonce != nonce) return;
            if (m.T == RvMessage.PeerInfo) peerTcs.TrySetResult(m);
            else if (m.T == RvMessage.Fail) peerTcs.TrySetException(new InvalidOperationException(m.Error ?? "join failed"));
        }
        void OnPunch(IPEndPoint from, ulong token, bool ack)
        {
            if (session != 0 && token == session && !from.Equals(masterUdp)) direct.TrySetResult(from);
        }

        udp.RendezvousReceived += OnRv;
        udp.PunchReceived += OnPunch;
        try
        {
            var join = new RvMessage
            {
                T = RvMessage.Join, Nonce = nonce, Code = isCode ? codeOrId : null, Lobby = isCode ? null : codeOrId,
                Local = MasterClient.LocalEndpoints(udp.LocalEndPoint.Port),
            }.Encode();
            RvMessage peer = null!;
            for (int attempt = 0; attempt < 10 && !peerTcs.Task.IsCompleted; attempt++)
            {
                udp.SendRendezvous(masterUdp, join);
                await Task.WhenAny(peerTcs.Task, Task.Delay(400, ct));
            }
            if (!peerTcs.Task.IsCompleted) throw new TimeoutException("The master server did not answer.");
            peer = await peerTcs.Task;
            session = peer.Session;

            // Punch every candidate endpoint until one acknowledges.
            var candidates = (peer.Peer ?? Array.Empty<string>()).Select(p => IPEndPoint.TryParse(p, out var ep) ? ep : null).OfType<IPEndPoint>().ToList();
            var limit = punchTimeout ?? TimeSpan.FromSeconds(3);
            var deadline = DateTime.UtcNow + limit;
            while (!direct.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                foreach (var c in candidates) udp.SendPunch(c, session);
                await Task.WhenAny(direct.Task, Task.Delay(150, ct));
            }

            if (direct.Task.IsCompleted)
            {
                try { return new Result(await udp.ConnectAsync(new UdpPath(await direct.Task), TimeSpan.FromSeconds(4), ct), false); }
                catch (TimeoutException) { /* punched path died; use the relay */ }
            }
            return new Result(await udp.ConnectAsync(new UdpPath(masterUdp, session), TimeSpan.FromSeconds(6), ct), true);
        }
        finally
        {
            udp.RendezvousReceived -= OnRv;
            udp.PunchReceived -= OnPunch;
        }
    }
}
