using System.Diagnostics;

namespace H5.Combat;

/// <summary>How a <see cref="PendingDecision"/> is worked out.</summary>
public enum DecisionMode
{
    /// <summary>At once, inside <see cref="PendingDecision.Start(Battle, int, IBattleAI, DecisionMode)"/>.</summary>
    Immediate,
    /// <summary>On a worker thread.</summary>
    Background,
    /// <summary>
    /// In steps on the caller's thread, a time budget per <see cref="PendingDecision.Advance"/> (for a build without
    /// threads: the browser). Needs an <see cref="ISteppedBattleAI"/>; another AI decides at once.
    /// </summary>
    Sliced,
}

/// <summary>
/// One AI decision for the side to act, worked out on a copy of the battle so the presentation keeps
/// running while the AI thinks (the combat screen calls the AI once per turn and the original AI takes
/// tens to hundreds of milliseconds; on the render thread that froze every frame and the next frame then
/// caught up all at once). The copy is taken when the decision starts; the command is handed out only
/// while the live battle still matches that copy, so a decision never applies to a later position and
/// commands stay strictly one per turn, in order. Without threads (web) the decision is worked out in
/// steps over several frames (<see cref="DecisionMode.Sliced"/>), with the same result.
/// </summary>
public sealed class PendingDecision
{
    readonly string _snapshot;
    readonly Task<BattleCommand>? _task;
    IEnumerator<BattleCommand?>? _steps;
    BattleCommand? _result;
    Exception? _error;

    public int Side { get; }

    PendingDecision(string snapshot, int side, Task<BattleCommand>? task, IEnumerator<BattleCommand?>? steps)
    {
        _snapshot = snapshot;
        Side = side;
        _task = task;
        _steps = steps;
    }

    /// <summary>
    /// Starts deciding for <paramref name="side"/> on a copy of <paramref name="live"/>. The AI instance must not be
    /// used elsewhere until <see cref="IsCompleted"/>.
    /// </summary>
    public static PendingDecision Start(Battle live, int side, IBattleAI ai, bool background) =>
        Start(live, side, ai, background ? DecisionMode.Background : DecisionMode.Immediate);

    /// <inheritdoc cref="Start(Battle, int, IBattleAI, bool)"/>
    public static PendingDecision Start(Battle live, int side, IBattleAI ai, DecisionMode mode)
    {
        var json = live.State.Serialize();
        var copy = new Battle(live.Db, BattleState.Deserialize(json));
        if (mode == DecisionMode.Sliced && ai is ISteppedBattleAI stepped)
            return new PendingDecision(json, side, null, stepped.DecideSteps(copy, side).GetEnumerator());
        BattleCommand Run() => ai.Decide(copy, side) with { Side = side };
        Task<BattleCommand> task;
        if (mode == DecisionMode.Background) task = Task.Run(Run);
        else
        {
            try { task = Task.FromResult(Run()); }
            catch (Exception e) { task = Task.FromException<BattleCommand>(e); }
        }
        return new PendingDecision(json, side, task, null);
    }

    /// <summary>
    /// A sliced decision works on for about <paramref name="budgetMs"/> milliseconds (at least one step); returns
    /// <see cref="IsCompleted"/>. Nothing to do for the other modes.
    /// </summary>
    public bool Advance(double budgetMs)
    {
        if (_steps is not { } steps) return IsCompleted;
        long until = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
        try
        {
            do
            {
                if (!steps.MoveNext()) throw new InvalidOperationException("the AI decision ended without a command");
                if (steps.Current is { } cmd) { _result = cmd with { Side = Side }; break; }
            }
            while (Stopwatch.GetTimestamp() < until);
        }
        catch (Exception e) { _error = e; }
        if (_result != null || _error != null) { steps.Dispose(); _steps = null; }
        return IsCompleted;
    }

    public bool IsCompleted => _task?.IsCompleted ?? (_result != null || _error != null);

    /// <summary>The AI's failure, if it threw.</summary>
    public Exception? Error => _task is { } t ? (t.IsFaulted ? t.Exception?.GetBaseException() : null) : _error;

    /// <summary>
    /// The decided command once finished and only while <paramref name="live"/> is still in the position it was
    /// decided for; null while thinking, after a failure, or when the battle has moved on (decide again).
    /// </summary>
    public BattleCommand? TakeIfCurrent(Battle live)
    {
        BattleCommand? result = _task is { } t ? (t.IsCompletedSuccessfully ? t.Result : null) : _result;
        if (result == null) return null;
        if (live.SideToAct != Side || live.State.Serialize() != _snapshot) return null;
        return result;
    }
}
