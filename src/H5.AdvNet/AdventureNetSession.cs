using System.Text.Json;
using System.Text.Json.Nodes;
using H5.Adventure.AI;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Combat.AI;
using H5.Net;
using H5.Scripting.Runtime;
using AdvGameEnded = H5.Adventure.Engine.GameEnded;
using NetGameEnded = H5.Net.GameEnded;

namespace H5.AdvNet;

/// <summary>Lobby mode "adventure": networked adventure-map games (stock, user and random maps).</summary>
public sealed class AdventureSessionFactory(AdvMapSource maps) : IGameSessionFactory, IRoomSettingsValidator
{
    public string Mode => "adventure";
    public int MinPlayers => 1;
    public int MaxPlayers => 8;
    public AdvMapSource Maps => maps;
    GhostData? _ghosts;

    public string? ValidatePlayerOptions(JsonObject? options)
    {
        var o = AdvPlayerOptions.From(options);
        if (o.MapPlayer is < -1 or > 7) return "Invalid player slot.";
        if (o.Race != null && !o.Race.StartsWith("TOWN_")) return "Invalid race.";
        return null;
    }

    public string? ValidateRoomSettings(JsonObject? settings)
    {
        var s = AdvNetSettings.From(settings);
        if (s.Validate() is { } err) return err;
        if (s.Rmg != null && !maps.Rmg.Templates.ContainsKey(s.Rmg.Template)) return $"Unknown random map template '{s.Rmg.Template}'.";
        if (s.Map != null && MapCatalog.Find(maps.Fs, null, s.Map) == null && !s.Map.StartsWith("user:")) return $"Map '{s.Map}' is not installed on the host.";
        return null;
    }

    public IGameSession Create() => new AdventureNetSession(maps, _ghosts ??= GhostData.Load(maps.Fs, maps.Db.Core.Stats));
}

/// <summary>
/// Authoritative host side of a networked adventure game. Owns the full rules engine (plus the
/// map script host), plays computer players with the ported original AI, runs battles that involve
/// humans interactively (<see cref="NetBattle"/>), and sends each side only its redacted view
/// (<see cref="ViewFilter"/>) as a JSON patch plus the events it may see.
///
/// Commands (GameCommand.Command):
///   {"$t":"adv","cmd":AdvCommand}        adventure command for your map player (on your turn)
///   {"$t":"battle","cmd":BattleCommand}   command in a battle you take part in
///   {"$t":"ghost", …}                     ghost-mode action while another player moves
///   {"$t":"chat","text":"…"}              in-game chat; "/a" all, "/t" team, "/&lt;name&gt; " private (UI/MPChatInGame)
/// Update payload per side: {"events":[AdvEvent], "view":patch, "battle":[…], "chat":[…], "ghosts":{…}, "turn":{…}}.
/// </summary>
public sealed class AdventureNetSession : IGameSession, ITickableSession
{
    readonly AdvMapSource _src;
    readonly GhostData _ghostData;
    readonly Func<long> _clock;
    readonly OriginalAdventureAI _ai = new();
    readonly IBattleAI _battleAi = BattleAiRegistry.Create("original");

    AdvNetSettings _set = new();
    LoadedMap _lm = null!;
    H5.Adventure.Engine.Adventure _g = null!;
    AdventureScripting? _scripts;
    GhostMode? _ghosts;
    /// <summary>
    /// Running interactive battles. Every player holds its own combat (CPlayer vf +0x8c; both side owners get it
    /// when it starts, 008545b0), so during the simultaneous phase several run at once while the other humans
    /// keep moving. The engine's single pending-battle slot is parked here while a battle runs and put back
    /// to apply the result.
    /// </summary>
    readonly List<RunningBattle> _battles = new();
    /// <summary>Simultaneous phase: each human's pending adventure choice (the offer goes to that player only, 00a7d0e0).</summary>
    readonly SortedDictionary<int, PendingChoice> _choices = new();
    /// <summary>Per map player: combat time credited to its turn timer (CPlayer vf +0x5c/+0x60, CCloseCombat 004e0f10).</summary>
    readonly Dictionary<int, long> _combatCredit = new();
    int _turnStamp;
    AiRunner? _runner;
    SimultaneousTurns? _simul;
    int _phaseDay = -1;
    bool _wasInPhase;
    int[] _shownCurrent = Array.Empty<int>();
    int _shownDay = -1;
    ulong _seed, _battleSeq;
    int[] _sideMapPlayer = Array.Empty<int>();
    string[] _sideName = Array.Empty<string>();
    readonly HashSet<int> _humanPlayers = new();   // map players controlled by a connected human
    JsonNode?[] _lastView = Array.Empty<JsonNode?>();
    int _lastTurnPlayer = -1;
    long _turnStartMs, _lastTickMs;
    double _scriptCarryMs;

    // Output accumulated since the last result.
    readonly List<(AdvEvent Event, int Actor)> _events = new();
    readonly Dictionary<int, (int Owner, Tile Pos)> _heroPos = new();
    readonly Dictionary<int, List<JsonObject>> _battleOut = new();
    readonly List<(int From, string Scope, int? To, string Text)> _chat = new();
    bool _dirty;

