using System.Text.Json.Nodes;
using Godot;
using H5.AdvNet;
using H5.Adventure.Engine;
using H5.Combat;
using H5.Net;
using Message = H5.Net.Message;

namespace H5Reborn.AdvNet;

/// <summary>
/// The client side of a networked adventure game inside the Godot client: owns the
/// <see cref="AdventureNetReplica"/> (this player's view), forwards host updates to it and
/// sends commands. Created by LobbyService as soon as an adventure GameStarted arrives, so
/// no update is missed while the adventure screen loads.
///
/// The adventure screen consumes it through <see cref="RemoteAdventure"/> (see that type for
/// the hook the screen needs).
/// </summary>
public sealed class AdvNetGame
{
    readonly Func<JsonNode, int> _send;
    readonly Func<int> _pending;

    public AdventureNetReplica Replica { get; }
    public string? LoadError { get; }
    public bool Desynced => Replica.Desynced;
    public event Action? Updated;
    public event Action<BattleReplica>? BattleStarted;
    public event Action<string>? Rejected;

    static AdvMapSource? _maps;
    public static AdvMapSource Maps => _maps ??= new AdvMapSource(Game.Instance.Db, Game.Instance.Fs, Game.Instance.Install);

    public static bool IsAdventure(GameStarted gs) => (string?)gs.Setup["mode"] == "adventure";

    public AdvNetGame(GameStarted started, Func<JsonNode, int> send, Func<int> pending)
    {
        _send = send;
        _pending = pending;
        Replica = new AdventureNetReplica(Maps, started) { DeferBattleUpdates = true };
        Replica.BattleStarted += b => BattleStarted?.Invoke(b);
        GD.Print($"[advnet] game on {Replica.Map.Map.Id} as map player {Replica.MyPlayer + 1} (side {Replica.MySide})");
    }

    public void OnMessage(Message m)
    {
        switch (m)
        {
            case GameUpdate u:
                Replica.Apply(u);
                if (Replica.Desynced) GD.PushWarning("[advnet] view desync — the client will resync on reconnect");
                Updated?.Invoke();
                break;
            case CommandRejected rej:
                GD.Print($"[advnet] command rejected: {rej.Reason}");
                Rejected?.Invoke(rej.Reason);
                break;
        }
    }

    public bool Busy => _pending() > 0;
    public int Send(AdvCommand c) => _send(AdvNetCommands.Adv(c));
    public int Send(BattleCommand c) => _send(AdvNetCommands.Battle(c));
    public int Chat(string text) => _send(AdvNetCommands.Chat(text));
    public int Ghost(JsonNode ghostCommand) => _send(ghostCommand);
}

/// <summary>
/// What the adventure screen needs from a remote game, mirroring AdventureSession:
/// <c>Game</c> (the view, for rendering and path previews), <c>Apply</c> (sends instead of
/// simulating; the result arrives as an update), <c>TryDequeue</c> (events to animate) and
/// <c>IsMyTurn</c>. Battles start from <see cref="AdvNetGame.BattleStarted"/>.
///
/// Hook needed in game/src/Adventure (owned by the adventure-screen workstream):
///   AdventureSession gains a remote mode, e.g. <c>public static AdventureSession FromRemote(RemoteAdventure r)</c>
///   built on <c>r.Game</c>, whose <c>Apply</c> calls <c>r.Apply</c>, whose event queue drains
///   <c>r.TryDequeue</c>, whose <c>Human</c> is <c>r.MyPlayer</c>, and which never runs
///   computer turns or local battles (the host does); and AdventureScreen opens the combat screen
///   for <c>AdvNetGame.BattleStarted</c> in a networked-battle mode that sends
///   <c>AdvNetGame.Send(BattleCommand)</c> and animates <c>Replica.BattleEvents</c>.
/// </summary>
public sealed class RemoteAdventure(AdvNetGame net)
{
    public AdvNetGame Net => net;
    public H5.Adventure.Engine.Adventure Game => net.Replica.Game;
    public int MyPlayer => net.Replica.MyPlayer;
    public bool IsMyTurn => net.Replica.IsMyTurn && !net.Busy;

    /// <summary>Sends the command; returns an accepted placeholder (the host's answer arrives as an update).</summary>
    public AdvResult Apply(AdvCommand cmd)
    {
        if (!IsMyTurn) return AdvResult.Reject("not your turn");
        net.Send(cmd);
        return new AdvResult { Ok = true };
    }

    public bool TryDequeue(out AdvEvent ev)
    {
        if (net.Replica.Events.Count > 0) { ev = net.Replica.Events.Dequeue(); return true; }
        ev = null!;
        return false;
    }
}
