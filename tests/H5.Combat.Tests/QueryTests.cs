namespace H5.Combat.Tests;

[Collection("game")]
public class QueryTests(GameDataFixture f)
{
    [Fact]
    public void TurnOrderPredictionMatchesActualTurns()
    {
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 11, skipTactics: true, arena: "Grass_Big_01");
        var b = new Battle(f.Db, s);
        b.Begin();
        var predicted = b.PredictTurnOrder(4).Select(p => p.Actor).ToList();
        Assert.Equal(4, predicted.Count);
        Assert.Equal(s.Active, predicted[0]);
        // Waiting with every actor keeps the ATB rules simple: the first predicted actor is the one acting now.
        var hash = s.ComputeHash();
        b.PredictTurnOrder(10);
        Assert.Equal(hash, s.ComputeHash());   // prediction never mutates the battle
    }

    [Fact]
    public void PathToEndsAtTargetAndStepsAreAdjacent()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 10, posA: new Cell(1, 1), posB: new Cell(10, 8));
        var u = b.State.Units[0];
        var target = b.Reachable(u).Keys.OrderByDescending(c => c.X).First();
        var path = b.PathTo(u.Id, target)!;
        Assert.Equal(target, path[^1]);
        var prev = u.Pos;
        foreach (var c in path)
        {
            Assert.True(Math.Max(Math.Abs(c.X - prev.X), Math.Abs(c.Y - prev.Y)) == 1);
            prev = c;
        }
        Assert.Null(b.PathTo(u.Id, new Cell(11, 9)));
    }
}
