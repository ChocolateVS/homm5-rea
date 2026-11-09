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
    public (Adventure.Engine.Adventure G, AdventureScripting Host) Start(string mapId, Dictionary<string, string>? settings = null, int segments = 30, bool noMapScriptCheck = false)
    {
        var files = MapCatalog.Find(Fs!, Install!, mapId)!.Open(Fs!);
        var map = Loader!.Load(files);
        var g = Adventure.Engine.Adventure.NewGame(Db!, map, GameSetup.AllPlayers(map, humans: 1, seed: 7)).Game;
        var host = AdventureScripting.Create(g, files, Ids!, Loader.Catalog);
        host.SettingOverrides = settings;
        host.RunWithoutMapScript = noMapScriptCheck;
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

    /// <summary>
    /// UpgradeTownBuilding (0x005e7a30 → 0x0042fd40 → 0x00cd4a40 free, vf 0xb4 = 0x00cd0de0 on the current level): one
    /// level past the map limit, then no more; the limit itself is not changed.
    /// </summary>
    [Fact]
    public void UpgradeTownBuildingGoesOneLevelPastTheLimit()
    {
        if (!m.Present) return;
        var (g, host) = m.Start(Map);
        var t = g.S.Towns.First();
        var name = g.Map.Objects[t.Object].Name;
        t.Built["TB_TAVERN"] = 0;
        t.MaxLevel["TB_TAVERN"] = 0;
        host.L.DoString($"UpgradeTownBuilding(\"{name}\", TOWN_BUILDING_TAVERN); UpgradeTownBuilding(\"{name}\", TOWN_BUILDING_TAVERN);", "test");
        host.Tick();
        Assert.Empty(Mmh55Install.OwnErrors(host));
        Assert.Equal(1, t.Level("TB_TAVERN"));
        Assert.Equal(0, t.MaxLevel["TB_TAVERN"]);
    }

    /// <summary>
    /// Town Conversion end to end (H55_ConvertNow, reached from the Town Management dialog): on a multiplayer map (the
    /// conversion is off on campaign and scenario maps; the stock engine runs no scripts on maps without a map script,
    /// so this uses MMH55's RemoveMapScriptCheck behaviour), the town becomes the hero's race, the resources are paid and
    /// the recorded buildings are rebuilt by UpgradeTownBuilding.
    /// </summary>
    [Fact]
    public void TownConversionConvertsAndRebuilds()
    {
        if (!m.Present) return;
        var (g, host) = m.Start("Maps/Multiplayer/A2M1", noMapScriptCheck: true);
        var h = HumanHero(g);
        var p = g.Player(h.Owner)!;
        var t = g.S.Towns.First(x => x.Type != p.Race);
        t.Owner = h.Owner;
        t.Built["TB_FORT"] = 1;
        t.Built["TB_DWELLING_1"] = 1;
        t.Built["TB_TAVERN"] = 1;
        for (int r = 0; r < 7; r++) host.L.DoString($"SetPlayerResource({h.Owner + 1}, {r}, 100000);", "test");
        var name = g.Map.Objects[t.Object].Name;
        host.L.DoString($"startThread(H55_ConvertNow, \"{h.DefId}\", \"{name}\", 2);", "test");
        host.Tick(20);
        Assert.Empty(Mmh55Install.OwnErrors(host));
        var n = g.S.Town(t.Object)!;
        Assert.Equal(p.Race, n.Type);
        Assert.Equal(1, n.Level("TB_FORT"));
        Assert.Equal(1, n.Level("TB_DWELLING_1"));
        Assert.Equal(1, n.Level("TB_TAVERN"));
        Assert.Equal(100000 - (2 * 3000 - 2000), p.Purse[6]);
    }

    static (int Creatures, int Stacks, int Monsters) Neutrals(Adventure.Engine.Adventure g)
    {
        int creatures = 0, stacks = 0, monsters = 0;
        foreach (var o in g.Map.OfKind(ObjectKind.Monster))
        {
            if (g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            var guards = g.S.Obj(o.Index).Guards;
            monsters++;
            stacks += guards.Count(x => x.Count > 0);
            creatures += guards.Sum(x => x.Count);
        }
        return (creatures, stacks, monsters);
    }

    /// <summary>
    /// NeutralStackSize (H55_ModifyStackSize: AddObjectCreatures round(count·k − count) per stack) and ForceMixedStacks
    /// (H55_MixStacks: each stack is split into several creature types) run once the start-up has processed the map.
    /// </summary>
    [Fact]
    public void NeutralStackSizeAndMixedStacksChangeTheNeutrals()
    {
        if (!m.Present) return;
        const string mp = "Maps/Multiplayer/A2M1";
        var (g1, h1) = m.Start(mp, new() { ["H55_GenerateMixedStacks"] = "0" }, segments: 300, noMapScriptCheck: true);
        var (g2, h2) = m.Start(mp, new() { ["H55_GenerateMixedStacks"] = "0", ["H55_NeutralStackSize"] = "2" }, segments: 300, noMapScriptCheck: true);
        var (g3, h3) = m.Start(mp, new() { ["H55_ForceMixedStacks"] = "1" }, segments: 300, noMapScriptCheck: true);
        var (g4, h4) = m.Start(mp, new() { ["H55_MixStacksThreshold"] = "1000" }, segments: 300, noMapScriptCheck: true);
        foreach (var h in new[] { h1, h2, h3, h4 }) Assert.Empty(Mmh55Install.OwnErrors(h));
        var a = Neutrals(g1); var b = Neutrals(g2); var c = Neutrals(g3); var d = Neutrals(g4);
        Assert.True(a.Monsters > 0);
        Assert.Equal(a.Monsters, b.Monsters);
        Assert.InRange(b.Creatures, a.Creatures * 2 - a.Stacks, a.Creatures * 2 + a.Stacks);
        Assert.True(c.Stacks > c.Monsters, $"no mixed stacks: {c.Stacks} stacks on {c.Monsters} monsters");
        // Without ForceMixedStacks the start-up decides before H55_UpdateMapType has run (H55-Core's chunk sleeps once and
        // is resumed first, as by the exe's scheduler FUN_004c0720 / FUN_00d0c8a0 append order): H55_MapType is still
        // "" there, so the multiplayer-only mix is skipped (H55_MixDecisionDelay = 1).
        Assert.Equal(1, h4.L.GetGlobal("H55_MixDecisionDelay").N);
        Assert.Equal(d.Monsters, d.Stacks);
    }

    /// <summary>
    /// Town Gate (H55_TeleportNow from the Town Management dialog): the hero is put on the target town's active tile
    /// (H55_GetTownActiveTile: position + rotated entrance offset) with SetObjectPosition, paying move points and 25 mana.
    /// </summary>
    [Fact]
    public void TownGateTeleportsTheHeroToTheTownEntrance()
    {
        if (!m.Present) return;
        var (g, host) = m.Start("Maps/Multiplayer/A2M1", noMapScriptCheck: true);
        var h = HumanHero(g);
        var t = g.S.Towns.OrderByDescending(x => Math.Abs(x.Pos.X - h.Pos.X) + Math.Abs(x.Pos.Y - h.Pos.Y)).First();
        t.Owner = h.Owner;
        h.Mana = 50;
        var name = g.Map.Objects[t.Object].Name;
        var before = h.Pos;
        host.L.DoString($"startThread(H55_TeleportNow, \"{h.DefId}\", \"{name}\");", "test");
        host.Tick(30);
        Assert.Empty(Mmh55Install.OwnErrors(host));
        Assert.NotEqual(before, h.Pos);
        Assert.Equal(g.EntranceOf(t), h.Pos);
        Assert.Equal(Math.Min(g.ScriptMaxMana(h), 25), h.Mana);   // ChangeHeroStat clamps mana to [0, max] (0x005c3620)
    }

    /// <summary>LowResGame: every human player loses 10 wood, 10 ore, 5 of each rare resource and 10000 gold at the start.</summary>
    [Fact]
    public void LowResGameTakesTheHumansResources()
    {
        if (!m.Present) return;
        const string mp = "Maps/Multiplayer/A2M1";
        var (g0, _) = m.Start(mp, segments: 60, noMapScriptCheck: true);
        var (g1, h1) = m.Start(mp, new() { ["H55_LowResGame"] = "1" }, segments: 60, noMapScriptCheck: true);
        Assert.Empty(Mmh55Install.OwnErrors(h1));
        var p0 = g0.S.Players.First(p => p.Human); var p1 = g1.S.Players.First(p => p.Human);
        int[] taken = { 10, 10, 5, 5, 5, 5, 10000 };
        for (int r = 0; r < 7; r++) Assert.Equal(Math.Max(0, p0.Purse[r] - taken[r]), p1.Purse[r]);
    }

    /// <summary>NoArtifactMerchantsInTowns: DestroyTownBuildingToLevel(…, 0, 0) on Academy TB_SPECIAL_3 / Dungeon TB_SPECIAL_4 caps them at 0.</summary>
    [Fact]
    public void NoArtifactMerchantsInTownsForbidsTheMerchants()
    {
        if (!m.Present) return;
        var (g, host) = m.Start("Maps/Multiplayer/A2M1", new() { ["H55_NoArtifactMerchantsInTowns"] = "1" }, segments: 60, noMapScriptCheck: true);
        Assert.Empty(Mmh55Install.OwnErrors(host));
        var towns = g.S.Towns.Where(t => t.Type is "TOWN_ACADEMY" or "TOWN_DUNGEON").ToList();
        foreach (var t in towns)
            Assert.Equal(0, t.MaxLevel[t.Type == "TOWN_ACADEMY" ? "TB_SPECIAL_3" : "TB_SPECIAL_4"]);
    }
}