    public AdventureNetSession(AdvMapSource src, GhostData ghostData, Func<long>? clock = null)
    {
        _src = src;
        _ghostData = ghostData;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    // For tests and tools.
    public H5.Adventure.Engine.Adventure Game => _g;
    public AdvMap Map => _lm.Map;
    public NetBattle? ActiveBattle => _battles.Count > 0 ? _battles[0].Net : null;
    public IReadOnlyList<NetBattle> Battles => _battles.Select(b => b.Net).ToList();
    /// <summary>The battle map player <paramref name="mapPlayer"/> takes part in (it "holds a combat"), if any.</summary>
    public NetBattle? BattleOf(int mapPlayer) => Running(mapPlayer)?.Net;
    /// <summary>What <paramref name="mapPlayer"/>'s client sees (its own pending battle and choice included).</summary>
    public JsonNode ViewFor(int mapPlayer) => WithOwnSlots(mapPlayer, () => ViewFilter.ViewFor(_g.S, _lm.Map, mapPlayer, _simul?.ViewCurrentFor(mapPlayer)));

    sealed record RunningBattle(NetBattle Net, PendingBattle Pending, int AtkOwner, int DefOwner, int Stamp, bool ForAi);

    RunningBattle? Running(int mapPlayer) => mapPlayer < 0 ? null : _battles.Find(b => b.AtkOwner == mapPlayer || b.DefOwner == mapPlayer);

    /// <summary>Runs <paramref name="f"/> with <paramref name="mapPlayer"/>'s own battle and choice in the engine's slots.</summary>
    T WithOwnSlots<T>(int mapPlayer, Func<T> f)
    {
        var (battle, choice) = (_g.S.Battle, _g.S.Choice);
        if (Running(mapPlayer) is { } rb) _g.S.Battle = rb.Pending;
        if (_choices.TryGetValue(mapPlayer, out var c)) _g.S.Choice = c;
        try { return f(); }
        finally { (_g.S.Battle, _g.S.Choice) = (battle, choice); }
    }

    /// <summary>Puts the player's parked choice back into the engine before it acts.</summary>
    void Unpark(int mapPlayer)
    {
        if (_g.S.Choice == null && _choices.Remove(mapPlayer, out var c)) _g.S.Choice = c;
    }

    /// <summary>Simultaneous phase: the player's pending choice moves to its own slot, so the others keep playing.</summary>
    void Park(int mapPlayer)
    {
        if (_simul is not { InPhase: true } || _g.S.Choice is not { } c) return;
        if (_g.S.Hero(c.Hero) is { } h && h.Owner != mapPlayer) return;
        _choices[mapPlayer] = c;
        _g.S.Choice = null;
    }

    int OwnerOfTarget(int defenderHero, int defenderObject)
    {
        if (defenderHero >= 0) return _g.S.Heroes.Find(h => h.Id == defenderHero)?.Owner ?? -1;
        if (defenderObject < 0) return -1;
        if (_g.S.Town(defenderObject) is { } t) return t.Owner;
        return _g.S.Objects.TryGetValue(defenderObject, out var o) ? o.Owner : -1;
    }

    /// <summary>
    /// 00426b30 (simultaneous turns only): the target's owner holds a combat (CPlayer vf +0x8c / +0x90), or the
    /// target is the defending army (CCombat+0x90, vf +0x84(false)) of a running combat.
    /// </summary>
    bool AttackRefused(int defenderHero, int defenderObject)
    {
        if (_simul is not { Active: true }) return false;
        if (Running(OwnerOfTarget(defenderHero, defenderObject)) != null) return true;
        return _battles.Any(b => (defenderHero >= 0 && b.Pending.DefenderHero == defenderHero)
            || (defenderObject >= 0 && b.Pending.DefenderObject == defenderObject));
    }
    public SimultaneousTurns? Simultaneous => _simul;
    public int MapPlayerOf(int side) => _sideMapPlayer[side];

    public IReadOnlyList<JsonNode> Start(IReadOnlyList<RoomPlayer> playersBySide, JsonObject? roomSettings, ulong seed)
    {
        _set = AdvNetSettings.From(roomSettings);
        _seed = seed;
        if (_set.Rmg is { Seed: 0 } r) r.Seed = (int)(seed % int.MaxValue) | 1;
        _lm = _src.Load(_set);
        var map = _lm.Map;

        // Seats: each side takes its chosen map player if free and human-capable, else the next free one.
        var free = map.Players.Where(p => p.Active && p.CanBeHuman).Select(p => p.Index).ToList();
        if (free.Count < playersBySide.Count) throw new InvalidOperationException($"This map has room for {free.Count} human players.");
        _sideMapPlayer = new int[playersBySide.Count];
        _sideName = playersBySide.Select(p => p.Name).ToArray();
        var opts = playersBySide.Select(p => AdvPlayerOptions.From(p.Options)).ToList();
        for (int s = 0; s < playersBySide.Count; s++)
            if (free.Contains(opts[s].MapPlayer)) { _sideMapPlayer[s] = opts[s].MapPlayer; free.Remove(opts[s].MapPlayer); }
            else _sideMapPlayer[s] = -1;
        for (int s = 0; s < playersBySide.Count; s++)
            if (_sideMapPlayer[s] < 0) { _sideMapPlayer[s] = free[0]; free.RemoveAt(0); }

        var setup = new GameSetup { Seed = seed, Difficulty = _set.Difficulty };
        foreach (var p in map.Players.Where(p => p.Active))
        {
            int side = Array.IndexOf(_sideMapPlayer, p.Index);
            setup.Players.Add(new PlayerSetup { MapPlayer = p.Index, Human = side >= 0, Race = side >= 0 ? opts[side].Race : null });
            if (side >= 0) _humanPlayers.Add(p.Index);
        }
        var (game, opening) = H5.Adventure.Engine.Adventure.NewGame(_lm.Db, map, setup);
        _g = game;
        StartScripts();
        var inner = _g.CommandSink;
        _g.CommandSink = cmd => Intercept(cmd) ?? (inner != null ? inner(cmd) : _g.Apply(cmd));
        _g.Observer = (cmd, res) =>
        {
            if (!res.Ok) return;
            foreach (var e in res.Events) _events.Add((e, cmd.Player));
            _simul?.OnCommand(cmd);
        };
        _g.AttackRefused = (_, dh, dobj) => AttackRefused(dh, dobj);
        if (_set.SimultaneousTurns)
        {
            // Humans first, then the computer players (00998060); all humans move at once until areas meet.
            _simul = new SimultaneousTurns(_g, IsHuman, _set.Pest ? new PestSimultaneousPolicy() : new OriginalSimultaneousPolicy());
            _g.TurnOrder = i => _g.HumansFirstOrder(i, IsHuman);
            int firstHuman = _g.HumansFirstOrder(-1, IsHuman);
            if (firstHuman >= 0) _g.S.CurrentPlayer = firstHuman;
        }
        if (_set.GhostMode) _ghosts = new GhostMode(_ghostData, _g);
        if (_set.DevStartAdjacent) PlaceSecondHumanNextToFirst();

        CaptureHeroes();
        foreach (var e in opening) _events.Add((e, -1));
        _lastTickMs = _clock();
        Continue();

        // Setup payload: the map to load, the initial view and whatever happened already.
        var mapInfo = new JsonObject
        {
            ["id"] = map.Id,
            ["name"] = map.Name,
            ["hash"] = MapHash.Of(map),
        };
        if (_lm.UserMapBytes is { Length: <= 16 * 1024 * 1024 } bytes) mapInfo["userMap"] = Convert.ToBase64String(bytes);
        var players = new JsonArray();
        for (int s = 0; s < _sideMapPlayer.Length; s++)
            players.Add(new JsonObject { ["side"] = s, ["mapPlayer"] = _sideMapPlayer[s], ["name"] = _sideName[s] });

        _lastView = new JsonNode?[_sideMapPlayer.Length];
        var first = (SessionResult.AcceptedPerSide)BuildResult(-1);
        var setups = new List<JsonNode>();
        for (int s = 0; s < _sideMapPlayer.Length; s++)
        {
            var payload = first.EventsBySide[s].AsObject();
            setups.Add(new JsonObject
            {
                ["mode"] = "adventure",
                ["side"] = s,
                ["mapPlayer"] = _sideMapPlayer[s],
                ["settings"] = _set.ToJson(),
                ["map"] = mapInfo.DeepClone(),
                ["players"] = players.DeepClone(),
                ["view"] = _lastView[s]!.DeepClone(),
                ["hash"] = first.HashBySide[s],
                ["events"] = payload["events"]?.DeepClone(),
                ["battle"] = payload["battle"]?.DeepClone(),
                ["ghosts"] = payload["ghosts"]?.DeepClone(),
                ["turn"] = payload["turn"]?.DeepClone(),
                ["simul"] = payload["simul"]?.DeepClone(),
            });
        }
        return setups;
    }

    void StartScripts()
    {
        if (_lm.Files == null || string.IsNullOrEmpty(_lm.Map.ScriptSource)) return;
        try
        {
            var fs = _src.Fs;
            _scripts = AdventureScripting.Create(_g, _lm.Files, new EnumTables(fs), new SharedCatalog(new H5.Core.Xdb.XdbLoader(fs)));
            // Map-script dialogs cannot block a shared game: they are answered on the host and
            // their texts are passed on as messages.
            _scripts.AutoAnswerUi = true;
            _scripts.Start(_lm.Map.ScriptPath, _lm.Map.ScriptSource);
        }
        catch (Exception) { _scripts = null; }
    }

    // ------------------------------------------------------------------ commands

    public SessionResult Apply(int side, JsonNode command)
    {
        if (side < 0 || side >= _sideMapPlayer.Length) return new SessionResult.Rejected("not a player");
        int me = _sideMapPlayer[side];
        if (command is not JsonObject c) return new SessionResult.Rejected("malformed command");
        CaptureHeroes();
        string? err = (string?)c["$t"] switch
        {
            "adv" => DoAdv(me, c["cmd"]),
            "battle" => DoBattle(side, c["cmd"]),
            "ghost" => _ghosts == null ? "ghost mode is off" : MarkDirty(_ghosts.Command(me, c)),
            "chat" => DoChat(side, (string?)c["text"]),
            "simul" => DoSimul(me, (string?)c["action"]),
            _ => "unknown command",
        };
        if (err != null) return new SessionResult.Rejected(err);
        return BuildResult(me);
    }

    string? MarkDirty(string? err) { if (err == null) _dirty = true; return err; }

    string? DoAdv(int me, JsonNode? cmdNode)
    {
        if (_g.S.Finished) return "the game is over";
        // A player in a battle is on its combat screen; in sequential turns nobody else moves meanwhile.
        if (Running(me) != null || (_simul is not { InPhase: true } && _battles.Count > 0)) return "a battle is in progress";
        AdvCommand cmd;
        try { cmd = AdvJson.DeserializeCommand(cmdNode!.ToJsonString()); }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException) { return "malformed command"; }
        if (cmd is ResolveBattleCmd) return "battles are resolved by the host";
        if (_simul is { InPhase: true } sim)
        {
            if (!sim.Playing(me)) return IsHuman(me) ? "wait for other players" : "not your turn";
            if (cmd is EndTurnCmd)
            {
                if (_choices.ContainsKey(me)) return "a choice must be answered first";
                if (_g.S.Heroes.Any(h => h.Owner == me && h.Alive && h.PendingLevelUps.Count > 0)) return "a hero has a pending level-up";
                sim.EndTurn(me);
                Continue();
                return null;
            }
            sim.Enter(me);
        }
        else if (_g.Current.Index != me || !_humanPlayers.Contains(me)) return "not your turn";
        Unpark(me);
        var r = Send(cmd with { Player = me });
        if (_g.S.Battle == null) Park(me);
        if (!r.Ok) return r.Error ?? "rejected";
        Continue();
        return null;
    }

