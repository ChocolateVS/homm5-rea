using H5.Adventure.Engine;
using H5.Core.Xdb;
using H5.Adventure.Map;
using H5.Scripting.Runtime;

namespace H5.Save.Tests;

/// <summary>
/// 00c9fa80 makes every player number 1..8; a number with no setup slot is a 004ffb60 player in state 4 (vf 0x70),
/// which GetPlayerState (005c1390, player vf 0x6c) returns and GetPlayerHeroes (005c1540, state != 0) accepts.
/// </summary>
[Collection("save")]
public class AbsentPlayerScriptTests(Fixture f)
{
    [Fact]
    public void AbsentPlayersAreStateFourWithNoHeroes()
    {
        var (files, map) = f.Open("A2M1");
        var absent = Enumerable.Range(0, 8).First(i => i >= map.Players.Count || !map.Players[i].Active);
        var present = map.Players.First(p => p.Active).Index;
        var g = H5.Adventure.Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 1, 3)).Game;
        var host = AdventureScripting.Create(g, files, new EnumTables(f.Fs), new SharedCatalog(new XdbLoader(f.Fs)));
        host.AutoAnswerUi = true;
        host.Start(null, $"print(GetPlayerState({absent + 1}) .. \":\" .. length(GetPlayerHeroes({absent + 1})) .. \":\" .. GetPlayerState({present + 1}))");
        Assert.Empty(host.Errors);
        Assert.True(host.Log.Any(l => l.Trim() == "4:0:1"), string.Join("|", host.Log));
    }
}
