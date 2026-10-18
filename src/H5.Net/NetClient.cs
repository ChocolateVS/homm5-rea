using System.Collections.Concurrent;
using H5.Net.Transport;

namespace H5.Net;

/// <summary>
/// Client side of the protocol. Incoming messages are queued; the owner calls
/// <see cref="Poll"/> from its main loop (e.g. Godot's _Process) so handlers run
/// on the game thread.
///
/// The transport is supplied by a connector (TCP to an address, or a reliable UDP
/// channel that was punched/relayed via the master), so the same client works for
/// LAN, direct-IP and internet play. If the connection drops while in a game, the
/// client redials with the same connector and resumes its seat using the server's
/// ResumeToken; the server replays any updates it missed.
/// </summary>
public sealed class NetClient : IAsyncDisposable
{
    readonly ConcurrentQueue<Message> _inbox = new();
    readonly HashSet<int> _pending = new();
    Connection? _conn;
    Func<CancellationToken, Task<IFrameChannel>>? _connector;
    string _playerName = "", _fingerprint = "";
    string? _resumeToken;
    int _clientSeq, _lastSeq = -1;
    bool _inGame, _disposed;
    volatile string? _disconnectReason;
    volatile bool _reconnecting;
    long _lastPingSent;
    CancellationTokenSource _cts = new();

    public int PlayerId { get; private set; }
    public bool Connected => _conn != null && PlayerId != 0 && !_reconnecting;
    public bool Reconnecting => _reconnecting;
    /// <summary>Latest measured round trip to the server in milliseconds (-1 until measured).</summary>
    public int PingMs { get; private set; } = -1;
    public string? RemoteName => _conn?.RemoteEndPoint;
    /// <summary>How long to keep retrying after an in-game disconnect.</summary>
    public TimeSpan ReconnectWindow { get; set; } = TimeSpan.FromSeconds(75);

    public event Action<Message>? MessageReceived;
    public event Action<string>? Disconnected;
    /// <summary>Raised (on Poll) when an in-game connection drops and the client starts redialling.</summary>
    public event Action<string>? ConnectionLost;
    /// <summary>Raised (on Poll) after a successful resume.</summary>
    public event Action? ConnectionRestored;

    /// <summary>Commands sent but not yet answered by a GameUpdate or CommandRejected.</summary>
    public int PendingCommands => _pending.Count;

    /// <summary>Connects over TCP to host:port (LAN / direct IP).</summary>
    public Task ConnectAsync(string host, int port, string playerName, string dataFingerprint, CancellationToken ct = default) =>
        ConnectAsync(async c => await TcpFrameChannel.ConnectAsync(host, port, TimeSpan.FromSeconds(8), c), playerName, dataFingerprint, ct);

    /// <summary>Connects through an arbitrary transport connector (used again for reconnects).</summary>
    public async Task ConnectAsync(Func<CancellationToken, Task<IFrameChannel>> connector, string playerName, string dataFingerprint, CancellationToken ct = default)
    {
        _connector = connector;
        _playerName = playerName;
        _fingerprint = dataFingerprint;
        await OpenAsync(resume: false, ct);
    }

    async Task OpenAsync(bool resume, CancellationToken ct)
    {
        var ch = await _connector!(ct);
        var conn = new Connection(ch);
        conn.Received += (c, m) => { if (c == _conn) Enqueue(m); };
        conn.Closed += (c, e) => { if (c == _conn) OnTransportClosed(e?.Message ?? "connection closed"); };
        _conn = conn;
        conn.Start();
        conn.Send(new Hello(ProtocolInfo.Version, _playerName, _fingerprint,
            resume ? _resumeToken : null, resume ? _lastSeq : null));
    }

    void Enqueue(Message m) => _inbox.Enqueue(m);

    void OnTransportClosed(string reason)
    {
        if (_disposed) return;
        if (_inGame && _resumeToken != null && _connector != null && !_reconnecting)
        {
            _reconnecting = true;
            _inbox.Enqueue(new ErrorMsg("connection_lost", reason));
            _ = ReconnectLoop();
            return;
        }
        if (!_reconnecting) _disconnectReason = reason;
    }

    async Task ReconnectLoop()
    {
        var deadline = DateTime.UtcNow + ReconnectWindow;
        var delay = 500;
        while (!_disposed && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(delay, _cts.Token);
                await OpenAsync(resume: true, _cts.Token);
                return; // Welcome/Resumed will clear _reconnecting
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { delay = Math.Min(delay * 2, 4000); }
        }
        _reconnecting = false;
        _disconnectReason = "connection lost (could not reconnect)";
    }

    public void Send(Message m) => _conn?.Send(m);

    /// <summary>Sends a gameplay command; returns its sequence number for matching rejections.</summary>
    public int SendCommand(System.Text.Json.Nodes.JsonNode command)
    {
        var seq = ++_clientSeq;
        _pending.Add(seq);
        Send(new GameCommand(seq, command));
        return seq;
    }

    public void Poll()
    {
        var now = Environment.TickCount64;
        if (Connected && now - _lastPingSent > 3000)
        {
            _lastPingSent = now;
            Send(new Ping(now));
        }
        while (_inbox.TryDequeue(out var m))
        {
            switch (m)
            {
                case Welcome w:
                    PlayerId = w.PlayerId;
                    _resumeToken = w.ResumeToken ?? _resumeToken;
                    break;
                case Pong pong: PingMs = (int)(Environment.TickCount64 - pong.Ticks); continue;
                case ErrorMsg { Code: "connection_lost" } lost: ConnectionLost?.Invoke(lost.Text); continue;
                case GameStarted: _pending.Clear(); _inGame = true; _lastSeq = 0; break;
                case GameUpdate u:
                    _lastSeq = Math.Max(_lastSeq, u.Seq);
                    if (u.ClientSeq is { } seq) _pending.Remove(seq);
                    break;
                case CommandRejected rej: _pending.Remove(rej.ClientSeq); break;
                case GameEnded: _pending.Clear(); _inGame = false; _lastSeq = -1; break;
                case Resumed:
                    // Commands lost in the outage are dropped; the player simply re-issues them.
                    _pending.Clear();
                    if (_reconnecting) { _reconnecting = false; ConnectionRestored?.Invoke(); }
                    break;
            }
            MessageReceived?.Invoke(m);
        }
        if (_disconnectReason is { } reason)
        {
            _disconnectReason = null;
            PlayerId = 0;
            _inGame = false;
            Disconnected?.Invoke(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _cts.Cancel();
        var c = _conn;
        _conn = null;
        if (c != null) await c.DisposeAsync();
    }
}
