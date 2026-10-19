using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Deployment axis and duel arenas (docs/rules/EXE_FINDINGS.md "Deployment axis"). The armies stand at
/// the two ends of the 12-cell axis: the obstacle generator (exe 004b3860 family) keeps every random
/// obstacle in internal x 4..W−5 (field columns 2..9), leaving the two outer columns at each end free
/// for deployment; the stock manual says "the attacking army is displayed on the left".
/// </summary>
[Collection("game")]
public class DeploymentAxisTests(GameDataFixture f)
{
    [StockFact]
    public void DeployZonesAreTheOuterColumnsOfTheTwelveCellAxis()
    {
        var b = new Battle(f.Db, BattleSetup.CreateDuel(f.Db, 0, 3, 5));
        Assert.Equal(12, b.State.Width);
        Assert.Equal(10, b.State.Height);
        // Two columns, three for a hero with Tactics when the enemy lacks it (preset 0 has Tactics).
        Assert.Equal((0, b.State.DeployColumns0 - 1), b.DeployZone(0));
        Assert.Equal((12 - b.State.DeployColumns1, 11), b.DeployZone(1));
        Assert.InRange(b.State.DeployColumns0, 2, 3);
        Assert.InRange(b.State.DeployColumns1, 2, 3);
        foreach (var u in b.State.Units.Where(u => u.Kind == UnitKind.Creature))
        {
            var (min, max) = b.DeployZone(u.Side);
            Assert.All(u.Cells(), c => Assert.InRange(c.X, min, max));
        }
    }

    [Fact]
    public void RandomObstaclesNeverTouchTheDeployColumns()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var s = BattleSetup.CreateDuel(f.Db, (int)(seed % 24), (int)((seed * 7) % 24), seed);
            Assert.All(s.Obstacles, c => Assert.InRange(c.X, 2, 9));
        }
    }

    [StockFact]
    public void ArenaTableGrassAverageIsTheDuelList()
    {
        var grass = f.Db.ArenaTable.ForBattle(CombatArenaTable.TerrainGrass);
        Assert.Equal(new[] { "Grass_Big_01", "Grass_Big_02", "Grass_Small_03" }, grass);
        // Grass defines no small/large "new" lists, so those fall back to average.
        Assert.Equal(grass, f.Db.ArenaTable.ForBattle(CombatArenaTable.TerrainGrass, CombatArenaTable.Size.Large));
        Assert.True(f.Db.ArenaTable.TerrainCount >= 8);
    }

    [Fact]
    public void DuelsAreFoughtOnGrassArenas()
    {
        var grass = f.Db.ArenaTable.ForBattle(CombatArenaTable.TerrainGrass).ToHashSet();
        var seen = new HashSet<string>();
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var s = BattleSetup.CreateDuel(f.Db, 0, 1, seed);
            Assert.NotNull(s.Arena);
            Assert.Contains(s.Arena!, grass);
            seen.Add(s.Arena!);
        }
        Assert.Equal(grass.Count, seen.Count);
        // Deterministic: same seed, same arena and obstacles.
        var a = BattleSetup.CreateDuel(f.Db, 2, 4, 99);
        var c = BattleSetup.CreateDuel(f.Db, 2, 4, 99);
        Assert.Equal(a.Arena, c.Arena);
        Assert.Equal(a.ComputeHash(), c.ComputeHash());
    }

    [Fact]
    public void HeroesSharingAnInternalNameStayDistinct()
    {
        // Orlando exists as a Haven and an Inferno shared hero with the same InternalName.
        var orlandos = f.Db.HeroesByName["Orlando"];
        Assert.True(orlandos.Count >= 2);
        Assert.Equal(orlandos.Count, orlandos.Select(h => h.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(orlandos, h => Assert.Same(h, f.Db.HeroByPath(h.SourcePath)));
        // The stock entry (file name = InternalName) keeps the by-name slot.
        Assert.EndsWith("Inferno/Orlando.(AdvMapHeroShared).xdb", f.Db.Heroes["Orlando"].SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Academy/Tan.(AdvMapHeroShared).xdb", f.Db.Heroes["Tan"].SourcePath, StringComparison.OrdinalIgnoreCase);
    }
}
