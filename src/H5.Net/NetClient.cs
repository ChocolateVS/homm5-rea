using System.Collections.Concurrent;

namespace H5.Net;

/// <summary>
/// Client side of the protocol. Incoming messages are queued; the owner calls
/// <see cref="Poll"/> from its main loop (e.g. Godot's _Process) so handlers run
/// on the game thread.
/// </summary>
public sealed class NetClient : IAsyncDisposable
{
    readonly ConcurrentQueue<Message> _inbox = new();
    Connection? _conn;
    int _clientSeq;

    public int PlayerId { get; private set; }
    public bool Connected => _conn != null && PlayerId != 0;
    public event Action<Message>? MessageReceived;
    public event Action<string>? Disconnected;
    volatile string? _disconnectReason;

    public async Task ConnectAsync(string host, int port, string playerName, string dataFingerprint, CancellationToken ct = default)
    {
        _conn = await Connection.ConnectAsync(host, port, ct);
        _conn.Received += (_, m) => _inbox.Enqueue(m);
        _conn.Closed += (_, e) => _disconnectReason = e?.Message ?? "connection closed";
        _conn.Start();
        _conn.Send(new Hello(ProtocolInfo.Version, playerName, dataFingerprint));
    }

    public void Send(Message m) => _conn?.Send(m);

    readonly HashSet<int> _pending = new();

    /// <summary>Commands sent but not yet answered by a GameUpdate or CommandRejected.</summary>
    public int PendingCommands => _pending.Count;

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
        while (_inbox.TryDequeue(out var m))
        {
            switch (m)
            {
                case Welcome w: PlayerId = w.PlayerId; break;
                case GameUpdate { ClientSeq: { } seq }: _pending.Remove(seq); break;
                case CommandRejected rej: _pending.Remove(rej.ClientSeq); break;
                case GameEnded or GameStarted: _pending.Clear(); break;
            }
            MessageReceived?.Invoke(m);
        }
        if (_disconnectReason is { } reason)
        {
            _disconnectReason = null;
            PlayerId = 0;
            Disconnected?.Invoke(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn != null) await _conn.DisposeAsync();
        _conn = null;
    }
}
