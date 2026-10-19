using H5.Adventure.AI;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

[Collection("adv")]
public class OriginalAdventureAiTests(Fixture f)
{
    [StockFact]
    public void TunablesComeFromDefaultStats()
    {
        var st = AdventureAiStats.Load(f.Core);
        Assert.Equal(2.7, st.RoleSpread[0], 6);
        Assert.Equal(0.25, st.RoleSpread[2], 6);
        Assert.Equal(17, st.Difficulty[3].HireHeroesGlobalLimit);
        Assert.Equal(128000, st.Difficulty[3].ValueOfTown);
        Assert.Equal(2, st.Difficulty[0].TownBuildPeriodInDays);
        Assert.Equal(300, st.SearchDangerEnemyMobilityBonus, 6);
    }

    [StockFact]
    public void ExeConfirmedFormulas()
    {
        var st = AdventureAiStats.Load(f.Core);
        // FUN_004e9f60: Σ attribute · ValueOfAttribute* (300 each in stock data).
        Assert.Equal(300L * (2 + 3 + 4 + 5), st.AttributeValue(2, 3, 4, 5));
        // FUN_004eb840: artifact class values.
        Assert.Equal(9000, st.ArtifactValue("ARTF_CLASS_MAJOR"));
        // FUN_00868a40 × FUN_004e9a80: RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel).
        var hero = new HeroState { Level = 10 };
        Assert.Equal(2.7 * 2.0, OriginalAdventureAI.SpreadWeight(st, st.Difficulty[1], HeroRole.Commander, hero), 6);
    }

    [Fact]
    public void PlaysDeterministicallyWithoutStalling()
    {
        string Run()
        {
            var g = f.NewGame("A2M1", 5);
            var ai = new OriginalAdventureAI();
            for (int turn = 0; turn < 6 * 7 && !g.S.Finished; turn++) ai.PlayTurn(g, 1000UL + (ulong)turn);
            Assert.True(g.S.Day >= 7, $"game advanced only to day {g.S.Day}");
            return g.S.ComputeHash();
        }
        Assert.Equal(Run(), Run());
    }
}
