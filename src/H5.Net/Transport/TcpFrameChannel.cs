using System.Net.Sockets;
using System.Threading.Channels;

namespace H5.Net.Transport;

/// <summary>
/// Frames over TCP: 4-byte little-endian length + payload. Writes are queued and
/// performed by a single writer task; reads run on a reader task.
/// </summary>
public sealed class TcpFrameChannel : IFrameChannel
{
    readonly TcpClient _tcp;
    readonly Stream _stream;
    readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    Task? _reader, _writer;
    int _closed;

    public string RemoteName { get; }
    public event Action<byte[]>? FrameReceived;
    public event Action<Exception?>? Closed;

    public TcpFrameChannel(TcpClient tcp, Stream? stream = null)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = stream ?? tcp.GetStream();
        RemoteName = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public static async Task<TcpFrameChannel> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await tcp.ConnectAsync(host, port, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"No answer from {host}:{port}");
        }
        catch { tcp.Dispose(); throw; }
        return new TcpFrameChannel(tcp);
    }

    public void Start()
    {
        _reader = Task.Run(ReadLoop);
        _writer = Task.Run(WriteLoop);
    }

    public void Send(byte[] frame)
    {
        if (_closed != 0) return;
        var buf = new byte[4 + frame.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), frame.Length);
        frame.CopyTo(buf, 4);
        _outbox.Writer.TryWrite(buf);
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
                if (len <= 0 || len > ProtocolInfo.MaxFrameBytes) throw new InvalidDataException($"bad frame length {len}");
                var body = new byte[len];
                await _stream.ReadExactlyAsync(body, _cts.Token);
                FrameReceived?.Invoke(body);
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) { }
        catch (IOException) { }
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
        Closed?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        Close(null);
        foreach (var t in new[] { _reader, _writer })
            if (t != null) try { await t; } catch { /* reported via Closed */ }
        _cts.Dispose();
    }
}
