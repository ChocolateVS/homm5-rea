using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace H5.Net.Transport;

/// <summary>
/// Where datagrams for a peer go: straight to its (possibly NAT-punched) public
/// endpoint, or wrapped in a relay envelope through the master server.
/// </summary>
public readonly record struct UdpPath(IPEndPoint Target, ulong RelaySession = 0)
{
    public bool IsRelay => RelaySession != 0;
    public string Key => IsRelay ? $"relay:{RelaySession:x}" : Target.ToString();
    public override string ToString() => IsRelay ? $"relay via {Target}" : Target.ToString();
}

/// <summary>
/// One UDP socket multiplexing reliable channels (direct or relayed), hole-punch
/// probes and master-server rendezvous messages. Using a single socket matters:
/// the NAT mapping created by talking to the master is the one peers punch into.
/// </summary>
public sealed class UdpHost : IAsyncDisposable
{
    readonly UdpClient _udp;
    readonly ConcurrentDictionary<(string, uint), ReliableUdpChannel> _channels = new();
    readonly ConcurrentDictionary<(string, uint), TaskCompletionSource<bool>> _pendingConnects = new();
    readonly CancellationTokenSource _cts = new();
    readonly Task? _receiveLoop, _tickLoop;

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>When true, incoming Connect requests create channels raised through <see cref="Accepted"/>.</summary>
    public bool AcceptConnections { get; set; }
    public event Action<ReliableUdpChannel>? Accepted;
    /// <summary>JSON control message from the master (or a peer) and its sender.</summary>
    public event Action<IPEndPoint, byte[]>? RendezvousReceived;
    /// <summary>A hole-punch probe or its acknowledgement arrived from the given endpoint.</summary>
    public event Action<IPEndPoint, ulong, bool>? PunchReceived;

    /// <summary>Test hook: drop outgoing datagrams to simulate NATs that block direct paths.</summary>
    public Func<IPEndPoint, bool>? DropOutgoing { get; set; }

    /// <summary>
    /// Sees every datagram first; returning true consumes it. The master server uses this
    /// to forward relay envelopes while still running its own reliable channels on the socket.
    /// </summary>
    public Func<IPEndPoint, byte[], bool>? Intercept { get; set; }

    /// <summary>True when the owner drives the socket by calling <see cref="Pump"/> (no background tasks).</summary>
    public bool ManualPump { get; }

    UdpHost(UdpClient udp, bool manualPump)
    {
        _udp = udp;
        ManualPump = manualPump;
        LocalEndPoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        if (manualPump) { _udp.Client.Blocking = false; return; }
        _receiveLoop = Task.Run(ReceiveLoop);
        _tickLoop = Task.Run(TickLoop);
    }

