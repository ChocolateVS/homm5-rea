using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace H5.Net.Transport;

/// <summary>
/// Lets one TCP port serve both native clients (length-prefixed frames) and
/// WebSocket clients (HTTP upgrade), so a host or dedicated server needs a single
/// port and works behind an HTTP reverse proxy for wss://.
/// </summary>
public static class PortSharing
{
    /// <summary>Inspects the first bytes of an accepted connection and returns the matching channel.</summary>
    public static async Task<IFrameChannel> AcceptAsync(TcpClient tcp, CancellationToken ct)
    {
        var stream = tcp.GetStream();
        var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        var head = new byte[4];
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.ReadExactlyAsync(head, timeout.Token);
        }
        if (head.AsSpan().SequenceEqual("GET "u8))
        {
            var ws = await UpgradeAsync(stream, head, ct);
            return new WebSocketFrameChannel(ws, remote + " (ws)");
        }
        return new TcpFrameChannel(tcp, new PrefixedStream(head, stream));
    }

    static async Task<WebSocket> UpgradeAsync(NetworkStream stream, byte[] head, CancellationToken ct)
    {
        // Read the HTTP request head (bounded) byte by byte so no WebSocket bytes are consumed.
        var sb = new StringBuilder(Encoding.ASCII.GetString(head));
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            if (sb.Length > 8192) throw new InvalidDataException("HTTP request too large");
            await stream.ReadExactlyAsync(one, ct);
            sb.Append((char)one[0]);
        }
        var lines = sb.ToString().Split("\r\n");
        string? key = null; bool upgrade = false;
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            var name = line[..c].Trim();
            var value = line[(c + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) && value.Equals("websocket", StringComparison.OrdinalIgnoreCase)) upgrade = true;
        }
        if (!upgrade || key == null)
        {
            var body = "H5Reborn game server. Connect with the game or a WebSocket client."u8.ToArray();
            var resp = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(resp.Concat(body).ToArray(), ct);
            throw new InvalidDataException("plain HTTP request");
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
    }

    /// <summary>Replays already-read bytes before continuing with the underlying stream.</summary>
    sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_pos < prefix.Length) { int n = Math.Min(buffer.Length, prefix.Length - _pos); prefix.AsSpan(_pos, n).CopyTo(buffer); _pos += n; return n; }
            return inner.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < prefix.Length) { int n = Math.Min(buffer.Length, prefix.Length - _pos); prefix.AsSpan(_pos, n).CopyTo(buffer.Span); _pos += n; return ValueTask.FromResult(n); }
            return inner.ReadAsync(buffer, ct);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
