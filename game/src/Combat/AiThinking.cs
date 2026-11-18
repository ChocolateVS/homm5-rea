using Godot;
using H5.Combat;

namespace H5Reborn.Combat;

/// <summary>
/// How the combat screens work out AI decisions (auto-combat, the computer side of a duel or an adventure battle) so the
/// picture keeps moving while the AI thinks (user report 27): on a worker thread on the desktop; in the browser build,
/// which has no threads, in steps of about <see cref="FrameBudgetMs"/> per frame (<see cref="DecisionMode.Sliced"/>,
/// same commands). `--ai-think=inline|thread|sliced` forces a mode (measurements); `--frame-stats` logs the frame
/// times of each battle when its results show.
/// </summary>
public static class AiThinking
{
    /// <summary>AI time per frame in the sliced mode.</summary>
    public const double FrameBudgetMs = 8;

    static DecisionMode? _override;
    public static DecisionMode Mode => _override ?? (OS.HasFeature("web") ? DecisionMode.Sliced : DecisionMode.Background);

    public static bool HandleArg(string arg)
    {
        if (arg.StartsWith("--ai-think="))
        {
            _override = arg["--ai-think=".Length..] switch
            {
                "inline" => DecisionMode.Immediate, "thread" => DecisionMode.Background, "sliced" => DecisionMode.Sliced,
                var v => throw new ArgumentException($"--ai-think={v}: inline, thread or sliced"),
            };
            return true;
        }
        if (arg == "--frame-stats") { FrameStats = true; return true; }
        return false;
    }

    /// <summary>
    /// One frame's turn of the decision in <paramref name="pending"/> (started here when there is none): the command once
    /// it is ready and still for the live position, else null (thinking, failed, or stale: a new decision starts next time).
    /// </summary>
    public static BattleCommand? Poll(ref PendingDecision? pending, Battle live, int side, IBattleAI ai, string what)
    {
        ulong t0 = FrameStats ? Time.GetTicksUsec() : 0;
        try { return PollCore(ref pending, live, side, ai, what); }
        finally { if (FrameStats) _aiUs += Time.GetTicksUsec() - t0; }
    }

    static BattleCommand? PollCore(ref PendingDecision? pending, Battle live, int side, IBattleAI ai, string what)
    {
        if (pending is not { } d)
        {
            _aiWork = true;
            pending = PendingDecision.Start(live, side, ai, Mode);
            return null;
        }
        if (Mode == DecisionMode.Sliced) _aiWork = true;
        if (!d.Advance(FrameBudgetMs)) return null;
        pending = null;
        if (d.Error is { } err) GD.PushError($"[H5] {what}: {err}");
        return d.TakeIfCurrent(live);
    }

    // ---------------------------------------------------------------- frame statistics (--frame-stats)

    public static bool FrameStats;
    static bool _aiWork;
    /// <summary>Time spent in <see cref="Poll"/> during the current frame (on this thread: the work a frame waits for).</summary>
    static ulong _aiUs;
    static int _aiOver20;
    static double _maxAiPollMs;
    static ulong _lastUs;
    static int _frames, _over40, _over40Ai;
    static double _maxMs, _maxAiMs;

    /// <summary>Called once per combat-screen frame: the time since the previous call is the previous frame's length.</summary>
    public static void Frame()
    {
        if (!FrameStats) return;
        ulong now = Time.GetTicksUsec();
        if (_lastUs != 0)
        {
            double ms = (now - _lastUs) / 1000.0;
            _frames++;
            _maxMs = Math.Max(_maxMs, ms);
            if (ms > 40) _over40++;
            if (_aiWork)   // the AI worked in the frame just measured
            {
                _maxAiMs = Math.Max(_maxAiMs, ms);
                if (ms > 40) _over40Ai++;
            }
        }
        double aiMs = _aiUs / 1000.0;
        _maxAiPollMs = Math.Max(_maxAiPollMs, aiMs);
        if (aiMs > 20) _aiOver20++;
        _lastUs = now;
        _aiWork = false;
        _aiUs = 0;
    }

    /// <summary>Logs and resets the statistics (the battle is over).</summary>
    public static void Report()
    {
        if (!FrameStats) return;
        GD.Print($"[H5] frame stats ({Mode}): {_frames} frames, {_over40} over 40 ms (max {_maxMs:F0} ms); "
            + $"frames with AI work: {_over40Ai} over 40 ms (max {_maxAiMs:F0} ms); "
            + $"AI time on the frame's thread: {_aiOver20} frames over 20 ms (max {_maxAiPollMs:F0} ms)");
        _frames = _over40 = _over40Ai = _aiOver20 = 0;
        _maxMs = _maxAiMs = _maxAiPollMs = 0;
        _lastUs = 0;
    }
}
