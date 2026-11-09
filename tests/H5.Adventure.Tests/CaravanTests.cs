using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Caravans end to end (H5_Game.exe 00428f20 / 0042c630 / 0042cb30 / 004291d0 / 00ce4de0 / 00ce4d40 / 00433440).</summary>
[Collection("adv")]
public class CaravanTests(Fixture f)
{
    /// <summary>A2M1 with the current player's town and an owned map dwelling that has a caravan route to it.</summary>
    static (Engine.Adventure g, TownState town, MapObject dwelling) Setup(Fixture f)
    {
        var g = f.NewGame("A2M1");
        var town = g.S.Towns.First(t => t.Owner == g.Current.Index);
        foreach (var o in g.Map.Objects.Where(o => o.Kind == ObjectKind.Dwelling && o.ActiveTiles.Any() && o.Pos.Floor == town.Pos.Floor))
        {
            var os = g.S.Obj(o.Index);
            int owner = os.Owner;
            os.Owner = town.Owner;
            if (g.CaravanSources(town).Any(s => s.Object == o.Index) && g.CaravanStartTiles(o.Index).Count > 0)
            {
                for (int i = 0; i < os.Available.Length; i++) os.Available[i] = 10;
                g.Current.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 9999999));
                return (g, town, o);
            }
            os.Owner = owner;
        }
        throw new InvalidOperationException("no dwelling with a caravan route on A2M1");
    }

    static string FirstCreature(Engine.Adventure g, MapObject d) => d.Shared.DwellingCreatures[g.DwellingRows(d.Index)[0]];

    static void NextDay(Engine.Adventure g)
    {
        int day = g.S.Day;
        for (int i = 0; i < 20 && g.S.Day == day; i++)
        {
            var r = g.Apply(new EndTurnCmd { Player = g.Current.Index });
            Assert.True(r.Ok, r.Error);
        }
    }

    [Fact]
    public void PurchaseClampsToThePoolPaysAndMakesOneCaravanBesideTheSource()
    {
        var (g, town, d) = Setup(f);
        var creature = FirstCreature(g, d);
        var os = g.S.Obj(d.Index);
        int row = g.DwellingRows(d.Index)[0];
        int gold = g.Current.Purse[Purse.Gold];
        // 00428f20: the count is clamped to the pool (vf 0xc); cost × count is paid (vf 0xe0).
        var r = g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 50) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0, os.Available[row]);
        Assert.Equal(gold - 10 * g.Core.Creatures[creature].Cost.Gold, g.Current.Purse[Purse.Gold]);
        var c = Assert.Single(g.S.Caravans);
        // 00ce5f40 → 00cec660: the first free neighbour of the source's entrance tiles; destination = the town's entrance.
        Assert.Contains(d.ActiveTiles, e => Math.Max(Math.Abs(e.X - c.Pos.X), Math.Abs(e.Y - c.Pos.Y)) == 1);
        Assert.Equal(g.EntranceOf(town), c.Dest);
        Assert.Contains(c.Army, s => s is { Count: 10 } && s.Creature == creature);
        Assert.True(c.DaysLeft >= 1);                                           // 00ce4d40
        // An empty pool refuses; a second purchase from a refilled pool joins the same caravan (00ce4270 → 00ce4450).
        Assert.False(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 1) { Player = g.Current.Index }).Ok);
        os.Available[row] = 3;
        Assert.True(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 2) { Player = g.Current.Index }).Ok);
        Assert.Single(g.S.Caravans);
        Assert.Contains(c.Army, s => s is { Count: 12 } && s.Creature == creature);
    }

    [Fact]
    public void PurchaseNeedsTheSourceOwnerAndMoney()
    {
        var (g, town, d) = Setup(f);
        var creature = FirstCreature(g, d);
        g.Current.Purse = new Purse();
        Assert.False(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 1) { Player = g.Current.Index }).Ok);
        Assert.Empty(g.S.Caravans);
        g.Current.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 9999999));
        g.S.Obj(d.Index).Owner = -1;
        Assert.False(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 1) { Player = g.Current.Index }).Ok);
    }

    [Fact]
    public void CaravanWalksDailyAndDeliversIntoTheTown()
    {
        var (g, town, d) = Setup(f);
        var creature = FirstCreature(g, d);
        Assert.True(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 4) { Player = g.Current.Index }).Ok);
        var c = g.S.Caravans[0];
        foreach (var h in g.S.Heroes.Where(h => h.InTown == town.Object)) h.InTown = -1;
        for (int i = 0; i < Army.Slots; i++) town.Garrison[i] = null;
        int days = c.DaysLeft;
        var start = c.Pos;
        NextDay(g);
        if (g.S.Caravans.Contains(c))
        {
            Assert.NotEqual(start, c.Pos);                                      // 00ce4de0 walked
            Assert.True(c.DaysLeft < days || c.DaysLeft == -1);
        }
        for (int i = 0; i < 40 && g.S.Caravans.Contains(c); i++) NextDay(g);
        // Arrival (00ce4de0 → 0045add0 / 0045a2c0): the army joins the town's garrison.
        Assert.DoesNotContain(c, g.S.Caravans);
        Assert.Contains(town.Garrison, s => s is { Count: 4 } && s.Creature == creature);
    }

    [Fact]
    public void EnemyHeroAttacksTheCaravanArmy()
    {
        var (g, town, d) = Setup(f);
        var creature = FirstCreature(g, d);
        Assert.True(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 3) { Player = g.Current.Index }).Ok);
        var c = g.S.Caravans[0];
        var enemy = g.S.Heroes.First(h => h.Alive && h.Owner != c.Owner && !g.S.Players.First(p => p.Index == h.Owner).Team.Equals(g.S.Players.First(p => p.Index == c.Owner).Team));
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == enemy.Owner);
        var nb = new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) }.Select(o => c.Pos.Offset(o.Item1, o.Item2))
            .First(t => g.Map.Floor(t.Floor) is { } fl && fl.InBounds(t.X, t.Y) && !fl.IsBlocked(t.X, t.Y) && !fl.IsWater(t.X, t.Y) && !g.ObjectsAtTile(t).Any() && g.HeroAtTile(t) == null);
        enemy.Pos = nb; enemy.InTown = -1; enemy.InBoat = false;
        enemy.MovePoints = enemy.MaxMovePoints = 2000;
        var r = g.Apply(new MoveHeroCmd(enemy.Id, c.Pos) { Player = enemy.Owner });
        Assert.True(r.Ok, r.Error);
        // 00433440 → CCombatHeroVsCaravanArenaAndOpponentsLoader (00436570): the caravan's army defends.
        Assert.Contains(r.Events, e => e is BattleRequested br && br.DefenderArmy.Any(s => s.Creature == creature && s.Count == 3));
        Assert.Equal(c.Id, g.S.Battle!.DefenderCaravan);
        r = g.Apply(new ResolveBattleCmd(0, enemy.Army.ToSlots(), new List<ArmySlot>()) { Player = enemy.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.DoesNotContain(c, g.S.Caravans);
    }

    [Fact]
    public void OwnerMeetsAndExchangesWithItsCaravanAndDismissIsTheOwners()
    {
        var (g, town, d) = Setup(f);
        var creature = FirstCreature(g, d);
        Assert.True(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, creature, 2) { Player = g.Current.Index }).Ok);
        var c = g.S.Caravans[0];
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == c.Owner);
        var nb = new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) }.Select(o => c.Pos.Offset(o.Item1, o.Item2))
            .First(t => g.Map.Floor(t.Floor) is { } fl && fl.InBounds(t.X, t.Y) && !fl.IsBlocked(t.X, t.Y) && !fl.IsWater(t.X, t.Y) && !g.ObjectsAtTile(t).Any() && g.HeroAtTile(t) == null);
        h.Pos = nb; h.InTown = -1; h.InBoat = false; h.MovePoints = 2000;
        var r = g.Apply(new MoveHeroCmd(h.Id, c.Pos) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events, e => e is ObjectVisited { What: var w } && w == $"caravan:{c.Id}");   // 0042f870
        int slot = Array.FindIndex(c.Army, s => s is { Count: > 0 });
        int free = Array.FindIndex(h.Army, s => s is not { Count: > 0 });
        Assert.True(free >= 0);
        r = g.Apply(new TransferStackCmd(-1, -1, slot, h.Id, -1, free, 1) { Player = h.Owner, FromCaravan = c.Id });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(c.Army, s => s is { Count: 1 });
        // 004291d0: dismissing removes it; another player may not.
        int other = g.S.Players.First(p => p.Index != c.Owner).Index;
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == other);
        Assert.False(g.Apply(new DismissCaravanCmd(c.Id) { Player = other }).Ok);
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == c.Owner);
        Assert.True(g.Apply(new DismissCaravanCmd(c.Id) { Player = c.Owner }).Ok);
        Assert.Empty(g.S.Caravans);
    }

    [Fact]
    public void FortOpensForACaravanHeadingToTheTown()
    {
        // 0061a390: the world's caravan list (vf 0x90) holds one of the owner's caravans whose destination is the town.
        var (g, town, d) = Setup(f);
        Assert.True(g.Apply(new BuyToCaravanCmd(d.Index, town.Object, FirstCreature(g, d), 1) { Player = g.Current.Index }).Ok);
        for (int tier = 1; tier <= 7; tier++) town.Built[$"TB_DWELLING_{tier}"] = 0;
        foreach (var (idx, os) in g.S.Objects) Array.Fill(os.Available, 0);
        foreach (var t in g.S.Towns) Array.Fill(t.Available, 0);
        Assert.True(g.CanEnterFort(town));
        g.S.Caravans.Clear();
        Assert.False(g.CanEnterFort(town));
    }

    [Fact]
    public void MoveToCaravanTakesAWholeStackButNotTheLast()
    {
        var (g, town, d) = Setup(f);
        var other = g.S.Towns.FirstOrDefault(t => t != town && t.Owner != town.Owner && g.CaravanStartTiles(t.Object).Count > 0);
        if (other == null) return;
        other.Owner = town.Owner;
        foreach (var h in g.S.Heroes.Where(h => h.InTown == other.Object)) { h.InTown = -1; h.Garrisoned = false; }
        Array.Fill(other.Garrison, null);
        other.Garrison[0] = new Stack("CREATURE_PEASANT", 5);
        Assert.False(g.Apply(new MoveToCaravanCmd(other.Object, town.Object, 0, 5) { Player = g.Current.Index }).Ok);
        other.Garrison[3] = new Stack("CREATURE_ARCHER", 2);
        var r = g.Apply(new MoveToCaravanCmd(other.Object, town.Object, 0, 5) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Null(other.Garrison[0]);
        Assert.Contains(g.S.Caravans.Single().Army, s => s is { Count: 5, Creature: "CREATURE_PEASANT" });
    }
}

