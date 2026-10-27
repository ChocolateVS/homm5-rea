using System.Text.Json.Nodes;
using Godot;
using H5.Combat;
using H5.Core.Data;
using H5.Duel;
using H5.Net;

namespace H5Reborn.Combat;

/// <summary>
/// Source of truth for a combat screen. The screen only reads <see cref="Battle"/>
/// between event batches: a batch is dequeued, its events are animated, and only
/// then is the next one taken, so the local state always matches what is on screen
/// once playback is idle.
/// </summary>
public abstract class CombatSession
{
    public abstract Battle Battle { get; }
    /// <summary>Sides this client issues commands for (human input or local autoplay).</summary>
    public abstract IReadOnlyCollection<int> LocalSides { get; }
    /// <summary>The side the camera/HUD belongs to (left-hand perspective is always side 0 in duels).</summary>
    public virtual int ViewSide => LocalSides.Count > 0 ? LocalSides.First() : 0;
    public abstract IReadOnlyList<int> Presets { get; }
    public abstract ulong Seed { get; }
    /// <summary>True while a submitted command awaits the authority's answer.</summary>
    public abstract bool Busy { get; }
    public virtual bool Desynced => false;
    public bool Ended => EndResult != null;
    public (int? Winner, string Reason)? EndResult { get; protected set; }
    public string? LastRejection { get; protected set; }
    public bool IsNetworked { get; protected init; }

    /// <summary>Events produced before the first command (battle start, first turn).</summary>
    public IReadOnlyList<BattleEvent> Opening { get; protected set; } = Array.Empty<BattleEvent>();

    public abstract void Submit(BattleCommand cmd);

    /// <summary>Takes the next batch of authoritative events (applying it to the local replica if needed).</summary>
    public abstract bool TryDequeue(out IReadOnlyList<BattleEvent> events);

    /// <summary>Called by the screen when playback is idle; lets local AI sides act.</summary>
    public virtual void Tick() { }

    public virtual void Leave() { }

    public bool CanAct(int side) =>
        !Busy && !Ended && LocalSides.Contains(side) && Battle.State.Phase == BattlePhase.Combat && Battle.SideToAct == side;

    /// <summary>Duel mode: the retreat button is refused with "Can't retreat. (Duel Mode)" (exe 0053feb0 code 4).</summary>
    public virtual bool IsDuelMode => false;

    /// <summary>
    /// Ransom ("safe passage") for a side's remaining troops, or null when surrender is not
    /// possible (no enemy hero to pay). The adventure host supplies it; duels have none.
    /// </summary>
    public virtual int? SurrenderCost(int side) =>
        Battle.RetreatRefusal(side, surrender: true) == 0 ? (int)Math.Min(int.MaxValue, Battle.SurrenderCost(side)) : null;

    /// <summary>The side's gold for the ransom check (box 00570cf0: the army's player's resources, gold = entry 6); 0 without a player.</summary>
    public virtual long Gold(int side) => 0;

    /// <summary>
    /// Experience the winning hero gains, for the results screen (-1 when the winner has no hero). Default:
    /// the slain enemy creatures' Exp (fan manual p.298) plus 500 for defeating (not chasing off) the enemy
    /// hero; the adventure host overrides it with its own coefficients (neutral difficulty, moon weeks).
    /// </summary>
    public virtual long WinnerExperience(int winner)
    {
        var s = Battle.State;
        if (!s.Heroes[winner].Present) return -1;
        long exp = 0;
        foreach (var u in s.Units)
            if (u.Side != winner && u.Kind == UnitKind.Creature && !u.Summoned
                && Game.Instance.Db.Creatures.TryGetValue(u.CreatureId, out var cd))
                exp += (long)Math.Max(0, u.InitialCount - Math.Max(0, u.Count)) * cd.Exp;
        var loser = s.Heroes[1 - winner];
        if (loser.Present && !loser.Fled) exp += 500;
        return exp;
    }
}

/// <summary>A duel played entirely on this machine: some sides human, others the built-in AI.</summary>
public sealed class LocalCombatSession : CombatSession
{
    readonly Battle _battle;
    readonly int[] _humanSides;
    readonly HashSet<int> _aiSides;
    readonly Queue<IReadOnlyList<BattleEvent>> _pending = new();
    readonly IBattleAI _ai = H5.Combat.AI.BattleAiRegistry.Create("original");
    readonly int[] _presets;
    readonly ulong _seed;

    public LocalCombatSession(GameDatabase db, int presetA, int presetB, ulong seed, IEnumerable<int> humanSides)
        : this(db, BattleSetup.CreateDuel(db, presetA, presetB, seed), presetA, presetB, seed, humanSides) { }

