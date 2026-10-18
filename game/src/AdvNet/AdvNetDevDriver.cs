using System.Text.Json.Nodes;
using Godot;
using H5.Adventure.Engine;
using H5.Net;
using Message = H5.Net.Message;
using H5Reborn.Combat;
using H5Reborn.Ui.Net;

namespace H5Reborn.AdvNet;

/// <summary>
/// Dev/test driver for networked adventure games, used to verify the full client path off-screen:
/// `--advnet-test=host` hosts in-process and creates an adventure room, `--advnet-test=join` joins it.
/// With `--advnet-battle` the room uses <c>DevStartAdjacent</c> and the host walks into the other
/// player's hero, so both clients fight a human-versus-human battle on the combat screen (auto
/// combat), then return to the adventure map. `--advnet-hotseat` instead starts a two-human
/// hot-seat game and ends a turn to show the hand-over screen. `--advnet-map=`, `--advnet-shots=dir`,
/// `--advnet-port=`, `--advnet-quit-after=seconds`.
/// </summary>
public partial class AdvNetDevDriver : Node
{
    static string? _mode, _shots, _map = "Maps/Multiplayer/A2M1";
    static bool _battle, _hotSeat, _ghost;
    static int _port = 27631;
    static double _quitAfter = 240;

    public static bool HandleArg(string arg)
    {
        string V(string p) => arg[p.Length..];
        if (arg.StartsWith("--advnet-test=")) { _mode = V("--advnet-test="); Schedule(); return true; }
        if (arg == "--advnet-battle") { _battle = true; return true; }
        if (arg == "--advnet-ghost") { _ghost = true; return true; }
        if (arg == "--advnet-hotseat") { _hotSeat = true; _mode ??= "hotseat"; Schedule(); return true; }
        if (arg.StartsWith("--advnet-map=")) { _map = V("--advnet-map="); return true; }
        if (arg.StartsWith("--advnet-shots=")) { _shots = V("--advnet-shots="); return true; }
        if (arg.StartsWith("--advnet-port=")) { _port = int.Parse(V("--advnet-port=")); return true; }
        if (arg.StartsWith("--advnet-quit-after=")) { _quitAfter = double.Parse(V("--advnet-quit-after="), System.Globalization.CultureInfo.InvariantCulture); return true; }
        return false;
    }

    static bool _scheduled;
    static void Schedule()
    {
        if (_scheduled) return;
        _scheduled = true;
        Callable.From(() => ((SceneTree)Engine.GetMainLoop()).Root.AddChild(new AdvNetDevDriver { Name = "AdvNetDevDriver" })).CallDeferred();
    }

    enum Step { Connect, Room, Wait, Playing, Battle, Back, Done }
    Step _step;
    double _t, _stepT;
    bool _attacked, _battleSeen, _hotSeatEnded;
    int _shot;

    public override void _Ready()
    {
        Log($"driver started as {_mode} (battle={_battle}, hotseat={_hotSeat}) on {_map}");
        CombatScreen.Autoplay = true;
        if (_mode == "hotseat") { StartHotSeat(); return; }
        _ = Connect();
        LobbyService.Instance.MessageReceived += OnMessage;
    }

