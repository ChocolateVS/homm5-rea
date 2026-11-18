namespace H5.Combat;

/// <summary>
/// One AI decision for the side to act, worked out on a copy of the battle so the presentation keeps
/// running while the AI thinks (the combat screen calls the AI once per turn and the original AI takes
/// tens to hundreds of milliseconds; on the render thread that froze every frame and the next frame then
/// caught up all at once). The copy is taken when the decision starts; the command is handed out only
/// while the live battle still matches that copy, so a decision never applies to a later position and
/// commands stay strictly one per turn, in order. Without threads (web) the decision runs at once.
/// </summary>
public sealed class PendingDecision
{
    readonly string _snapshot;
    readonly Task<BattleCommand> _task;

    public int Side { get; }

    PendingDecision(string snapshot, int side, Task<BattleCommand> task)
    {
        _snapshot = snapshot;
        Side = side;
        _task = task;
    }

    /// <summary>
    /// Starts deciding for <paramref name="side"/> on a copy of <paramref name="live"/>. The AI instance must not be
    /// used elsewhere until <see cref="IsCompleted"/>.
    /// </summary>
    public static PendingDecision Start(Battle live, int side, IBattleAI ai, bool background)
    {
        var json = live.State.Serialize();
        var copy = new Battle(live.Db, BattleState.Deserialize(json));
        BattleCommand Run() => ai.Decide(copy, side) with { Side = side };
        Task<BattleCommand> task;
        if (background) task = Task.Run(Run);
        else
        {
            try { task = Task.FromResult(Run()); }
            catch (Exception e) { task = Task.FromException<BattleCommand>(e); }
        }
        return new PendingDecision(json, side, task);
    }

    public bool IsCompleted => _task.IsCompleted;

    /// <summary>The AI's failure, if it threw.</summary>
    public Exception? Error => _task.IsFaulted ? _task.Exception?.GetBaseException() : null;

    /// <summary>
    /// The decided command once finished and only while <paramref name="live"/> is still in the position it was
    /// decided for; null while thinking, after a failure, or when the battle has moved on (decide again).
    /// </summary>
    public BattleCommand? TakeIfCurrent(Battle live)
    {
        if (!_task.IsCompletedSuccessfully) return null;
        if (live.SideToAct != Side || live.State.Serialize() != _snapshot) return null;
        return _task.Result;
    }
}
