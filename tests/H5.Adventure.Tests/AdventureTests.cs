using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Core.IO;

namespace H5.Adventure.Tests;

/// <summary>Loads the game data and a reference map once for all tests.</summary>
public sealed class Fixture
{
    public readonly GameInstall Install = GameInstall.Locate();
    public readonly PakFileSystem Fs;
    public readonly GameDatabase Core;
    public readonly AdventureDb Db;
    public readonly MapLoader Loader;
    readonly Dictionary<string, AdvMap> _maps = new();

    public Fixture()
    {
        Fs = PakFileSystem.OpenInstall(Install);
        Core = GameDatabase.Load(Fs);
        Db = AdventureDb.Load(Core, Fs);
        Loader = new MapLoader(Core, Fs);
    }

    public AdvMap Map(string id)
    {
        lock (_maps)
        {
            if (!_maps.TryGetValue(id, out var m)) _maps[id] = m = Loader.Load(MapCatalog.Find(Fs, Install, id)!);
            return m;
        }
    }

    public Engine.Adventure NewGame(string mapId = "A2M1", ulong seed = 7, int humans = 0) =>
        Engine.Adventure.NewGame(Db, Map(mapId), GameSetup.AllPlayers(Map(mapId), humans, seed)).Game;
}

[CollectionDefinition("adv")]
public sealed class AdvCollection : ICollectionFixture<Fixture> { }

[Collection("adv")]
public class MapLoadingTests(Fixture f)
{
    [Fact]
    public void EveryStockAndUserMapLoads()
    {
        var maps = MapCatalog.All(f.Fs, f.Install);
        Assert.True(maps.Count >= 40, $"only {maps.Count} maps found");
        foreach (var r in maps)
        {
            var m = f.Loader.Load(r);
            Assert.True(m.TilesX > 0 && m.TilesY > 0, r.Id);
            Assert.NotNull(m.Floors[0] ?? m.Floors[1]);
            Assert.DoesNotContain(m.Objects, o => o.Kind == ObjectKind.Unknown);
            Assert.DoesNotContain(m.Objects, o => o.Shared.Tag == "missing");
        }
    }

    [Fact]
    public void A2M1HasItsSixPlayersAndTowns()
    {
        var m = f.Map("A2M1");
        Assert.Equal(136, m.TilesX);
        Assert.True(m.HasUnderground);
        Assert.Equal(6, m.Players.Count(p => p.Active));
        foreach (var p in m.Players.Where(p => p.Active))
            Assert.Contains(m.Objects, o => o.Uid == p.MainTownUid && o.Kind == ObjectKind.Town);
        Assert.Contains(m.Objectives, o => o.Kind == "OBJECTIVE_KIND_DEFEAT_ALL");
    }

    /// <summary>
    /// Evidence for the CCW footprint convention: with it, entrances of rotated objects are almost never
    /// covered by other objects' blocked tiles or water (h5tool adv-footprint-check: 3.4% vs ≥13%).
    /// </summary>
    [Fact]
    public void RotatedEntrancesAreReachable()
    {
        var m = f.Map("A2M1");
        var f0 = m.Floor(0)!;
        int checkedN = 0, blocked = 0;
        foreach (var o in m.Objects.Where(o => o.Kind != ObjectKind.Static && Footprint.QuarterTurns(o.Rot) != 0 && o.Pos.Floor == 0))
            foreach (var t in o.ActiveTiles)
            {
                if (!f0.InBounds(t.X, t.Y)) continue;
                checkedN++;
                if (f0.IsBlocked(t.X, t.Y)) blocked++;
            }
        Assert.True(checkedN > 50);
        Assert.True(blocked * 10 < checkedN, $"{blocked}/{checkedN} entrances blocked");
    }
}

[Collection("adv")]
public class MovementTests(Fixture f)
{
    HeroState Hero(string town = "TOWN_HEAVEN") => new() { Town = town, Pos = new Tile(10, 10) };

