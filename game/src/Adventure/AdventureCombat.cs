using Godot;
using H5.Combat;
using H5.Scripting.Runtime;
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
    readonly IBattleAI _ai;
    readonly ulong _seed;
    ulong _scriptClock;

    /// <summary>
    /// Combat scripts of this battle (null when the exe's combat screen would not run them: multiplayer,
    /// hotseat, or a map without scripts), created by the screen like CCombatArenaScreen's ctor FUN_0054c2f0.
    /// </summary>
    public CombatScripting? Scripts { get; }

    /// <param name="gameDifficulty">Adventure difficulty (script scale); the original combat AI picks its row from it.</param>
    /// <param name="scripts">Creates the combat script session for the battle (FUN_0054c2f0 → FUN_0053e810).</param>
    public AdventureCombatSession(BattleState state, ulong seed, IEnumerable<int> humanSides, int gameDifficulty = 1,
        Func<Battle, CombatScripting?>? scripts = null)
    {
        _seed = seed;
        _ai = new H5.Combat.AI.OriginalBattleAI { GameDifficulty = gameDifficulty };
        _battle = new Battle(Game.Instance.Db, state);
        _humanSides = humanSides.ToArray();
        _aiSides = new HashSet<int>(new[] { 0, 1 }.Except(_humanSides));
        _battle.State.AiControlled = new[] { _aiSides.Contains(0), _aiSides.Contains(1) };
        Opening = _battle.Begin();
        try { Scripts = scripts?.Invoke(_battle); }
        catch (Exception e) { GD.PushError($"[H5] combat scripts: {e.Message}"); }
        if (Scripts != null)
        {
            ((IBattleScriptSession)Scripts).Begun(Opening);
            Opening = Opening.Concat(Scripts.TakeScriptEvents()).ToList();
            FlushScriptLog();
            _scriptClock = Time.GetTicksMsec();
        }
    }

    void FlushScriptLog()
    {
        if (Scripts == null) return;
        foreach (var e in Scripts.Errors) GD.PushWarning($"[H5] combat script: {e}");
        Scripts.Errors.Clear();
        foreach (var l in Scripts.Log) GD.Print($"[H5] combat script: {l}");
        Scripts.Log.Clear();
        Scripts.Events.Clear();
    }

    /// <summary>Script time: one segment per 200 ms of real time (GetSoundTimeInSleeps divides by 200), at most 10 per call.</summary>
    void TickScripts()
    {
        if (Scripts == null) return;
        ulong now = Time.GetTicksMsec();
        int segments = (int)Math.Min(10, (now - _scriptClock) / 200);
        if (segments <= 0) return;
        _scriptClock += (ulong)segments * 200;
        if (now - _scriptClock > 2000) _scriptClock = now;
        Scripts.Tick(segments);
        var ev = Scripts.TakeScriptEvents();
        if (ev.Count > 0) _pending.Enqueue(ev);
        FlushScriptLog();
        CheckEnd();
    }

    void CheckEnd()
    {
        if (_battle.State.Phase == BattlePhase.Finished && EndResult == null)
            EndResult = (_battle.State.Winner, _battle.State.EndReason);
    }

    public override Battle Battle => _battle;
    public override IReadOnlyCollection<int> LocalSides => _humanSides;
    public override int ViewSide => _humanSides.Length > 0 ? _humanSides[0] : 0;
    public override IReadOnlyList<int> Presets => Array.Empty<int>();
    public override ulong Seed => _seed;
    public override bool Busy => false;

    public override void Submit(BattleCommand cmd)
    {
        var r = Scripts != null ? Scripts.Apply(cmd) : _battle.Apply(cmd);
        if (!r.Ok) { LastRejection = r.Error; GD.Print($"[H5] command rejected: {r.Error}"); return; }
        LastRejection = null;
        // Scripts react to the command's events (UnitMove/UnitDeath) and may issue commands of their own.
        _pending.Enqueue(Scripts != null ? r.Events.Concat(Scripts.TakeScriptEvents()).ToList() : r.Events);
        FlushScriptLog();
        CheckEnd();
    }

    public override bool TryDequeue(out IReadOnlyList<BattleEvent> events)
    {
        if (_pending.Count == 0) { events = Array.Empty<BattleEvent>(); return false; }
        events = _pending.Dequeue();
        return true;
    }

    public override void Tick()
    {
        if (!Ended) TickScripts();
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
