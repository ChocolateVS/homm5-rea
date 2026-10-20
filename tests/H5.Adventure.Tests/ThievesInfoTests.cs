using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Thieves' guild (CPlayersThievesInfo 00504380 / 005041a0 / 005056b0, words 00503db0).</summary>
[Collection("adv")]
public class ThievesInfoTests(Fixture f)
{
    [Fact]
    public void CountWordsFollowTheExeThresholds()
    {
        int[] at = { 0, 4, 5, 9, 10, 19, 20, 49, 50, 99, 100, 249, 250, 499, 500, 999, 1000, 99999 };
        int[] want = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8 };
        Assert.Equal(want, at.Select(Engine.Adventure.ThievesCountWord).ToArray());
    }

    [Fact]
    public void ThresholdsLevelsAndRevealOrder()
    {
        var g = f.NewGame("A2M1");
        int players = g.S.Players.Count(p => p.Alive);
        int n = players > 2 ? 3 : players;
        foreach (var (p, i) in g.S.Players.Where(p => p.Alive).Select((p, i) => (p, i))) p.Purse[Purse.Gold] = 1000 * (i + 1);
        var info = g.BuildThievesInfo(0);

        // Index 0: T[0] - 2 = -1, one reveal step → level 0, nothing shown.
        Assert.Equal(1, info.Thresholds[0]);
        Assert.Equal(0, info.Level);
        Assert.Empty(info.Visible());

        // Each buy reveals one more line; the lowest-ranked line of a category comes first, players only (precision 0).
        Assert.True(info.Buy());
        var v = info.Visible();
        var first = Assert.Single(v);
        Assert.Equal("HeroesCount", first.Name);
        if (first.Entries.Count > 1) Assert.Equal(-1, first.Entries[0].Player);   // the leader stays hidden
        Assert.NotEqual(-1, first.Entries[^1].Player);
        Assert.Equal(-2, first.Entries[^1].CountType);

        // Gold is ranked descending and truncated to N.
        var full = g.BuildThievesInfo(99);
        Assert.Equal(full.Thresholds[^1] - 1, full.Level);       // clamped to the last index
        Assert.False(full.CanBuy);
        var gold = full.Visible().First(c => c.Name == "GoldAmount");
        Assert.Equal(n, gold.Entries.Count);
        Assert.All(gold.Entries, e => Assert.Equal(-1, e.CountType));   // fully revealed: exact values
        Assert.True(gold.Entries.Select(e => e.Value).SequenceEqual(gold.Entries.Select(e => e.Value).OrderByDescending(x => x)));
    }

    [Fact]
    public void DenIndexIsDensOwnedPlusOne()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        Assert.Equal(0, g.ThievesDensOwned(p));
        var info = g.BuildThievesInfo(g.ThievesDensOwned(p) + 1);
        Assert.Equal(info.Thresholds[Math.Min(1, info.Thresholds.Count - 1)] - 1, info.Level);
        Assert.Equal(250, ThievesInfo.Cost);
    }
}
