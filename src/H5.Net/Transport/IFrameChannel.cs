namespace H5.Net.Transport;

/// <summary>
/// A bidirectional, reliable, ordered stream of opaque frames (one serialized
/// <see cref="Message"/> each). Implemented over TCP for direct/LAN play and over
/// a reliable UDP channel for NAT-punched or relayed internet play, so the lobby
/// and game protocol above it are identical on every path.
/// </summary>
public interface IFrameChannel : IAsyncDisposable
{
    /// <summary>Human-readable remote description (address, "relay", …).</summary>
    string RemoteName { get; }

    /// <summary>Queues a frame; never blocks on the network.</summary>
    void Send(byte[] frame);

    /// <summary>Raised on a transport thread for every complete frame received.</summary>
    event Action<byte[]>? FrameReceived;

    /// <summary>Raised once when the channel closes (null = orderly close).</summary>
    event Action<Exception?>? Closed;

    void Start();
}
