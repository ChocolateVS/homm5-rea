using H5.AdvNet;
using Xunit;

namespace H5.AdvNet.Tests;

/// <summary>Local turn time limit: CWorld +0x94 / +0x104, 0098e4e0, the tick 00998520 and the view 0060edf0 / 0060e8f0.</summary>
public class TurnTimerTests
{
    static readonly int[] StockTimes = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 30, 60, 300, 600 };

    [Fact]
    public void TimeLeftCountsFromTheTurnStartLessTheCombatCredit()
    {
        var t = new TurnTimer(2);
        t.NewTurn(1000);
        Assert.Equal(120_000, t.TimeLeft(1000));
        Assert.Equal(90_000, t.TimeLeft(31_000));
        t.AddCombat(20_000);   // CCloseCombat 004e0f10
        Assert.Equal(110_000, t.TimeLeft(31_000));
        Assert.False(t.Expired(140_999));
        Assert.True(t.Expired(141_000));
        Assert.Equal(0, t.TimeLeft(500_000));
        t.NewTurn(500_000);
        Assert.Equal(120_000, t.TimeLeft(500_000));
    }

    [Fact]
    public void NoLimitNeverExpires()
    {
        var t = new TurnTimer(0);
        t.NewTurn(0);
        Assert.False(t.Enabled);
        Assert.False(t.Expired(10_000_000));
        Assert.Equal(0, t.TimeLeft(5));
    }

    /// <summary>0060edf0: (ms + 999) / 1000 seconds, "%02d" minutes and seconds.</summary>
    [Fact]
    public void DisplayRoundsUpToWholeSeconds()
    {
        Assert.Equal(("05", "00", false), TurnTimer.Display(300_000));
        Assert.Equal(("04", "59", false), TurnTimer.Display(299_000));
        Assert.Equal(("00", "01", false), TurnTimer.Display(1));
        Assert.Equal(("00", "00", false), TurnTimer.Display(0));
        Assert.Equal(("60", "00", true), TurnTimer.Display(3_600_000));
    }

    /// <summary>0060e8f0: only thresholds below the limit, the smallest reached, each once and only downwards.</summary>
    [Fact]
    public void MessagesFollowTheThresholdsDownwards()
    {
        var t = new TurnTimer(5);
        t.NewTurn(0);
        Assert.Equal(-1, t.NextMessage(300_000, StockTimes));   // 300 s is not below the 5-minute limit
        Assert.Equal(12, t.NextMessage(60_000, StockTimes));    // 1 minute
        Assert.Equal(-1, t.NextMessage(59_000, StockTimes));
        Assert.Equal(10, t.NextMessage(9_500, StockTimes));     // 30 s and 10 s are reached: the smallest (10 s) is taken
        Assert.Equal(9, t.NextMessage(9_000, StockTimes));
        Assert.Equal(0, t.NextMessage(0, StockTimes));
        Assert.Equal(-1, t.NextMessage(0, StockTimes));
        t.NewTurn(0);
        Assert.Equal(-1, t.NextMessage(280_000, StockTimes));
    }
}
