using H5.Adventure.Engine;
using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>
/// Map objects of a player left out of the game (H5_Game.exe 00c9fa80 makes it a state-4 player, 004ffb60): the map
/// hero creation 0041ce20 returns no hero when the owner's state is 4; the owner set-up 00451c90 (towns via 00cd79d0,
/// other owned objects) gives the object owner 0 (neutral) when the owner's state is 4.
/// </summary>
[Collection("adv")]
public class AbsentPlayerObjectTests(Fixture f)
{
    [Fact]
    public void AbsentPlayersTownsAreNeutralAndTheirHeroesAreNotPlaced()
    {
        var map = f.Map("A2M1");
        var setup = GameSetup.AllPlayers(map, 1, 3);
        var left = setup.Players[^1];
        setup.Players.Remove(left);
        var g = Engine.Adventure.NewGame(f.Db, map, setup).Game;
        var towns = map.Objects.Where(o => o.Kind == ObjectKind.Town && o.Owner == left.MapPlayer).ToList();
        Assert.NotEmpty(towns);
        foreach (var t in towns) Assert.Equal(-1, g.S.Towns.Single(x => x.Object == t.Index).Owner);
        Assert.DoesNotContain(g.S.Heroes, h => h.Owner == left.MapPlayer);
    }
}
