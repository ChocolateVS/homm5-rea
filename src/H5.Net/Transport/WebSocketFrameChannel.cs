using System.Net.WebSockets;

namespace H5.Net.Transport;

/// <summary>
/// Frames over a WebSocket: one binary message per frame. This is the transport a
/// browser build can use (browsers cannot open raw TCP/UDP sockets), and desktop
/// clients use it too when going through a TLS reverse proxy (wss://).
///
/// Written without Task.Run/threads: receiving is an async loop and sending is an
/// async queue drain, both continuing on whatever scheduler the caller runs on, so
/// it works in single-threaded (browser/WASM) runtimes.
/// </summary>
public sealed class WebSocketFrameChannel : IFrameChannel
{
    readonly WebSocket _ws;
    readonly Queue<byte[]> _outbox = new();
    readonly CancellationTokenSource _cts = new();
    bool _sending;
    int _closed;

    public string RemoteName { get; }
    public event Action<byte[]>? FrameReceived;
    public event Action<Exception?>? Closed;

    public WebSocketFrameChannel(WebSocket ws, string remoteName)
    {
        _ws = ws;
        RemoteName = remoteName;
    }

    /// <summary>Connects to ws:// or wss:// (e.g. "wss://master.example.com/ws/join?code=ABC123").</summary>
    public static async Task<WebSocketFrameChannel> ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        // Browsers manage WebSocket pings themselves; setting ClientWebSocketOptions there throws
        // PlatformNotSupportedException (found by the web spike, docs/WEB_SPIKE.md).
        if (!OperatingSystem.IsBrowser()) ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await ws.ConnectAsync(uri, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TimeoutException($"No answer from {uri.Host}");
        }
        catch (WebSocketException e)
        {
            ws.Dispose();
            throw new IOException($"Could not connect to {uri.Host}: {e.Message}", e);
        }
        return new WebSocketFrameChannel(ws, uri.Authority);
    }

    public void Start() => _ = ReceiveLoop();

    public void Send(byte[] frame)
    {
        if (_closed != 0) return;
        lock (_outbox) _outbox.Enqueue(frame);
        _ = Drain();
    }

    async Task Drain()
    {
        lock (_outbox)
        {
            if (_sending) return;
            _sending = true;
        }
        try
        {
            while (true)
            {
                byte[] next;
                lock (_outbox)
                {
                    if (_outbox.Count == 0 || _closed != 0) { _sending = false; return; }
                    next = _outbox.Dequeue();
                }
                await _ws.SendAsync(next, WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
            }
        }
        catch (Exception e)
        {
            lock (_outbox) _sending = false;
            Close(e is OperationCanceledException ? null : e);
        }
    }

    async Task ReceiveLoop()
    {
        Exception? error = null;
        var buf = new byte[16384];
        try
        {
            using var ms = new MemoryStream();
            while (!_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > ProtocolInfo.MaxFrameBytes) throw new InvalidDataException("frame too large");
                if (!r.EndOfMessage) continue;
                var frame = ms.ToArray();
                ms.SetLength(0);
                if (frame.Length > 0) FrameReceived?.Invoke(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception e) { error = e; }
        Close(error);
    }

    void Close(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        if (_ws.State == WebSocketState.Open)
            _ = _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                .ContinueWith(_ => _ws.Dispose(), TaskScheduler.Default);
        else _ws.Dispose();
        Closed?.Invoke(error);
    }

    public ValueTask DisposeAsync()
    {
        Close(null);
        return ValueTask.CompletedTask;
    }
}
