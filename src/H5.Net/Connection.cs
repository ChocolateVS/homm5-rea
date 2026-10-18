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

    public string RemoteEndPoint => _channel.RemoteName;
    public IFrameChannel Channel => _channel;
    public event Action<Connection, Message>? Received;
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
        Message m;
        try { m = MessageCodec.Decode(frame); }
        catch (Exception e)
        {
            _ = DisposeWithError(e);
            return;
        }
        Received?.Invoke(this, m);
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
