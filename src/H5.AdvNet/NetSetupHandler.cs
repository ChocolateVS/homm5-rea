using System.Text.Json.Nodes;
using H5.Adventure.Map;
using H5.Net;

namespace H5.AdvNet;

/// <summary>
/// The host side of the network setup screen (CMPUbiComWaitHostLogic): the rows live in the room settings
/// (<see cref="AdvNetSettings.Setup"/>), the lobby server routes joins, leaves, ready toggles and row requests here.
/// </summary>
public sealed partial class AdventureSessionFactory : IRoomSetupHandler
{
    readonly Dictionary<string, (AdvMap Map, H5.Adventure.Data.AdventureDb Db)> _setupMaps = new();

    static string MapKey(AdvNetSettings s) =>
        s.Rmg is { } r ? $"rmg:{System.Text.Json.JsonSerializer.Serialize(r, new System.Text.Json.JsonSerializerOptions { IncludeFields = true })}" : $"map:{s.Map}";

    (AdvMap Map, H5.Adventure.Data.AdventureDb Db) SetupMap(AdvNetSettings s)
    {
        var key = MapKey(s);
        lock (_setupMaps)
        {
            if (_setupMaps.TryGetValue(key, out var m)) return m;
            var lm = Maps.Load(s);
            return _setupMaps[key] = (lm.Map, lm.Db);
        }
    }

    NetGameSetup? Rows(AdvNetSettings s)
    {
        if (!s.NetSetup || s.Setup == null || s.Load != null) return null;
        var (map, db) = SetupMap(s);
        return NetGameSetup.FromRows(map, db, s.Setup);
    }

    static JsonObject Store(AdvNetSettings s, NetGameSetup g)
    {
        s.Setup = g.ToRows();
        return s.ToJson();
    }

    public bool HasSetup(JsonObject? settings)
    {
        var s = AdvNetSettings.From(settings);
        return s.NetSetup && s.Setup != null && s.Load == null;
    }

    public JsonObject? Created(JsonObject? settings, int hostId, string hostName)
    {
        var s = AdvNetSettings.From(settings);
        if (!s.NetSetup || s.Load != null) return null;
        // The rows depend on the generated map, so a random map's seed is fixed now (the start keeps a set seed).
        if (s.Rmg is { Seed: 0 } r) r.Seed = Random.Shared.Next(1, int.MaxValue);
        var (map, db) = SetupMap(s);
        return Store(s, NetGameSetup.Create(map, db, hostId, hostName));
    }

    public (JsonObject? Settings, ErrorMsg? Error) Joining(JsonObject? settings, int playerId, string name, IReadOnlyList<string> inRoom)
    {
        var s = AdvNetSettings.From(settings);
        // 006f8330 / 006f7100: joining a saved game needs the player's name among the saved players; otherwise
        // not_in_save_header / not_in_save_format with the saved names ("players") and the connected ones
        // ("players_in_game"). The client formats the message from the error's JSON.
        if (s.Load != null && s.LoadSlots is { } saved && !saved.Any(x => x.Name == name))
            return (null, new ErrorMsg("not_in_save", new JsonObject
            {
                ["playername"] = name,
                ["players"] = new JsonArray(saved.Select(x => (JsonNode?)x.Name).ToArray()),
                ["players_in_game"] = new JsonArray(inRoom.Select(x => (JsonNode?)x).ToArray()),
            }.ToJsonString()));
        if (Rows(s) is not { } g) return (null, null);
        if (!g.Join(playerId, name)) return (null, new ErrorMsg("room", "That game is full."));
        return (Store(s, g), null);
    }

    public JsonObject? Left(JsonObject? settings, int playerId)
    {
        var s = AdvNetSettings.From(settings);
        if (Rows(s) is not { } g) return null;
        g.Leave(playerId);
        return Store(s, g);
    }

    public JsonObject? Ready(JsonObject? settings, int playerId, bool ready)
    {
        var s = AdvNetSettings.From(settings);
        if (Rows(s) is not { } g || !g.SetReady(playerId, ready)) return null;
        return Store(s, g);
    }

    public bool IsReady(JsonObject? settings, int playerId) =>
        Rows(AdvNetSettings.From(settings))?.RowOf(playerId)?.NetState == NetSlotState.Ready;

    public JsonObject? Request(JsonObject? settings, int playerId, int hostId, SetupRequest request)
    {
        var s = AdvNetSettings.From(settings);
        if (Rows(s) is not { } g) return null;
        if (!g.Request(playerId, hostId, request.Row, (CustomGameSetup.Change)request.Kind, request.Right)) return null;
        return Store(s, g);
    }

    public string? CanStart(JsonObject? settings) =>
        Rows(AdvNetSettings.From(settings)) is { } g && !g.CanStart() ? "Not everyone is ready." : null;
}
