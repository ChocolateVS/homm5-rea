using System.Text.Json;
using System.Text.Json.Serialization;

namespace H5.Net.Master;

// Shared contract between game clients/hosts and the master server (src/H5.Master).
// HTTP (JSON) lists and advertises lobbies; UDP "rendezvous" datagrams (UdpKind.Rendezvous)
// exchange public endpoints for NAT hole punching; UDP "relay" envelopes (UdpKind.Relay)
// carry game traffic through the master when punching fails.

/// <summary>What a host advertises when it publishes an online lobby.</summary>
public sealed record LobbyAd(string Name, string Mode, int MaxPlayers, bool HasPassword,
    int ProtocolVersion, string Fingerprint, int Players, int OpenRooms);

/// <summary>Response to publishing: the lobby id, its short join code and the host's secret.</summary>
public sealed record LobbyCreated(string Id, string Code, string HostToken, int UdpPort);

/// <summary>Heartbeat body (sent every few seconds by the host).</summary>
public sealed record LobbyHeartbeat(int Players, int OpenRooms);

/// <summary>A public lobby as listed by the master.</summary>
public sealed record OnlineLobby(string Id, string Code, string Name, string Mode, int Players, int MaxPlayers,
    bool HasPassword, int ProtocolVersion, string Fingerprint, int OpenRooms, int AgeSeconds);

public sealed record MasterInfo(int UdpPort, int ProtocolVersion, string Motd);

/// <summary>UDP rendezvous message; "t" selects the kind, other fields depend on it.</summary>
public sealed record RvMessage
{
    [JsonPropertyName("t")] public string T { get; init; } = "";
    public string? Lobby { get; init; }
    public string? Token { get; init; }
    public string? Code { get; init; }
    public ulong Nonce { get; init; }
    public ulong Session { get; init; }
    /// <summary>The sender's public endpoint as the master sees it.</summary>
    public string? Public { get; init; }
    /// <summary>LAN addresses the sender is reachable on (for peers behind the same NAT).</summary>
    public string[]? Local { get; init; }
    /// <summary>The other peer's endpoints (public first).</summary>
    public string[]? Peer { get; init; }
    public string? Error { get; init; }
    /// <summary>WebRTC session description (offer/answer) for browser peer-to-peer.</summary>
    public string? Sdp { get; init; }
    /// <summary>WebRTC ICE candidate (JSON as produced by the browser/Godot WebRTC API).</summary>
    public string? Candidate { get; init; }

    // Kinds
    public const string Register = "register";      // host → master: keep NAT mapping + record public endpoint
    public const string Registered = "registered";  // master → host
    public const string Join = "join";              // joiner → master: wants lobby (by code or id)
    public const string PeerInfo = "peer";          // master → joiner: host endpoints + relay session
    public const string Incoming = "incoming";      // master → host: a joiner is coming; punch towards it
    public const string Fail = "fail";              // master → peer: error
    // WebRTC signalling, relayed by the master between a joiner and a lobby host (Session identifies the pair).
    public const string RtcOffer = "rtc_offer";
    public const string RtcAnswer = "rtc_answer";
    public const string RtcIce = "rtc_ice";
    public const string RtcUnsupported = "rtc_unsupported"; // host can't do WebRTC: joiner uses the WebSocket bridge

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, Json);
    public static RvMessage? Decode(ReadOnlySpan<byte> d)
    {
        try { return JsonSerializer.Deserialize<RvMessage>(d, Json); }
        catch (JsonException) { return null; }
    }
}

public static class JoinCodes
{
    // No 0/O, 1/I/L: codes are read out loud and typed.
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public static string Normalize(string code) =>
        new string(code.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
}
