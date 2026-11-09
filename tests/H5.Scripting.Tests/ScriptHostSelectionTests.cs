using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Core.IO;
using H5.Scripting.Runtime;
using Xunit;

namespace H5.Scripting.Tests;

/// <summary>
/// Which host runs advmap-startup.lua (docs/mmh55/SCRIPTS.md §1): single player = the adventure controller
/// (FUN_006089a0 → FUN_00603b20) on every map; multiplayer / hotseat = the world (FUN_00992d00) only with a MapScript.
/// </summary>
public class ScriptHostSelectionTests(Mmh55Install m) : IClassFixture<Mmh55Install>
{
    [Theory]
    [InlineData(false, false, false, false, true)]   // single player, no map script: controller runs the startup
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, false)]   // multiplayer, no map script: 0x00992d2a returns
    [InlineData(false, false, true, false, false)]   // hotseat, no map script
    [InlineData(true, true, false, false, true)]     // multiplayer with a (possibly empty) MapScript resource
    [InlineData(true, false, true, false, true)]
    [InlineData(false, true, false, true, true)]     // MMH55 DLL RemoveMapScriptCheck
    public void StartupRunsFollowsTheExeHosts(bool hasMapScript, bool multiplayer, bool hotseat, bool dll, bool runs) =>
        Assert.Equal(runs, AdventureScripting.StartupRuns(hasMapScript, multiplayer, hotseat, dll));

    (AdventureScripting Host, AdvMap Map) StartOn(string mapId, bool multiplayer)
    {
        var files = MapCatalog.Find(m.Fs!, m.Install!, mapId)!.Open(m.Fs!);
        var map = m.Loader!.Load(files);
        var g = Adventure.Engine.Adventure.NewGame(m.Db!, map, GameSetup.AllPlayers(map, humans: 1, seed: 7)).Game;
        var host = AdventureScripting.Create(g, files, m.Ids!, m.Loader.Catalog);
        host.Multiplayer = multiplayer;
        string? src = map.ScriptPath != null && files.TryReadAllBytes(map.ScriptPath, out var b) ? ScriptText.FromBytes(b) : null;
        host.Start(map.ScriptPath, src);
        host.Tick(30);
        return (host, map);
    }

    /// <summary>A stock multiplayer map has no MapScript: MMH55 (H55-Core's detector threads) runs in single player only.</summary>
    [Fact]
    public void Mmh55RunsOnAMultiplayerMapPlayedSinglePlayer()
    {
        if (!m.Present) return;
        var (sp, map) = StartOn("Maps/Multiplayer/A2M1", multiplayer: false);
        Assert.Null(map.ScriptPath);
        Assert.NotEmpty(sp.L.Threads);
        var (mp, _) = StartOn("Maps/Multiplayer/A2M1", multiplayer: true);
        Assert.Empty(mp.L.Threads);
    }

    /// <summary>The exe's hero lists iterate HEROES_ANY; the cut-scene Godric_ds is not in it (and is a ScenarioHero).</summary>
    [Fact]
    public void DialogSceneHeroIsNotInTheHeroLists()
    {
        if (!m.Present) return;
        var ds = m.Db!.Core.HeroByPath("MapObjects/DialogScenes/Godrik_ds.(AdvMapHeroShared).xdb");
        Assert.NotNull(ds);
        Assert.False(m.Db.InHeroesAny(ds!));
        Assert.True(m.Db.InHeroesAny(m.Db.Core.Heroes["Godric"]));
    }
}