    /// <summary>Fan manual p.300: starting movement points with Logistics and Boots of the Swift Journey.</summary>
    [Theory]
    [InlineData(0, false, 2500)]
    [InlineData(1, false, 2749)]
    [InlineData(2, false, 2999)]
    [InlineData(3, false, 3249)]
    [InlineData(0, true, 3125)]
    [InlineData(1, true, 3436)]
    [InlineData(2, true, 3748)]
    [InlineData(3, true, 4061)]
    public void MovementPointsMatchManualTable(int logistics, bool boots, int expected)
    {
        var g = f.NewGame();
        var h = Hero();
        if (logistics > 0) h.Skills.Add(new SkillEntry("HERO_SKILL_LOGISTICS", (Mastery)logistics));
        if (boots) h.Artifacts.Add("BOOTS_OF_SPEED");
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        Assert.Equal(expected, g.ComputeMaxMovePoints(h));
    }

    /// <summary>Fan manual p.299-300 step costs (orthogonal / diagonal) per terrain.</summary>
    [Fact]
    public void StepCostsMatchManual()
    {
        var g = f.NewGame();
        var map = g.Map;
        var floor = map.Floor(0)!;
        Tile Find(TerrainClass c) =>
            Enumerable.Range(1, floor.Height - 2).SelectMany(y => Enumerable.Range(1, floor.Width - 2).Select(x => new Tile(x, y)))
                .First(t => floor.At(t.X, t.Y) == c);
        void Check(TerrainClass c, string town, int ortho, int diag, bool pathfinding = false)
        {
            var to = Find(c);
            var h = Hero(town);
            if (pathfinding) h.Perks.Add("HERO_SKILL_PATHFINDING");
            Assert.Equal(ortho, g.StepCost(h, to.Offset(-1, 0), to));
            Assert.Equal(diag, g.StepCost(h, to.Offset(-1, -1), to));
        }
        Check(TerrainClass.Grass, "TOWN_INFERNO", 100, 141);
        Check(TerrainClass.Dirt, "TOWN_HEAVEN", 125, 176);
        Check(TerrainClass.Dirt, "TOWN_NECROMANCY", 100, 141);          // native terrain
        Check(TerrainClass.Dirt, "TOWN_HEAVEN", 112, 158, pathfinding: true);
        Check(TerrainClass.Lava, "TOWN_HEAVEN", 125, 176);
        Check(TerrainClass.Road, "TOWN_HEAVEN", 75, 105);
    }

    [Fact]
    public void PathsAvoidBlockedTilesAndCostWhatTheyClaim()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Owner == g.Current.Index);
        var target = g.Map.Objects.Where(o => o.Kind == ObjectKind.Treasure && o.Pos.Floor == h.Pos.Floor)
            .OrderBy(o => Math.Abs(o.Pos.X - h.Pos.X) + Math.Abs(o.Pos.Y - h.Pos.Y)).First().Pos;
        var path = g.FindPath(h, target);
        Assert.NotNull(path);
        var floor = g.Map.Floor(h.Pos.Floor)!;
        for (int i = 1; i < path!.Count; i++)
        {
            Assert.True(Math.Max(Math.Abs(path[i].X - path[i - 1].X), Math.Abs(path[i].Y - path[i - 1].Y)) == 1);
            Assert.False(floor.IsBlocked(path[i].X, path[i].Y));
        }
    }
}

[Collection("adv")]
public class RulesTests(Fixture f)
{
    /// <summary>Fan manual p.320 worked example without the artifact factor: 100 Peasants, +5 morale, +3 luck.</summary>
    [Fact]
    public void ArmyPowerFollowsManualFormula()
    {
        var g = f.NewGame();
        var h = new HeroState { NextBattleMorale = 5, NextBattleLuck = 3 };
        var power = g.ArmyPower(new[] { new Stack("CREATURE_PEASANT", 100) }, h);
        int basePower = f.Core.Creatures["CREATURE_PEASANT"].Power;
        Assert.Equal(basePower * 100 * (1 + 5 * 0.0173) * (1 + 3 * 0.0173), power, 3);
        Assert.Equal(5060, Math.Floor(power * (1 + 0.8 / 10)));   // manual figure (truncated) with the +10% initiative artifact
    }

