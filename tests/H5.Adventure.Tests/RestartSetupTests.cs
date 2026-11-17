using H5.Adventure.Engine;

namespace H5.Adventure.Tests;

/// <summary>
/// Restart Mission of a custom / hot-seat game (H5_Game.exe 007581b0 → 007e09c0): 0059fbc0 copies the game info — the
/// setup model with every slot's choices — and 007df530 takes a new seed.
/// </summary>
[Collection("adv")]
public class RestartSetupTests(Fixture f)
{
    [Fact]
    public void RestartKeepsEverySlotsChoicesWithTheNewSeed()
    {
        var map = f.Map("A2M1");
        var setup = GameSetup.AllPlayers(map, 2, 11);
        setup.Difficulty = 3;
        setup.Players[0].Race = "TOWN_ACADEMY";
        setup.Players[0].Bonus = 2;
        setup.Players[1].StartHero = "Hero1";
        setup.Players[1].Bonus = 3;
        var again = setup.Restart(99);
        Assert.Equal(99UL, again.Seed);
        Assert.Equal(3, again.Difficulty);
        Assert.Equal(setup.Campaign, again.Campaign);
        Assert.Equal(setup.Players.Select(p => (p.MapPlayer, p.Human, p.Race, p.StartHero, p.Bonus)),
            again.Players.Select(p => (p.MapPlayer, p.Human, p.Race, p.StartHero, p.Bonus)));
        Assert.All(again.Players, p => Assert.DoesNotContain(p, setup.Players));
        // The restarted game plays the same slots.
        var g = Engine.Adventure.NewGame(f.Db, f.Map("A2M1"), again).Game;
        Assert.Equal(again.Players.Where(p => p.Human).Select(p => p.MapPlayer).OrderBy(i => i),
            g.S.Players.Where(p => p.Human).Select(p => p.Index).OrderBy(i => i));
        Assert.Equal(3, g.S.Difficulty);
    }
}
