using System.Text.Json;
using System.Text.Json.Nodes;
using H5.Combat;
using H5.Core.Data;
using H5.Net;

namespace H5.Duel;

// Duel mode over the network. The server owns the authoritative battle. Every
// client builds a replica from the same initial state (sent in GameStarted.Setup),
// applies each accepted command from GameUpdate, and compares the resulting
// state hash with the server's to detect desyncs. Replicas let the client UI ask
// the engine for legal actions without a round trip.
//
// GameStarted.Setup: { "side": n, "presets": [a, b], "state": <BattleState json>, "opening": [events] }
// GameCommand.Command: a BattleCommand json ("$t" tagged)
// GameUpdate.Events:  { "command": <BattleCommand json with Side>, "events": [BattleEvent json] }

public sealed class DuelSessionFactory(GameDatabase db) : IGameSessionFactory
{
    public string Mode => "duel";
    public int MinPlayers => 2;
    public int MaxPlayers => 2;

    public string? ValidatePlayerOptions(JsonObject? options)
    {
        if (options?["preset"] is not JsonValue v || !v.TryGetValue<int>(out var p)) return null;
        return p >= 0 && p < db.DuelPresets.Count ? null : $"preset must be 0..{db.DuelPresets.Count - 1}";
    }

    public IGameSession Create() => new DuelSession(db);
}

public sealed class DuelSession(GameDatabase db) : IGameSession
{
    Battle _battle = null!;

    public Battle Battle => _battle;

    public static int PresetOf(RoomPlayer p, int fallback) =>
        p.Options?["preset"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    public IReadOnlyList<JsonNode> Start(IReadOnlyList<RoomPlayer> playersBySide, JsonObject? roomSettings, ulong seed)
    {
        int a = PresetOf(playersBySide[0], 0), b = PresetOf(playersBySide[1], 1);
        var state = BattleSetup.CreateDuel(db, a, b, seed);
        // The initial state is captured before Begin() so replicas reproduce the opening exactly.
        var stateJson = JsonNode.Parse(state.Serialize())!;
        _battle = new Battle(db, state);
        var opening = JsonNode.Parse(MessageJson.Serialize(_battle.Begin()))!;
        return playersBySide.Select((_, side) => (JsonNode)new JsonObject
        {
            ["side"] = side,
            ["presets"] = new JsonArray(a, b),
            ["state"] = stateJson.DeepClone(),
            ["opening"] = opening.DeepClone(),
        }).ToList();
    }

    public SessionResult Apply(int side, JsonNode command)
    {
        BattleCommand cmd;
        try { cmd = MessageJson.DeserializeCommand(command.ToJsonString()); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return new SessionResult.Rejected("malformed command"); }
        // Never trust the client's claimed side.
        cmd = cmd with { Side = side };

        var result = _battle.Apply(cmd);
        if (!result.Ok) return new SessionResult.Rejected(result.Error ?? "rejected");

        var payload = new JsonObject
        {
            ["command"] = JsonNode.Parse(MessageJson.Serialize(cmd)),
            ["events"] = JsonNode.Parse(MessageJson.Serialize(result.Events)),
        };
        GameEnded? ended = null;
        if (_battle.State.Phase == BattlePhase.Finished)
            ended = new GameEnded(_battle.State.Winner, string.IsNullOrEmpty(_battle.State.EndReason) ? "battle over" : _battle.State.EndReason);
        return new SessionResult.Accepted(payload, _battle.State.ComputeHash(), ended);
    }

    public GameEnded? OnSideLeft(int side) =>
        _battle.State.Phase == BattlePhase.Finished ? null : new GameEnded(1 - side, "opponent left");
}

/// <summary>
/// Client-side replica of a networked duel. Feed it GameStarted and GameUpdate
/// messages; it keeps a local Battle in lockstep and reports desyncs.
/// </summary>
public sealed class DuelReplica
{
    public Battle Battle { get; }
    public int MySide { get; }
    public IReadOnlyList<int> Presets { get; }
    public IReadOnlyList<BattleEvent> OpeningEvents { get; }
    public bool Desynced { get; private set; }

    public DuelReplica(GameDatabase db, GameStarted started)
    {
        var setup = started.Setup;
        MySide = started.YourSide;
        Presets = setup["presets"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        Battle = new Battle(db, BattleState.Deserialize(setup["state"]!.ToJsonString()));
        OpeningEvents = Battle.Begin();
    }

    /// <summary>
    /// Applies an authoritative update to the replica. Returns the server's events
    /// (what the client should animate). Sets <see cref="Desynced"/> on mismatch.
    /// </summary>
    public IReadOnlyList<BattleEvent> Apply(GameUpdate update)
    {
        var cmd = MessageJson.DeserializeCommand(update.Events["command"]!.ToJsonString());
        var local = Battle.Apply(cmd);
        if (!local.Ok || Battle.State.ComputeHash() != update.StateHash) Desynced = true;
        return MessageJson.DeserializeEvents(update.Events["events"]!.ToJsonString());
    }

    public bool IsMyTurn => Battle.SideToAct == MySide;
}