    async System.Threading.Tasks.Task Connect()
    {
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                if (_mode == "host") await LobbyService.Instance.HostAsync(_port);
                else await LobbyService.Instance.ConnectAsync("127.0.0.1", _port);
                Log($"{_mode} connected on port {_port}");
                return;
            }
            catch (Exception e)
            {
                Log($"connect attempt {attempt} failed: {e.Message}");
                await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
            }
        }
    }

    void StartHotSeat()
    {
        var settings = new H5.AdvNet.AdvNetSettings { Map = _map };
        var plan = new HotSeatPlan { Settings = settings };
        foreach (var p in AdvNetGame.Maps.Load(settings).Map.Players.Where(p => p.Active && p.CanBeHuman).Take(2))
            plan.Humans[p.Index] = $"Player {p.Index + 1}";
        Log($"hot-seat humans: {string.Join(", ", plan.Humans.Keys)}");
        AdvNetScreens.OpenHotSeatGame?.Invoke(plan);
        _step = Step.Playing;
    }

    void OnMessage(Message m)
    {
        if (m is GameStarted && LobbyService.Instance.Adventure is { } game && _step < Step.Playing)
        {
            Log($"game started on {game.Replica.Map.Map.Id} as map player {game.Replica.MyPlayer}");
            game.BattleStarted += _ => { _battleSeen = true; Log("battle started (BattleStarted)"); };
            Callable.From(() => { AdvNetScreens.OpenNetworkGame?.Invoke(game); }).CallDeferred();
            _step = Step.Playing;
            _stepT = 0;
        }
        if (m is ErrorMsg e) Log($"server error {e.Code}: {e.Text}");
    }

    public override void _Process(double delta)
    {
        _t += delta;
        _stepT += delta;
        if (_step == Step.Done) return;
        if (_t > _quitAfter) { Log("time limit reached"); Finish(); return; }
        var lobby = LobbyService.Instance;
        switch (_step)
        {
            case Step.Connect when lobby.Connected:
                if (_mode == "host")
                {
                    var settings = new H5.AdvNet.AdvNetSettings { Map = _map, DevStartAdjacent = _battle, GhostMode = _ghost }.ToJson();
                    lobby.Send(new CreateRoom("advnet test", "adventure", 2, null, settings));
                    _step = Step.Room;
                }
                else if (lobby.Rooms?.Rooms.FirstOrDefault(r => r.Mode == "adventure" && !r.InProgress) is { } room)
                {
                    lobby.Send(new JoinRoom(room.RoomId, null));
                    _step = Step.Room;
                }
                else if (_stepT > 1) { lobby.Send(new ListRooms()); _stepT = 0; }
                break;
            case Step.Room when lobby.Room is { } rs:
                if (_mode == "join") { lobby.Send(new SetReady(true)); _step = Step.Wait; }
                else if (rs.Players.Count == 2 && rs.Players.All(p => p.Ready || p.IsHost)) { lobby.Send(new StartGame()); _step = Step.Wait; }
                break;
            case Step.Playing:
                PlayAdventure();
                break;
            case Step.Battle:
                if (GetTree().CurrentScene is CombatScreen cs)
                {
                    if (_stepT > 8 && _shot == 1) Shot("battle");
                    if (cs.Session.Ended && _stepT > 3)
                    {
                        Log($"battle over: winner {cs.Session.EndResult?.Winner} ({cs.Session.EndResult?.Reason}), desynced={cs.Session.Desynced}");
                        Shot("results");
                        cs.ReturnToMenu();
                        _step = Step.Back;
                        _stepT = 0;
                    }
                }
                break;
            case Step.Back when _stepT > 4:
                Shot("back");
                if (lobby.Adventure is { } g)
                    Log($"back on the map: day {g.Replica.View.Day}, desynced={g.Desynced}, heroes={string.Join(",", g.Replica.View.Heroes.Where(h => h.Alive).Select(h => $"{h.Name}@{h.Owner}"))}");
                Finish();
                break;
        }
    }

    void PlayAdventure()
    {
        if (GetTree().CurrentScene is CombatScreen)
        {
            if (!_battleSeen) { _battleSeen = true; Log("combat screen opened"); }
            _step = Step.Battle;
            _stepT = 0;
            Shot("battle_start");
            return;
        }
        if (_mode == "hotseat")
        {
            if (GetTree().CurrentScene is Adventure.AdventureScreen screen && _stepT > 8 && !_hotSeatEnded)
            {
                Shot("hotseat_turn1");
                _hotSeatEnded = true;
                screen.EndTurn();
                _stepT = 0;
            }
            else if (_hotSeatEnded && _stepT > 5)
            {
                if (GetTree().CurrentScene is Adventure.AdventureScreen sc)
                    Log($"hand-over: current player {sc.Session.S.CurrentPlayer} ({sc.Session.S.Players[sc.Session.S.CurrentPlayer].Colour}), day {sc.Session.S.Day}");
                Shot("hotseat_handover");
                Finish();
            }
            return;
        }
        if (_ghost) { GhostStep(); return; }
        if (_mode != "host" || !_battle || _attacked || _stepT < 6) return;
        if (LobbyService.Instance.Adventure is not { } game || !game.Replica.IsMyTurn || game.Busy) return;
        var v = game.Replica.View;
        var mine = v.Heroes.FirstOrDefault(h => h.Alive && h.Owner == game.Replica.MyPlayer && !h.Garrisoned);
        var enemy = v.Heroes.FirstOrDefault(h => h.Alive && h.Owner != game.Replica.MyPlayer && mine != null
            && h.Pos.Floor == mine.Pos.Floor && Math.Abs(h.Pos.X - mine.Pos.X) <= 1 && Math.Abs(h.Pos.Y - mine.Pos.Y) <= 1);
        if (mine == null || enemy == null) { if (_stepT > 20) { Log("no adjacent enemy hero visible"); _attacked = true; } return; }
        Shot("before_attack");
        Log($"hero {mine.Name} attacks {enemy.Name} at {enemy.Pos}");
        game.Send(new MoveHeroCmd(mine.Id, enemy.Pos) { Player = game.Replica.MyPlayer });
        _attacked = true;
    }

    int _ghostPhase;

    /// <summary>Ghost Mode check: the waiting client moves its ghost and opens the GhostInfo screen.</summary>
    void GhostStep()
    {
        var gc = GetTree().CurrentScene?.FindChild("GhostController", true, false) as GhostController;
        if (gc == null || !gc.IsActive) { if (_stepT > 40 && _ghostPhase == 0) { Log($"ghost mode not active for this client ({_mode})"); Finish(); } return; }
        var g = LobbyService.Instance.Adventure?.Replica.Ghosts;
        switch (_ghostPhase)
        {
            case 0 when _stepT > 8:
                Log($"ghosts: {g?["ghosts"]?.ToJsonString()}");
                Shot("ghost_map");
                gc.DevMove(2, 0);
                _ghostPhase = 1; _stepT = 0;
                break;
            case 1 when _stepT > 4:
                Log($"after move: {g?["ghosts"]?.ToJsonString()}");
                Shot("ghost_moved");
                gc.DevToggleInfo();
                _ghostPhase = 2; _stepT = 0;
                break;
            case 2 when _stepT > 3:
                Shot("ghost_info");
                Finish();
                break;
        }
    }

    void Shot(string name)
    {
        _shot++;
        if (_shots == null) return;
        System.IO.Directory.CreateDirectory(_shots);
        var path = System.IO.Path.Combine(_shots, $"{_mode}_{_shot:00}_{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }

    void Finish()
    {
        if (_step == Step.Done) return;
        _step = Step.Done;
        Log("driver done");
        GetTree().CreateTimer(1.0, ignoreTimeScale: true).Timeout += () => GetTree().Quit();
    }

    static void Log(string s) => GD.Print($"[advnet-test] {s}");
}
