using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace H5.Net;

// Wire protocol: each frame is a 4-byte little-endian length followed by a UTF-8
// JSON object with a "$t" discriminator. Gameplay payloads (commands, events,
// snapshots) are opaque JSON so the transport stays independent of the rules
// engine; the game session layer owns their schema.

public static class ProtocolInfo
{
    public const int Version = 2;
    public const int DefaultPort = 27615;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(ErrorMsg), "error")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
[JsonDerivedType(typeof(ListRooms), "list_rooms")]
[JsonDerivedType(typeof(RoomList), "room_list")]
[JsonDerivedType(typeof(CreateRoom), "create_room")]
[JsonDerivedType(typeof(JoinRoom), "join_room")]
[JsonDerivedType(typeof(LeaveRoom), "leave_room")]
[JsonDerivedType(typeof(RoomState), "room_state")]
[JsonDerivedType(typeof(SetReady), "set_ready")]
[JsonDerivedType(typeof(SetPlayerOptions), "set_player_options")]
[JsonDerivedType(typeof(Chat), "chat")]
[JsonDerivedType(typeof(StartGame), "start_game")]
[JsonDerivedType(typeof(GameStarted), "game_started")]
[JsonDerivedType(typeof(GameCommand), "game_command")]
[JsonDerivedType(typeof(GameUpdate), "game_update")]
[JsonDerivedType(typeof(CommandRejected), "command_rejected")]
[JsonDerivedType(typeof(GameEnded), "game_ended")]
[JsonDerivedType(typeof(Resumed), "resumed")]
public abstract record Message;

/// <summary>
/// Client → server, first frame. DataFingerprint guards against mismatched game data/mods.
/// ResumeToken (from an earlier Welcome) reattaches a dropped player to its seat in a
/// running game; LastSeq is the last GameUpdate it applied (null = replay everything).
/// </summary>
public sealed record Hello(int ProtocolVersion, string PlayerName, string DataFingerprint,
    string? ResumeToken = null, int? LastSeq = null) : Message;
/// <summary>Server → client. Keep ResumeToken to reconnect after a dropped connection.</summary>
public sealed record Welcome(int PlayerId, string ServerName, string? ResumeToken = null) : Message;
/// <summary>Sent after a reconnect replay; the client's game is current again.</summary>
public sealed record Resumed(int RoomId, int Seq) : Message;
public sealed record ErrorMsg(string Code, string Text) : Message;
public sealed record Ping(long Ticks) : Message;
public sealed record Pong(long Ticks) : Message;

public sealed record ListRooms : Message;
public sealed record RoomSummary(int RoomId, string Name, string Mode, int Players, int MaxPlayers, bool HasPassword, bool InProgress);
public sealed record RoomList(IReadOnlyList<RoomSummary> Rooms) : Message;

public sealed record CreateRoom(string Name, string Mode, int MaxPlayers, string? Password, JsonObject? Settings) : Message;
public sealed record JoinRoom(int RoomId, string? Password) : Message;
public sealed record LeaveRoom : Message;

/// <summary>Connected is false while a dropped player's seat is held for reconnection.</summary>
public sealed record RoomPlayer(int PlayerId, string Name, int Slot, bool Ready, bool IsHost, JsonObject? Options, bool Connected = true);
public sealed record RoomState(int RoomId, string Name, string Mode, int MaxPlayers, IReadOnlyList<RoomPlayer> Players, JsonObject? Settings, bool InProgress) : Message;
public sealed record SetReady(bool Ready) : Message;
/// <summary>Per-player lobby choices, e.g. {"preset": 4} in duel mode.</summary>
public sealed record SetPlayerOptions(JsonObject Options) : Message;
public sealed record Chat(int FromPlayerId, string Text) : Message;
/// <summary>Host only; the server validates everyone is ready.</summary>
public sealed record StartGame : Message;

/// <summary>Sent to each player when the session starts; Setup is mode-specific (seed, armies, side assignment).</summary>
public sealed record GameStarted(int YourSide, JsonNode Setup) : Message;
public sealed record GameCommand(int ClientSeq, JsonNode Command) : Message;
/// <summary>Authoritative result of an accepted command: events to animate plus the post-state hash for desync checks.</summary>
public sealed record GameUpdate(int Seq, int? ClientSeq, int FromSide, JsonNode Events, string StateHash) : Message;
public sealed record CommandRejected(int ClientSeq, string Reason) : Message;
public sealed record GameEnded(int? WinnerSide, string Reason) : Message;

public static class MessageCodec
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(Message m)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(m, Options);
        var frame = new byte[4 + json.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>The JSON body of a message, without length prefix (transports add their own framing).</summary>
    public static byte[] EncodeBody(Message m) => JsonSerializer.SerializeToUtf8Bytes(m, Options);

    public static Message Decode(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<Message>(json, Options) ?? throw new JsonException("null message");
}
