using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace H5.Net.Discovery;

/// <summary>What a LAN host advertises; the browser adds the address it came from.</summary>
public sealed record LanGameInfo(string Id, string Name, int Port, int ProtocolVersion, string Fingerprint,
    int Players, int OpenRooms, int Rooms)
{
    public string Address { get; init; } = "";
    public DateTime LastSeen { get; init; }
}

/// <summary>
/// LAN discovery that works on modern networks: browsers send a probe to the IPv4
/// limited broadcast, every interface's directed broadcast, an IPv4 multicast group
/// and an IPv6 link-local multicast group (per interface); hosts answer each probe
/// with a unicast reply. Unlike the original's single broadcast, this crosses
/// multi-homed PCs, Wi-Fi/Ethernet bridges and IPv6-only links.
/// </summary>
public static class LanDiscovery
{
    public const int DefaultPort = 27616;
    public static readonly IPAddress MulticastV4 = IPAddress.Parse("239.255.27.15");
    public static readonly IPAddress MulticastV6 = IPAddress.Parse("ff02::2715");
    internal static readonly byte[] ProbeMagic = Encoding.ASCII.GetBytes("H5RB?");
    internal static readonly byte[] ReplyMagic = Encoding.ASCII.GetBytes("H5RB!");
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static IEnumerable<NetworkInterface> UsableInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

    /// <summary>All probe destinations for the real network (broadcasts + multicast groups).</summary>
    public static IReadOnlyList<IPEndPoint> DefaultProbeTargets(int port = DefaultPort)
    {
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, port), new(MulticastV4, port) };
        foreach (var nic in UsableInterfaces())
        {
            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.IPv4Mask is { } mask)
                {
                    var a = ua.Address.GetAddressBytes(); var m = mask.GetAddressBytes();
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                    list.Add(new IPEndPoint(new IPAddress(b), port));
                }
            }
            if (nic.Supports(NetworkInterfaceComponent.IPv6))
            {
                try
                {
                    var idx = props.GetIPv6Properties().Index;
                    list.Add(new IPEndPoint(new IPAddress(MulticastV6.GetAddressBytes(), idx), port));
                }
                catch (NetworkInformationException) { }
            }
        }
        return list.Distinct().ToList();
    }
}

/// <summary>Host side: answers LAN probes with the current game info.</summary>
public sealed class LanBeacon : IAsyncDisposable
{
    readonly List<UdpClient> _sockets = new();
    readonly CancellationTokenSource _cts = new();
    readonly List<Task> _loops = new();
    readonly Func<LanGameInfo> _info;

    public int Port { get; }

    /// <summary>
    /// Starts answering probes. With <paramref name="bind"/> = null it listens on all
    /// IPv4 and IPv6 interfaces and joins the discovery multicast groups; tests pass a
    /// loopback endpoint instead.
    /// </summary>
    public LanBeacon(Func<LanGameInfo> info, IPEndPoint? bind = null)
    {
        _info = info;
        if (bind != null)
        {
            var u = new UdpClient(bind);
            Port = ((IPEndPoint)u.Client.LocalEndPoint!).Port;
            Add(u);
            return;
        }
        Port = LanDiscovery.DefaultPort;
        var v4 = new UdpClient(AddressFamily.InterNetwork);
        v4.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        v4.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        foreach (var nic in LanDiscovery.UsableInterfaces())
            foreach (var ua in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                try { v4.JoinMulticastGroup(LanDiscovery.MulticastV4, ua.Address); } catch (SocketException) { }
        Add(v4);
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var v6 = new UdpClient(AddressFamily.InterNetworkV6);
                v6.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                v6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
                foreach (var nic in LanDiscovery.UsableInterfaces().Where(n => n.Supports(NetworkInterfaceComponent.IPv6)))
                    try { v6.JoinMulticastGroup(nic.GetIPProperties().GetIPv6Properties().Index, LanDiscovery.MulticastV6); } catch { }
                Add(v6);
            }
            catch (SocketException) { }
        }
    }

    void Add(UdpClient u)
    {
        _sockets.Add(u);
        _loops.Add(Task.Run(() => Loop(u)));
    }

    async Task Loop(UdpClient u)
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await u.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            if (!r.Buffer.AsSpan().StartsWith(LanDiscovery.ProbeMagic)) continue;
            var json = JsonSerializer.SerializeToUtf8Bytes(_info(), LanDiscovery.Json);
            var reply = LanDiscovery.ReplyMagic.Concat(json).ToArray();
            try { await u.SendAsync(reply, reply.Length, r.RemoteEndPoint); } catch (SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var s in _sockets) s.Dispose();
        try { await Task.WhenAll(_loops); } catch { }
    }
}

/// <summary>Client side: probes the LAN periodically and keeps a list of hosts that answered.</summary>
public sealed class LanBrowser : IAsyncDisposable
{
    readonly UdpClient _v4;
    readonly UdpClient? _v6;
    readonly IReadOnlyList<IPEndPoint> _targets;
    readonly ConcurrentDictionary<string, LanGameInfo> _games = new();
    readonly CancellationTokenSource _cts = new();
    readonly List<Task> _loops = new();

    /// <summary>Games heard from within the last few seconds, keyed by host id.</summary>
    public IReadOnlyList<LanGameInfo> Games =>
        _games.Values.Where(g => DateTime.UtcNow - g.LastSeen < TimeSpan.FromSeconds(6)).OrderBy(g => g.Name).ToList();

    public LanBrowser(IReadOnlyList<IPEndPoint>? targets = null, TimeSpan? interval = null)
    {
        _targets = targets ?? LanDiscovery.DefaultProbeTargets();
        // Probing only loopback (tests) needs no network-facing socket.
        var bind = _targets.All(t => IPAddress.IsLoopback(t.Address)) ? IPAddress.Loopback : IPAddress.Any;
        _v4 = new UdpClient(new IPEndPoint(bind, 0)) { EnableBroadcast = true };
        _loops.Add(Task.Run(() => Listen(_v4)));
        if (Socket.OSSupportsIPv6 && _targets.Any(t => t.AddressFamily == AddressFamily.InterNetworkV6))
        {
            try { _v6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Any, 0)); _loops.Add(Task.Run(() => Listen(_v6))); }
            catch (SocketException) { }
        }
        _loops.Add(Task.Run(() => ProbeLoop(interval ?? TimeSpan.FromSeconds(2))));
    }

    public void ProbeNow()
    {
        foreach (var t in _targets)
        {
            var sock = t.AddressFamily == AddressFamily.InterNetworkV6 ? _v6 : _v4;
            try { sock?.Send(LanDiscovery.ProbeMagic, LanDiscovery.ProbeMagic.Length, t); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    async Task ProbeLoop(TimeSpan interval)
    {
        while (!_cts.IsCancellationRequested)
        {
            ProbeNow();
            try { await Task.Delay(interval, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    async Task Listen(UdpClient u)
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await u.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            if (!r.Buffer.AsSpan().StartsWith(LanDiscovery.ReplyMagic)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<LanGameInfo>(r.Buffer.AsSpan(LanDiscovery.ReplyMagic.Length), LanDiscovery.Json);
                if (info == null) continue;
                var addr = r.RemoteEndPoint.Address;
                if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                var host = addr.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{addr}]" : addr.ToString();
                _games[info.Id] = info with { Address = host, LastSeen = DateTime.UtcNow };
            }
            catch (JsonException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _v4.Dispose();
        _v6?.Dispose();
        try { await Task.WhenAll(_loops); } catch { }
    }
}
