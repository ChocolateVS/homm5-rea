using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.Data;
using H5.Adventure.State;
using H5.Core.IO;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

/// <summary>Trigger type ids (advmap-startup.lua).</summary>
public static class TriggerTypes
{
    public const int NewDay = 0, PlayerAddHero = 1, PlayerRemoveHero = 2, ObjectiveStateChange = 3, ObjectTouch = 4,
        ObjectCapture = 5, RegionEnterAndStop = 6, RegionEnterWithoutStop = 7, HeroLevelUp = 8, WarFogEnter = 9,
        TownHeroDeploy = 10, CombatResults = 11, CustomAbility = 12, RegionExit = 13, HeroAddSkill = 14,
        HeroRemoveSkill = 15, HeroTouch = 16;
}

/// <summary>
/// Runs an adventure map's scripts against the rules engine like the original: loads
/// scripts/advmap-startup.lua (constants, MessageBox/SyncCommand wrappers, aliases), then the
/// map script; fires Trigger() handlers from engine events; advances script threads per segment.
///
/// Usage: wrap every command with <see cref="Apply"/>, call <see cref="Tick"/> for elapsed script
/// segments, show <see cref="ScriptWorld.PendingUi"/> requests and answer them with
/// <see cref="AnswerUi"/>. Everything is deterministic given the same commands and ticks.
/// </summary>
public sealed partial class AdventureScripting : IAdventureScriptHooks
{
    public LuaState L { get; }
    public H5.Adventure.Engine.Adventure Game { get; }
    public MapScriptData Data { get; }
    public EnumTables Ids { get; }
    public ScriptWorld World { get; private set; } = new();
    public AdvState S => Game.S;
    public AdvMap Map => Game.Map;

    /// <summary>Presentation events (camera, sound, effects, UI) produced since the last drain.</summary>
    public readonly List<ScriptEvent> Events = new();
    /// <summary>Rule events produced by script commands (resources, objects, heroes…), for the client.</summary>
    public readonly List<AdvEvent> GameEvents = new();
    /// <summary>Console output (print, errors).</summary>
    public readonly List<string> Log = new();
    public readonly List<string> Errors = new();

    /// <summary>Headless mode: answer every UI request immediately with OK / yes.</summary>
    public bool AutoAnswerUi { get; set; }

    readonly Func<string, byte[]?> _readFile;
    readonly SharedCatalog _catalog;
    readonly PakFileSystem _game;

    public AdventureScripting(H5.Adventure.Engine.Adventure game, MapScriptData data, EnumTables ids, Func<string, byte[]?> readFile,
        SharedCatalog catalog, PakFileSystem gameFiles)
    {
        Game = game;
        Data = data;
        Ids = ids;
        _readFile = readFile;
        _catalog = catalog;
        _game = gameFiles;
        L = new LuaState { ArgFormats = NativeFormats.Adventure };
        L.Output = s => Log.Add(s);
        L.ErrorReported = (msg, label) => Errors.Add($"{label}: {msg}");
        game.ScriptHooks = this;
        game.CommandSink = Apply;
        NameUnnamedObjects();
    }

    /// <summary>
    /// World objects left unnamed in the editor get "floor-x-y-tag-id" at creation
    /// (H5_Game.exe FUN_00ce1020: "%i-%i-%i-%s-%i" with the cell, the class part of the object's
    /// DBID such as AdvMapMonster, and the world object id, here the map index). Scripts see
    /// every object by name (GetObjectNamesByType returns them).
    /// </summary>
    void NameUnnamedObjects()
    {
        foreach (var o in Map.Objects)
            if (o.Name.Length == 0 && o.Kind != ObjectKind.Hero) o.Name = AutoName(o, o.Tag);
    }

    static string AutoName(MapObject o, string dbClass) => $"{o.Pos.Floor}-{o.Pos.X}-{o.Pos.Y}-{dbClass}-{o.Index}";

