using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

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
        Assert.Equal(g.S.Players[0].Mission.Creatures, s.CreaturesDuring);   // the map-start count (vf 0x8) so far
    }

    (Engine.Adventure G, HeroState Att, HeroState Def) HeroBattle()
    {
        var g = f.NewGame();
        var att = g.S.Heroes.First(h => h.Alive && h.Owner == g.Current.Index);
        var def = g.S.Heroes.First(h => h.Alive && h.Owner != g.Current.Index && h.Owner >= 0);
        g.S.Battle = new PendingBattle
        {
            AttackerHero = att.Id, DefenderHero = def.Id, DefenderObject = -1, Where = def.Pos,
            DefenderArmy = def.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList(),
        };
        return (g, att, def);
    }

    static MissionRecord Rec(Engine.Adventure g, int player) => g.S.Players.Single(p => p.Index == player).Mission;

    [Fact]
    public void AHeroBeatenInBattleCountsTheBattleAndTheKill()
    {
        // 00af6680 → logger vf 0x18 (00877570): both owners +0x24, the winner's +0x28, the loser's +0x2c; the death
        // 004fe5b0 → vf 0x28 (00877680): the killer's player +0x34; not a tavern trip, so no +0x30.
        var (g, att, def) = HeroBattle();
        var r = g.Apply(new ResolveBattleCmd(0, att.Army.Live().Select(s => new ArmySlot(s.Creature, s.Count)).ToList(), new List<ArmySlot>())
        { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.False(def.Alive);
        var a = g.Summary(att.Owner);
        var d = g.Summary(def.Owner);
        Assert.Equal((1, 1, 0, 1, 0), (a.BattlesFought, a.BattlesWon, a.BattlesLost, a.HeroesKilled, a.HeroesLost));
        Assert.Equal((1, 0, 1, 0, 0), (d.BattlesFought, d.BattlesWon, d.BattlesLost, d.HeroesKilled, d.HeroesLost));
    }

    [Fact]
    public void AFleeingHeroIsKilledForTheWinnerAndLostForHisOwner()
    {
        // 004fe770: vf 0x28 for the winner's player, then hero vf 0x1d8 → 00b52cd0 → vf 0x2c (00877700): owner +0x30.
        var (g, att, def) = HeroBattle();
        var r = g.Apply(new ResolveBattleCmd(1, new List<ArmySlot>(), def.Army.Live().Select(s => new ArmySlot(s.Creature, s.Count)).ToList())
        { Player = g.Current.Index, EndReason = "fled" });
        Assert.True(r.Ok, r.Error);
        Assert.False(att.Alive);
        Assert.Equal(1, Rec(g, def.Owner).HeroesKilled);
        Assert.Equal(1, Rec(g, att.Owner).HeroesLost);
        Assert.Equal(1, Rec(g, att.Owner).BattlesLost);
        Assert.Equal(1, Rec(g, def.Owner).BattlesWon);
    }

    [Fact]
    public void ABattleAgainstNeutralsCountsOnlyForThePlayer()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        g.S.Battle = new PendingBattle { AttackerHero = h.Id, DefenderObject = -1, Where = h.Pos, DefenderArmy = { new Stack("CREATURE_PEASANT", 1) } };
        var r = g.Apply(new ResolveBattleCmd(0, h.Army.Live().Select(s => new ArmySlot(s.Creature, s.Count)).ToList(), new List<ArmySlot>())
        { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        var s = g.Summary(h.Owner);
        Assert.Equal((1, 1, 0, 0), (s.BattlesFought, s.BattlesWon, s.BattlesLost, s.HeroesKilled));
        Assert.All(g.S.Players.Where(p => p.Index != h.Owner), p => Assert.Equal(0, p.Mission.BattlesFought));
    }
}
