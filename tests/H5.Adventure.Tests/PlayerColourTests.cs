using H5.Adventure.Engine;

namespace H5.Adventure.Tests;

/// <summary>
/// Player colours in the game (H5_Game.exe 00ca0240, the game's player slots): a slot's colour starts as its player
/// number (1 = PCOLOR_RED … 8 = PCOLOR_BROWN) and the map player's Colour (+0x84) replaces it only when not 0
/// (PCOLOR_NEUTRAL). User report: the adventure tooltip of the human's own hero said "Owned by Neutral Player" on a
/// map whose player records are all PCOLOR_NEUTRAL.
/// </summary>
[Collection("adv")]
public class PlayerColourTests(Fixture f)
{
    [Fact]
    public void NeutralMapColoursBecomeThePlayerNumber()
    {
        var map = f.Map("A2M2");
        Assert.All(map.Players, p => Assert.Equal("PCOLOR_NEUTRAL", p.Colour));
        var g = Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 1, 3)).Game;
        string[] byNumber = { "PCOLOR_RED", "PCOLOR_BLUE", "PCOLOR_GREEN", "PCOLOR_YELLOW", "PCOLOR_ORANGE", "PCOLOR_TEAL", "PCOLOR_PURPLE", "PCOLOR_BROWN" };
        Assert.NotEmpty(g.S.Players);
        foreach (var p in g.S.Players) Assert.Equal(byNumber[p.Index], p.Colour);
    }

    [Fact]
    public void AMapColourIsKept()
    {
        var map = f.Map("A2M1");
        var g = Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 1, 3)).Game;
        Assert.Equal("PCOLOR_BLUE", g.S.Players.Single(p => p.Index == 0).Colour);
    }
}
