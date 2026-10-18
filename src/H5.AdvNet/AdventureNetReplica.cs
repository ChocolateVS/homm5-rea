using System.Reflection;
using System.Text.Json.Nodes;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Combat;
using H5.Net;

namespace H5.AdvNet;

/// <summary>Builders for the commands a client sends in a networked adventure game.</summary>
public static class AdvNetCommands
{
    public static JsonNode Adv(AdvCommand c) => new JsonObject { ["$t"] = "adv", ["cmd"] = JsonNode.Parse(AdvJson.Serialize(c)) };
    public static JsonNode Battle(BattleCommand c) => new JsonObject { ["$t"] = "battle", ["cmd"] = JsonNode.Parse(MessageJson.Serialize(c)) };
    public static JsonNode Chat(string text) => new JsonObject { ["$t"] = "chat", ["text"] = text };
    public static JsonNode GhostMove(int hero, int x, int y, int floor) =>
        new JsonObject { ["$t"] = "ghost", ["action"] = "move", ["hero"] = hero, ["x"] = x, ["y"] = y, ["floor"] = floor };
    public static JsonNode GhostUpgrade(int hero, string skill) => new JsonObject { ["$t"] = "ghost", ["action"] = "upgrade", ["hero"] = hero, ["skill"] = skill };
    public static JsonNode GhostTouch(int hero, string action, int targetObjectOrHero) => action == "curse"
        ? new JsonObject { ["$t"] = "ghost", ["action"] = action, ["hero"] = hero, ["target"] = targetObjectOrHero }
        : new JsonObject { ["$t"] = "ghost", ["action"] = action, ["hero"] = hero, ["object"] = targetObjectOrHero };
}

/// <summary>Client copy of a battle this player fights; kept in lockstep with the host by state hash.</summary>
public sealed class BattleReplica
{
    public Battle Battle { get; }
    public int MySide { get; }
    public bool Desynced { get; private set; }
    public bool Ended { get; private set; }
    public int? Winner { get; private set; }
    public IReadOnlyList<BattleEvent> Opening { get; }

    public BattleReplica(H5.Core.Data.GameDatabase core, JsonObject start)
    {
        MySide = (int)start["mySide"]!;
        Battle = new Battle(core, BattleState.Deserialize(start["state"]!.ToJsonString()));
        Opening = Battle.Begin();
    }

    public bool IsMyTurn => !Ended && (Battle.State.Phase == BattlePhase.Tactics ? !Battle.State.TacticsDone[MySide] : Battle.SideToAct == MySide);

    /// <summary>Applies one authoritative battle update; returns the events to animate.</summary>
    public IReadOnlyList<BattleEvent> Apply(JsonObject update)
    {
        if (update["end"] is JsonObject end) { Ended = true; Winner = (int?)end["winner"]; return Array.Empty<BattleEvent>(); }
        var cmd = MessageJson.DeserializeCommand(update["command"]!.ToJsonString());
        var r = Battle.Apply(cmd);
        if (!r.Ok || Battle.State.ComputeHash() != (string?)update["hash"]) Desynced = true;
        return MessageJson.DeserializeEvents(update["events"]!.ToJsonString());
    }
}

public sealed record ChatLine(int FromSide, string Name, string Scope, string Text);

/// <summary>
/// Client side of a networked adventure game. Holds this player's redacted view of the game in a
/// real <see cref="AdvState"/> wrapped in an <see cref="H5.Adventure.Engine.Adventure"/> so the
/// existing adventure screen can render it and query paths, but never simulates: commands go to
/// the host, and every update replaces the view (applied as a patch and verified by hash).
/// </summary>
public sealed class AdventureNetReplica
{
    public LoadedMap Map { get; }
    public H5.Adventure.Engine.Adventure Game { get; }
    public AdvState View => Game.S;
    public int MySide { get; }
    public int MyPlayer { get; }
    public AdvNetSettings Settings { get; }
    public IReadOnlyList<(int Side, int MapPlayer, string Name)> Players { get; }
    public bool Desynced { get; private set; }
    public BattleReplica? Battle { get; private set; }
    public JsonObject? Ghosts { get; private set; }
    public JsonObject? Turn { get; private set; }

