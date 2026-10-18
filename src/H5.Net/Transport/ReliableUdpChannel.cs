using System.Buffers.Binary;

namespace H5.Net.Transport;

/// <summary>
/// Reliable, ordered frame channel over UDP. Frames are split into fragments with
/// consecutive sequence numbers; the receiver acknowledges cumulatively plus a
/// 64-bit selective-ack window, and the sender retransmits on an RTT-based timer.
/// Traffic in a turn-based game is small, so a simple fixed window suffices.
///
/// Chosen over QUIC (System.Net.Quic) because QUIC in .NET cannot run on a socket
/// we already hole-punched, cannot be wrapped in our relay envelope, and requires
/// TLS certificates on every player's machine.
/// </summary>
public sealed class ReliableUdpChannel : IFrameChannel
{
    const int Window = 256;
    const int MaxSends = 60;
    const long IdleTimeoutMs = 15000, KeepAliveMs = 1000;

    sealed class Outgoing(byte[] datagram)
    {
        public readonly byte[] Datagram = datagram;
        public long LastSent;
        public int Sends;
    }

    readonly UdpHost _host;
    readonly object _lock = new();
    readonly SortedDictionary<uint, Outgoing> _unacked = new(new SeqComparer());
    readonly Queue<(uint Seq, byte[] Datagram)> _waiting = new();
    readonly Dictionary<uint, (ushort Frag, ushort Count, byte[] Payload)> _received = new();
    readonly List<byte[]> _assembling = new();
    uint _nextSeq, _nextExpected;
    long _lastRecv, _lastSend;
    double _srttMs = 150;
    int _closed;

    public UdpPath Path { get; }
    public uint ConnId { get; }
    public string RemoteName => Path.ToString();
    /// <summary>Smoothed round-trip time, for latency display.</summary>
    public int RoundTripMs => (int)_srttMs;

    public event Action<byte[]>? FrameReceived;
    public event Action<Exception?>? Closed;

    internal ReliableUdpChannel(UdpHost host, UdpPath path, uint connId)
    {
        _host = host;
        Path = path;
        ConnId = connId;
        _lastRecv = _lastSend = Environment.TickCount64;
    }

    public void Start() { }

    public void Send(byte[] frame)
    {
        if (_closed != 0) return;
        int max = UdpPacket.MaxFragmentPayload;
        int count = Math.Max(1, (frame.Length + max - 1) / max);
        if (count > ushort.MaxValue) throw new InvalidOperationException("frame too large");
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                var part = frame.AsSpan(i * max, Math.Min(max, frame.Length - i * max));
                var seq = _nextSeq++;
                _waiting.Enqueue((seq, UdpPacket.Data(ConnId, seq, (ushort)i, (ushort)count, part)));
            }
            Pump(Environment.TickCount64);
        }
    }

    void Pump(long now)
    {
        while (_waiting.Count > 0 && _unacked.Count < Window)
        {
            var (seq, d) = _waiting.Dequeue();
            var o = new Outgoing(d) { LastSent = now, Sends = 1 };
            _unacked[seq] = o;
            Transmit(d, now);
        }
    }

    void Transmit(byte[] d, long now)
    {
        _lastSend = now;
        _host.SendOnPath(Path, d);
    }

    internal void Handle(UdpKind kind, byte[] d)
    {
        if (_closed != 0) return;
        var now = Environment.TickCount64;
        _lastRecv = now;
        switch (kind)
        {
            case UdpKind.Data when d.Length >= UdpPacket.DataHeader: OnData(d, now); break;
            case UdpKind.Ack when d.Length >= 17: OnAck(d, now); break;
            case UdpKind.Ping when d.Length >= 13:
                var pong = UdpPacket.Control(UdpKind.Pong, ConnId, 8);
                d.AsSpan(5, 8).CopyTo(pong.AsSpan(5));
                Transmit(pong, now);
                break;
            case UdpKind.Pong: break;
            case UdpKind.Close: Close(null, sendClose: false); break;
        }
    }

    void OnData(byte[] d, long now)
    {
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(5));
        var frag = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(9));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(11));
        var deliver = new List<byte[]>();
        byte[] ack;
        lock (_lock)
        {
            int ahead = (int)(seq - _nextExpected);
            if (ahead >= 0 && ahead < Window * 2 && !_received.ContainsKey(seq))
                _received[seq] = (frag, count, d.AsSpan(UdpPacket.DataHeader).ToArray());
            while (_received.Remove(_nextExpected, out var part))
            {
                _assembling.Add(part.Payload);
                if (part.Frag == part.Count - 1)
                {
                    deliver.Add(_assembling.Count == 1 ? _assembling[0] : _assembling.SelectMany(p => p).ToArray());
                    _assembling.Clear();
                }
                _nextExpected++;
            }
            ack = UdpPacket.Control(UdpKind.Ack, ConnId, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(5), _nextExpected);
            ulong sack = 0;
            for (int i = 0; i < 64; i++)
                if (_received.ContainsKey(_nextExpected + 1 + (uint)i)) sack |= 1UL << i;
            BinaryPrimitives.WriteUInt64LittleEndian(ack.AsSpan(9), sack);
        }
        Transmit(ack, now);
        foreach (var f in deliver)
        {
            if (f.Length > ProtocolInfo.MaxFrameBytes) { Close(new InvalidDataException("frame too large"), true); return; }
            FrameReceived?.Invoke(f);
        }
    }

    void OnAck(byte[] d, long now)
    {
        var next = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(5));
        var sack = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(9));
        lock (_lock)
        {
            foreach (var seq in _unacked.Keys.ToList())
            {
                bool acked = (int)(seq - next) < 0;
                if (!acked)
                {
                    int bit = (int)(seq - next) - 1;
                    acked = bit >= 0 && bit < 64 && (sack & (1UL << bit)) != 0;
                }
                if (!acked) continue;
                var o = _unacked[seq];
                if (o.Sends == 1) _srttMs = _srttMs * 0.875 + (now - o.LastSent) * 0.125;
                _unacked.Remove(seq);
            }
            Pump(now);
        }
    }

    internal void Tick(long now)
    {
        if (_closed != 0) return;
        if (now - _lastRecv > IdleTimeoutMs) { Close(new TimeoutException($"Connection to {RemoteName} timed out"), true); return; }
        bool failed = false;
        lock (_lock)
        {
            var rto = Math.Clamp(_srttMs * 2.5, 120, 2000);
            foreach (var o in _unacked.Values)
            {
                if (now - o.LastSent < rto * Math.Min(o.Sends, 4)) continue;
                if (++o.Sends > MaxSends) { failed = true; break; }
                o.LastSent = now;
                Transmit(o.Datagram, now);
            }
        }
        if (failed) { Close(new TimeoutException($"Connection to {RemoteName} lost"), true); return; }
        if (now - _lastSend > KeepAliveMs)
        {
            var ping = UdpPacket.Control(UdpKind.Ping, ConnId, 8);
            BinaryPrimitives.WriteInt64LittleEndian(ping.AsSpan(5), now);
            Transmit(ping, now);
        }
    }

    void Close(Exception? error, bool sendClose)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (sendClose) _host.SendOnPath(Path, UdpPacket.Control(UdpKind.Close, ConnId));
        _host.Remove(this);
        Closed?.Invoke(error);
    }

    public ValueTask DisposeAsync()
    {
        Close(null, sendClose: true);
        return ValueTask.CompletedTask;
    }

    sealed class SeqComparer : IComparer<uint>
    {
        public int Compare(uint a, uint b) => ((int)(a - b)).CompareTo(0);
    }
}
