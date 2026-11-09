using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Core.IO;
using H5.Scripting.Runtime;
using Xunit;

namespace H5.Scripting.Tests;

/// <summary>
/// The 5.5 install (MMH55 scripts on the stock engine), shared by the feature tests. Absent install: tests return early.
/// </summary>
public sealed class Mmh55Install : IDisposable
{
    public const string Root = "G:/HoMM 5 - Tribes of the East (5.5)";
    public readonly bool Present;
    public readonly PakFileSystem? Fs;
    public readonly GameInstall? Install;
    public readonly AdventureDb? Db;
    public readonly MapLoader? Loader;
    public readonly EnumTables? Ids;

    public Mmh55Install()
    {
        Present = Directory.Exists(Root) && PakLoadOrder.IsMmh55Install(Root);
        if (!Present) return;
        Install = GameInstall.At(Root);
        Fs = PakFileSystem.OpenInstall(Install);
        var core = GameDatabase.Load(Fs);
        Db = AdventureDb.Load(core, Fs);
        Loader = new MapLoader(core, Fs);
        Ids = new EnumTables(Fs);
    }

    /// <summary>
    /// A new game on the map with the MMH55 scripts running (bots' setup, one human = player 1), after the startup and
    /// a few script segments so the detector threads and H55_ContinuesEvent have run once.
    /// </summary>
    public (Adventure.Engine.Adventure G, AdventureScripting Host) Start(string mapId, Dictionary<string, string>? settings = null, int segments = 30)
    {
        var files = MapCatalog.Find(Fs!, Install!, mapId)!.Open(Fs!);
        var map = Loader!.Load(files);
        var g = Adventure.Engine.Adventure.NewGame(Db!, map, GameSetup.AllPlayers(map, humans: 1, seed: 7)).Game;
        var host = AdventureScripting.Create(g, files, Ids!, Loader.Catalog);
        host.SettingOverrides = settings;
        string? src = null;
        if (map.ScriptPath != null && files.TryReadAllBytes(map.ScriptPath, out var b)) src = ScriptText.FromBytes(b);
        host.Start(map.ScriptPath, src);
        host.Tick(segments);
        return (g, host);
    }

    /// <summary>Script errors other than the ones the stock exe raises too (docs/mmh55/SCRIPTS.md §5).</summary>
    public static List<string> OwnErrors(AdventureScripting host) => host.Errors.Where(e =>
        !e.Contains("is not belong to AI player") && !e.Contains("doesn't taking part in the current game")
        && !e.Contains("in H55_GetUnusualSchools]")).ToList();

    public void Dispose() => Fs?.Dispose();
}

/// <summary>
/// MMH55 settings whose script path needs engine support (docs/mmh55/OPTIONS.md §4): the scripts are run in our
/// engine on a scripted single mission and the feature's effect is checked.
/// </summary>
public class Mmh55FeatureTests(Mmh55Install m) : IClassFixture<Mmh55Install>
{
    const string Map = "A1SM1";

    static HeroState HumanHero(Adventure.Engine.Adventure g) => g.S.Heroes.First(h => h.Alive && h.Owner >= 0 && g.Player(h.Owner) is { Human: true });

    /// <summary>
    /// Town Gate / Town Conversion / Governance are offered through the hero's custom ability 4: H55_ContinuesEvent
    /// calls ControlHeroCustomAbility(hero, CUSTOM_ABILITY_4, CUSTOM_ABILITY_ENABLED) for human heroes, and the cast of
    /// SPELL_ABILITY_CUSTOM4 fires CUSTOM_ABILITY_TRIGGER → H55_TownManagement(hero, 4) (exe FUN_00b56970, 008656e0).
    /// </summary>
    [Fact]
    public void TownManagementAbilityIsGivenAndItsCastRunsTheScript()
    {
        if (!m.Present) return;
        var (g, host) = m.Start(Map);
        var h = HumanHero(g);
        Assert.Contains("SPELL_ABILITY_CUSTOM4", h.Spells);
        Assert.Contains("SPELL_ABILITY_CUSTOM4", g.BookSpells(h));
        int before = host.World.PendingUi.Count;
        var r = host.Apply(new CastAdventureSpellCmd(h.Id, "SPELL_ABILITY_CUSTOM4") { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        host.Tick(10);
        Assert.Empty(Mmh55Install.OwnErrors(host));
        // H55_TownManagement answers with a message or question box (info / teleport / convert).
        Assert.True(host.World.PendingUi.Count > before, "H55_TownManagement opened no dialog");
    }

    [Fact]
    public void DisabledCustomAbilityCannotBeCastAndRemovedOneLeavesTheBook()
    {
        if (!m.Present) return;
        var (g, host) = m.Start(Map);
        var h = HumanHero(g);
        host.L.DoString($"ControlHeroCustomAbility(\"{h.DefId}\", CUSTOM_ABILITY_2, CUSTOM_ABILITY_DISABLED);", "test");
        host.Tick();
        Assert.Contains("SPELL_ABILITY_CUSTOM2", h.Spells);
        Assert.False(host.Apply(new CastAdventureSpellCmd(h.Id, "SPELL_ABILITY_CUSTOM2") { Player = h.Owner }).Ok);
        host.L.DoString($"ControlHeroCustomAbility(\"{h.DefId}\", CUSTOM_ABILITY_2, CUSTOM_ABILITY_NOT_PRESENT);", "test");
        host.Tick();
        Assert.DoesNotContain("SPELL_ABILITY_CUSTOM2", h.Spells);
        Assert.DoesNotContain("SPELL_ABILITY_CUSTOM2", h.DisabledSpells);
    }

    /// <summary>
    /// Town Conversion (H55_ConvertNow) reads the building levels, calls TransformTown and rebuilds them with
    /// UpgradeTownBuilding. TransformTown recreates the town (exe 0x005c9ee0, vf 0xf4 record): new type, same name and
    /// owner, buildings back to the initial state, empty garrison.
    /// </summary>
    [Fact]
    public void TransformTownRecreatesTheTownAndConversionRebuildsIt()
    {
        if (!m.Present) return;
        var (g, host) = m.Start(Map);
        var t = g.S.Towns.First(x => x.Owner >= 0);
        var o = g.Map.Objects[t.Object];
        string old = t.Type, target = old == "TOWN_ACADEMY" ? "TOWN_HEAVEN" : "TOWN_ACADEMY";
        int targetId = EnumTables.TownIndex(target);
        t.Garrison[0] = new Stack(t.Garrison[0]?.Creature ?? "CREATURE_PEASANT", 5);
        host.L.DoString($"TransformTown(\"{o.Name}\", {targetId});", "test");
        host.Tick();
        Assert.Empty(Mmh55Install.OwnErrors(host));
        var n = g.S.Town(t.Object)!;
        Assert.Equal(target, n.Type);
        Assert.Equal(t.Owner, n.Owner);
        Assert.Equal(1, n.Level("TB_TOWN_HALL"));
        Assert.All(n.Built.Where(kv => kv.Key != "TB_TOWN_HALL"), kv => Assert.Equal(0, kv.Value));
        Assert.All(n.Garrison, s => Assert.Null(s));
    }
}