    /// <summary>Creates the host for a map opened from the game data (or an .h5m).</summary>
    public static AdventureScripting Create(H5.Adventure.Engine.Adventure game, MapFiles files, EnumTables ids, SharedCatalog catalog)
    {
        var data = MapScriptData.Load(files);
        return new AdventureScripting(game, data, ids, path =>
        {
            var p = PakFileSystem.Normalize(path);
            return files.TryReadAllBytes(p, out var b) ? b : null;
        }, catalog, files.Game);
    }

    string? LoadScript(string path)
    {
        var b = _readFile(path);
        return b == null ? null : ScriptText.FromBytes(b);
    }

    /// <summary>
    /// MMH55's DLL group RemoveMapScriptCheck (nop at 0x00992d2a) makes the engine run the startup
    /// script on maps without a map script. The stock engine (the default) runs no scripts there.
    /// </summary>
    public bool RunWithoutMapScript { get; set; }

    /// <summary>
    /// Registers the API, then starts the scripts like FUN_00992d00: only when the map has a script
    /// (else nothing runs at all); DoString(scripts/advmap-startup.lua), DoString("createAdvmapAliases();"),
    /// one scheduler pass (engine vf8 = FUN_004c0720), then the map script text if it is not empty.
    /// </summary>
    public void Start(string? mapScriptPath, string? mapScriptSource)
    {
        BaseLibrary.Register(L, LoadScript, () => (uint)S.Rng.NextU64());
        RegisterAdventureApi();
        InitObjectives();
        if (mapScriptSource == null && !RunWithoutMapScript) return;

        RunFile("scripts/advmap-startup.lua");
        L.DoString("createAdvmapAliases();", "DoString script");
        L.Step();
        if (!string.IsNullOrEmpty(mapScriptSource)) L.DoString(mapScriptSource, mapScriptPath ?? "MapScript");
        Pump();
    }

    /// <summary>
    /// Combat scripts for a battle started from this map, as the combat screen does (FUN_0054c2f0 →
    /// FUN_0053e810): combat-startup.lua, createCombatAliases(), the battle's combat script, DoPrepare().
    /// The game-variable table is the adventure map's own object, shared by reference (IAdventureMap vf8
    /// handed to CCreateCombatArenaScreen at 0x005a789b), so combat SetGameVar is seen by the map.
    /// </summary>
    public CombatScripting CreateCombatScripts(H5.Combat.Battle battle, string? scriptPath = null, string? scriptSource = null)
    {
        var cs = new CombatScripting(Game.Core, battle, Ids, _readFile, World.GameVars, _game);
        cs.Load(scriptPath, scriptSource);
        return cs;
    }

    void RunFile(string path)
    {
        var src = LoadScript(path);
        if (src == null) { Log.Add("Can`t find script file: " + path); return; }
        L.DoString(src, path);
    }

    void InitObjectives()
    {
        foreach (var o in Data.Objectives)
        {
            if (o.Name.Length == 0) continue;
            var players = o.Player >= 0 ? new[] { o.Player } : S.Players.Select(p => p.Index).ToArray();
            foreach (var p in players)
            {
                World.Objectives[ScriptWorld.ObjectiveKey(o.Name, p)] = new ObjectiveState
                {
                    State = o.Kind == "OBJECTIVE_KIND_SCENARIO_INFO" ? ObjectiveStates.ScenarioInfo
                        : o.InitiallyActive ? ObjectiveStates.Active : ObjectiveStates.Unknown,
                    Visible = o.InitiallyVisible,
                };
            }
        }
    }

    // ---- driving ---------------------------------------------------------------------------

    /// <summary>Applies a player/AI command to the engine and fires the script triggers it causes.</summary>
    public AdvResult Apply(AdvCommand cmd)
    {
        // BlockGame stops the interface and the AI: nothing can be ordered until UnblockGame.
        if (World.GameBlocked) return AdvResult.Reject("the game is blocked by a script");
        var heroesBefore = HeroSnapshot();
        var r = Game.Apply(cmd);
        if (r.Ok) Dispatch(r.Events, heroesBefore);
        Pump();
        if (SegmentsPerCommand > 0) Tick(SegmentsPerCommand);
        if (_turnSegments > 0)
        {
            int n = _turnSegments;
            _turnSegments = 0;
            Tick(n);
        }
        return r;
    }

