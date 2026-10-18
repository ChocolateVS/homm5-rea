using System.Net.Sockets;
using System.Threading.Channels;

namespace H5.Net;

/// <summary>
/// A framed message stream over TCP. Sends are queued and written by a single
/// writer task so callers never block on the network; received messages are
/// raised from the reader task.
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    Task? _reader, _writer;
    int _closed;

    public string RemoteEndPoint { get; }
    public event Action<Connection, Message>? Received;
    public event Action<Connection, Exception?>? Closed;

    public Connection(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        RemoteEndPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public static async Task<Connection> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        return new Connection(tcp);
    }

    public void Start()
    {
        _reader = Task.Run(ReadLoop);
        _writer = Task.Run(WriteLoop);
    }

    public void Send(Message m)
    {
        if (_closed == 0) _outbox.Writer.TryWrite(MessageCodec.Encode(m));
    }

    async Task ReadLoop()
    {
        Exception? error = null;
        try
        {
            var header = new byte[4];
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token);
                int len = BitConverter.ToInt32(header);
                if (len <= 0 || len > ProtocolInfo.MaxFrameBytes)
                    throw new InvalidDataException($"bad frame length {len}");
                var body = new byte[len];
                await _stream.ReadExactlyAsync(body, _cts.Token);
                Received?.Invoke(this, MessageCodec.Decode(body));
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) { }
        catch (Exception e) { error = e; }
        Close(error);
    }

    async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(_cts.Token))
                await _stream.WriteAsync(frame, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Close(e); }
    }

    void Close(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        _tcp.Dispose();
        Closed?.Invoke(this, error);
    }

    public async ValueTask DisposeAsync()
    {
        Close(null);
        foreach (var t in new[] { _reader, _writer })
            if (t != null) try { await t; } catch { /* already reported via Closed */ }
        _cts.Dispose();
    }
}
