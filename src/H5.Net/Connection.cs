using H5.Net.Transport;

namespace H5.Net;

/// <summary>
/// A message stream over any <see cref="IFrameChannel"/> (TCP, punched UDP or
/// relayed UDP). Sends never block; received messages are raised from the
/// transport thread.
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    readonly IFrameChannel _channel;
    int _closed;
    long _lastReceived = Environment.TickCount64;
    int _dispatching;

    public string RemoteEndPoint => _channel.RemoteName;
    public IFrameChannel Channel => _channel;
    public event Action<Connection, Message>? Received;

    /// <summary>
    /// When a frame last arrived or finished being handled (Environment.TickCount64), recorded on the transport
    /// thread before any handler runs, so a frame that waits for a busy server still counts as a sign of life.
    /// </summary>
    public long LastReceivedTicks => Volatile.Read(ref _lastReceived);
    /// <summary>A received frame is being handled (possibly waiting for the server lock) right now.</summary>
    public bool Dispatching => Volatile.Read(ref _dispatching) > 0;
    public event Action<Connection, Exception?>? Closed;

    public Connection(IFrameChannel channel)
    {
        _channel = channel;
        _channel.FrameReceived += OnFrame;
        _channel.Closed += e =>
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(this, e);
        };
    }

    public static async Task<Connection> ConnectAsync(string host, int port, CancellationToken ct = default) =>
        new(await TcpFrameChannel.ConnectAsync(host, port, TimeSpan.FromSeconds(8), ct));

    void OnFrame(byte[] frame)
    {
        Volatile.Write(ref _lastReceived, Environment.TickCount64);
        Interlocked.Increment(ref _dispatching);
        try
        {
            Message m;
            try { m = MessageCodec.Decode(frame); }
            catch (Exception e)
            {
                _ = DisposeWithError(e);
                return;
            }
            Received?.Invoke(this, m);
        }
        finally
        {
            // Frames that queued up behind a slow handler were not read yet: the connection was alive meanwhile.
            Volatile.Write(ref _lastReceived, Environment.TickCount64);
            Interlocked.Decrement(ref _dispatching);
        }
    }

    async Task DisposeWithError(Exception e)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(this, e);
        await _channel.DisposeAsync();
    }

    public void Start() => _channel.Start();

    public void Send(Message m)
    {
        if (_closed == 0) _channel.Send(MessageCodec.EncodeBody(m));
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
    }
}