    /// <summary>Events to animate, in order (adventure events and battle events interleaved by arrival).</summary>
    public readonly Queue<AdvEvent> Events = new();
    public readonly Queue<(BattleReplica Battle, IReadOnlyList<BattleEvent> Events)> BattleEvents = new();
    public readonly Queue<ChatLine> Chat = new();
    /// <summary>Raised when a battle this player fights begins (the client opens the combat screen).</summary>
    public event Action<BattleReplica>? BattleStarted;
    public event Action<BattleReplica>? BattleEnded;

    JsonNode _viewNode;

    public AdventureNetReplica(AdvMapSource maps, GameStarted started)
    {
        var s = started.Setup.AsObject();
        MySide = started.YourSide;
        MyPlayer = (int)s["mapPlayer"]!;
        Settings = AdvNetSettings.From(s["settings"]?.AsObject());
        var mapInfo = s["map"]!.AsObject();
        byte[]? userMap = mapInfo["userMap"] is JsonValue b ? Convert.FromBase64String((string)b!) : null;
        Map = maps.Load(Settings, userMap);
        if (MapHash.Of(Map.Map) != (string?)mapInfo["hash"])
            throw new InvalidDataException("This map differs from the host's copy (different version or random map generator output).");
        Players = s["players"]!.AsArray().Select(p => ((int)p!["side"]!, (int)p["mapPlayer"]!, (string)p["name"]!)).ToList();

        _viewNode = s["view"]!.DeepClone();
        if (JsonDiff.CanonicalHash(_viewNode) != (string?)s["hash"]) Desynced = true;
        Game = new H5.Adventure.Engine.Adventure(Map.Db, Map.Map, AdvState.Deserialize(_viewNode.ToJsonString()));
        Absorb(s);
    }

    /// <summary>Applies an authoritative update from the host.</summary>
    public void Apply(GameUpdate update)
    {
        var p = update.Events.AsObject();
        try { _viewNode = JsonDiff.Apply(_viewNode, p["view"]!.AsArray())!; }
        catch (Exception) { Desynced = true; return; }
        if (JsonDiff.CanonicalHash(_viewNode) != update.StateHash) Desynced = true;
        CopyInto(View, AdvState.Deserialize(_viewNode.ToJsonString()));
        Absorb(p);
    }

    void Absorb(JsonObject p)
    {
        if (p["events"] is JsonArray evs)
            foreach (var e in AdvJson.DeserializeEvents(evs.ToJsonString())) Events.Enqueue(e);
        if (p["battle"] is JsonArray bs)
            foreach (var n in bs)
            {
                var o = n!.AsObject();
                if (o["start"] is JsonObject start)
                {
                    Battle = new BattleReplica(Map.Db.Core, start);
                    BattleEvents.Enqueue((Battle, Battle.Opening));
                    BattleStarted?.Invoke(Battle);
                }
                else if (Battle != null)
                {
                    var evts = Battle.Apply(o);
                    if (Battle.Desynced) Desynced = true;
                    BattleEvents.Enqueue((Battle, evts));
                    if (Battle.Ended) { var ended = Battle; Battle = null; BattleEnded?.Invoke(ended); }
                }
            }
        if (p["chat"] is JsonArray chat)
            foreach (var c in chat)
                Chat.Enqueue(new ChatLine((int)c!["from"]!, (string)c["name"]!, (string)c["scope"]!, (string)c["text"]!));
        if (p["ghosts"] is JsonObject g) Ghosts = g;
        if (p["turn"] is JsonObject t) Turn = t;
    }

    public bool IsMyTurn => !View.Finished && View.CurrentPlayer >= 0 && View.Players[View.CurrentPlayer].Index == MyPlayer && Battle == null && Turn?["battle"]?.GetValue<bool>() != true;

    static readonly FieldInfo[] StateFields = typeof(AdvState).GetFields(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Replaces the view in place so references held by the UI stay valid.</summary>
    static void CopyInto(AdvState target, AdvState source)
    {
        foreach (var f in StateFields) f.SetValue(target, f.GetValue(source));
    }
}
