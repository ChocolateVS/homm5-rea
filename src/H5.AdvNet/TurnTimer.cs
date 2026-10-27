namespace H5.AdvNet;

/// <summary>
/// The turn time limit of a local game (single player, hot-seat) as the stock world keeps it: the limit is
/// CWorld +0x94 (custom game: wait screen +0x270 = minutes × 60000; vf 0x1bc = 0098c4a0 "limit set", vf 0x1c0 =
/// 0098c4b0 the limit), the turn start CWorld +0x104, the current player's combat credit (CPlayer vf +0x5c, added
/// by CCloseCombat 004e0f10 with the combat's duration). The world tick 00998520 ends the turn (slot 52) of a
/// current player whose time left (vf 0x1c4 = 0098e4e0) is 0, unless the player holds a combat.
/// The view (vtable 00e29d5c, made by 0060ecf0 from the adventure screen 00598f90 only when a limit is set) shows
/// the time left in TIME_LIMIT_TIMER_WINDOW and the TurnTimeLimitMsgs fade messages (0060edf0 / 0060e8f0).
/// </summary>
public sealed class TurnTimer
{
    public long LimitMs { get; }
    long _turnStart, _credit;
    int _msg = -1;      // view +0x18: index of the last message shown this turn
    long _msgMs;        // view +0x1c: its threshold in ms

    public TurnTimer(int minutes) => LimitMs = Math.Max(0, minutes) * 60_000L;

    /// <summary>vf 0x1bc (0098c4a0): a limit is set.</summary>
    public bool Enabled => LimitMs != 0;

    /// <summary>A new turn of the current player: the turn start, no credit, the view's message state cleared (0060ec30).</summary>
    public void NewTurn(long nowMs)
    {
        _turnStart = nowMs;
        _credit = 0;
        _msg = -1;
        _msgMs = 0;
    }

    /// <summary>CCloseCombat (004e0f10): the combat's duration is credited to the player.</summary>
    public void AddCombat(long ms) => _credit += Math.Max(0, ms);

    /// <summary>
    /// 0098e4e0 for the current player: 0 without a limit; else limit − max(0, now − turn start − credit), at least 0.
    /// </summary>
    public long TimeLeft(long nowMs)
    {
        if (LimitMs == 0) return 0;
        long used = nowMs - _turnStart > _credit ? nowMs - _turnStart - _credit : 0;
        return used < LimitMs ? LimitMs - used : 0;
    }

    /// <summary>00998520: the current player's turn ends when a limit is set and no time is left.</summary>
    public bool Expired(long nowMs) => Enabled && TimeLeft(nowMs) == 0;

    /// <summary>
    /// 0060edf0: seconds = (ms + 999) / 1000, shown as "%02d" minutes and seconds (time_min / time_sec of TimeView);
    /// the window's vf 0x44 gets 1 instead of 0 for 60 minutes and more.
    /// </summary>
    public static (string Min, string Sec, bool Over59) Display(long ms)
    {
        long s = (ms + 999) / 1000;
        long m = s / 60;
        return (m.ToString("00"), (s % 60).ToString("00"), m > 59);
    }

    /// <summary>
    /// 0060e8f0: among the TurnTimeLimitMsgs thresholds T (Time, seconds; the exe converts ROUND(T + 0.5) · 1000 with the
    /// FPU's rounding — taken as T for the stock whole-second Times), takes the smallest
    /// with T·1000 &lt; limit, time left ≤ T·1000 and below the last one shown this turn; returns its index when one
    /// was taken, else -1.
    /// </summary>
    public int NextMessage(long timeLeftMs, IReadOnlyList<int> thresholdsSec)
    {
        int picked = -1;
        for (int i = 0; i < thresholdsSec.Count; i++)
        {
            long t = thresholdsSec[i] * 1000L;
            if (t < LimitMs && timeLeftMs <= t && (_msg == -1 || t < _msgMs))
            {
                _msg = i;
                _msgMs = t;
                picked = i;
            }
        }
        return picked;
    }
}
