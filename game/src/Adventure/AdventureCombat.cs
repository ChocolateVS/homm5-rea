using Godot;
using H5.Combat;
using H5Reborn.Combat;

namespace H5Reborn.Adventure;

/// <summary>
/// A battle started on the adventure map, played on the existing combat screen: the
/// human commands their side (the attacker, side 0), the built-in combat AI the other.
/// Mirrors LocalCombatSession but starts from the adventure engine's BattleState.
/// </summary>
public sealed class AdventureCombatSession : CombatSession
{
    readonly Battle _battle;
    readonly int[] _humanSides;
    readonly HashSet<int> _aiSides;
    readonly Queue<IReadOnlyList<BattleEvent>> _pending = new();
    readonly IBattleAI _ai = H5.Combat.AI.BattleAiRegistry.Create("original");
    readonly ulong _seed;

    public AdventureCombatSession(BattleState state, ulong seed, IEnumerable<int> humanSides)
    {
        _seed = seed;
        _battle = new Battle(Game.Instance.Db, state);
        _humanSides = humanSides.ToArray();
        _aiSides = new HashSet<int>(new[] { 0, 1 }.Except(_humanSides));
        _battle.State.AiControlled = new[] { _aiSides.Contains(0), _aiSides.Contains(1) };
        Opening = _battle.Begin();
    }

    public override Battle Battle => _battle;
    public override IReadOnlyCollection<int> LocalSides => _humanSides;
    public override int ViewSide => _humanSides.Length > 0 ? _humanSides[0] : 0;
    public override IReadOnlyList<int> Presets => Array.Empty<int>();
    public override ulong Seed => _seed;
    public override bool Busy => false;

    public override void Submit(BattleCommand cmd)
    {
        var r = _battle.Apply(cmd);
        if (!r.Ok) { LastRejection = r.Error; GD.Print($"[H5] command rejected: {r.Error}"); return; }
        LastRejection = null;
        _pending.Enqueue(r.Events);
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
