using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace H5.Net.Nat;

/// <summary>
/// Best-effort router port mapping for hosts that publish online games: NAT-PMP
/// (RFC 6886) first, then UPnP IGD (WANIPConnection/WANPPPConnection AddPortMapping).
/// Success means joiners can reach the host directly even when hole punching fails;
/// failure is harmless because punching and the master relay remain available.
/// Only used when the player chooses to publish a game online.
/// </summary>
public sealed class PortMapper : IAsyncDisposable
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    readonly List<Func<Task>> _undo = new();

    public string? Method { get; private set; }
    public IPAddress? ExternalAddress { get; private set; }

    /// <summary>Maps the given internal port (UDP and TCP) to the same external port. Returns true if any mapping succeeded.</summary>
    public async Task<bool> MapAsync(int port, string description = "H5Reborn", CancellationToken ct = default)
    {
        foreach (var gw in Gateways())
        {
            if (await NatPmpAsync(gw, port, ct)) { Method = "NAT-PMP"; return true; }
        }
        try
        {
            foreach (var (control, service) in await DiscoverIgdAsync(TimeSpan.FromSeconds(2), ct))
            {
                var local = LocalAddressFor(control.Host);
                if (local == null) continue;
                bool any = false;
                foreach (var proto in new[] { "UDP", "TCP" })
                {
                    if (await AddPortMappingAsync(_http, control, service, port, proto, local, description, ct))
                    {
                        any = true;
                        var (c, s, pr) = (control, service, proto);
                        _undo.Add(() => DeletePortMappingAsync(_http, c, s, port, pr));
                    }
                }
                if (any) { Method = "UPnP"; return true; }
            }
        }
        catch (Exception e) when (e is SocketException or HttpRequestException or TaskCanceledException) { }
        return false;
    }

    static IEnumerable<IPAddress> Gateways() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
            .Distinct();

    static IPAddress? LocalAddressFor(string host)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(host, 1900);
            return ((IPEndPoint)s.LocalEndPoint!).Address;
        }
        catch (SocketException) { return null; }
    }

    async Task<bool> NatPmpAsync(IPAddress gateway, int port, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var gw = new IPEndPoint(gateway, 5351);
        bool any = false;
        foreach (byte op in new byte[] { 1, 2 }) // 1 = UDP, 2 = TCP
        {
            var req = new byte[12];
            req[1] = op;
            WriteBE16(req, 4, port);
            WriteBE16(req, 6, port);
            WriteBE32(req, 8, 7200);
            var resp = await RequestAsync(udp, gw, req, ct);
            if (resp is { Length: >= 16 } && resp[1] == 128 + op && ReadBE16(resp, 2) == 0)
            {
                any = true;
                var o = op;
                _undo.Add(async () =>
                {
                    using var u = new UdpClient(AddressFamily.InterNetwork);
                    var del = new byte[12]; del[1] = o; WriteBE16(del, 4, port);
                    await RequestAsync(u, gw, del, CancellationToken.None);
                });
            }
        }
        if (any)
        {
            var ext = await RequestAsync(udp, gw, new byte[2], ct); // opcode 0: external address
            if (ext is { Length: >= 12 } && ReadBE16(ext, 2) == 0) ExternalAddress = new IPAddress(ext.AsSpan(8, 4));
        }
        return any;
    }

    static async Task<byte[]?> RequestAsync(UdpClient udp, IPEndPoint to, byte[] req, CancellationToken ct)
    {
        for (int attempt = 0, wait = 250; attempt < 3; attempt++, wait *= 2)
        {
            try
            {
                await udp.SendAsync(req, req.Length, to);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(wait);
                var r = await udp.ReceiveAsync(cts.Token);
                if (r.RemoteEndPoint.Address.Equals(to.Address)) return r.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { return null; }
        }
        return null;
    }

    /// <summary>SSDP search for Internet Gateway Devices; returns (controlURL, serviceType) pairs.</summary>
    async Task<List<(Uri Control, string Service)>> DiscoverIgdAsync(TimeSpan wait, CancellationToken ct)
    {
        var found = new List<(Uri, string)>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var search = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await udp.SendAsync(search, search.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
        var locations = new HashSet<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.ASCII.GetString(r.Buffer);
                foreach (var line in text.Split("\r\n"))
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) locations.Add(line[9..].Trim());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        foreach (var loc in locations)
            found.AddRange(await ReadDeviceAsync(_http, new Uri(loc), ct));
        return found;
    }

    /// <summary>Parses an IGD device description and returns its WAN connection control URLs.</summary>
    public static async Task<List<(Uri Control, string Service)>> ReadDeviceAsync(HttpClient http, Uri location, CancellationToken ct)
    {
        var list = new List<(Uri, string)>();
        try
        {
            var xml = XDocument.Parse(await http.GetStringAsync(location, ct));
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            foreach (var svc in xml.Descendants(ns + "service"))
            {
                var type = (string?)svc.Element(ns + "serviceType") ?? "";
                if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;
                var control = (string?)svc.Element(ns + "controlURL");
                if (control != null) list.Add((new Uri(location, control), type));
            }
        }
        catch (Exception e) when (e is HttpRequestException or System.Xml.XmlException or TaskCanceledException) { }
        return list;
    }

    public static async Task<bool> AddPortMappingAsync(HttpClient http, Uri control, string service, int port, string protocol,
        IPAddress internalClient, string description, CancellationToken ct)
    {
        var args = $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>{protocol}</NewProtocol>" +
                   $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{internalClient}</NewInternalClient>" +
                   $"<NewEnabled>1</NewEnabled><NewPortMappingDescription>{System.Security.SecurityElement.Escape(description)}</NewPortMappingDescription>" +
                   "<NewLeaseDuration>7200</NewLeaseDuration>";
        return await SoapAsync(http, control, service, "AddPortMapping", args, ct);
    }

    static Task<bool> DeletePortMappingAsync(HttpClient http, Uri control, string service, int port, string protocol) =>
        SoapAsync(http, control, service, "DeletePortMapping",
            $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>{protocol}</NewProtocol>", CancellationToken.None);

    static async Task<bool> SoapAsync(HttpClient http, Uri control, string service, string action, string args, CancellationToken ct)
    {
        var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                   "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                   $"<u:{action} xmlns:u=\"{service}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");
        try { using var r = await http.SendAsync(req, ct); return r.IsSuccessStatusCode; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    static void WriteBE16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    static void WriteBE32(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static int ReadBE16(byte[] b, int o) => (b[o] << 8) | b[o + 1];

    /// <summary>Removes the mappings that were created.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var u in _undo) try { await u(); } catch { }
        _undo.Clear();
        _http.Dispose();
    }
}
