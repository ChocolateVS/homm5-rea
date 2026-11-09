using System.Text.Json.Nodes;

namespace H5.Net;

/// <summary>
/// Server-side authority for one running game. Implemented per game mode (duel,
/// adventure map…) on top of the rules engines; the lobby server only routes.
/// Implementations are called under the server lock, never concurrently.
/// </summary>
public interface IGameSession
{
    /// <summary>Creates the game. Returns the GameStarted.Setup payload for each side (index = side).</summary>
    IReadOnlyList<JsonNode> Start(IReadOnlyList<RoomPlayer> playersBySide, JsonObject? roomSettings, ulong seed);

    SessionResult Apply(int side, JsonNode command);

    /// <summary>Called when a side disconnects or concedes; returns the outcome if the game ends.</summary>
    GameEnded? OnSideLeft(int side);
}

public abstract record SessionResult
{
    public sealed record Accepted(JsonNode Events, string StateHash, GameEnded? Ended = null) : SessionResult;
    public sealed record Rejected(string Reason) : SessionResult;
    /// <summary>
    /// Accepted, but each side receives its own payload and hash (hidden-information games:
    /// every player only learns what their side may see). Index = side.
    /// </summary>
    public sealed record AcceptedPerSide(IReadOnlyList<JsonNode> EventsBySide, IReadOnlyList<string> HashBySide, GameEnded? Ended = null) : SessionResult;
}

/// <summary>Optional: sessions with timers (turn time limits, real-time energy) are ticked by the server.</summary>
public interface ITickableSession
{
    /// <summary>Called about twice a second under the server lock. Returns an update to broadcast, or null.</summary>
    SessionResult? Tick(long nowMs);
}

/// <summary>Optional: validates host-chosen room settings (map, options) when a room is created or changed.</summary>
public interface IRoomSettingsValidator
{
    string? ValidateRoomSettings(JsonObject? settings);
}

public interface IGameSessionFactory
{
    string Mode { get; }
    int MinPlayers { get; }
    int MaxPlayers { get; }
    /// <summary>Validates per-player lobby options (e.g. chosen preset). Null means valid.</summary>
    string? ValidatePlayerOptions(JsonObject? options);
    IGameSession Create();
}

/// <summary>
/// Optional: factories whose rooms keep a host-authoritative setup (player rows) in the room settings. Each call gets
/// the current settings and returns the new ones, or null when nothing changed / the room has no setup.
/// </summary>
public interface IRoomSetupHandler
{
    /// <summary>True when the room's settings carry a setup (the start then uses <see cref="CanStart"/> instead of the ready flags).</summary>
    bool HasSetup(JsonObject? settings);
    /// <summary>The room was created by <paramref name="hostId"/>; may throw InvalidOperationException to refuse it.</summary>
    JsonObject? Created(JsonObject? settings, int hostId, string hostName);
    /// <summary>A player asks to join: the new settings, or an error message that refuses the join.</summary>
    (JsonObject? Settings, ErrorMsg? Error) Joining(JsonObject? settings, int playerId, string name, IReadOnlyList<string> inRoom);
    JsonObject? Left(JsonObject? settings, int playerId);
    JsonObject? Ready(JsonObject? settings, int playerId, bool ready);
    bool IsReady(JsonObject? settings, int playerId);
    JsonObject? Request(JsonObject? settings, int playerId, int hostId, SetupRequest request);
    /// <summary>Null when the game may start, else the reason.</summary>
    string? CanStart(JsonObject? settings);
}