/// <summary>Abandoned mine resource (H5_Game.exe 008b59d0 → 008b5840 at capture; income 008b54a0).</summary>
[Collection("adv")]
public class AbandonedMineTests(Fixture f)
{
    [Fact]
    public void CaptureDrawsAResourceFromTheMapWeightsAndItPaysDaily()
    {
        Assert.Equal((2, 1), Engine.Adventure.AbandonedMineYield(2));
        Assert.Equal((6, 1000), Engine.Adventure.AbandonedMineYield(6));
        Assert.Equal((-1, 0), Engine.Adventure.AbandonedMineYield(0));
        foreach (var id in new[] { "A1M2", "A1M5", "A2M11", "A1L1" })
        {
            Engine.Adventure g;
            try { g = f.NewGame(id); } catch { continue; }
            var mine = g.Map.Objects.FirstOrDefault(o => o.Kind == ObjectKind.AbandonedMine && o.AbanMineWeights is { Length: >= 7 } w && w.Take(7).Sum() > 0);
            if (mine == null) continue;
            var os = g.S.Obj(mine.Index);
            os.Guards.Clear();
            var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
            h.Pos = mine.ActiveTiles.First();
            Assert.True(g.Apply(new MoveHeroCmd(h.Id, h.Pos) { Player = h.Owner }).Ok);
            Assert.Equal(h.Owner, os.Owner);
            Assert.InRange(os.ResourceKind, 0, 6);
            Assert.True(mine.AbanMineWeights![os.ResourceKind] > 0);    // only a weighted resource is drawn
            return;
        }
        Assert.Fail("no abandoned mine with resource weights on the test maps");
    }
}