    string? DoBattle(int side, JsonNode? cmdNode)
    {
        if (_battles.Find(b => b.Net.BattleSideOf(side) != null) is not { } rb) return "no battle in progress";
        BattleCommand cmd;
        try { cmd = MessageJson.DeserializeCommand(cmdNode!.ToJsonString()); }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException) { return "malformed command"; }
        var updates = new List<JsonObject>();
        var err = rb.Net.ApplyHuman(side, cmd, updates, _clock());
        if (err != null) return err;
        PublishBattle(rb, updates);
        if (rb.Net.Finished) FinishBattle(rb);
        Continue();
        return null;
    }

    /// <summary>{"$t":"simul","action":"approve"}: CApproveSimultaneousConflict (004289a0), sent by clients automatically.</summary>
    string? DoSimul(int me, string? action)
    {
        if (_simul == null) return "simultaneous turns are off";
        if (action != "approve") return "unknown action";
        var err = _simul.Approve(me);
        if (err != null) return err;
        Continue();
        _dirty = true;
        return null;
    }

    string? DoChat(int side, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "empty message";
        text = text.Length > 500 ? text[..500] : text;
        string scope = "all";
        int? to = null;
        if (text.StartsWith("/a ")) text = text[3..];
        else if (text.StartsWith("/t ")) { scope = "team"; text = text[3..]; }
        else if (text.StartsWith('/'))
        {
            var space = text.IndexOf(' ');
            var name = space > 1 ? text[1..space] : "";
            int target = Array.FindIndex(_sideName, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (target < 0) return $"no player called {name}";
            scope = "private";
            to = target;
            text = text[(space + 1)..];
        }
        _chat.Add((side, scope, to, text));
        _dirty = true;
        return null;
    }

    AdvResult Send(AdvCommand cmd) => _g.CommandSink?.Invoke(cmd) ?? _g.Apply(cmd);

    // ------------------------------------------------------------------ flow

    bool IsHuman(int mapPlayer) => mapPlayer >= 0 && _humanPlayers.Contains(mapPlayer);

    int SideOf(int mapPlayer) => IsHuman(mapPlayer) ? Array.IndexOf(_sideMapPlayer, mapPlayer) : -1;

    /// <summary>Runs everything that needs no human: battles to set up, computer turns, turn bookkeeping.</summary>
    void Continue()
    {
        for (int guard = 0; guard < 1000; guard++)
        {
            if (_simul != null && _runner == null && _simul.Step(combatRunning: _battles.Count > 0) is var last and >= 0)
            {
                // Every human ended the simultaneous phase: the turn passes on (computer players, new day).
                Send(new EndTurnCmd { Player = last });
                continue;
            }
            OnTurnMaybeChanged();
            if (_g.S.Finished) return;
            if (_runner is { WaitingBattle: true }) return;
            if (_g.S.Battle != null && IsHuman(_g.Current.Index))
            {
                // The human's own move started a battle.
                var (quick, rb) = StartBattle();
                if (quick != null) Send(quick);
                else if (rb!.Net.Finished) FinishBattle(rb);
                continue;
            }
            if (!IsHuman(_g.Current.Index))
            {
                if (_battles.Count > 0) return;
                _runner ??= new AiRunner(this);
                _runner.RunUntilHumanOrBattle();
                if (_runner.Error is { } e) { _runner = null; ForceEndAiTurn(e); }
                else if (!_runner.WaitingBattle) _runner = null;
                continue;
            }
            return;
        }
    }

    void ForceEndAiTurn(Exception e)
    {
        _events.Add((new H5.Adventure.Engine.Message("ai_error", $"Computer player error: {e.Message}"), -1));
        if (_g.S.Battle != null) Send(QuickCombat.Resolve(_g, NextSeed(), _battleAi));
        if (!_g.S.Finished && !IsHuman(_g.Current.Index)) Send(new EndTurnCmd { Player = _g.Current.Index });
    }

    void OnTurnMaybeChanged()
    {
        if (_simul is { InPhase: true })
        {
            // One turn start for the whole humans' phase (CWorld+0x104, 00998520); ghosts stay idle (GAP).
            if (_phaseDay != _g.S.Day) { _phaseDay = _g.S.Day; NewTurnClock(); }
            _lastTurnPlayer = _g.S.CurrentPlayer;
            _wasInPhase = true;
            return;
        }
        if (_wasInPhase) { _wasInPhase = false; _lastTurnPlayer = -1; }
        if (_g.S.CurrentPlayer == _lastTurnPlayer) return;
        _lastTurnPlayer = _g.S.CurrentPlayer;
        NewTurnClock();
        _ghosts?.BeginTurn(_g.Current.Index, _humanPlayers, _turnStartMs);
    }

    void NewTurnClock()
    {
        _turnStartMs = _clock();
        _combatCredit.Clear();
        _turnStamp++;
    }

    /// <summary>
    /// Turn time left for a player (CWorld vf +0x1c4, 0098e4e0): limit − max(0, time since the turn start − its
    /// combat credit). A player that is not current: 0 in sequential turns, else the largest credit of the alive humans.
    /// </summary>
    long TimeLeft(int mapPlayer)
    {
        long limit = _set.TimeLimitMinutes * 60_000L;
        bool current = _simul is { InPhase: true } sim ? sim.Playing(mapPlayer) : _g.Current.Index == mapPlayer;
        long credit = _combatCredit.GetValueOrDefault(mapPlayer);
        if (!current)
        {
            if (_simul is not { Active: true } s2) return 0;
            credit = s2.Humans.Select(h => _combatCredit.GetValueOrDefault(h)).DefaultIfEmpty(0).Max();
        }
        long used = Math.Max(0, _clock() - _turnStartMs - credit);
        return Math.Max(0, limit - used);
    }

    /// <summary>
    /// Test setup (<see cref="AdvNetSettings.DevStartAdjacent"/>): moves side 1's first hero next to
    /// side 0's first hero, so a human-versus-human battle starts with one move.
    /// </summary>
    void PlaceSecondHumanNextToFirst()
    {
        if (_sideMapPlayer.Length < 2) return;
        var a = _g.S.Heroes.FirstOrDefault(x => x.Owner == _sideMapPlayer[0] && x.Alive && !x.Garrisoned);
        var d = _g.S.Heroes.FirstOrDefault(x => x.Owner == _sideMapPlayer[1] && x.Alive && !x.Garrisoned);
        if (a == null || d == null) return;
        var floor = _g.Map.Floor(a.Pos.Floor)!;
        foreach (var t in Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => a.Pos.Offset(dx, dy))))
        {
            if (t == a.Pos || !floor.InBounds(t.X, t.Y) || floor.IsBlocked(t.X, t.Y) || _g.FindPath(a, t) is not { Count: > 0 }) continue;
            d.Pos = t;
            d.InTown = -1;
            return;
        }
    }

    ulong NextSeed() => _seed ^ (0x9E3779B97F4A7C15UL * ++_battleSeq);

    /// <summary>
    /// The pending adventure battle: interactive when a human takes part (unless Quick Combat is on), otherwise
    /// resolved at once (the resolve command is returned). An interactive battle is parked in <see cref="_battles"/>:
    /// both side owners now hold it (008545b0) and each participant gets its combat screen.
    /// </summary>
    (ResolveBattleCmd? Quick, RunningBattle? Running) StartBattle(bool forAi = false)
    {
        var pb = _g.S.Battle!;
        int atk = _g.S.Heroes.Find(h => h.Id == pb.AttackerHero)?.Owner ?? -1;
        int def = OwnerOfTarget(pb.DefenderHero, pb.DefenderObject);
        if ((!IsHuman(atk) && !IsHuman(def)) || _set.QuickCombat)
            return (QuickCombat.Resolve(_g, NextSeed(), _battleAi), null);

        var now = _clock();
        var state = _g.CreatePendingBattle(NextSeed());
        // Ghost Mode: a possessed neutral stack may be taken over by the ghosts when attacked.
        if (_ghosts != null && pb.DefenderHero < 0 && pb.DefenderObject >= 0 && pb.DefenderArmy.Count > 0
            && _ghosts.RollPossession(pb.DefenderObject, pb.DefenderArmy[0].Creature, _g.Core, _g.S.Rng) >= 0)
            GhostMode.ApplyPossession(state, 1);
        var net = new NetBattle(_g.Core, state, new[] { SideOf(atk), SideOf(def) }, atk, _battleAi, now);
        var rb = new RunningBattle(net, pb, atk, def, _turnStamp, forAi);
        _battles.Add(rb);
        _g.S.Battle = null;
        foreach (var (side, start) in net.Start()) Out(side).Add(start);
        var updates = new List<JsonObject>();
        net.RunAi(updates, now);
        PublishBattle(rb, updates);
        return (null, rb);
    }

    /// <summary>
    /// The battle is over: the participants get the end record, the engine's slot gets the battle back and the
    /// result command is built (CFinishCombat → CCombat slot 55). CCloseCombat (004e0f10) then clears each owner's
    /// combat and, for a battle begun in this turn, credits its turn timer with the duration minus its own acting time.
    /// </summary>
    ResolveBattleCmd CloseBattle(RunningBattle rb)
    {
        var b = rb.Net;
        var now = _clock();
        b.CountActingTime(now);
        var cmd = new ResolveBattleCmd(b.Battle.State.Winner ?? 1, b.Survivors(0), b.Survivors(1), b.AttackerFled)
        { Player = rb.AtkOwner, HeroMana = b.Battle.State.Heroes.Select(h => h.Mana).ToArray(), EndReason = b.Battle.State.EndReason ?? "" };
        foreach (var side in b.Participants) Out(side).Add(new JsonObject { ["end"] = new JsonObject { ["winner"] = b.Battle.State.Winner } });
        if (rb.Stamp == _turnStamp)
            foreach (var (owner, bs) in new[] { (rb.AtkOwner, 0), (rb.DefOwner, 1) })
                if (owner >= 0) _combatCredit[owner] = _combatCredit.GetValueOrDefault(owner) + (now - b.StartedMs - b.ActingMs[bs]);
        _battles.Remove(rb);
        _g.S.Battle = rb.Pending;
        return cmd;
    }

    void FinishBattle(RunningBattle rb)
    {
        var resolve = CloseBattle(rb);
        if (rb.ForAi && _runner is { WaitingBattle: true } runner)
        {
            runner.ResumeWith(resolve);
            if (runner.Error is { } e) { _runner = null; ForceEndAiTurn(e); }
            else if (!runner.WaitingBattle) _runner = null;
            return;
        }
        // Applied for the attacker, whatever player the engine has current meanwhile.
        int saved = _g.S.CurrentPlayer;
        int idx = _g.S.Players.FindIndex(p => p.Index == rb.AtkOwner);
        if (idx >= 0) _g.S.CurrentPlayer = idx;
        Send(resolve);
        if (_g.S.Battle == null) { Park(rb.AtkOwner); _g.S.CurrentPlayer = saved; }
    }

    void PublishBattle(RunningBattle rb, List<JsonObject> updates)
    {
        if (updates.Count == 0) return;
        foreach (var side in rb.Net.Participants)
            foreach (var u in updates) Out(side).Add((JsonObject)u.DeepClone());
    }

    List<JsonObject> Out(int side)
    {
        if (!_battleOut.TryGetValue(side, out var l)) _battleOut[side] = l = new List<JsonObject>();
        return l;
    }

    /// <summary>Sink wrapper: a computer player's battle against a human becomes interactive.</summary>
    AdvResult? Intercept(AdvCommand cmd)
    {
        if (cmd is not ResolveBattleCmd || _runner == null || !_runner.OnWorker || _g.S.Battle == null) return null;
        var pb = _g.S.Battle;
        if (_set.QuickCombat || (!IsHuman(_g.S.Hero(pb.AttackerHero)?.Owner ?? -1) && !IsHuman(OwnerOfTarget(pb.DefenderHero, pb.DefenderObject)))) return null;
        var (quick, rb) = StartBattle(forAi: true);
        if (quick != null) return null; // nobody human involved: let the AI's own result through
        if (rb!.Net.Finished) { CloseBattle(rb); return null; }
        return _runner.WaitForBattle();
    }

    // ------------------------------------------------------------------ timers

    public SessionResult? Tick(long nowMs)
    {
        if (_g == null || _g.S.Finished) return null;
        bool changed = _dirty;
        CaptureHeroes();

        if (_scripts != null)
        {
            _scriptCarryMs += nowMs - _lastTickMs;
            int segs = (int)(_scriptCarryMs / 200);
            if (segs > 0)
            {
                _scriptCarryMs -= segs * 200;
                int before = _scripts.GameEvents.Count;
                _scripts.Tick(Math.Min(segs, 50));
                if (_scripts.GameEvents.Count > before) changed = true;
            }
        }
        _lastTickMs = nowMs;

        if (_set.DynamicBattles > 0)
            foreach (var rb in _battles.ToList())
            {
                if (!_battles.Contains(rb)) continue;
                var updates = new List<JsonObject>();
                if (rb.Net.TimeoutIfDue(TimeToCommand, _set.DynamicBattles, updates, nowMs))
                {
                    PublishBattle(rb, updates);
                    if (rb.Net.Finished) FinishBattle(rb);
                    Continue();
                    changed = true;
                }
            }

        if (_set.TimeLimitMinutes > 0 && _runner == null)
        {
            if (_simul is { InPhase: true } sim)
            {
                // Each current human runs out on its own time (00998520: vf +0x1c4 == 0 → end turn); one that
                // holds a combat never does (its end flag is cleared instead).
                var due = sim.Humans.Where(p => sim.Playing(p) && Running(p) == null && TimeLeft(p) == 0).ToList();
                if (due.Count > 0) { TimeOutPhase(due); changed = true; }
            }
            else if (IsHuman(_g.Current.Index) && _battles.Count == 0 && TimeLeft(_g.Current.Index) == 0)
            {
                TimeOutTurn();
                changed = true;
            }
        }

        if (_ghosts != null && _ghosts.Regenerate(nowMs)) changed = true;
        if (!changed) return null;
        return BuildResult(-1);
    }

    /// <summary>The current human ran out of time: pending decisions get defaults, then the turn ends.</summary>
    void TimeOutTurn()
    {
        int me = _g.Current.Index;
        _events.Add((new H5.Adventure.Engine.Message("turn_time_out", "Time is up."), -1));
        AnswerDefaults(me);
        Send(new EndTurnCmd { Player = me });
        Continue();
    }

    /// <summary>Humans of the simultaneous phase whose time ran out end their turns.</summary>
    void TimeOutPhase(List<int> players)
    {
        var sim = _simul!;
        foreach (var mp in players)
        {
            _events.Add((new H5.Adventure.Engine.Message("turn_time_out", "Time is up."), mp));
            sim.Enter(mp);
            AnswerDefaults(mp);
            sim.EndTurn(mp);
        }
        Continue();
    }

    void AnswerDefaults(int me)
    {
        Unpark(me);
        for (int guard = 0; guard < 20 && _g.S.Choice != null; guard++)
            if (!Send(new AnswerChoiceCmd(false) { Player = me }).Ok) break;
        Park(me);
        foreach (var h in _g.S.Heroes.Where(h => h.Owner == me && h.Alive))
            for (int guard = 0; guard < 40 && h.PendingLevelUps.Count > 0; guard++)
                if (!Send(new ChooseLevelUpCmd(h.Id, 0) { Player = me }).Ok) break;
    }

    readonly Dictionary<string, double> _creatureTime = new();

    /// <summary>Creature TimeToCommand from its xdb; heroes use DynamicBattle_HeroTimeToCommand.</summary>
    double TimeToCommand(BattleState s, int actor)
    {
        if (Actor.IsHero(actor)) return _g.Core.Stats.Get("combat/DynamicBattle_HeroTimeToCommand", 20);
        var u = s.Units.Find(x => x.Id == actor);
        if (u == null || !_g.Core.Creatures.TryGetValue(u.CreatureId, out var def)) return 10;
        if (!_creatureTime.TryGetValue(def.Id, out var t))
        {
            t = 10;
            try
            {
                var root = new H5.Core.Xdb.XdbLoader(_src.Fs).Load(def.SourcePath).Root;
                if (double.TryParse((string?)root?.Element("TimeToCommand"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0) t = v;
            }
            catch (Exception) { }
            _creatureTime[def.Id] = t;
        }
        return t;
    }

    // ------------------------------------------------------------------ players leaving

    public NetGameEnded? OnSideLeft(int side)
    {
        if (side < 0 || side >= _sideMapPlayer.Length || _g == null) return null;
        int mp = _sideMapPlayer[side];
        if (!_humanPlayers.Remove(mp)) return null;
        _simul?.Left(mp);
        _events.Add((new H5.Adventure.Engine.Message("player_left", $"{_sideName[side]} left the game; the computer takes over."), -1));
        if (_humanPlayers.Count == 0) return new NetGameEnded(null, "all players left");
        // The computer takes over the leaver's side (also in a running battle).
        if (_battles.Find(b => b.Net.BattleSideOf(side) != null) is { } rb)
        {
            var updates = new List<JsonObject>();
            rb.Net.ReleaseSide(side, updates, _clock());
            PublishBattle(rb, updates);
            if (rb.Net.Finished) FinishBattle(rb);
        }
        // A choice it left open is declined, so it does not hold up the engine.
        if (_choices.ContainsKey(mp))
        {
            int saved = _g.S.CurrentPlayer;
            _simul?.Enter(mp);
            AnswerDefaults(mp);
            _choices.Remove(mp);
            _g.S.CurrentPlayer = saved;
        }
        Continue();
        _dirty = true;
        return null;
    }

    // ------------------------------------------------------------------ results

    void CaptureHeroes()
    {
        _heroPos.Clear();
        foreach (var h in _g?.S.Heroes ?? new()) _heroPos[h.Id] = (h.Owner, h.Pos);
    }

    SessionResult BuildResult(int actor)
    {
        if (_scripts != null)
        {
            foreach (var e in _scripts.GameEvents) _events.Add((e, -1));
            _scripts.GameEvents.Clear();
            foreach (var se in _scripts.Events.Where(e => e.Kind == "ui_request"))
                if (se.Data.TryGetValue("resource", out var res) && res.Length > 0)
                    _events.Add((new H5.Adventure.Engine.Message("script_ui", _lm.Files?.ReadText(res) ?? res), -1));
            _scripts.Events.Clear();
        }

        int n = _sideMapPlayer.Length;
        var payloads = new JsonNode[n];
        var hashes = new string[n];
        if (_shownCurrent.Length != n) _shownCurrent = Enumerable.Repeat(-1, n).ToArray();
        for (int s = 0; s < n; s++)
        {
            int mp = _sideMapPlayer[s];
            int? viewCurrent = _simul?.ViewCurrentFor(mp);
            var view = ViewFor(mp);
            var patch = JsonDiff.Diff(_lastView[s], view);
            _lastView[s] = view;
            hashes[s] = JsonDiff.CanonicalHash(view);

            var evs = new List<AdvEvent>();
            int i = 0;
            while (i < _events.Count)
            {
                int a = _events[i].Actor, j = i;
                while (j < _events.Count && _events[j].Actor == a) j++;
                int from = i, count = j - i;
                evs.AddRange(WithOwnSlots(mp, () => ViewFilter.EventsFor(_events.Skip(from).Take(count).Select(x => x.Event), _g.S, _lm.Map, mp, a, _heroPos)));
                i = j;
            }
            if (_simul != null)
            {
                // Each side is told whose turn it sees: its own while it plays the simultaneous phase.
                int shown = viewCurrent ?? _g.S.CurrentPlayer;
                evs.RemoveAll(e => e is H5.Adventure.Engine.TurnStarted);
                if (shown != _shownCurrent[s] || _g.S.Day != _shownDay)
                    evs.Add(new H5.Adventure.Engine.TurnStarted(_g.S.Players[shown].Index, _g.S.Day));
                _shownCurrent[s] = shown;
            }

            var payload = new JsonObject
            {
                ["events"] = JsonNode.Parse(AdvJson.Serialize(evs)),
                ["view"] = patch,
                ["turn"] = TurnInfo(mp),
            };
            if (_battleOut.TryGetValue(s, out var bl) && bl.Count > 0) payload["battle"] = new JsonArray(bl.Select(x => (JsonNode)x.DeepClone()).ToArray());
            var chat = new JsonArray();
            foreach (var (from, scope, to, text) in _chat)
            {
                bool deliver = scope switch
                {
                    "team" => _g.S.Players.Find(p => p.Index == _sideMapPlayer[from])?.Team == _g.S.Players.Find(p => p.Index == mp)?.Team,
                    "private" => to == s || from == s,
                    _ => true,
                };
                if (deliver) chat.Add(new JsonObject { ["from"] = from, ["name"] = _sideName[from], ["scope"] = scope, ["text"] = text });
            }
            if (chat.Count > 0) payload["chat"] = chat;
            if (_ghosts != null) payload["ghosts"] = _ghosts.ViewFor(mp, _lm.Map);
            if (_simul != null && _simul.Notices.Any(x => x.MapPlayer == mp))
                payload["simul"] = new JsonArray(_simul.Notices.Where(x => x.MapPlayer == mp)
                    .Select(x => (JsonNode)new JsonObject { ["approve"] = x.Approve, ["message"] = x.Message }).ToArray());
            payloads[s] = payload;
        }
        _shownDay = _g.S.Day;
        _simul?.Notices.Clear();
        _events.Clear();
        _battleOut.Clear();
        _chat.Clear();
        _dirty = false;

        NetGameEnded? ended = null;
        if (_g.S.Finished)
        {
            int? winnerSide = null;
            if (_g.S.WinnerTeam is { } team)
                for (int s = 0; s < n && winnerSide == null; s++)
                    if (_g.S.Players.Find(p => p.Index == _sideMapPlayer[s])?.Team == team) winnerSide = s;
            ended = new NetGameEnded(winnerSide, winnerSide != null ? "victory" : "defeat");
        }
        return new SessionResult.AcceptedPerSide(payloads, hashes, ended);
    }

    JsonObject TurnInfo(int mapPlayer)
    {
        var o = new JsonObject { ["player"] = _g.Current.Index, ["day"] = _g.S.Day, ["battle"] = Running(mapPlayer) != null || (_simul is not { InPhase: true } && _battles.Count > 0) };
        if (_simul != null)
            o["simul"] = new JsonObject
            {
                ["active"] = _simul.Active,
                ["phase"] = _simul.InPhase,
                ["waiting"] = new JsonArray(_simul.InPhase ? _simul.Humans.Where(h => !_simul.Playing(h)).Select(h => (JsonNode)h).ToArray() : Array.Empty<JsonNode>()),
                ["pending"] = new JsonArray(_simul.Pending.Select(h => (JsonNode)h).ToArray()),
            };
        if (_set.TimeLimitMinutes > 0 && IsHuman(_g.Current.Index))
            o["msLeft"] = TimeLeft(_simul is { InPhase: true } ? mapPlayer : _g.Current.Index);
        return o;
    }

    // ------------------------------------------------------------------ computer turns

    /// <summary>
    /// Runs computer players' turns on a worker thread so a turn can pause mid-way when the AI
    /// attacks a human (the human fights the battle; the AI's turn then continues with the real
    /// result). Only one of host thread / worker runs at any time, so execution stays sequential.
    /// </summary>
    sealed class AiRunner(AdventureNetSession s)
    {
        readonly SemaphoreSlim _toMain = new(0), _toWorker = new(0);
        Thread? _thread;
        ResolveBattleCmd? _result;
        public bool WaitingBattle { get; private set; }
        public Exception? Error { get; private set; }
        public bool OnWorker => Thread.CurrentThread == _thread;

        public void RunUntilHumanOrBattle()
        {
            _thread = new Thread(Work) { IsBackground = true, Name = "H5 adventure AI" };
            _thread.Start();
            _toMain.Wait();
        }

        void Work()
        {
            try
            {
                var g = s._g;
                for (int guard = 0; guard < 64 && !g.S.Finished && !s.IsHuman(g.Current.Index); guard++)
                {
                    int player = g.S.CurrentPlayer, day = g.S.Day;
                    s._ai.PlayTurn(g, s._seed ^ (ulong)(day * 977 + player * 31) ^ ++s._battleSeq);
                    // Make sure the turn passes even if the AI stopped early.
                    if (!g.S.Finished && g.S.CurrentPlayer == player && g.S.Day == day && !s.IsHuman(g.Current.Index))
                    {
                        if (g.S.Battle != null) s.Send(QuickCombat.Resolve(g, s.NextSeed(), s._battleAi));
                        s.Send(new EndTurnCmd { Player = g.Current.Index });
                    }
                }
            }
            catch (Exception e) { Error = e; }
            WaitingBattle = false;
            _toMain.Release();
        }

        /// <summary>Called on the worker from the command sink: blocks until the host finishes the battle.</summary>
        public AdvResult WaitForBattle()
        {
            WaitingBattle = true;
            _toMain.Release();
            _toWorker.Wait();
            var cmd = _result!;
            _result = null;
            var inner = s._g.ScriptHooks is AdventureScripting sc ? sc.Apply(cmd) : s._g.Apply(cmd);
            return inner;
        }

        /// <summary>Host thread: hands the battle result back to the AI and waits until it pauses again or ends.</summary>
        public void ResumeWith(ResolveBattleCmd result)
        {
            _result = result;
            WaitingBattle = false;
            _toWorker.Release();
            _toMain.Wait();
        }
    }
}
