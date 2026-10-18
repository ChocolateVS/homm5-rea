namespace H5.Combat.Tests;

/// <summary>AI-vs-AI duels that once looped forever (free hero abilities re-used every turn) must finish.</summary>
[Collection("game")]
public class NoStallTests(GameDataFixture f)
{
    [Theory]
    [InlineData(7, 21)]
    [InlineData(23, 6)]
    public void DuelFinishes(int a, int b)
    {
        int n = f.Db.DuelPresets.Count;
        var s = BattleSetup.CreateDuel(f.Db, a, b, 1 + (ulong)(a * n + b));
        var battle = new Battle(f.Db, s);
        var ai = new GreedyBattleAI();
        BattleRunner.RunToEnd(battle, ai, ai);
        Assert.Equal(BattlePhase.Finished, s.Phase);
    }
}
