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
    NetBattle? _battle;
    AiRunner? _runner;
    ulong _seed, _battleSeq;
    int[] _sideMapPlayer = Array.Empty<int>();
    string[] _sideName = Array.Empty<string>();
    readonly HashSet<int> _humanPlayers = new();   // map players controlled by a connected human
    JsonNode?[] _lastView = Array.Empty<JsonNode?>();
    int _lastTurnPlayer = -1;
    long _turnStartMs, _battlePausedMs, _lastTickMs;
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
    public NetBattle? ActiveBattle => _battle;
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
        _g.Observer = (cmd, res) => { if (res.Ok) foreach (var e in res.Events) _events.Add((e, cmd.Player)); };
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
            _ => "unknown command",
        };
        if (err != null) return new SessionResult.Rejected(err);
        return BuildResult(me);
    }

    string? MarkDirty(string? err) { if (err == null) _dirty = true; return err; }

    string? DoAdv(int me, JsonNode? cmdNode)
    {
        if (_g.S.Finished) return "the game is over";
        if (_battle != null) return "a battle is in progress";
        if (_g.Current.Index != me || !_humanPlayers.Contains(me)) return "not your turn";
        AdvCommand cmd;
        try { cmd = AdvJson.DeserializeCommand(cmdNode!.ToJsonString()); }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException) { return "malformed command"; }
        if (cmd is ResolveBattleCmd) return "battles are resolved by the host";
        var r = Send(cmd with { Player = me });
        if (!r.Ok) return r.Error ?? "rejected";
        Continue();
        return null;
    }

    string? DoBattle(int side, JsonNode? cmdNode)
    {
        if (_battle == null) return "no battle in progress";
        BattleCommand cmd;
        try { cmd = MessageJson.DeserializeCommand(cmdNode!.ToJsonString()); }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException) { return "malformed command"; }
        var updates = new List<JsonObject>();
        var err = _battle.ApplyHuman(side, cmd, updates, _clock());
        if (err != null) return err;
        PublishBattle(updates);
        if (_battle.Finished) FinishBattle();
        Continue();
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
            OnTurnMaybeChanged();
            if (_g.S.Finished || _battle != null) return;
            if (_runner is { WaitingBattle: true }) return;
            if (_g.S.Battle != null && IsHuman(_g.Current.Index))
            {
                // The human's own move started a battle.
                if (StartBattle() is { } quick) Send(quick);
                continue;
            }
            if (!IsHuman(_g.Current.Index))
            {
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
        if (_g.S.CurrentPlayer == _lastTurnPlayer) return;
        _lastTurnPlayer = _g.S.CurrentPlayer;
        _turnStartMs = _clock();
        _battlePausedMs = 0;
        _ghosts?.BeginTurn(_g.Current.Index, _humanPlayers, _turnStartMs);
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
    /// The pending adventure battle: interactive when a human takes part (unless Quick Combat is
    /// on), otherwise resolved at once. Returns the resolve command when resolved immediately.
    /// </summary>
    ResolveBattleCmd? StartBattle()
    {
        var pb = _g.S.Battle!;
        int atk = _g.S.Heroes.Find(h => h.Id == pb.AttackerHero)?.Owner ?? -1;
        int def = pb.DefenderHero >= 0 ? _g.S.Heroes.Find(h => h.Id == pb.DefenderHero)?.Owner ?? -1
            : pb.DefenderObject >= 0 ? _g.S.Town(pb.DefenderObject)?.Owner ?? -1 : -1;
        if ((!IsHuman(atk) && !IsHuman(def)) || _set.QuickCombat)
            return QuickCombat.Resolve(_g, NextSeed(), _battleAi);

        var now = _clock();
        var state = _g.CreatePendingBattle(NextSeed());
        // Ghost Mode: a possessed neutral stack may be taken over by the ghosts when attacked.
        if (_ghosts != null && pb.DefenderHero < 0 && pb.DefenderObject >= 0 && pb.DefenderArmy.Count > 0
            && _ghosts.RollPossession(pb.DefenderObject, pb.DefenderArmy[0].Creature, _g.Core, _g.S.Rng) >= 0)
            GhostMode.ApplyPossession(state, 1);
        _battle = new NetBattle(_g.Core, state, new[] { SideOf(atk), SideOf(def) }, atk, _battleAi, now);
        _battleStartMs = now;
        foreach (var (side, start) in _battle.Start()) Out(side).Add(start);
        var updates = new List<JsonObject>();
        _battle.RunAi(updates, now);
        PublishBattle(updates);
        if (_battle.Finished) return BuildResolve();
        return null;
    }

    long _battleStartMs;

    bool BattleHasHuman(PendingBattle pb)
    {
        int atk = _g.S.Heroes.Find(h => h.Id == pb.AttackerHero)?.Owner ?? -1;
        int def = pb.DefenderHero >= 0 ? _g.S.Heroes.Find(h => h.Id == pb.DefenderHero)?.Owner ?? -1
            : pb.DefenderObject >= 0 ? _g.S.Town(pb.DefenderObject)?.Owner ?? -1 : -1;
        return IsHuman(atk) || IsHuman(def);
    }

    ResolveBattleCmd BuildResolve()
    {
        var b = _battle!;
        var cmd = new ResolveBattleCmd(b.Battle.State.Winner ?? 1, b.Survivors(0), b.Survivors(1), b.AttackerFled) { Player = _g.Current.Index };
        foreach (var side in b.Participants) Out(side).Add(new JsonObject { ["end"] = new JsonObject { ["winner"] = b.Battle.State.Winner } });
        _battlePausedMs += _clock() - _battleStartMs;
        _battle = null;
        return cmd;
    }

    void FinishBattle()
    {
        var resolve = BuildResolve();
        if (_runner is { WaitingBattle: true } runner)
        {
            runner.ResumeWith(resolve);
            if (runner.Error is { } e) { _runner = null; ForceEndAiTurn(e); }
            else if (!runner.WaitingBattle) _runner = null;
        }
        else Send(resolve);
    }

    void PublishBattle(List<JsonObject> updates)
    {
        if (_battle == null || updates.Count == 0) return;
        foreach (var side in _battle.Participants)
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
        if (_set.QuickCombat || !BattleHasHuman(_g.S.Battle)) return null;
        var quick = StartBattle();
        if (quick != null) return null; // nobody human involved: let the AI's own result through
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

        if (_battle != null && _set.DynamicBattles > 0)
        {
            var updates = new List<JsonObject>();
            if (_battle.TimeoutIfDue(TimeToCommand, _set.DynamicBattles, updates, nowMs))
            {
                PublishBattle(updates);
                if (_battle.Finished) FinishBattle();
                Continue();
                changed = true;
            }
        }

        if (_set.TimeLimitMinutes > 0 && _battle == null && IsHuman(_g.Current.Index) && _runner == null)
        {
            long used = nowMs - _turnStartMs - _battlePausedMs;
            if (used >= _set.TimeLimitMinutes * 60_000L)
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
        for (int guard = 0; guard < 20 && _g.S.Choice != null; guard++) Send(new AnswerChoiceCmd(false) { Player = me });
        foreach (var h in _g.S.Heroes.Where(h => h.Owner == me && h.Alive))
            for (int guard = 0; guard < 40 && h.PendingLevelUps.Count > 0; guard++)
                if (!Send(new ChooseLevelUpCmd(h.Id, 0) { Player = me }).Ok) break;
        Send(new EndTurnCmd { Player = me });
        Continue();
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
        _events.Add((new H5.Adventure.Engine.Message("player_left", $"{_sideName[side]} left the game; the computer takes over."), -1));
        if (_humanPlayers.Count == 0) return new NetGameEnded(null, "all players left");
        // The computer takes over the leaver's side (also in a running battle).
        if (_battle != null)
        {
            var updates = new List<JsonObject>();
            _battle.ReleaseSide(side, updates, _clock());
            PublishBattle(updates);
            if (_battle.Finished) FinishBattle();
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
        for (int s = 0; s < n; s++)
        {
            int mp = _sideMapPlayer[s];
            var view = ViewFilter.ViewFor(_g.S, _lm.Map, mp);
            var patch = JsonDiff.Diff(_lastView[s], view);
            _lastView[s] = view;
            hashes[s] = JsonDiff.CanonicalHash(view);

            var evs = new List<AdvEvent>();
            int i = 0;
            while (i < _events.Count)
            {
                int a = _events[i].Actor, j = i;
                while (j < _events.Count && _events[j].Actor == a) j++;
                evs.AddRange(ViewFilter.EventsFor(_events.Skip(i).Take(j - i).Select(x => x.Event), _g.S, _lm.Map, mp, a, _heroPos));
                i = j;
            }

            var payload = new JsonObject
            {
                ["events"] = JsonNode.Parse(AdvJson.Serialize(evs)),
                ["view"] = patch,
                ["turn"] = TurnInfo(),
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
            payloads[s] = payload;
        }
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

    JsonObject TurnInfo()
    {
        var o = new JsonObject { ["player"] = _g.Current.Index, ["day"] = _g.S.Day, ["battle"] = _battle != null };
        if (_set.TimeLimitMinutes > 0 && IsHuman(_g.Current.Index))
            o["msLeft"] = Math.Max(0, _set.TimeLimitMinutes * 60_000L - (_clock() - _turnStartMs - _battlePausedMs));
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