    [Fact]
    public void ExperienceLevelsAndOffersFollowTheRules()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Owner == g.Current.Index);
        int before = h.Attack + h.Defence + h.SpellPower + h.Knowledge;
        g.GainExperience(h, 1000);
        Assert.Equal(2, h.Level);
        Assert.Equal(before + 1, h.Attack + h.Defence + h.SpellPower + h.Knowledge);
        var offer = Assert.Single(h.PendingLevelUps);
        Assert.InRange(offer.Skills.Count, 1, 2);
        Assert.True(offer.Perks.Count <= 2);
        foreach (var p in offer.Perks)
        {
            var def = f.Db.Perks[p];
            Assert.Contains(h.Skills, s => s.SkillId == def.BasicSkill);
        }
        var r = g.Apply(new ChooseLevelUpCmd(h.Id, 0) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Empty(h.PendingLevelUps);
    }

    [Fact]
    public void TownBuildingRespectsCostsAndOncePerDay()
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        var p = g.Current;
        p.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 999999));
        string? built = null;
        foreach (var b in t.MaxLevel.Keys.OrderBy(k => k))
            if (g.Apply(new BuildCmd(t.Object, b) { Player = p.Index }).Ok) { built = b; break; }
        Assert.NotNull(built);
        var def = f.Db.Building(t.Type, built!, t.Level(built!))!;
        Assert.Equal(999999 - def.Cost.Gold, p.Purse[Purse.Gold]);
        Assert.Contains("already built today", t.MaxLevel.Keys.Select(b => g.Apply(new BuildCmd(t.Object, b) { Player = p.Index }).Error ?? ""));
    }

    [Fact]
    public void DailyIncomeComesFromTownHall()
    {
        var g = f.NewGame();
        var p = g.Current;
        int before = p.Purse[Purse.Gold];
        int hall = g.S.Towns.Where(t => t.Owner == p.Index).Sum(t => t.Level("TB_TOWN_HALL") switch { 1 => 500, 2 => 1000, 3 => 2000, >= 4 => 4000, _ => 0 });
        // End every player's turn once to reach day 2.
        foreach (var _ in g.S.Players) g.Apply(new EndTurnCmd { Player = g.Current.Index });
        Assert.Equal(2, g.S.Day);
        Assert.True(p.Purse[Purse.Gold] >= before + hall, $"{p.Purse[Purse.Gold]} < {before} + {hall}");
    }
}

[Collection("adv")]
public class DeterminismTests(Fixture f)
{
    static string Play(Engine.Adventure g, int turns, ulong seed)
    {
        var bot = new SimpleAdventureBot();
        for (int i = 0; i < turns && !g.S.Finished; i++) bot.PlayTurn(g, seed + (ulong)i);
        return g.S.ComputeHash();
    }

    [Fact]
    public void SameSeedSameGame()
    {
        var a = Play(f.NewGame("A2M1", 11), 30, 5);
        var b = Play(f.NewGame("A2M1", 11), 30, 5);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SerialisedStateContinuesIdentically()
    {
        var g = f.NewGame("A2M1", 3);
        Play(g, 12, 1);
        var copy = new Engine.Adventure(f.Db, g.Map, AdvState.Deserialize(g.S.Serialize()));
        Assert.Equal(g.S.ComputeHash(), copy.S.ComputeHash());
        Assert.Equal(Play(g, 12, 99), Play(copy, 12, 99));
    }

    [Fact]
    public void RejectedCommandsDoNotChangeState()
    {
        var g = f.NewGame();
        var before = g.S.ComputeHash();
        var other = g.S.Players.First(p => p.Index != g.Current.Index);
        Assert.False(g.Apply(new EndTurnCmd { Player = other.Index }).Ok);
        Assert.False(g.Apply(new BuildCmd(-5, "TB_FORT") { Player = g.Current.Index }).Ok);
        Assert.Equal(before, g.S.ComputeHash());
    }
}