    /// <summary>A local battle on a prepared state (e.g. a siege built with BattleSetup.Create(..., siege:)).</summary>
    public LocalCombatSession(GameDatabase db, BattleState state, int presetA, int presetB, ulong seed, IEnumerable<int> humanSides)
    {
        _presets = new[] { presetA, presetB };
        _seed = seed;
        _battle = new Battle(db, state);
        _humanSides = humanSides.ToArray();
        _aiSides = new HashSet<int>(new[] { 0, 1 }.Except(_humanSides));
        _battle.State.AiControlled = new[] { _aiSides.Contains(0), _aiSides.Contains(1) };
        Opening = _battle.Begin();
    }

    public override Battle Battle => _battle;
    public override IReadOnlyCollection<int> LocalSides => _humanSides;
    public override int ViewSide => _humanSides.Length > 0 ? _humanSides[0] : 0;
    public override IReadOnlyList<int> Presets => _presets;
    public override ulong Seed => _seed;
    public override bool Busy => false;
    public override bool IsDuelMode => true;

    public override void Submit(BattleCommand cmd)
    {
        var r = _battle.Apply(cmd);
        if (!r.Ok) { LastRejection = r.Error; GD.Print($"[H5] command rejected: {r.Error}"); return; }
        LastRejection = null;
        _pending.Enqueue(r.Events);
        CheckEnd();
    }

    void CheckEnd()
    {
        if (_battle.State.Phase == BattlePhase.Finished && EndResult == null)
            EndResult = (_battle.State.Winner, _battle.State.EndReason);
    }

    public override bool TryDequeue(out IReadOnlyList<BattleEvent> events)
    {
        if (_pending.Count == 0) { events = Array.Empty<BattleEvent>(); return false; }
        events = _pending.Dequeue();
        return true;
    }

    public override void Tick()
    {
        if (Ended || _pending.Count > 0) return;
        var s = _battle.State;
        if (s.Phase == BattlePhase.Tactics)
        {
            foreach (var side in _aiSides)
                if (!s.TacticsDone[side]) Submit(new EndTacticsCmd { Side = side });
            return;
        }
        if (_battle.SideToAct is int toAct && _aiSides.Contains(toAct))
            Submit(_ai.Decide(_battle, toAct) with { Side = toAct });
    }
}

/// <summary>
/// A networked duel: the lobby server is authoritative, this client keeps a
/// <see cref="DuelReplica"/> in lockstep. Updates are buffered as they arrive and
/// applied to the replica only when the screen takes them for playback.
/// </summary>
public sealed class NetCombatSession : CombatSession
{
    readonly DuelReplica _replica;
    readonly Queue<GameUpdate> _updates = new();
    readonly Func<JsonNode, int> _send;
    readonly Func<int> _pendingCommands;
    GameEnded? _endedMessage;

    public NetCombatSession(GameDatabase db, GameStarted started, Func<JsonNode, int> send, Func<int> pendingCommands)
    {
        _replica = new DuelReplica(db, started);
        _send = send;
        _pendingCommands = pendingCommands;
        Opening = _replica.OpeningEvents;
        IsNetworked = true;
        Seed = ulong.TryParse(started.Setup["state"]?["Rng"]?["S0"]?.ToString(), out var s0) ? s0 : 0;
    }

    public override Battle Battle => _replica.Battle;
    public override IReadOnlyCollection<int> LocalSides => new[] { _replica.MySide };
    public override IReadOnlyList<int> Presets => _replica.Presets;
    public override ulong Seed { get; }
    public override bool Busy => _pendingCommands() > 0 || _updates.Count > 0;
    public override bool Desynced => _replica.Desynced;
    public override bool IsDuelMode => true;

    /// <summary>Feed every lobby message here (in arrival order).</summary>
    public void OnMessage(Message m)
    {
        switch (m)
        {
            case GameUpdate u: _updates.Enqueue(u); break;
            case CommandRejected r: LastRejection = r.Reason; GD.Print($"[H5] command rejected: {r.Reason}"); break;
            case GameEnded e: _endedMessage = e; break;
        }
    }

    public override void Submit(BattleCommand cmd)
    {
        if (Busy || Ended) return;
        LastRejection = null;
        _send(JsonNode.Parse(MessageJson.Serialize(cmd))!);
    }

    public override bool TryDequeue(out IReadOnlyList<BattleEvent> events)
    {
        if (_updates.Count == 0)
        {
            events = Array.Empty<BattleEvent>();
            // The end is reported only after every update before it has been shown.
            if (_endedMessage != null && EndResult == null) EndResult = (_endedMessage.WinnerSide, _endedMessage.Reason);
            return false;
        }
        events = _replica.Apply(_updates.Dequeue());
        return true;
    }
}
