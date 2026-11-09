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

    /// <summary>
    /// 00b4dd40: chop((Logistics · 0.1f + 1.0001f) · B), then chop((boots · 0.25f + 1) · L) — 2750 / 3000 / 3250 with
    /// stock B = 2500 (the fan manual lists 2749 / 2999 / 3249), MMH 5.5 B = 2800.
    /// </summary>
    [Theory]
    [InlineData(0, false, 2500, 2800)]
    [InlineData(1, false, 2750, 3080)]
    [InlineData(2, false, 3000, 3360)]
    [InlineData(3, false, 3250, 3640)]
    [InlineData(0, true, 3125, 3500)]
    [InlineData(1, true, 3437, 3850)]
    [InlineData(2, true, 3750, 4200)]
    [InlineData(3, true, 4062, 4550)]
    public void MovementPointsFollowTheExe(int logistics, bool boots, int expected, int expectedMmh55)
    {
        var g = f.NewGame();
        var h = Hero();
        if (logistics > 0) h.Skills.Add(new SkillEntry("HERO_SKILL_LOGISTICS", (Mastery)logistics));
        if (boots) h.Artifacts.Add("BOOTS_OF_SPEED");
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        Assert.Equal(DataFixture.Pick(expected, expectedMmh55), g.ComputeMaxMovePoints(h));
    }

    /// <summary>00b4dd40 / 00bcdc30: the week adds B / 2 before the skills; HERO_SPEC_LOGISTICS (Wanderer) scales B.</summary>
    [Fact]
    public void MovementWeekAndWandererSpecialityApplyToTheBase()
    {
        var g = f.NewGame();
        var h = Hero();
        h.Skills.Add(new SkillEntry("HERO_SKILL_LOGISTICS", Mastery.Basic));
        g.S.MoonWeek = "WEEK_OF_FLAME";
        Assert.Equal(DataFixture.Pick(4125, 4620), g.ComputeMaxMovePoints(h));   // chop(1.1001 · (B + B / 2))
        g.S.MoonWeek = "WEEK_OF_WINDS";                                         // sea only
        Assert.Equal(DataFixture.Pick(2750, 3080), g.ComputeMaxMovePoints(h));
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        h.Skills.Clear();
        h.Specialization = "HERO_SPEC_LOGISTICS";
        h.Level = 5;                                                            // ⌈5 / 2⌉ · 0.01 + 0 = 0.03
        Assert.Equal(DataFixture.Pick(2575, 2884), g.ComputeMaxMovePoints(h));
    }

    /// <summary>
    /// 00b500f0 → 00ca9a70 on the destination tile record of a real map (ground +0, road +4): penalties 25 / 50 / 0,
    /// none on the native ground (00b4c2a0), Pathfinding chop(× 0.5), road 75, diagonal chop(× 1.4142135f).
    /// </summary>
    [Fact]
    public void StepCostsFollowTheExe()
    {
        var g = f.NewGame();
        var floor = g.Map.Floor(0)!;
        Tile Find(Func<int, int, bool> pick) =>
            Enumerable.Range(1, floor.Height - 2).SelectMany(y => Enumerable.Range(1, floor.Width - 2).Select(x => new Tile(x, y)))
                .First(t => pick(floor.Ground[t.Y * floor.Width + t.X], floor.Road[t.Y * floor.Width + t.X]));
        void Check(int ground, string town, int ortho, int diag, bool pathfinding = false)
        {
            var to = ground < 0 ? Find((_, r) => r is >= 6 and <= 8) : Find((gr, r) => gr == ground && r == 9);
            var h = Hero(town);
            if (pathfinding) h.Perks.Add("HERO_SKILL_PATHFINDING");
            Assert.Equal(ortho, g.StepCost(h, to.Offset(-1, 0), to));
            Assert.Equal(diag, g.StepCost(h, to.Offset(-1, -1), to));
        }
        Check(1, "TOWN_INFERNO", 100, 141);                     // grass
        Check(0, "TOWN_HEAVEN", 125, 176);                      // dirt (the manual's 176 / ours was 176)
        Check(0, "TOWN_NECROMANCY", 100, 141);                  // native ground
        Check(0, "TOWN_HEAVEN", 112, 158, pathfinding: true);   // chop(25 · 0.5) = 12
        Check(-1, "TOWN_HEAVEN", 75, 106);                      // road: chop(75 · 1.4142135) = 106 (was 105)
    }

    /// <summary>00ca9a70 directly: ground table, settings order, the diagonal rule on the movement left.</summary>
    [Fact]
    public void ExeStepCostTableAndDiagonalRule()
    {
        var none = new MoveCostSettings(false, false, 9, 0);
        foreach (var (gr, c) in new[] { (0, 125), (1, 100), (2, 150), (3, 150), (4, 125), (5, 125), (9, 100), (10, 100), (11, 100), (12, 100), (13, 125), (14, 125) })
            Assert.Equal(c, Engine.Adventure.ExeStepCost(gr, 9, false, 0, none, 0));
        Assert.Equal(212, Engine.Adventure.ExeStepCost(2, 9, true, int.MaxValue, none, 0));         // sand diagonal
        Assert.Equal(75, Engine.Adventure.ExeStepCost(2, 7, false, 0, none, 0));                    // road wins over the ground
        Assert.Equal(100, Engine.Adventure.ExeStepCost(2, 9, false, 0, none, 1));                   // ship mode
        Assert.Equal(100, Engine.Adventure.ExeStepCost(2, 9, false, 0, none, 2));
        // Diagonal: cost ≤ left < chop(cost · √2) pays the straight cost; less than cost or enough pays the diagonal.
        Assert.Equal(176, Engine.Adventure.ExeStepCost(0, 9, true, 124, none, 0));
        Assert.Equal(125, Engine.Adventure.ExeStepCost(0, 9, true, 125, none, 0));
        Assert.Equal(125, Engine.Adventure.ExeStepCost(0, 9, true, 175, none, 0));
        Assert.Equal(176, Engine.Adventure.ExeStepCost(0, 9, true, 176, none, 0));
        Assert.Equal(100, Engine.Adventure.ExeStepCost(0, 9, true, 120, none, 1));                  // at sea too
        // Boots zero the penalty after Pathfinding; Rash's percent then chops (1 − 0.01f · p) · penalty.
        Assert.Equal(100, Engine.Adventure.ExeStepCost(2, 9, false, 0, new MoveCostSettings(true, true, 9, 0), 0));
        Assert.Equal(122, Engine.Adventure.ExeStepCost(0, 9, false, 0, new MoveCostSettings(false, false, 9, 9), 0));   // chop(0.91 · 25)
        Assert.Equal(118, Engine.Adventure.ExeStepCost(2, 9, false, 0, new MoveCostSettings(true, false, 9, 25), 0));   // chop(0.75 · 25)
    }

    /// <summary>
    /// 00b4ffe0: HERO_SPEC_RASH's percent is chop(level · Rash_MovementPenaltyReducePerLevel · 100) — 0.02f · 5 · 100
    /// chops to 9 (MMH 5.5 0.05: 25) — clamped to 100; the native ground follows the race table 00b4c2a0 (Fortress: snow only).
    /// </summary>
    [Fact]
    public void MoveCostSettingsFollowTheHero()
    {
        var g = f.NewGame();
        var h = Hero("TOWN_STRONGHOLD");
        h.Specialization = "HERO_SPEC_RASH";
        h.Level = 5;
        var s = g.MoveCostSettingsFor(h);
        Assert.Equal(13, s.NativeGround);
        Assert.Equal(DataFixture.Pick(9, 25), s.PenaltyReducePercent);   // MMH 5.5: 0.05
        h.Level = 60;
        Assert.Equal(100, g.MoveCostSettingsFor(h).PenaltyReducePercent);
        h.Artifacts.Add("WAYFARER_BOOTS");
        h.Perks.Add("HERO_SKILL_PATHFINDING");
        s = g.MoveCostSettingsFor(h);
        Assert.True(s.NoPenalty && s.HalfPenalty);
        Assert.Equal(3, Engine.Adventure.NativeGround("TOWN_FORTRESS"));
        Assert.Equal(5, Engine.Adventure.NativeGround("TOWN_DUNGEON"));
        Assert.Equal(4, Engine.Adventure.NativeGround("TOWN_INFERNO"));
        Assert.Equal(9, Engine.Adventure.NativeGround(""));
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
    [StockFact]
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
        Assert.Contains("build_thisturn", t.MaxLevel.Keys.Select(b => g.Apply(new BuildCmd(t.Object, b) { Player = p.Index }).Error ?? ""));
    }

    [Fact]
    public void TownBuildStateDrivesTheListMarker()
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        var p = g.Current;
        p.Purse = new Purse(new Resources(0, 0, 0, 0, 0, 0, 0));
        // An empty purse still allows any free building that is otherwise available.
        // The grail needs a Tear of Asha in the town (exe 00cd0e20), however cheap.
        bool freeAvailable = t.MaxLevel.Any(kv => kv.Key != "TB_GRAIL" && t.Level(kv.Key) < kv.Value
            && f.Db.Building(t.Type, kv.Key, t.Level(kv.Key) + 1) is { } d
            && t.DevLevel >= d.DevLevelNeeded && d.Dependencies.All(x => t.Level(x.Type) >= x.Level)
            && p.Purse.CanAfford(d.Cost));
        Assert.Equal(freeAvailable ? H5.Adventure.Engine.Adventure.TownBuildState.CanBuild : H5.Adventure.Engine.Adventure.TownBuildState.NoResources, g.BuildState(t));
        p.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 999999));
        Assert.Equal(H5.Adventure.Engine.Adventure.TownBuildState.CanBuild, g.BuildState(t));
        foreach (var b in t.MaxLevel.Keys.OrderBy(k => k))
            if (g.Apply(new BuildCmd(t.Object, b) { Player = p.Index }).Ok) break;
        Assert.Equal(H5.Adventure.Engine.Adventure.TownBuildState.BuiltToday, g.BuildState(t));
        t.BuiltToday = false;
        foreach (var (b, max) in t.MaxLevel) t.Built[b] = max;
        Assert.Equal(H5.Adventure.Engine.Adventure.TownBuildState.AllComplete, g.BuildState(t));
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