    /// <summary>
    /// Script segments given to a computer player's turn as it starts. The exe's world (and with it
    /// the script scheduler, 200 ms per segment) keeps running while the AI plays its animated turn;
    /// our AI turn takes no time, so scripts that poll the current player (MMH55's
    /// H55_ContinuesActivator: GetDate, sleep(H55_CycleSpeed = 2), then IsPlayerCurrent) would never
    /// see a computer player's turn. This guarantees them at least this much script time.
    /// </summary>
    public int SegmentsPerComputerTurn { get; set; } = 5;
    int _turnSegments;

    /// <summary>Script segments to run after each command (headless drivers; a real client ticks by time).</summary>
    public int SegmentsPerCommand { get; set; }

    /// <summary>Advances the script scheduler by the given number of game segments.</summary>
    public void Tick(int segments = 1)
    {
        for (int i = 0; i < segments; i++)
        {
            L.Step();
            Pump();
        }
    }

    /// <summary>Answers a UI request; runs its callback (name()) like the original.</summary>
    public bool AnswerUi(int id, bool yes = true)
    {
        var req = World.PendingUi.Find(r => r.Id == id);
        if (req == null) return false;
        World.PendingUi.Remove(req);
        var cb = yes ? req.Callback : req.CallbackNo;
        if (!string.IsNullOrEmpty(cb)) L.DoString(cb + "()", $"callback {cb}");
        return true;
    }

    /// <summary>In headless mode, resolve UI requests as they appear.</summary>
    void Pump()
    {
        if (!AutoAnswerUi) return;
        for (int guard = 0; guard < 1000 && World.PendingUi.Count > 0; guard++)
            AnswerUi(World.PendingUi[0].Id, yes: true);
    }

