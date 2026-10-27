using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Tavern rumours: CRerollRumourCmd 004f4750 (price RPGStats +0x54 = RumorCost), the generator CRumours slot 7
/// 008e85b0 (candidates, plain weights, pick, duplicate test 008ea5d0), the last events (slots 2..6), the town's
/// weekly rumour 00cd2720 and the map start 0041ecd0 / 008e7cc0. Also the town hire status (004f4570).
/// </summary>
[Collection("adv")]
public class TavernRumourTests(Fixture f)
{
    (Engine.Adventure G, TownState T) Town(int gold)
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Built["TB_TAVERN"] = 1;
        g.Current.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, gold));
        return (g, t);
    }

    /// <summary>One player in play and no grail / map artifacts / statistics leader: only what the test adds.</summary>
    static void Isolate(Engine.Adventure g)
    {
        foreach (var p in g.S.Players.Skip(1)) p.Alive = false;
        g.S.Grail = null;
        foreach (var o in g.Map.Objects.Where(o => o.Kind == ObjectKind.Artifact)) g.S.Obj(o.Index).Removed = true;
        g.S.Rumours = new RumoursState();
    }

    [Fact]
    public void RumourCostIsDefaultStatsRumorCost()
    {
        var (g, _) = Town(0);
        Assert.Equal(100, g.RumourCost);
        Assert.NotEmpty(f.Db.CommonRumours);
        Assert.Equal("Text/Game/Rumours/Formats/CommonRumour.txt", f.Db.RumourFormat("RUMOUR_FORMAT_PLAINTEXT").TrimStart('/'));
    }

    [Fact]
    public void MapStartFillsThePlainListWithTheCommonSetThenTheMapRumours()
    {
        var g = f.NewGame();
        Assert.Equal(f.Db.CommonRumours.Count + g.Map.Rumours.Count, g.S.Rumours.Plain.Count);
        Assert.Equal(f.Db.CommonRumours[0].Text, g.S.Rumours.Plain[0].Text);
        Assert.All(g.S.Rumours.Plain, r => Assert.Equal(0, r.Told));
        Assert.Equal(8, g.S.Rumours.Last.Length);
    }

    [Fact]
    public void BuyingPaysAndAddsOneRumour()
    {
        var (g, t) = Town(250);
        Assert.True(g.Apply(new BuyRumourCmd(t.Object) { Player = g.Current.Index }).Ok);
        Assert.Equal(150, g.Current.Purse[Purse.Gold]);
        Assert.Single(t.Rumours);
        Assert.StartsWith("Text/Game/Rumours/Formats/", t.Rumours[0].Format.TrimStart('/'));
        Assert.True(g.Apply(new BuyRumourCmd(t.Object) { Player = g.Current.Index }).Ok);
        Assert.InRange(t.Rumours.Count, 1, 2);
    }

    [Fact]
    public void NoGoldNoRumour()
    {
        var (g, t) = Town(99);
        Assert.False(g.Apply(new BuyRumourCmd(t.Object) { Player = g.Current.Index }).Ok);
        Assert.Equal(99, g.Current.Purse[Purse.Gold]);
        Assert.Empty(t.Rumours);
    }

    [Fact]
    public void PlainRumourIsToldWithThePlainFormatAndCounted()
    {
        var (g, t) = Town(0);
        Isolate(g);
        g.S.Rumours.Plain.Add(new PlainRumour { Text = "A", Weight = 100, TownType = Engine.Adventure.RumourAnyTown });
        g.GenerateRumour(t.Rumours, t.Type);
        Assert.Equal(f.Db.RumourFormat("RUMOUR_FORMAT_PLAINTEXT"), t.Rumours.Single().Format);
        Assert.Equal("A", t.Rumours[0].Text);
        Assert.Equal(0, t.Rumours[0].Player);
        Assert.Equal(1, g.S.Rumours.Plain[0].Told);
    }

    [Fact]
    public void TheSameRumourAsTheLastIsNotRepeatedButStillCounted()
    {
        // 008ea5d0 against the last entry, two tries (the pick and the next candidate); told +1 on each try.
        var (g, t) = Town(0);
        Isolate(g);
        g.S.Rumours.Plain.Add(new PlainRumour { Text = "A", Weight = 100, TownType = Engine.Adventure.RumourAnyTown });
        g.GenerateRumour(t.Rumours, t.Type);
        g.GenerateRumour(t.Rumours, t.Type);
        Assert.Single(t.Rumours);
        Assert.Equal(3, g.S.Rumours.Plain[0].Told);
    }

    [Fact]
    public void PlainWeightsAreThreeTimesWeightOverToldPlusOneScaledToThreeTimesTheOthers()
    {
        var (g, t) = Town(0);
        Isolate(g);
        g.S.Rumours.Plain.Add(new PlainRumour { Text = "A", Weight = 100, TownType = Engine.Adventure.RumourAnyTown });
        g.S.Rumours.Plain.Add(new PlainRumour { Text = "B", Weight = 100, TownType = Engine.Adventure.RumourAnyTown, Told = 2 });
        g.S.Rumours.Plain.Add(new PlainRumour { Text = "C", Weight = 100, TownType = "TOWN_INFERNO" });
        // No other candidate: f = 1 → 300, 100 (C is not told in a non-Inferno tavern).
        var c = g.RumourCandidates(H5.Combat.Towns.Haven);
        Assert.Equal(new[] { 300, 100 }, c.Select(x => x.Weight));
        // Five last events (500): f = 1500 / 400 = 3.
        var e = g.S.Rumours.Last[0];
        e.Monster = "m"; e.Artifact = "a"; e.Capture = "c"; e.Purchase = "p"; e.Build = "b";
        c = g.RumourCandidates(H5.Combat.Towns.Haven);
        Assert.Equal(new[] { 100, 100, 100, 100, 100, 900, 300 }, c.Select(x => x.Weight));
        Assert.Equal(new[] { 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0, 0 }, c.Select(x => x.Kind));
        Assert.Equal(3, g.RumourCandidates("TOWN_INFERNO").Count(x => x.Kind == 0));
    }

    [Fact]
    public void LastBuildAboveLevelOneIsAnUpgrade()
    {
        var (g, _) = Town(0);
        Isolate(g);
        g.S.Rumours.Last[0].Build = "Fort";
        g.S.Rumours.Last[0].BuildLevel = 1;
        Assert.Equal(f.Db.RumourFormat("RUMOUR_FORMAT_PLAYER_LAST_BUILD"), g.RumourCandidates(Engine.Adventure.RumourAnyTown).Single().Format);
        g.S.Rumours.Last[0].BuildLevel = 2;
        Assert.Equal(f.Db.RumourFormat("RUMOUR_FORMAT_PLAYER_LAST_UPGRADE"), g.RumourCandidates(Engine.Adventure.RumourAnyTown).Single().Format);
    }

    [Fact]
    public void AStatisticNeedsALeaderThatOvertookAnEarlierPlayer()
    {
        // 008e94e9: the flag is cleared only when a later player beats an earlier positive value.
        var g = f.NewGame();
        Isolate(g);
        foreach (var h in g.S.Heroes) h.Alive = false;
        foreach (var t in g.S.Towns) t.Owner = -1;
        foreach (var os in g.S.Objects.Values) os.Owner = -1;
        var (a, b) = (g.S.Players[0], g.S.Players[1]);
        b.Alive = true;
        a.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 200));
        b.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 100));
        Assert.DoesNotContain(g.RumourCandidates(Engine.Adventure.RumourAnyTown), c => c.Kind == 9);
        a.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 100));
        b.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 200));
        var gold = Assert.Single(g.RumourCandidates(Engine.Adventure.RumourAnyTown), c => c.Kind == 9);
        Assert.Equal(1, gold.Index);
        Assert.Equal(f.Db.RumourFormat("RUMOUR_FORMAT_PLAYER_MOST_GOLD"), gold.Format);
        var list = new List<RumourEntry>();
        g.GenerateRumour(list, Engine.Adventure.RumourAnyTown);
        Assert.Equal(b.Index + 1, list.Single().Player);
    }

    [Fact]
    public void EventsAreRecordedByPlayer()
    {
        var (g, t) = Town(100000);
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var tier1 = g.DwellingCreature(t, 1, 0)!;
        t.Built["TB_DWELLING_1"] = 1;
        t.Available[0] = 5;
        Assert.True(g.Apply(new RecruitCmd(t.Object, 1, 1) { Player = p.Index }).Ok);
        Assert.Equal(f.Core.Creatures[tier1].Name, g.S.Rumours.Last[p.Index].Purchase);
        var art = f.Core.Artifacts.Keys.First(a => f.Core.Artifacts[a].Name.Length > 0);
        g.ScriptGiveArtifact(h, art);
        Assert.Equal(f.Core.Artifacts[art].Name, g.S.Rumours.Last[p.Index].Artifact);
    }

    [Fact]
    public void MapTavernListGrowsOnlyByBoughtRumoursWithTheNoTypeRumours()
    {
        var g = f.NewGame("A2M1");
        var p = g.Current;
        p.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 1000));
        var o = g.Map.Objects.First(x => x.Kind == ObjectKind.Building && H5.Adventure.Data.AdventureDb.BankFor(x.Shared.Type) == null);
        var old = o.Shared;
        try
        {
            o.Shared = new SharedDef { Type = Engine.Adventure.MapTavern, Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
            Isolate(g);
            g.S.Rumours.Plain.Add(new PlainRumour { Text = "Heaven only", Weight = 100, TownType = H5.Combat.Towns.Haven });
            g.S.Rumours.Plain.Add(new PlainRumour { Text = "Any", Weight = 100, TownType = Engine.Adventure.RumourAnyTown });
            Assert.True(g.Apply(new BuyRumourCmd(o.Index) { Player = p.Index }).Ok);
            Assert.Equal(900, p.Purse[Purse.Gold]);
            Assert.Equal("Any", g.S.Obj(o.Index).Rumours.Single().Text);
            Assert.Equal(0, g.S.Rumours.Plain[0].Told);
            Assert.Equal(g.S.Obj(o.Index).Rumours.Count, g.S.Clone().Obj(o.Index).Rumours.Count);
        }
        finally { o.Shared = old; }
    }

    [Fact]
    public void TheTownsListIsResetEachWeekWithOneRumour()
    {
        var (g, t) = Town(0);
        t.Rumours.Add(new RumourEntry { Format = "x", Text = "old" });
        t.Rumours.Add(new RumourEntry { Format = "x", Text = "older" });
        int day = g.S.Day;
        while (g.S.DayOfWeek != 1 || g.S.Day == day) Assert.True(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Ok);
        Assert.Single(t.Rumours);
        Assert.NotEqual("old", t.Rumours[0].Text);
    }

    [Fact]
    public void TownHireStatusOrder()
    {
        var (g, t) = Town(0);
        t.Built["TB_TAVERN"] = 0;
        Assert.Equal(2, g.TownHireStatus(g.Current, t));
        t.Built["TB_TAVERN"] = 1;
        Assert.Equal(3, g.TownHireStatus(g.Current, t));
        g.Current.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 100000));
        foreach (var h in g.S.Heroes.Where(h => h.Pos == g.EntranceOf(t)).ToList()) h.Pos = new H5.Adventure.Map.Tile(0, 0, 0);
        Assert.Equal(0, g.TownHireStatus(g.Current, t));
    }
}
