namespace H5.Combat.Tests;

/// <summary>Original obstacle generator (exe 004b3860 family), see docs/rules/EXE_FINDINGS.md.</summary>
[Collection("game")]
public class ObstacleTests(GameDataFixture f)
{
    [Fact]
    public void ArenasAndGroupsLoad()
    {
        Assert.True(f.Db.Arenas.Count >= 60);
        Assert.Contains(f.Db.Arenas.Values, a => a.Obstacles is { FixedObstacles: false, Obstacles.Count: > 5 });
        var boat = f.Db.Arenas["Boat_Arena"];
        Assert.True(boat.Obstacles!.FixedObstacles);
    }

    [Fact]
    public void RandomObstaclesStayInTheMiddleZone()
    {
        int nonEmpty = 0;
        foreach (var arena in f.Db.Arenas.Values.Where(a => a.Obstacles is { FixedObstacles: false }))
            for (ulong seed = 1; seed <= 25; seed++)
            {
                var rng = new Rng(seed);
                var placed = ObstacleGenerator.Generate(f.Db, rng, arena.Obstacles, 12, 10);
                if (placed.Count > 0) nonEmpty++;
                var cells = placed.SelectMany(p => p.Cells).ToList();
                Assert.Equal(cells.Count, cells.Distinct().Count());                      // no overlaps
                Assert.All(cells, c => Assert.InRange(c.X, 2, 9));                        // internal x 4..W-5
                Assert.All(cells, c => Assert.InRange(c.Y, 0, 9));
                int area = placed.Sum(p => p.Cells.Count);
                Assert.True(area <= 30, $"{arena.Name} seed {seed}: area {area}");      // (W-4)(H-2)/4
                // At least FreeLines (2) consecutive rows are completely free.
                var rows = Enumerable.Range(0, 10).Select(y => cells.All(c => c.Y != y)).ToArray();
                Assert.True(Enumerable.Range(0, 9).Any(y => rows[y] && rows[y + 1]), $"{arena.Name} seed {seed}");
            }
        Assert.True(nonEmpty > 0);
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        var group = f.Db.Arenas["Grass_Big_01"].Obstacles;
        var a = ObstacleGenerator.Generate(f.Db, new Rng(42), group, 12, 10);
        var b = ObstacleGenerator.Generate(f.Db, new Rng(42), group, 12, 10);
        Assert.Equal(a.SelectMany(p => p.Cells), b.SelectMany(p => p.Cells));
        Assert.Equal(a.Select(p => (p.Obstacle, p.Rotation)), b.Select(p => (p.Obstacle, p.Rotation)));
    }

    [Fact]
    public void FixedBoatLayoutUsesInternalCoordinates()
    {
        var placed = ObstacleGenerator.Generate(f.Db, new Rng(1), f.Db.Arenas["Boat_Arena"].Obstacles, 12, 10);
        var cells = placed.SelectMany(p => p.Cells).ToHashSet();
        // BoatArenaBlocked: internal x 6..9 → playable 4..7, internal y 1 → playable 0.
        Assert.Contains(new Cell(4, 0), cells);
        Assert.Contains(new Cell(7, 0), cells);
        Assert.DoesNotContain(new Cell(0, 0), cells);
    }

    [Fact]
    public void BattleSetupRecordsArenaAndPlacements()
    {
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 7, arena: "Grass_Big_01");
        Assert.Equal("Grass_Big_01", s.Arena);
        Assert.Equal(s.ObstaclePlacements.SelectMany(p => p.Cells).Distinct().OrderBy(c => (c.X, c.Y)),
                     s.Obstacles.OrderBy(c => (c.X, c.Y)));
        Assert.DoesNotContain(s.Units, u => !u.IsWarMachine && s.Obstacles.Any(c => u.Occupies(c)));
        var auto = BattleSetup.CreateDuel(f.Db, 0, 3, 7);
        Assert.NotNull(auto.Arena);
        Assert.True(f.Db.Arenas.ContainsKey(auto.Arena!));
    }
}
