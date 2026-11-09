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

    /// <summary>
    /// GetPlayerResource 005c19a0, SetPlayerResource 005c1c30, SetPlayerStartResources 005c1fb0, HasBorderguardKey 005c22a0,
    /// GiveBorderguardKey 005c2540, OpenCircleFog 005da1e0, OpenRegionFog 005d9df0 and IsObjectVisible 005cc2a0 print
    /// "doesn't taking part" only for a missing player or state 0 (player vf 0x6c): they act on a state-4 player.
    /// </summary>
    [Fact]
    public void ScriptPlayerNativesActOnAbsentPlayers()
    {
        var (files, map) = f.Open("A2M1");
        var absent = Enumerable.Range(0, 8).First(i => i >= map.Players.Count || !map.Players[i].Active);
        var g = H5.Adventure.Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 1, 3)).Game;
        int players = g.S.Players.Count;
        var hero = g.S.Heroes.First(h => h.Alive && h.DefId.Length > 0);
        var townName = hero.DefId;
        Assert.False(string.IsNullOrEmpty(townName));
        var host = AdventureScripting.Create(g, files, new EnumTables(f.Fs), new SharedCatalog(new XdbLoader(f.Fs)));
        host.AutoAnswerUi = true;
        int n = absent + 1;
        var t = hero.Pos;
        host.Start(null,
            $"function B(x) if x then return 1 end return 0 end local a = GetPlayerResource({n}, 6) " +
            $"SetPlayerResource({n}, 6, 700) SetPlayerResource({n}, 0, 5) " +
            $"local k0 = HasBorderguardKey({n}, 3) GiveBorderguardKey({n}, 3) " +
            $"local v0 = IsObjectVisible({n}, '{townName}') OpenCircleFog({t.X}, {t.Y}, {t.Floor}, 3, {n}) " +
            $"print(a .. ':' .. GetPlayerResource({n}, 6) .. ':' .. GetPlayerResource({n}, 0) .. ':' .. B(k0) .. ':' .. " +
            $"B(HasBorderguardKey({n}, 3)) .. ':' .. B(v0) .. ':' .. B(IsObjectVisible({n}, '{townName}')) .. ':' .. GetPlayerState({n}))");
        Assert.Empty(host.Errors);
        Assert.True(host.Log.Any(l => l.Trim() == "0:700:5:0:1:0:1:4"), string.Join("|", host.Log));
        Assert.Equal(players, g.S.Players.Count);
        Assert.Contains(g.S.AbsentPlayers, p => p.Index == absent);
    }
}
