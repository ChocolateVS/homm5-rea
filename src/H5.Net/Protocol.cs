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
    public const int Version = 1;
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
public abstract record Message;

/// <summary>Client → server, first frame. DataFingerprint guards against mismatched game data/mods.</summary>
public sealed record Hello(int ProtocolVersion, string PlayerName, string DataFingerprint) : Message;
public sealed record Welcome(int PlayerId, string ServerName) : Message;
public sealed record ErrorMsg(string Code, string Text) : Message;
public sealed record Ping(long Ticks) : Message;
public sealed record Pong(long Ticks) : Message;

public sealed record ListRooms : Message;
public sealed record RoomSummary(int RoomId, string Name, string Mode, int Players, int MaxPlayers, bool HasPassword, bool InProgress);
public sealed record RoomList(IReadOnlyList<RoomSummary> Rooms) : Message;

public sealed record CreateRoom(string Name, string Mode, int MaxPlayers, string? Password, JsonObject? Settings) : Message;
public sealed record JoinRoom(int RoomId, string? Password) : Message;
public sealed record LeaveRoom : Message;

public sealed record RoomPlayer(int PlayerId, string Name, int Slot, bool Ready, bool IsHost, JsonObject? Options);
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

    public static Message Decode(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<Message>(json, Options) ?? throw new JsonException("null message");
}
