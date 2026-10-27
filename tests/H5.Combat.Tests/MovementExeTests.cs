namespace H5.Combat.Tests;

/// <summary>
/// The combat movement search of H5_Game.exe (FUN_00c536f0 → FUN_00c52cc0 / FUN_00b5f0d0 / FUN_00b5ed00):
/// 8-connected, steps 2 orthogonal / 3 diagonal against 2 × speed, only the entered cell tested.
/// </summary>
[Collection("game")]
public class MovementExeTests(GameDataFixture f)
{
    [StockFact]
    public void StepCostsAreTwoAndThree()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_FOOTMAN", 10, posA: new Cell(2, 2), posB: new Cell(11, 9));
        var reach = b.Reachable(b.State.Units[0]);
        Assert.Equal(2, reach[new Cell(3, 2)]);
        Assert.Equal(3, reach[new Cell(3, 3)]);
        Assert.Equal(8, reach[new Cell(4, 5)]);   // 2 diagonal + 1 orthogonal
        // The exe's self test FUN_00caa300 expects 26 diagonals + 2 orthogonals to cost 82.
        Assert.Equal(82, Battle.MoveDistance(new Cell(0, 0), new Cell(28, 26)));
    }

    // The combat AI's whole-path planner (approach moves) uses the same search without a budget, so its path costs
    // and the move reach are on one scale (was 100 / 141 octile with a corner rule).
    [StockFact]
    public void AiWholePathMatchesTheMoveSearch()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_FOOTMAN", 10, posA: new Cell(2, 2), posB: new Cell(11, 9));
        b.State.Obstacles.Add(new Cell(3, 2));
        b.State.Obstacles.Add(new Cell(2, 3));
        var u = b.State.Units[0];
        var (dist, _) = H5.Combat.AI.OriginalBattleAI.WholePath(b, u);
        foreach (var (cell, cost) in b.Reachable(u)) Assert.Equal(cost, dist[cell]);
        Assert.Equal(3, dist[new Cell(3, 3)]);
        Assert.True(dist.Values.Max() > b.SpeedOf(u) * Battle.MoveBudgetPerSpeed);   // beyond this turn's reach
    }

    [StockFact]
    public void DiagonalStepMayPassBetweenTwoBlockedCells()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_FOOTMAN", 10, posA: new Cell(2, 2), posB: new Cell(11, 9));
        b.State.Obstacles.Add(new Cell(3, 2));
        b.State.Obstacles.Add(new Cell(2, 3));
        var u = b.State.Units[0];
        var reach = b.Reachable(u);
        Assert.Equal(3, reach[new Cell(3, 3)]);   // no corner rule: only the entered cell is tested
    }

    [StockFact]
    public void FlyersUseTheSameMetricOnAnEmptyBoard()
    {
        var b = f.Duel("CREATURE_GRIFFIN", 5, "CREATURE_PEASANT", 10, posA: new Cell(1, 1), posB: new Cell(11, 9));
        var g = b.State.Units[0];
        int budget = b.SpeedOf(g) * Battle.MoveBudgetPerSpeed;
        foreach (var (cell, cost) in b.Reachable(g))
        {
            Assert.Equal(Battle.MoveDistance(g.Pos, cell), cost);
            Assert.True(cost <= budget);
        }
    }
}