    /// <summary>
    /// Non-blocking single-threaded drive for <see cref="ManualPump"/> hosts: reads every
    /// queued datagram and runs retransmission/keepalive timers. Call it every frame.
    /// </summary>
    public void Pump()
    {
        var buf = new byte[65536];
        EndPoint from = new IPEndPoint(LocalEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (true)
        {
            int n;
            try
            {
                if (_udp.Client.Available <= 0) break;
                n = _udp.Client.ReceiveFrom(buf, ref from);
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { return; }
            try { Dispatch((IPEndPoint)from, buf.AsSpan(0, n).ToArray()); } catch { }
        }
        var now = Environment.TickCount64;
        foreach (var ch in _channels.Values) ch.Tick(now);
    }

    public static UdpHost Bind(IPEndPoint local, bool manualPump = false)
    {
        var udp = new UdpClient(local.AddressFamily);
        if (OperatingSystem.IsWindows())
        {
            // Ignore ICMP port-unreachable resets so one dead peer can't break the socket.
            const int SIO_UDP_CONNRESET = -1744830452;
            try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
        }
        udp.Client.Bind(local);
        return new UdpHost(udp, manualPump);
    }

    public void SendRaw(IPEndPoint to, byte[] datagram)
    {
        if (DropOutgoing?.Invoke(to) == true) return;
        try { _udp.Send(datagram, datagram.Length, to); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    internal void SendOnPath(UdpPath path, byte[] datagram) =>
        SendRaw(path.Target, path.IsRelay ? UdpPacket.Relay(path.RelaySession, datagram) : datagram);

    public void SendPunch(IPEndPoint to, ulong token) => SendRaw(to, UdpPacket.Token(UdpKind.Punch, token));

    public void SendRendezvous(IPEndPoint to, byte[] json)
    {
        var b = new byte[1 + json.Length];
        b[0] = (byte)UdpKind.Rendezvous;
        json.CopyTo(b, 1);
        SendRaw(to, b);
    }

    /// <summary>Opens a reliable channel to a listening UdpHost over the given path.</summary>
    public async Task<ReliableUdpChannel> ConnectAsync(UdpPath path, TimeSpan timeout, CancellationToken ct = default)
    {
        var connId = (uint)Random.Shared.Next(1, int.MaxValue);
        var key = (path.Key, connId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConnects[key] = tcs;
        var connect = UdpPacket.Control(UdpKind.Connect, connId, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(connect.AsSpan(5), UdpPacket.Magic);
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (!tcs.Task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"No answer from {path}");
                ct.ThrowIfCancellationRequested();
                SendOnPath(path, connect);
                for (int i = 0; i < 10 && !tcs.Task.IsCompleted; i++)
                {
                    if (ManualPump) Pump();
                    await Task.WhenAny(tcs.Task, Task.Delay(25, ct));
                }
            }
        }
        finally { _pendingConnects.TryRemove(key, out _); }
        var ch = new ReliableUdpChannel(this, path, connId);
        _channels[key] = ch;
        return ch;
    }

    internal void Remove(ReliableUdpChannel ch) => _channels.TryRemove((ch.Path.Key, ch.ConnId), out _);

    async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try { Dispatch(r.RemoteEndPoint, r.Buffer); }
            catch (Exception) { /* malformed datagram: ignore */ }
        }
    }

    void Dispatch(IPEndPoint from, byte[] d)
    {
        if (d.Length == 0) return;
        if (Intercept?.Invoke(from, d) == true) return;
        var kind = (UdpKind)d[0];
        switch (kind)
        {
            case UdpKind.Relay when d.Length >= 10:
                Handle(new UdpPath(from, BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(1))), d.AsSpan(9).ToArray());
                return;
            case UdpKind.Rendezvous:
                RendezvousReceived?.Invoke(from, d.AsSpan(1).ToArray());
                return;
            case UdpKind.Punch when d.Length >= 9:
                var token = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(1));
                SendRaw(from, UdpPacket.Token(UdpKind.PunchAck, token));
                PunchReceived?.Invoke(from, token, false);
                return;
            case UdpKind.PunchAck when d.Length >= 9:
                PunchReceived?.Invoke(from, BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(1)), true);
                return;
            default:
                Handle(new UdpPath(from), d);
                return;
        }
    }

    void Handle(UdpPath path, byte[] d)
    {
        if (d.Length < 5) return;
        var kind = (UdpKind)d[0];
        var connId = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(1));
        var key = (path.Key, connId);
        switch (kind)
        {
            case UdpKind.Connect:
                if (d.Length < 9 || BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(5)) != UdpPacket.Magic) return;
                if (!_channels.ContainsKey(key))
                {
                    if (!AcceptConnections) return;
                    var ch = new ReliableUdpChannel(this, path, connId);
                    if (!_channels.TryAdd(key, ch)) return;
                    Accepted?.Invoke(ch);
                }
                SendOnPath(path, UdpPacket.Control(UdpKind.Accept, connId));
                return;
            case UdpKind.Accept:
                if (_pendingConnects.TryGetValue(key, out var tcs)) tcs.TrySetResult(true);
                return;
            default:
                if (_channels.TryGetValue(key, out var channel)) channel.Handle(kind, d);
                return;
        }
    }

    async Task TickLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(15, _cts.Token); }
            catch (OperationCanceledException) { break; }
            var now = Environment.TickCount64;
            foreach (var ch in _channels.Values) ch.Tick(now);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ch in _channels.Values) await ch.DisposeAsync();
        _cts.Cancel();
        _udp.Dispose();
        foreach (var t in new[] { _receiveLoop, _tickLoop })
            if (t != null) try { await t; } catch { }
        _cts.Dispose();
    }
}
