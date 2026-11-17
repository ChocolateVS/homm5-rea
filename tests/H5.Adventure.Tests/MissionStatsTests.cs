using H5.Adventure.Engine;

namespace H5.Adventure.Tests;

/// <summary>
/// Mission statistics (H5_Game.exe CMissionResultsLogger, world +0x170; summary 00877dd0; rows 0079fcf0).
/// </summary>
[Collection("adv")]
public class MissionStatsTests(Fixture f)
{
    [Fact]
    public void MissionTimeComesFromTheDayCounter()
    {
        var g = f.NewGame("A2M2");
        var s = g.Summary(g.S.Players[0].Index);
        Assert.Equal((1, 1, 1), (s.Days, s.Weeks, s.Months));
        // 0079fcf0 with day counter d = Day − 1 = 29: d % 7 + 1, (d / 7) % 4 + 1, d / 28 + 1.
        g.S.Day = 30;
        s = g.Summary(g.S.Players[0].Index);
        Assert.Equal((2, 1, 2), (s.Days, s.Weeks, s.Months));
    }

    [Fact]
    public void ATownChangingHandsCountsForBothPlayers()
    {
        // 00cd5400 → logger vf 0x34 (00877780): old owner +0x20 (lost), new owner +0x1c (occupied).
        var g = f.NewGame("A2M2");
        var a = g.S.Players[0];
        var t = g.S.Towns.First(x => x.Owner >= 0 && x.Owner != a.Index);
        var b = g.S.Players.Single(p => p.Index == t.Owner);
        g.ScriptCaptureTown(null, t, a.Index);
        Assert.Equal(1, a.Mission.TownsOccupied);
        Assert.Equal(1, b.Mission.TownsLost);
        Assert.Equal(1, g.Summary(a.Index).TownsOccupied);
        Assert.Equal(1, g.Summary(b.Index).TownsLost);
    }

    [Fact]
    public void ArtifactsCountTheMapStartAndEveryGain()
    {
        // CLogMapStartedCmd (00428ad0 → 00877d40) sets the count; each hero gain (00b4b610 → 00877810) adds one.
        var g = f.NewGame("A2M2");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner >= 0);
        var p = g.S.Players.Single(x => x.Index == h.Owner);
        int start = g.S.Heroes.Where(x => x.Alive && x.Owner == p.Index).Sum(x => x.Artifacts.Count + x.Backpack.Count);
        Assert.Equal(start, p.Mission.Artifacts);
        g.ScriptGiveArtifact(h, "SWORD_OF_RUINS");
        var s = g.Summary(p.Index);
        Assert.Equal(start + 1, s.ArtifactsDuring);
        Assert.Equal(start + 1, s.ArtifactsAtEnd);
    }

    [Fact]
    public void CreaturesAtTheEndAreHeroArmiesAndOwnedGarrisonsAndEnemiesAreSummed()
    {
        var g = f.NewGame("A2M2");
        var me = g.S.Players[0].Index;
        int mine = g.S.Heroes.Where(h => h.Alive && h.Owner == me).Sum(h => h.Army.Sum(st => st?.Count ?? 0))
            + g.S.Towns.Where(t => t.Owner == me).Sum(t => t.Garrison.Sum(st => st?.Count ?? 0));
        var s = g.Summary(me);
        Assert.Equal(mine, s.CreaturesAtEnd);
        Assert.Equal(g.S.Players.Where(p => p.Team != g.S.Players[0].Team).Sum(p => g.Summary(p.Index).CreaturesAtEnd), s.EnemyCreaturesAtEnd);
        Assert.Null(s.BattlesFought);
    }
}