    internal void Emit(string kind, params (string Key, object? Value)[] data) =>
        Events.Add(new ScriptEvent(kind, data.ToDictionary(d => d.Key, d => Convert.ToString(d.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "")));

    internal UiRequest RequestUi(string kind, string resource, string callback = "", string callbackNo = "")
    {
        var req = new UiRequest { Id = World.NextUiId++, Kind = kind, Resource = resource, Callback = callback, CallbackNo = callbackNo };
        World.PendingUi.Add(req);
        Emit("ui_request", ("id", req.Id), ("kind", kind), ("resource", resource));
        return req;
    }

    /// <summary>Runs an engine change from a binding, collecting its rule events for the client.</summary>
    internal void Change(Action a)
    {
        var heroesBefore = HeroSnapshot();
        var evs = Game.RunScriptAction(a);
        GameEvents.AddRange(evs);
        Dispatch(evs, heroesBefore);
    }

    // ---- triggers ----------------------------------------------------------------------------

    static string Key(int type, string filter = "") => $"{type}|{filter}";

    public void SetTrigger(int type, string filter, string? function)
    {
        var k = Key(type, filter);
        if (string.IsNullOrEmpty(function)) World.Triggers.Remove(k);
        else World.Triggers[k] = function;
    }

    /// <summary>Calls the handler registered for (type, filter), plus the editor-set function if any.</summary>
    internal void Fire(int type, string filter, string? editorFunction, params LuaValue[] args)
    {
        if (World.Triggers.TryGetValue(Key(type, filter), out var fn)) CallHandler(fn, args);
        if (!string.IsNullOrEmpty(editorFunction)) CallHandler(editorFunction, args);
    }

    void CallHandler(string fn, LuaValue[] args)
    {
        var f = L.GetGlobal(fn);
        if (!f.IsFunction)
        {
            Errors.Add($"trigger handler `{fn}' is not a function");
            L.Output?.Invoke($"Script error: attempt to call global `{fn}' (a {f.TypeName()} value)");
            return;
        }
        L.CallFunction(f, fn, args);
    }

    string? ObjectTrigger(int objIndex, string triggerElement)
    {
        if (objIndex < 0 || objIndex >= Map.Objects.Count) return null;
        return Data.ObjectTriggers.TryGetValue(Map.Objects[objIndex].Uid, out var m) && m.TryGetValue(triggerElement, out var f) ? f : null;
    }

    Dictionary<int, (int Owner, int Level, int SkillCount, int InTown)> HeroSnapshot() =>
        S.Heroes.Where(h => h.Alive).ToDictionary(h => h.Id, h => (h.Owner, h.Level, h.Skills.Count + h.Perks.Count, h.InTown));

    /// <summary>Translates engine events into the original's script triggers.</summary>
    void Dispatch(IEnumerable<AdvEvent> events, Dictionary<int, (int Owner, int Level, int SkillCount, int InTown)> before)
    {
        foreach (var e in events.ToList())
        {
            switch (e)
            {
                case DayStarted:
                    Fire(TriggerTypes.NewDay, "", Data.NewDayTrigger);
                    break;
                case TurnStarted ts when Game.Player(ts.Player) is { Human: false }:
                    _turnSegments += SegmentsPerComputerTurn;
                    break;
                case HeroHired hh when S.Heroes.Find(x => x.Id == hh.HeroId) is { } h:
                    Fire(TriggerTypes.PlayerAddHero, (h.Owner + 1).ToString(), null, h.DefId);
                    break;
                case HeroRemoved hr:
                    {
                        var h = S.Heroes.Find(x => x.Id == hr.HeroId);
                        if (h == null) break;
                        LuaValue killer = LuaValue.Nil;
                        if (hr.Reason.StartsWith("defeated_by:") && int.TryParse(hr.Reason["defeated_by:".Length..], out var kid) && S.Heroes.Find(x => x.Id == kid) is { } k)
                            killer = k.DefId;
                        Fire(TriggerTypes.PlayerRemoveHero, (h.Owner + 1).ToString(), null, h.DefId, killer);
                        if (Map.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Hero && o.Hero?.Shared?.Id == h.DefId) is { } ho)
                            if (ObjectTrigger(ho.Index, "LossTrigger") is { } lf) CallHandler(lf, new LuaValue[] { h.DefId });
                        break;
                    }
                case ObjectVisited ov when ov.Object >= 0 && ov.HeroId >= 0:
                    {
                        var h = S.Heroes.Find(x => x.Id == ov.HeroId);
                        var o = Map.Objects[ov.Object];
                        if (h != null && o.Name.Length > 0) Fire(TriggerTypes.ObjectTouch, o.Name, null, h.DefId, o.Name);
                        break;
                    }
                case ObjectCaptured oc when oc.Object >= 0 && oc.Object < Map.Objects.Count:
                    {
                        var o = Map.Objects[oc.Object];
                        int oldOwner = _lastOwners.TryGetValue(oc.Object, out var oo) ? oo : o.Owner;
                        _lastOwners[oc.Object] = oc.Player;
                        var capturer = S.Heroes.Where(h => h.Alive && h.Owner == oc.Player).FirstOrDefault(h => o.ActiveTiles.Contains(h.Pos) || h.Pos == o.Pos);
                        LuaValue heroName = capturer != null ? capturer.DefId : LuaValue.Nil;
                        if (o.Name.Length > 0)
                            Fire(TriggerTypes.ObjectCapture, o.Name, ObjectTrigger(o.Index, "CaptureTrigger"),
                                oldOwner + 1, oc.Player + 1, heroName, o.Name);
                        break;
                    }
                case HeroMoved hm:
                    OnHeroMoved(hm);
                    break;
                case BattleResolved br:
                    RecordCombat(br);
                    if (World.PendingCombat is { } pc)
                    {
                        World.PendingCombat = null;
                        if (pc.FinishTrigger.Length > 0) CallHandler(pc.FinishTrigger, new LuaValue[] { pc.Hero, LuaValue.Bool(br.WinnerSide == 0) });
                    }
                    Fire(TriggerTypes.CombatResults, "", null, World.SavedCombats.Count > 0 ? World.SavedCombats[^1].Index : 0);
                    break;
                case SkillLearned sl when S.Heroes.Find(x => x.Id == sl.HeroId) is { } h:
                    Fire(TriggerTypes.HeroAddSkill, h.DefId, null, Ids.Skills.Id(sl.Skill));
                    break;
            }
        }

        // Level-ups and town departures are detected by comparing hero state.
        foreach (var h in S.Heroes.Where(h => h.Alive))
        {
            if (!before.TryGetValue(h.Id, out var b)) continue;
            if (h.Level > b.Level) Fire(TriggerTypes.HeroLevelUp, h.DefId, null);
            if (b.InTown >= 0 && h.InTown < 0)
            {
                var town = Map.Objects[b.InTown];
                if (town.Name.Length > 0) Fire(TriggerTypes.TownHeroDeploy, town.Name, ObjectTrigger(town.Index, "HeroDeployTrigger"), h.DefId);
            }
        }
    }

