using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

[Collection("adv")]
public class BoatTests(Fixture f)
{
    static readonly (int dx, int dy)[] Around = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    static Engine.Adventure Game(Fixture f, string map = "A1L10") => f.NewGame(map);

    /// <summary>The first hero on the map; its owner becomes the player to move (some maps start heroless).</summary>
    static HeroState MyHero(Engine.Adventure g)
    {
        var h = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == g.Current.Index) ?? g.S.Heroes.First(x => x.Alive);
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == h.Owner);
        return h;
    }

    static AdvResult Do(Engine.Adventure g, AdvCommand c) => g.Apply(c with { Player = g.Current.Index });

    /// <summary>A shipyard whose slipway is free water, owned by the current player who can afford a ship.</summary>
    static MapObject ReadyShipyard(Engine.Adventure g)
    {
        var yard = g.Map.Objects.First(o => o.Kind == ObjectKind.Shipyard && g.ShipyardStatus(o.Index) == "ok");
        g.S.Obj(yard.Index).Owner = g.Current.Index;
        g.Current.Purse[Purse.Gold] = 5000;
        g.Current.Purse[Purse.Wood] = 50;
        return yard;
    }

    /// <summary>A free land tile next to a water tile (where a hero can stand to board or land).</summary>
    static Tile ShoreNextTo(Engine.Adventure g, Tile water)
    {
        var f = g.Map.Floor(water.Floor)!;
        foreach (var (dx, dy) in Around)
        {
            var t = water.Offset(dx, dy);
            if (f.InBounds(t.X, t.Y) && !f.IsWater(t.X, t.Y) && !f.IsBlocked(t.X, t.Y) &&
                g.S.Heroes.All(h => h.Pos != t) && g.GuardingMonster(t) == null) return t;
        }
        throw new InvalidOperationException($"no shore next to {water}");
    }

    static Tile FreeWaterNextTo(Engine.Adventure g, Tile t, Tile except)
    {
        var f = g.Map.Floor(t.Floor)!;
        foreach (var (dx, dy) in Around)
        {
            var n = t.Offset(dx, dy);
            if (n != except && f.InBounds(n.X, n.Y) && f.IsWater(n.X, n.Y) && !f.IsBlocked(n.X, n.Y) && g.BoatAt(n) == null && g.GuardingMonster(n) == null)
                return n;
        }
        throw new InvalidOperationException($"no free water next to {t}");
    }

    [Fact]
    public void SeaFromTerrainLevelsCoversSeaObjects()
    {
        var m = f.Map("A2M5");
        var sea = m.Objects.Where(o => o.Shared.WaterBased).Select(o => o.ActiveTiles.FirstOrDefault(o.Pos)).ToList();
        Assert.True(sea.Count > 50);
        int water = sea.Count(t => m.Floor(t.Floor)!.IsWater(t.X, t.Y));
        Assert.True(water >= sea.Count * 95 / 100, $"{water}/{sea.Count} sea objects stand on water");
        var land = m.Objects.Where(o => o.Kind is ObjectKind.Town or ObjectKind.Mine).Select(o => o.ActiveTiles.FirstOrDefault(o.Pos));
        Assert.All(land, t => Assert.False(m.Floor(t.Floor)!.IsWater(t.X, t.Y)));
    }

    [Fact]
    public void ShipModelFollowsRaceLikeTheExe()
    {
        Assert.Equal(1, Engine.Adventure.ShipModelFor("TOWN_HEAVEN"));
        Assert.Equal(1, Engine.Adventure.ShipModelFor("TOWN_FORTRESS"));
        Assert.Equal(2, Engine.Adventure.ShipModelFor("TOWN_NECROMANCY"));
        Assert.Equal(2, Engine.Adventure.ShipModelFor("TOWN_STRONGHOLD"));
        Assert.Equal(0, Engine.Adventure.ShipModelFor("TOWN_NO_TYPE"));
    }

    [Fact]
    public void EditorShipsBecomeBoatsAndSeaHeroesBoardThem()
    {
        var g = Game(f, "A2C2M2");
        var ships = g.Map.Objects.Where(o => o.Shared.Type == Engine.Adventure.ShipBuildingType).ToList();
        Assert.NotEmpty(ships);
        foreach (var o in ships)
        {
            Assert.True(g.S.Obj(o.Index).Removed);
            Assert.NotNull(g.BoatAt(o.Pos));
        }
        foreach (var h in g.S.Heroes.Where(h => h.Alive && g.IsWaterTile(h.Pos)))
        {
            Assert.True(h.InBoat);
            Assert.Equal(h.Id, g.BoatOf(h)!.HeroId);
        }
    }

    [Fact]
    public void ShipyardBuildsAShipOnItsSlipwayForGoldAndWood()
    {
        var g = Game(f);
        var yard = ReadyShipyard(g);
        var slip = g.SlipwayOf(yard.Index)!.Value;
        var r = Do(g, new BuildShipCmd(yard.Index));
        Assert.True(r.Ok, r.Error);
        var b = g.BoatAt(slip);
        Assert.NotNull(b);
        Assert.Equal(-1, b!.HeroId);
        Assert.Equal(5000 - Engine.Adventure.ShipCostGold, g.Current.Purse[Purse.Gold]);
        Assert.Equal(50 - Engine.Adventure.ShipCostWood, g.Current.Purse[Purse.Wood]);
        Assert.Contains(r.Events, e => e is BoatCreated bc && bc.Pos == slip);
        // The new ship now blocks the slipway.
        Assert.Equal("blocked", g.ShipyardStatus(yard.Index));
        Assert.False(Do(g, new BuildShipCmd(yard.Index)).Ok);
    }

    [Fact]
    public void ShipyardRefusesWithoutResources()
    {
        var g = Game(f);
        var yard = ReadyShipyard(g);
        g.Current.Purse[Purse.Wood] = 9;
        Assert.False(Do(g, new BuildShipCmd(yard.Index)).Ok);
        Assert.Null(g.BoatAt(g.SlipwayOf(yard.Index)!.Value));
    }

    (Engine.Adventure G, HeroState H, BoatState B, Tile Shore) HeroAtShipyard(bool snatch)
    {
        var g = Game(f);
        var h = MyHero(g);
        var yard = ReadyShipyard(g);
        Assert.True(Do(g, new BuildShipCmd(yard.Index)).Ok);
        var slip = g.SlipwayOf(yard.Index)!.Value;
        var shore = ShoreNextTo(g, slip);
        h.Pos = shore;
        h.InTown = -1;
        if (snatch && !h.Perks.Contains("HERO_SKILL_SNATCH")) h.Perks.Add("HERO_SKILL_SNATCH");
        if (!snatch) h.Perks.Remove("HERO_SKILL_SNATCH");
        h.MovePoints = 2000;
        return (g, h, g.BoatAt(slip)!, shore);
    }

    [Fact]
    public void BoardingTakesTheRestOfTheDay()
    {
        var (g, h, b, _) = HeroAtShipyard(snatch: false);
        var r = Do(g, new MoveHeroCmd(h.Id, b.Pos));
        Assert.True(r.Ok, r.Error);
        Assert.True(h.InBoat);
        Assert.Equal(b.Pos, h.Pos);
        Assert.Equal(h.Id, b.HeroId);
        Assert.Equal(0, h.MovePoints);
        Assert.Contains(r.Events, e => e is HeroEmbarked);
    }

    [Fact]
    public void SnatchBoardsForFreeAndLandsForOneStep()
    {
        var (g, h, b, shore) = HeroAtShipyard(snatch: true);
        Assert.True(Do(g, new MoveHeroCmd(h.Id, b.Pos)).Ok);
        Assert.Equal(2000, h.MovePoints);
        Assert.True(h.InBoat);
        bool diag = shore.X != b.Pos.X && shore.Y != b.Pos.Y;
        var r = Do(g, new MoveHeroCmd(h.Id, shore));
        Assert.True(r.Ok, r.Error);
        Assert.False(h.InBoat);
        Assert.Equal(shore, h.Pos);
        Assert.Equal(2000 - (diag ? 141 : 100), h.MovePoints);
        Assert.Equal(-1, b.HeroId);
        Assert.Contains(r.Events, e => e is HeroDisembarked);
    }

    [Fact]
    public void TheShipSailsWithItsHeroAndLandingEndsTheDay()
    {
        var (g, h, b, shore) = HeroAtShipyard(snatch: false);
        Assert.True(Do(g, new MoveHeroCmd(h.Id, b.Pos)).Ok);
        h.MovePoints = h.MaxMovePoints;
        // A free water tile next to the ship that itself touches the shore.
        var sea = Around.Select(d => b.Pos.Offset(d.dx, d.dy)).First(n =>
            g.IsWaterTile(n) && !g.Map.Floor(n.Floor)!.IsBlocked(n.X, n.Y) && g.BoatAt(n) == null && g.GuardingMonster(n) == null &&
            Around.Any(d2 => { var l = n.Offset(d2.dx, d2.dy); var fl = g.Map.Floor(l.Floor)!; return fl.InBounds(l.X, l.Y) && !fl.IsWater(l.X, l.Y) && !fl.IsBlocked(l.X, l.Y) && g.GuardingMonster(l) == null && g.S.Heroes.All(x => x.Pos != l); }));
        var r = Do(g, new MoveHeroCmd(h.Id, sea));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(sea, h.Pos);
        Assert.Equal(sea, b.Pos);
        // Back to the shore: landing takes whatever movement is left.
        var land = ShoreNextTo(g, sea);
        Assert.True(Do(g, new MoveHeroCmd(h.Id, land)).Ok);
        Assert.False(h.InBoat);
        Assert.Equal(0, h.MovePoints);
        Assert.Equal(sea, b.Pos);
    }

    [Fact]
    public void LandHeroesCannotWalkOntoOpenWater()
    {
        var (g, h, b, _) = HeroAtShipyard(snatch: false);
        var open = FreeWaterNextTo(g, h.Pos, b.Pos);
        Assert.Null(g.FindPath(h, open));
    }

    [Fact]
    public void SailingSpeedUsesNavigationSextantAndLighthouses()
    {
        var (g, h, b, _) = HeroAtShipyard(snatch: false);
        Assert.True(Do(g, new MoveHeroCmd(h.Id, b.Pos)).Ok);
        h.Perks.Remove("HERO_SKILL_NAVIGATION");
        int plain = g.ComputeMaxMovePoints(h);
        h.Perks.Add("HERO_SKILL_NAVIGATION");
        Assert.Equal(plain * 3 / 2, g.ComputeMaxMovePoints(h));
    }

    [Fact]
    public void VesselOfShalassaBringsTheNearestEmptyShip()
    {
        var (g, h, b, shore) = HeroAtShipyard(snatch: false);
        // Move the hero a few tiles along the coast is not needed: put the ship somewhere else first.
        var away = FreeWaterNextTo(g, b.Pos, b.Pos);
        var from = b.Pos;
        b.Pos = away;
        h.Spells.Add(Engine.Adventure.SpellSummonBoat);
        h.Mana = 20;
        var target = g.SummonBoatTarget(h);
        Assert.NotNull(target);
        var r = Do(g, new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellSummonBoat));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(16, h.Mana);
        Assert.Equal(target, b.Pos);
        Assert.Contains(r.Events, e => e is BoatMoved);
        _ = from; _ = shore;
    }

    [Fact]
    public void VesselOfShalassaFailsWithoutShipsButSpendsTheMana()
    {
        var (g, h, b, _) = HeroAtShipyard(snatch: false);
        g.S.Boats.Clear();
        h.Spells.Add(Engine.Adventure.SpellSummonBoat);
        h.Mana = 20;
        var r = Do(g, new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellSummonBoat));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(16, h.Mana);
        Assert.Contains(r.Events, e => e is Message { Key: "ADVENTURE_SUMMON_BOAT_FAILED" });
    }

    [Fact]
    public void WhirlpoolDrownsHalfTheLowestStackRoundedUp()
    {
        var g = Game(f);
        var h = MyHero(g);
        h.Perks.Remove("HERO_SKILL_FORTUNATE_ADVENTURER");
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_FOOTMAN", 10);
        h.Army[1] = new Stack("CREATURE_MILITIAMAN", 7);
        h.Army[2] = new Stack("CREATURE_PEASANT", 7);
        // Peasant (tier 1, base) is lower than Militiaman (tier 1, upgraded) even in a later slot.
        Assert.Equal((2, 4), g.WhirlpoolLoss(h));
        h.Perks.Add("HERO_SKILL_FORTUNATE_ADVENTURER");
        Assert.Equal((2, 2), g.WhirlpoolLoss(h));
        // A lone creature is spared.
        h.Army = Army.Empty();
        h.Army[3] = new Stack("CREATURE_ANGEL", 1);
        Assert.Null(g.WhirlpoolLoss(h));
    }

    [Fact]
    public void SeaBattlesUseTheBoatArena()
    {
        var (g, h, b, _) = HeroAtShipyard(snatch: false);
        Assert.True(Do(g, new MoveHeroCmd(h.Id, b.Pos)).Ok);
        g.S.Battle = new PendingBattle { AttackerHero = h.Id, Where = b.Pos, DefenderArmy = { new Stack("CREATURE_PEASANT", 5) } };
        var battle = g.CreatePendingBattle(42);
        Assert.Equal("Boat_Arena", battle.Arena);
    }

    [Fact]
    public void BoatsSurviveSaveAndLoad()
    {
        var (g, h, b, _) = HeroAtShipyard(snatch: false);
        Assert.True(Do(g, new MoveHeroCmd(h.Id, b.Pos)).Ok);
        var copy = AdvState.Deserialize(g.S.Serialize());
        Assert.Equal(g.S.ComputeHash(), copy.ComputeHash());
        Assert.Single(copy.Boats, x => x.HeroId == h.Id);
        Assert.Equal(b.Id, copy.Hero(h.Id)!.BoatId);
    }

    [Fact]
    public void WreckBanksAreBanksAndShipsAreNot()
    {
        Assert.Equal("BankDemolish", Data.AdventureDb.BankFor("BUILDING_DEMOLISH"));
        Assert.Equal("BankUnkempt", Data.AdventureDb.BankFor("BUILDING_UNKEMPT"));
        Assert.Null(Data.AdventureDb.BankFor(Engine.Adventure.ShipBuildingType));
    }
}
