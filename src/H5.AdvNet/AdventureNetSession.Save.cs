using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Net;
using H5.Save;
using H5.Scripting.Runtime;

namespace H5.AdvNet;

/// <summary>A human slot of a network save: the map player and the name of whoever played it.</summary>
public sealed class NetSaveSlot
{
    public int MapPlayer { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Host-side save of a network adventure game (the exe's CSaveScreen&lt;SLanSaveHeaderInfo&gt; / CICSave): the full
/// rules state, the map scripts, the room settings, the human slots and the simultaneous-turns state. Every
/// player's view is derived from the full state (fog and ownership live in AdvState), so no per-client data is kept.
/// </summary>
public sealed class NetSave
{
    public const string Part = "netgame.json";

    public JsonObject Settings { get; set; } = new();
    public string State { get; set; } = "";
    public string? Scripts { get; set; }
    public ulong Seed { get; set; }
    public ulong BattleSeq { get; set; }
    public List<NetSaveSlot> Humans { get; set; } = new();
    public JsonObject? Simul { get; set; }

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SaveArchive ToArchive(string name, H5.Adventure.Engine.Adventure game, string mapDisplayName, string fingerprint)
    {
        var s = game.S;
        var set = AdvNetSettings.From(Settings);
        var save = new SaveArchive
        {
            Header = new SaveHeader
            {
                Name = name, Kind = SaveKind.Multiplayer, SavedUtc = DateTime.UtcNow, DataFingerprint = fingerprint,
                Map = set.Rmg != null
                    ? new MapSpec { Kind = "rmg", Id = game.Map.Id, Generator = new AdvNetSettings { Rmg = set.Rmg }.ToJson() }
                    : new MapSpec { Kind = "file", Id = set.Map ?? game.Map.Id, Source = game.Map.Source, IsUserMap = (set.Map ?? "").StartsWith("user:") },
                MapName = mapDisplayName, MapDescription = game.Map.Description, MapSize = AdventureSave.MapSizeClass(game.Map),
                Players = s.Players.Count, Difficulty = s.Difficulty, Day = s.Day, StateHash = s.ComputeHash(),
            },
        };
        save.SetText(AdventureSave.AdventurePart, State);
        save.SetText(Part, JsonSerializer.Serialize(this, Json));
        return save;
    }

    public static NetSave FromArchive(SaveArchive save)
    {
        var json = save.GetText(Part) ?? throw new SaveCorruptException("not a network game save");
        var n = JsonSerializer.Deserialize<NetSave>(json, Json) ?? throw new SaveCorruptException("damaged network game save");
        n.State = save.GetText(AdventureSave.AdventurePart) ?? throw new SaveCorruptException("save holds no adventure state");
        if (AdvState.Deserialize(n.State).ComputeHash() != save.Header.StateHash)
            throw new SaveCorruptException("restored state does not match the saved state");
        return n;
    }
}

/// <summary>Network saves the host has chosen to reload, by the id carried in the room settings (AdvNetSettings.Load).</summary>
public static class NetSaveLoads
{
    static readonly ConcurrentDictionary<string, NetSave> _loads = new();

    public static string Register(NetSave save)
    {
        var id = Guid.NewGuid().ToString("N");
        _loads[id] = save;
        return id;
    }

    public static NetSave? Get(string id) => _loads.TryGetValue(id, out var s) ? s : null;
    public static void Forget(string id) => _loads.TryRemove(id, out _);

    /// <summary>
    /// The room settings for reloading a save: its own settings plus the load id and the saved human slots
    /// (CMultiplayerHostLoadLogic 00754350: saved humans wait for players, computers and closed slots stay).
    /// </summary>
    public static JsonObject RoomSettings(NetSave save, string id)
    {
        var s = AdvNetSettings.From(save.Settings);
        s.Load = id;
        s.LoadSlots = save.Humans.Select(h => new NetSaveSlot { MapPlayer = h.MapPlayer, Name = h.Name }).ToList();
        return s.ToJson();
    }
}

public sealed partial class AdventureNetSession
{
    /// <summary>
    /// Where the host stores network saves (its profile's save folder, set by the game). Returns an error or null.
    /// A dedicated server without one refuses save orders.
    /// </summary>
    public static Func<SaveArchive, string?>? SaveSink;
    /// <summary>The data fingerprint written into network saves (set by the game).</summary>
    public static string Fingerprint = "";

    (string Name, int Side, int Day)? _saveOrder;
    readonly List<JsonObject> _saveNotices = new();

    /// <summary>
    /// {"$t":"save","name":…}: a player orders a save. Everybody is told at once (CAnnounceSaveGameOrderedCmd 00429300 →
    /// ID_SAVEGAMEORDERED_EVENTMESSAGE "Save was ordered and will be done when every player will end the turn."); the
    /// save is written when the turn has passed (<see cref="RunOrderedSave"/>).
    /// </summary>
    string? OrderSave(int side, string? name)
    {
        if (SaveSink == null) return "this host cannot store saves";
        if (string.IsNullOrWhiteSpace(name)) return "no save name";
        if (_saveOrder != null) return "a save is already ordered";
        _saveOrder = (name.Trim().Length > 64 ? name.Trim()[..64] : name.Trim(), side, _g.S.Day);
        _saveNotices.Add(new JsonObject { ["ordered"] = _saveOrder.Value.Name, ["by"] = _sideName[side] });
        _dirty = true;
        return null;
    }

    /// <summary>
    /// Writes an ordered save once every player has ended the turn: the day has advanced, the computer players are
    /// done and no battle or choice is open. The exact point inside the exe's turn change is not traced.
    /// </summary>
    void RunOrderedSave()
    {
        if (_saveOrder is not { } order || _g.S.Day <= order.Day || _runner != null || _battles.Count > 0
            || _g.S.Battle != null || _g.S.Choice != null || _choices.Count > 0 || !IsHuman(_g.Current.Index) || _g.S.Finished) return;
        _saveOrder = null;
        string? error;
        try { error = SaveSink?.Invoke(Capture().ToArchive(order.Name, _g, _lm.Map.Name, Fingerprint)); }
        catch (Exception e) { error = e.Message; }
        _saveNotices.Add(error == null ? new JsonObject { ["saved"] = order.Name } : new JsonObject { ["failed"] = order.Name, ["error"] = error });
    }

    /// <summary>The host's snapshot of the running game.</summary>
    public NetSave Capture() => new()
    {
        Settings = _set.ToJson(),
        State = _g.S.Serialize(),
        Scripts = _scripts?.Save(),
        Seed = _seed,
        BattleSeq = _battleSeq,
        Humans = _humanPlayers.OrderBy(x => x)
            .Select(mp => new NetSaveSlot { MapPlayer = mp, Name = Array.IndexOf(_sideMapPlayer, mp) is var s and >= 0 ? _sideName[s] : "" }).ToList(),
        Simul = _simul?.Snapshot(),
    };

    /// <summary>
    /// Resumes a saved game (CMultiplayerHostLoadLogic): every saved human slot must be taken by a player of the room
    /// (00754770); a player keeps the slot chosen in the wait screen if it is a saved human slot, else takes the slot
    /// saved under its name, else the first free one. Computer players stay computers.
    /// </summary>
    IReadOnlyList<JsonNode> StartLoaded(IReadOnlyList<RoomPlayer> playersBySide, ulong seed)
    {
        var save = NetSaveLoads.Get(_set.Load!) ?? throw new InvalidOperationException("The saved game is no longer available on the host.");
        var saved = save.Humans.Select(h => h.MapPlayer).ToList();
        if (playersBySide.Count != saved.Count)
            throw new InvalidOperationException($"This saved game needs {saved.Count} players (one for each saved human player).");
        _set = AdvNetSettings.From(save.Settings);
        _seed = save.Seed;
        _battleSeq = save.BattleSeq;
        _lm = _src.Load(_set);
        var state = AdvState.Deserialize(save.State);
        _g = new H5.Adventure.Engine.Adventure(_lm.Db, _lm.Map, state);

        _sideMapPlayer = Enumerable.Repeat(-1, playersBySide.Count).ToArray();
        _sideName = playersBySide.Select(p => p.Name).ToArray();
        var free = saved.ToList();
        var opts = playersBySide.Select(p => AdvPlayerOptions.From(p.Options)).ToList();
        for (int s = 0; s < playersBySide.Count; s++)
            if (free.Remove(opts[s].MapPlayer)) _sideMapPlayer[s] = opts[s].MapPlayer;
        for (int s = 0; s < playersBySide.Count; s++)
            if (_sideMapPlayer[s] < 0 && save.Humans.FirstOrDefault(h => free.Contains(h.MapPlayer) && h.Name.Equals(_sideName[s], StringComparison.OrdinalIgnoreCase)) is { } byName)
            { _sideMapPlayer[s] = byName.MapPlayer; free.Remove(byName.MapPlayer); }
        for (int s = 0; s < playersBySide.Count; s++)
            if (_sideMapPlayer[s] < 0) { _sideMapPlayer[s] = free[0]; free.RemoveAt(0); }
        foreach (var mp in saved) _humanPlayers.Add(mp);
        foreach (var p in _g.S.Players) p.Human = _humanPlayers.Contains(p.Index);

        if (_lm.Files != null && !string.IsNullOrEmpty(_lm.Map.ScriptSource) && save.Scripts != null)
        {
            try
            {
                var fs = _src.Fs;
                _scripts = AdventureScripting.Create(_g, _lm.Files, new EnumTables(fs), new H5.Adventure.Map.SharedCatalog(new H5.Core.Xdb.XdbLoader(fs)));
                _scripts.AutoAnswerUi = true;
                _scripts.Load(save.Scripts);
            }
            catch (Exception) { _scripts = null; }
        }
        return Begin(Array.Empty<AdvEvent>(), save);
    }
}
