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
