using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// Building visits of 00b02740: stables (case 0xf), Eldritch Spring (0x14), Eldritch Well (0x1c), Sirens (0x3d),
/// Tomb of the Warrior (0x67).
/// </summary>
[Collection("adv")]
public class VisitRulesTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h, List<MapObject> o) Setup(Fixture f, string type, int count = 1)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.MovePoints = h.MaxMovePoints = 2000;
        var os = g.Map.Objects.Where(x => x.Kind == ObjectKind.Building && x.ActiveTiles.Any() && g.S.Obj(x.Index).Guards.Count == 0
                                          && AdventureDb.BankFor(x.Shared.Type) == null).Take(count).ToList();
        foreach (var o in os)
        {
            var old = o.Shared;
            o.Shared = new SharedDef { Type = type, Messages = old.Messages, Active = old.Active, Blocked = old.Blocked, Passable = old.Passable, Path = old.Path };
        }
        return (g, h, os);
    }

    static List<AdvEvent> Visit(Engine.Adventure g, HeroState h, MapObject o)
    {
        h.Pos = o.ActiveTiles.First();
        h.MovePoints = Math.Max(h.MovePoints, 1000);
        var r = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        return r.Events.ToList();
    }

    static bool Says(List<AdvEvent> ev, string what) => ev.Any(e => e is ObjectVisited ov && ov.What == what);

    [Fact]
    public void WellIsOneDrinkPerHeroPerDayFromAnyWell()
    {
        var (g, h, wells) = Setup(f, "BUILDING_MAGIC_WELL", 2);
        int max = g.MaxMana(h);
        Assert.True(max > 0);
        h.Mana = 0;
        Assert.True(Says(Visit(g, h, wells[0]), $"mana:{max}"));   // the ICON_MANA flyoff carries the gain
        Assert.Equal(max, h.Mana);
        h.Mana = 0;
        // 00b02d76: the hero's daily flag (+0x174), not the well's: a second well the same day refuses with [3].
        Assert.True(Says(Visit(g, h, wells[1]), "fly:3"));
        Assert.Equal(0, h.Mana);
    }

    [Fact]
    public void WellWithFullManaSaysAlreadyMaximum()
    {
        var (g, h, wells) = Setup(f, "BUILDING_MAGIC_WELL");
        h.Mana = g.MaxMana(h);
        Assert.True(Says(Visit(g, h, wells[0]), "fly:4"));
        Assert.False(h.DrankFromWellToday);
    }

    [Fact]
    public void SpringDoublesManaForOneHeroAWeekAndTheExtraManaStays()
    {
        var (g, h, springs) = Setup(f, "BUILDING_MAGIC_SPRING");
        int max = g.MaxMana(h);
        h.Mana = 0;
        Assert.True(Says(Visit(g, h, springs[0]), $"mana:{2 * max}"));
        Assert.Equal(2 * max, h.Mana);
        // The flag is the spring's (+0xe8): anyone after that in the week gets [4].
        h.Mana = 0;
        Assert.True(Says(Visit(g, h, springs[0]), "fly:4"));
        Assert.Equal(0, h.Mana);
        // 2 · max − mana < 1 → [3] on a fresh spring week.
        g.S.Obj(springs[0].Index).LastVisitWeek = 0;
        h.Mana = 2 * max;
        Assert.True(Says(Visit(g, h, springs[0]), "fly:3"));
        // 00b50470 regenerates only below the maximum, so the doubled mana survives the new day.
        int day = g.S.Day;
        while (g.S.Day == day) g.Apply(new EndTurnCmd { Player = g.Current.Index });
        Assert.Equal(2 * max, h.Mana);
    }

    [Fact]
    public void StablesGiveTheMovementOnceAWeekWithTheirTexts()
    {
        var (g, h, st) = Setup(f, "BUILDING_STABLES");
        h.StablesThisWeek = false;
        h.MovePoints = 1000;
        h.Pos = st[0].ActiveTiles.First();
        var ev = g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner }).Events.ToList();
        Assert.True(Says(ev, "msg:2"));
        Assert.Equal(1000 + 600, h.MovePoints);   // stock VisitStablesMovementBonus
        Assert.True(Says(Visit(g, h, st[0]), "msg:3"));
    }

    [Fact]
    public void SirensTakeThirtyPercentRoundedFromStacksOfTwoOrMore()
    {
        var (g, h, s) = Setup(f, "BUILDING_SIRENS");
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_PEASANT", 15);   // (15·30 + 50) / 100 = 5
        h.Army[1] = new Stack("CREATURE_ARCHER", 1);     // single creatures are spared
        long exp = h.Experience;
        int hp = g.CreatureHitPoints(h, "CREATURE_PEASANT");
        Assert.True(Says(Visit(g, h, s[0]), "msg:2"));
        Assert.Equal(10, h.Army[0]!.Count);
        Assert.Equal(1, h.Army[1]!.Count);
        // 00b057f3: experience through +0x198, no modifiers.
        Assert.Equal(exp + 5L * hp, h.Experience);
        Assert.True(Says(Visit(g, h, s[0]), "fly:3"));   // [3] flies (009c90e0)
        Assert.Equal(10, h.Army[0]!.Count);
    }

    [Fact]
    public void SirensWithNothingToTakeSayThirdTextAndDoNotMarkTheHero()
    {
        var (g, h, s) = Setup(f, "BUILDING_SIRENS");
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_PEASANT", 1);
        Assert.True(Says(Visit(g, h, s[0]), "fly:3"));
        Assert.DoesNotContain(s[0].Index, h.VisitedOnce);
    }

    [Fact]
    public void TombIsLootedOnceAndPenalisesMoraleUntilTheNextBattle()
    {
        var (g, h, t) = Setup(f, "BUILDING_TOMB_OF_THE_WARRIOR");
        int morale = h.NextBattleMorale, arts = h.Artifacts.Count;
        Assert.True(Says(Visit(g, h, t[0]), "msg:2"));
        Assert.Equal(arts + 1, h.Artifacts.Count);
        Assert.Equal(morale - 3, h.NextBattleMorale);
        // Looted (object +0xe9) and already penalised since the battle: [4], nothing more.
        Assert.True(Says(Visit(g, h, t[0]), "msg:4"));
        Assert.Equal(morale - 3, h.NextBattleMorale);
        Assert.Equal(arts + 1, h.Artifacts.Count);
        // A hero not yet penalised: [3] with the −3.
        h.VisitedSinceBattle.Clear();
        Assert.True(Says(Visit(g, h, t[0]), "msg:3"));
        Assert.Equal(morale - 6, h.NextBattleMorale);
        Assert.Equal(arts + 1, h.Artifacts.Count);
    }
    [Fact]
    public void HutOfMagiRevealsTheEyesOfItsGroupAndToursThemForAHuman()
    {
        var (g, h, o) = Setup(f, "BUILDING_EYE_OF_MAGI", 4);
        var hut = o[0];
        hut.Shared = new SharedDef { Type = "BUILDING_HUT_OF_MAGI", Messages = hut.Shared.Messages, Active = hut.Shared.Active, Blocked = hut.Shared.Blocked, Passable = hut.Shared.Passable, Path = hut.Shared.Path };
        var props = o.Select(x => x.Props.GetValueOrDefault("GroupID")).ToList();
        try
        {
            hut.Props["GroupID"] = "7"; o[1].Props["GroupID"] = "7"; o[2].Props["GroupID"] = "3"; o[3].Props["GroupID"] = "7";
            var p = g.Current;
            p.Human = true;
            bool Seen(MapObject e) => p.Explored[e.Pos.Floor][e.Pos.Y * g.Map.Floor(e.Pos.Floor)!.Width + e.Pos.X];
            foreach (var e in o.Skip(1)) p.Explored[e.Pos.Floor][e.Pos.Y * g.Map.Floor(e.Pos.Floor)!.Width + e.Pos.X] = false;
            var ev = Visit(g, h, hut);
            Assert.True(Seen(o[1]));
            Assert.True(Seen(o[3]));
            var tour = ev.OfType<ObjectVisited>().Single(e => e.What.StartsWith("magi_tour:")).What;
            Assert.Equal("magi_tour:" + string.Join(";", new[] { o[1], o[3] }.OrderBy(e => e.Index).Select(e => $"{e.Pos.X},{e.Pos.Y},{e.Pos.Floor}")), tour);
            // An AI player gets the reveal but no tour.
            p.Human = false;
            Assert.DoesNotContain(Visit(g, h, hut), e => e is ObjectVisited ov && ov.What.StartsWith("magi_tour:"));
        }
        finally { for (int i = 0; i < o.Count; i++) { if (props[i] == null) o[i].Props.Remove("GroupID"); else o[i].Props["GroupID"] = props[i]!; } }
    }

    [Fact]
    public void CameraTourHoldsEachStopForItsTimeThenRestoresThePose()
    {
        var cam = new MissionCamera(100, 100);
        cam.Target.X = cam.Current.X = 30; cam.Target.Y = cam.Current.Y = 40;
        var saved = cam.Target;
        var tour = new MissionCameraTour(cam, new[] { new TourStop(10, 12, 0), new TourStop(50, 60, 1, Time: 2, Rod: 30) });
        Assert.Equal(0, tour.Update(0)?.Floor);              // the first stop shows at once
        Assert.Equal((10f, 12f, saved.Rod), (cam.Target.X, cam.Target.Y, cam.Target.Rod));
        Assert.Null(tour.Update(4.9f));                       // 5 s default time
        Assert.Equal(1, tour.Update(0.1f)?.Floor);
        Assert.Equal((50f, 60f, 30f, saved.Pitch), (cam.Target.X, cam.Target.Y, cam.Target.Rod, cam.Target.Pitch));
        tour.Update(1.9f);
        Assert.False(tour.Done);
        tour.Update(0.1f);
        Assert.True(tour.Done);
        Assert.Equal((saved.X, saved.Y, saved.Rod), (cam.Current.X, cam.Current.Y, cam.Current.Rod));
    }
}
