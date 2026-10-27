using System.Text;
using H5.Combat;
using H5.Core.Data;
using H5.Core.IO;
using H5.Scripting.Runtime;

namespace H5.Scripting.Tests;

public sealed class ScriptDataFixture
{
    public PakFileSystem Fs { get; } = PakFileSystem.OpenInstall(GameInstall.Locate());
    readonly Lazy<GameDatabase> _db;
    readonly Lazy<EnumTables> _ids;
    public ScriptDataFixture()
    {
        _db = new(() => GameDatabase.Load(Fs));
        _ids = new(() => new EnumTables(Fs));
    }
    public GameDatabase Db => _db.Value;
    public EnumTables Ids => _ids.Value;
}

[CollectionDefinition("scriptdata")]
public sealed class ScriptDataCollection : ICollectionFixture<ScriptDataFixture> { }

/// <summary>Combat script session as the combat screen creates it (FUN_0054c2f0 → FUN_0053e810), docs/mmh55/SCRIPTS.md §1.</summary>
[Collection("scriptdata")]
public class CombatScriptTests(ScriptDataFixture f)
{
    CombatScripting Session(Battle b, string startup, string? script = null)
    {
        byte[]? Read(string p) => p == "scripts/combat-startup.lua" ? Encoding.ASCII.GetBytes(startup) : null;
        var cs = new CombatScripting(f.Db, b, f.Ids, Read);
        cs.Load(script == null ? null : "test.lua", script);
        return cs;
    }

    [Fact]
    public void ScriptsOnlyOutsideMultiplayerAndHotseat()
    {
        Assert.True(CombatScripting.ScriptsAllowed(false, false));
        Assert.False(CombatScripting.ScriptsAllowed(true, false));
        Assert.False(CombatScripting.ScriptsAllowed(false, true));
    }

    [Fact]
    public void SchedulerPassRunsBetweenAliasesAndTheCombatScript()
    {
        var b = new Battle(f.Db, BattleSetup.CreateDuel(f.Db, 0, 1, 3));
        b.Begin();
        // A thread started by combat-startup.lua runs its first slice in the pass FUN_0053e810 makes (engine vf8)
        // before the battle's own script text.
        var cs = Session(b, @"
            order = ''
            function createCombatAliases() order = order .. 'A' end
            startThread(function() order = order .. 'T' end)", "order = order .. 'S'");
        Assert.Equal("ATS", cs.L.GetGlobal("order").Str);
    }

    [Fact]
    public void ScriptCommandsReachTheClientAsBattleEvents()
    {
        var b = new Battle(f.Db, BattleSetup.CreateDuel(f.Db, 0, 1, 3));
        var opening = b.Begin();
        var cs = Session(b, "");
        ((IBattleScriptSession)cs).Begun(opening);
        for (int i = 0; i < 20 && !(b.State.Phase == BattlePhase.Combat && b.State.Active is int a && !Actor.IsHero(a)); i++)
        {
            if (b.State.Phase == BattlePhase.Tactics) cs.Apply(new EndTacticsCmd { Side = b.State.TacticsDone[0] ? 1 : 0 });
            else cs.Apply(new WaitCmd(b.State.Active!.Value) { Side = b.SideToAct ?? 0 });
        }
        Assert.True(cs.L.DoString("commandDefend(combatReadyPerson())", "t"));
        // The script's command was applied; its events wait for the screen to animate them.
        Assert.NotEmpty(cs.TakeScriptEvents());
        Assert.Empty(cs.TakeScriptEvents());
    }
}