    readonly Dictionary<int, int> _lastOwners = new();

    void OnHeroMoved(HeroMoved hm)
    {
        var h = S.Heroes.Find(x => x.Id == hm.HeroId);
        if (h == null) return;
        if (!World.HeroRegions.TryGetValue(h.Id, out var inside)) World.HeroRegions[h.Id] = inside = new List<string>();
        foreach (var tile in hm.Path.Skip(1))
        {
            var now = Data.Regions.Where(r => r.Contains(tile)).Select(r => r.Name).ToList();
            foreach (var left in inside.Except(now).ToList())
            {
                inside.Remove(left);
                Fire(TriggerTypes.RegionExit, left, Data.RegionByName.GetValueOrDefault(left)?.Exit, h.DefId);
            }
            foreach (var entered in now.Except(inside).ToList())
            {
                inside.Add(entered);
                var reg = Data.RegionByName.GetValueOrDefault(entered);
                Fire(TriggerTypes.RegionEnterWithoutStop, entered, reg?.EnterWithoutStop, h.DefId);
                if (tile == h.Pos) Fire(TriggerTypes.RegionEnterAndStop, entered, reg?.EnterAndStop, h.DefId);
            }
        }
    }

    void RecordCombat(BattleResolved br)
    {
        var sc = new SavedCombat { Index = World.SavedCombats.Count, Result = br.Result, Winner = br.WinnerSide };
        foreach (var side in br.Armies ?? Array.Empty<BattleSideRecord>())
            sc.Armies.Add(new SavedArmy
            {
                Player = side.Player, Hero = side.Hero,
                Stacks = side.Stacks.Select(s => new SavedStack { Creature = Ids.Creatures.Id(s.Creature), Count = s.Count, Died = s.Died }).ToList(),
            });
        World.SavedCombats.Add(sc);
    }

    // ---- IAdventureScriptHooks ---------------------------------------------------------------

    public bool IsTileBlocked(int player, Tile tile)
    {
        if (World.BlockedRegions.Count == 0) return false;
        foreach (var (name, players) in World.BlockedRegions)
        {
            if (!players.Contains(0) && !players.Contains(player + 1)) continue;
            if (Data.RegionByName.TryGetValue(name, out var r) && r.Contains(tile)) return true;
        }
        return false;
    }

    public bool InterceptVisit(HeroState hero, MapObject obj)
    {
        if (!World.DisabledObjects.ContainsKey(obj.Index)) return false;
        // Disabled objects are "touched" (OBJECT_TOUCH fires from the ObjectVisited event the engine
        // emits); heroes touching a disabled hero fire HERO_TOUCH. Nothing else happens: the
        // SetDisabledObjectMode mode only selects the cursor (CSetDisabledObjectModeCmd 0x0042a1d0).
        if (obj.Kind == ObjectKind.Hero && obj.Hero?.Shared?.Id is { } heroName)
            Fire(TriggerTypes.HeroTouch, heroName, null, hero.DefId, heroName);
        return true;
    }

    public bool StopMovementAt(HeroState hero, Tile from, Tile to)
    {
        foreach (var r in Data.Regions)
        {
            if (!r.Contains(to) || r.Contains(from)) continue;
            if (r.EnterAndStop.Length > 0 || World.Triggers.ContainsKey(Key(TriggerTypes.RegionEnterAndStop, r.Name))) return true;
        }
        return false;
    }
}
