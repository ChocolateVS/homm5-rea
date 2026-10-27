using H5.Combat;
using H5.Core.Data;
using H5.Core.IO;
using H5.Scripting.Lua;
using CombatHero = H5.Combat.HeroState;

namespace H5.Scripting.Runtime;

/// <summary>
/// Combat scripts (CombatScript of heroes/monsters, StartCombat scripts, combat tutorials): loads
/// scripts/combat-startup.lua, then the combat script; calls DoPrepare()/DoStart(); reports turns
/// and deaths through UnitMove("name")/UnitDeath("name") like the engine (strings at 0x00a1e044 /
/// 0x00a1df98); binds the combat API (registration table 0x00f32e90) and the combat-tutorial
/// API (0x00f33040). The battle itself stays in H5.Combat; scripts act on its public state.
/// </summary>
public sealed class CombatScripting : IBattleScriptSession
{
    public LuaState L { get; } = new() { ArgFormats = NativeFormats.Combat };
    public Battle Battle { get; }
    public BattleState S => Battle.State;
    public GameDatabase Db { get; }
    public EnumTables Ids { get; }
    public readonly List<ScriptEvent> Events = new();
    public readonly List<string> Log = new();
    public readonly List<string> Errors = new();
    public readonly SortedSet<string> Registered = new(StringComparer.Ordinal);
    /// <summary>Game variables shared with the adventure map (SetGameVar/GetGameVar).</summary>
    public Dictionary<string, string> GameVars { get; }
    public bool Started { get; private set; }
    public bool Paused { get; private set; }
    public bool AutoFinish { get; private set; } = true;
    public bool TutorialMode { get; private set; }
    /// <summary>Per side: MODE_NORMAL (0), MODE_MANUAL (1), MODE_AUTO (2).</summary>
    public readonly int[] ControlMode = new int[2];
    /// <summary>Set when a script asked to finish the combat (Finish(side)).</summary>
    public int? FinishedBy { get; private set; }

    readonly Dictionary<int, string> _names = new();
    readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    readonly Func<string, byte[]?> _readFile;
    readonly PakFileSystem? _game;
    int _nextName;

    public CombatScripting(GameDatabase db, Battle battle, EnumTables ids, Func<string, byte[]?> readFile,
        Dictionary<string, string>? gameVars = null, PakFileSystem? game = null)
    {
        Db = db;
        Battle = battle;
        Ids = ids;
        _readFile = readFile;
        _game = game;
        GameVars = gameVars ?? new Dictionary<string, string>(StringComparer.Ordinal);
        L.Output = Log.Add;
        L.ErrorReported = (msg, label) => Errors.Add($"{label}: {msg}");
        foreach (var u in S.Units) NameOf(u);
    }

    string? LoadScript(string path)
    {
        var b = _readFile(PakFileSystem.Normalize(path));
        return b == null ? null : ScriptText.FromBytes(b);
    }

    // ---- unit names ----------------------------------------------------------------------------

    const string HeroPrefix = "hero:";

    string NameOf(Unit u)
    {
        if (_names.TryGetValue(u.Id, out var n)) return n;
        var kind = u.Kind switch { UnitKind.WarMachine => "war_machine", UnitKind.Obstacle => "building", _ => "creature" };
        n = $"{(u.Side == 0 ? "attacker" : "defender")}-{kind}-{_nextName++}";
        Bind(u.Id, n);
        return n;
    }

    void Bind(int id, string name)
    {
        _names[id] = name;
        _byName[name] = id;
    }

    static string HeroName(CombatHero h) => h.HeroId.Length > 0 ? h.HeroId : $"{HeroPrefix}{h.Side}";

    /// <summary>
    /// A named unit: a creature/war machine/building id, or a hero (Actor.Hero(side)). The exe looks names up in the
    /// combat world (world vf120 → vf4(name)); dead stacks stay there (UnitDeath("name") handlers in combat-startup.lua
    /// still call GetUnitSide/GetUnitType on them), so <paramref name="includeDead"/> keeps killed stacks resolvable.
    /// </summary>
    bool TryResolve(string name, out int actor, bool includeDead = false)
    {
        if (_byName.TryGetValue(name, out actor) && S.Unit(actor) is { } u
            && (u.Alive || includeDead && !u.Removed && !u.Reserve)) return true;
        for (int side = 0; side < 2; side++)
            if (S.Heroes[side].Present && HeroName(S.Heroes[side]) == name) { actor = Actor.Hero(side); return true; }
        actor = 0;
        return false;
    }

    /// <summary>Any unit (hero, creature, war machine, building, spell spawn); exe text "Unit \"%s\" is not exists".</summary>
    int Resolve(HostContext c, int i) => ResolveAs(c, i, "Unit \"{0}\" is not exists");

    /// <summary>Arena lookup (ICombatArenaCommon vfc) used by commandDefend/playAnimation/setATB.</summary>
    int ResolveOnArena(HostContext c, int i) => ResolveAs(c, i, "There is no unit on combat arena with name \"{0}\"");

    int ResolveAs(HostContext c, int i, string error)
    {
        var name = c.Str(i);
        if (!TryResolve(name, out var a, includeDead: true)) throw c.Error(string.Format(error, name));
        return a;
    }

    /// <summary>A unit of a given kind (exe unit vf6c creature / vf74 hero / vf7c war machine / vf84 building).</summary>
    Unit KindArg(HostContext c, int i, int type, string what)
    {
        var name = c.Str(i);
        if (!TryResolve(name, out var a, includeDead: true) || UnitType(a) != type) throw c.Error($"{what} \"{name}\" is not exists");
        return S.Unit(a)!;
    }

    Unit UnitArg(HostContext c, int i)
    {
        int a = Resolve(c, i);
        return Actor.IsHero(a) ? throw c.Error($"Creature \"{c.Str(i)}\" is not exists") : S.Unit(a)!;
    }

    static bool IsSiegeBuilding(Unit u) => u.Kind == UnitKind.Obstacle && u.CreatureId.StartsWith("SIEGE_", StringComparison.Ordinal);

    /// <summary>
    /// GetUnitType (0x005679f0): HERO 0 (vf74), CREATURE 1 (vf6c), WAR_MACHINE 2 (vf7c), BUILDING 3 (vf84: siege
    /// fortifications), SPELL_SPAWN 4 (vf8c: spell-summoned objects such as the Arcane Crystal or Blade Barrier).
    /// </summary>
    int UnitType(int actor) => Actor.IsHero(actor) ? 0 : S.Unit(actor)! switch
    {
        { Kind: UnitKind.WarMachine } => 2,
        { Kind: UnitKind.Obstacle } u => IsSiegeBuilding(u) ? 3 : 4,
        _ => 1,
    };

    /// <summary>BUILDING_* ids of combat-startup.lua (WALL 1 … MAGIC_WALL 7) by our siege building type.</summary>
    static readonly string[] BuildingTypes = { "", "WALL", "GATE", "LEFT_TOWER", "BIG_TOWER", "MOAT", "RIGHT_TOWER", "MAGIC_WALL" };

    int SideOf(int actor) => Actor.IsHero(actor) ? Actor.HeroSide(actor) : S.Unit(actor)!.Side;

    LuaTable Names(IEnumerable<string> names)
    {
        var t = L.NewTable();
        int i = 0;
        foreach (var n in names) t.Set(i++, n);
        return t;
    }

    void Emit(string kind, params (string Key, object? Value)[] data) =>
        Events.Add(new ScriptEvent(kind, data.ToDictionary(d => d.Key, d => Convert.ToString(d.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "")));

    void Reg(string name, HostCall call)
    {
        L.Register(name, call);
        Registered.Add(name);
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    /// <summary>
    /// Whether the engine runs combat scripts at all (CCombatArenaScreen ctor FUN_0054c2f0 at 0x0054ce2a: the game
    /// mode flags returned by combat-game vf14 must both be clear, FUN_006b9de0). These are the multiplayer and hotseat
    /// flags (same test refuses flybys with "Flyby`s are not allowed in multiplayer or hotseat"); otherwise the exe logs
    /// "Warning: scripts are not allowed" and neither combat-startup.lua, the combat script, DoPrepare/DoStart nor
    /// UnitMove/UnitDeath run (the turn continuations are called directly). MMH55's DLL NOPs the JZ at 0x0054ce31.
    /// </summary>
    public static bool ScriptsAllowed(bool multiplayer, bool hotseat) => !multiplayer && !hotseat;

    /// <summary>Tutorial arena (FUN_006a3fb0): also runs "createTutorialAliases();" after "createCombatAliases();".</summary>
    public bool TutorialArena { get; init; }

    /// <summary>
    /// Registers the API, runs combat-startup.lua, "createCombatAliases();" and the battle's combat script
    /// (FUN_0053e810), then DoPrepare() (the exe posts it from the arena update, state 0x4000, FUN_0056fb70).
    /// </summary>
    public void Load(string? scriptPath, string? scriptSource)
    {
        BaseLibrary.Register(L, LoadScript, () => (uint)S.Rng.NextU64());
        RegisterApi();
        var startup = LoadScript("scripts/combat-startup.lua");
        if (startup != null) L.DoString(startup, "scripts/combat-startup.lua");
        foreach (var alias in TutorialArena ? new[] { "createCombatAliases", "createTutorialAliases" } : new[] { "createCombatAliases" })
            if (L.GetGlobal(alias).IsFunction) L.CallGlobal(alias);
        // FUN_0053e810: engine vf8 (one scheduler pass, FUN_004c0720) between the aliases and the combat script.
        L.Step();
        if (scriptSource != null) L.DoString(scriptSource, scriptPath ?? "CombatScript");
        if (L.GetGlobal("DoPrepare").IsFunction) L.CallGlobal("DoPrepare");
    }

    /// <summary>Called when the battle proper begins (after tactics): DoStart() and combatStarted() = true.</summary>
    public void Start()
    {
        if (Started) return;
        Started = true;
        if (L.GetGlobal("DoStart").IsFunction) L.CallGlobal("DoStart");
    }

    public void Tick(int segments = 1)
    {
        for (int i = 0; i < segments; i++) L.Step();
    }

    /// <summary>Feeds the engine's events to the scripts (turns, deaths, phase changes).</summary>
    public void OnEvents(IEnumerable<BattleEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case PhaseChanged { Phase: BattlePhase.Combat }:
                    Start();
                    break;
                case TurnStarted ts:
                    if (!Started) Start();
                    var actorName = Actor.IsHero(ts.Actor) ? HeroName(S.Heroes[Actor.HeroSide(ts.Actor)])
                        : S.Unit(ts.Actor) is { } u ? NameOf(u) : null;
                    if (actorName != null && L.GetGlobal("UnitMove").IsFunction) L.CallGlobal("UnitMove", actorName);
                    break;
                case UnitSummoned us when S.Unit(us.UnitId) is { } su:
                    NameOf(su);
                    break;
                case UnitDied ud when _names.TryGetValue(ud.UnitId, out var dead):
                    if (L.GetGlobal("UnitDeath").IsFunction) L.CallGlobal("UnitDeath", dead);
                    break;
            }
        }
    }

    /// <summary>Applies a battle command and lets the scripts react to its events.</summary>
    public ApplyResult Apply(BattleCommand cmd)
    {
        var r = Battle.Apply(cmd);
        if (r.Ok) OnEvents(r.Events);
        if (SegmentsPerCommand > 0) Tick(SegmentsPerCommand);
        return r;
    }

    /// <summary>
    /// Battle events of commands the scripts issued (command*/UnitCast*Spell), for a client that animates
    /// events: the combat screen takes them after each tick or command (<see cref="TakeScriptEvents"/>).
    /// </summary>
    public readonly List<BattleEvent> ScriptBattleEvents = new();

    public List<BattleEvent> TakeScriptEvents()
    {
        var l = ScriptBattleEvents.ToList();
        ScriptBattleEvents.Clear();
        return l;
    }

    /// <summary>Script segments run after each command (headless drivers; a real client ticks by time).</summary>
    public int SegmentsPerCommand { get; set; }

    void IBattleScriptSession.Begun(IEnumerable<BattleEvent> events) => OnEvents(events);

    // ---- API ---------------------------------------------------------------------------------

    void RegisterApi()
    {
        Reg("EnableDynamicBattleMode", c => Emit("dynamic_battle", ("enable", c.Bool(0))));
        // ICombatArena vf0 (0x00552870): sets CCombatArenaScreen +0x4cc (default 1), which the arena update passes to
        // the combat-event presentation sequencer (0x0054b2f0) — camera only, no game state.
        Reg("EnableCinematicCamera", c => Emit("cinematic_camera", ("enable", c.Bool(0))));
        Reg("SetControlMode", c =>
        {
            int side = c.Int(0), mode = c.Int(1);
            if (side is < 0 or > 1) throw c.Error($"Invalid side #{side}");
            if (mode is < 0 or > 2) throw c.Error($"Invalid control mode #{mode}");
            if (S.AiControlled[side]) throw c.Error($"Side #{side} player is not a human");
            ControlMode[side] = mode;
            Emit("control_mode", ("side", side), ("mode", mode));
        });
        Reg("GetHost", c =>
        {
            int side = c.Int(0);
            if (side is < 0 or > 1) throw c.Error($"Invalid side #{side}");
            c.Return(S.AiControlled[side] ? 1 : 0);   // HUMAN 0 / COMPUTER 1; no player → 1
        });
        Reg("EnableAutoFinish", c => AutoFinish = c.Bool(0));
        Reg("combatEnableFinish", c => AutoFinish = c.Bool(0));
        Reg("Finish", c =>
        {
            int side = c.Int(0);
            if (side is < 0 or > 1) throw c.Error($"Invalid side #{side}");
            FinishedBy = side;
            if (S.Phase != BattlePhase.Finished)
            {
                S.Phase = BattlePhase.Finished;
                S.Winner = FinishedBy is 0 or 1 ? FinishedBy : null;
                S.EndReason = "script";
            }
            Emit("finish", ("winner", FinishedBy));
        });
        Reg("Break", c => Emit("break_combat"));

        Reg("GetUnitSide", c => c.Return(SideOf(Resolve(c, 0))));
        Reg("GetUnitType", c => c.Return(UnitType(Resolve(c, 0))));
        Reg("GetUnits", c =>
        {
            int side = c.Int(0), type = c.Int(1);
            if (side is < 0 or > 1) throw c.Error($"Invalid side id #{side}");
            if (type is < 0 or > 4) throw c.Error($"Invalid type id #{type}");
            var names = new List<string>();
            if (type == 0) { if (S.Heroes[side].Present) names.Add(HeroName(S.Heroes[side])); }
            else names.AddRange(S.Units.Where(u => u.Alive && u.Side == side && UnitType(u.Id) == type).Select(NameOf));
            c.Return(Names(names));
        });
        Reg("unitNames", c => c.Return(Names(S.Units.Where(u => u.Alive).Select(NameOf))));
        Reg("exist", c => c.ReturnBool(TryResolve(c.Str(0), out _)));
        HostCall position = c =>
        {
            int a = Resolve(c, 0);
            if (Actor.IsHero(a)) { int side = Actor.HeroSide(a); c.Return(side == 0 ? -1 : S.Width, S.Height / 2); return; }
            var u = S.Unit(a)!;
            c.Return(u.Pos.X, u.Pos.Y);
        };
        Reg("GetUnitPosition", position);
        Reg("pos", position);
        Reg("GetHeroName", c =>
        {
            var name = c.Str(0);
            if (!TryResolve(name, out var a, includeDead: true) || !Actor.IsHero(a)) throw c.Error($"Hero \"{name}\" is not exists");
            c.Return(S.Heroes[Actor.HeroSide(a)].HeroId);
        });
        Reg("GetCreatureType", c => c.Return(Ids.Creatures.Id(KindArg(c, 0, 1, "Creature").CreatureId)));
        Reg("GetCreatureNumber", c => c.Return(KindArg(c, 0, 1, "Creature").Count));
        Reg("GetWarMachineType", c =>
        {
            var u = KindArg(c, 0, 2, "War machine");
            c.Return(Array.IndexOf(EnumTables.WarMachines, u.CreatureId) is var i and > 0 ? i : 0);
        });
        Reg("GetBuildingType", c =>
        {
            var u = KindArg(c, 0, 3, "Building");
            c.Return(Array.IndexOf(BuildingTypes, u.CreatureId["SIEGE_".Length..]) is var i and > 0 ? i : 0);
        });

        // AddCreature/SummonCreature (0x00568d90/0x00569180): side, type, count, x=-1, y=-1, summoned (false/true), name="".
        Reg("AddCreature", c => AddUnit(c, c.Str(6), sideArg: 0, typeArg: 1, countArg: 2, xArg: 3, yArg: 4, summoned: c.Bool(5), exe: true));
        Reg("SummonCreature", c => AddUnit(c, c.Str(6), sideArg: 0, typeArg: 1, countArg: 2, xArg: 3, yArg: 4, summoned: c.Bool(5), exe: true));
        Reg("addUnit", c => AddUnit(c, c.Str(0), sideArg: 2, typeArg: 1, countArg: 3, xArg: 4, yArg: 5, summoned: false));
        Reg("removeUnit", c =>
        {
            // 0x0056b1d0: an unknown name is only logged (no script error); otherwise a remove command is posted.
            var name = c.Str(0);
            if (!TryResolve(name, out var a, includeDead: true)) { Log.Add("There is no unit on combat arena with name " + name); return; }
            if (Actor.IsHero(a)) { Emit("unit_removed", ("unit", name)); return; }
            var u = S.Unit(a)!;
            u.Removed = true;
            Emit("unit_removed", ("unit", NameOf(u)));
        });
        Reg("RemoveAllUnits", c =>
        {
            foreach (var u in S.Units.Where(u => u.Alive && u.Kind != UnitKind.Obstacle)) u.Removed = true;
            Emit("all_units_removed");
        });
        Reg("displace", c =>
        {
            var u = UnitArg(c, 0);
            var to = new Cell(c.Int(1), c.Int(2));
            if (u.Kind != UnitKind.Creature) throw c.Error($"Unit {NameOf(u)} (dbid {u.Id}) cannot be displaced (it's not a Creature)");
            if (to.X < 0 || to.Y < 0 || to.X + u.Size > S.Width || to.Y + u.Size > S.Height)
                throw c.Error($"Position ({to.X},{to.Y}) is outside of arena bounds [arg3-4]");
            u.Pos = to;
            Emit("unit_displaced", ("unit", NameOf(u)), ("x", to.X), ("y", to.Y));
        });
        Reg("setATB", c =>
        {
            int a = ResolveOnArena(c, 0);
            if (Actor.IsHero(a)) S.Heroes[Actor.HeroSide(a)].Atb = c.Number(1);
            else S.Unit(a)!.Atb = c.Number(1);
        });

        // Mana of any unit (unit vf234 / vf238), heroes included (MMH55 sets hero mana around its free casts).
        Reg("GetUnitManaPoints", c => { int a = Resolve(c, 0); c.Return(Actor.IsHero(a) ? S.Heroes[Actor.HeroSide(a)].Mana : S.Unit(a)!.Mana); });
        Reg("GetUnitMaxManaPoints", c => { int a = Resolve(c, 0); c.Return(Actor.IsHero(a) ? S.Heroes[Actor.HeroSide(a)].MaxMana : S.Unit(a)!.MaxMana); });
        Reg("SetUnitManaPoints", c =>
        {
            int a = Resolve(c, 0), mana = c.Int(1);
            if (mana < 0) throw c.Error($"Invalid mana points number {mana}");
            if (Actor.IsHero(a)) S.Heroes[Actor.HeroSide(a)].Mana = mana;
            else S.Unit(a)!.Mana = mana;
        });
        Reg("UnitCastGlobalSpell", c =>
        {
            int a = Resolve(c, 0);
            if (!CastSpell(a, c.Int(1), target: null, cell: null)) throw c.Error($"Unit \"{c.Str(0)}\" can`t cast spell #{c.Int(1)}");
        });
        Reg("UnitCastAreaSpell", c =>
        {
            int a = Resolve(c, 0);
            if (!CastSpell(a, c.Int(1), target: null, cell: new Cell(c.Int(2), c.Int(3))))
                throw c.Error($"Unit \"{c.Str(0)}\" can`t cast spell #{c.Int(1)} at ({c.Int(2)},{c.Int(3)})");
        });
        Reg("UnitCastAimedSpell", c =>
        {
            int a = Resolve(c, 0), t = Resolve(c, 2);
            if (!CastSpell(a, c.Int(1), target: t, cell: null))
                throw c.Error($"Unit \"{c.Str(0)}\" can`t cast spell #{c.Int(1)} on unit \"{c.Str(2)}\"");
        });

        Reg("commandDefend", c =>
        {
            int a = ResolveOnArena(c, 0);
            Command(c, new DefendCmd(a) { Side = SideOf(a) });
        });
        Reg("commandMove", c =>
        {
            var u = UnitArg(c, 0);
            Command(c, new MoveCmd(u.Id, new Cell(c.Int(1), c.Int(2))) { Side = u.Side });
        });
        Reg("commandMoveAttack", c =>
        {
            var u = UnitArg(c, 0);
            int target = Resolve(c, 1);
            Cell? from = c.Has(2) ? new Cell(c.Int(2), c.Int(3)) : null;
            Command(c, new AttackCmd(u.Id, target, from) { Side = u.Side });
        });
        Reg("commandShot", c =>
        {
            var u = UnitArg(c, 0);
            Command(c, new ShootCmd(u.Id, Resolve(c, 1)) { Side = u.Side });
        });
        Reg("commandDoSpecial", c =>
        {
            var u = UnitArg(c, 0);
            int abilityId = c.Int(1);
            Cell? cell = c.Has(2) ? new Cell(c.Int(2), c.Int(3)) : null;
            var ability = Db.Abilities.Keys.ElementAtOrDefault(abilityId) ?? abilityId.ToString();
            Command(c, new UseAbilityCmd(u.Id, ability, null, cell) { Side = u.Side });
        });
        Reg("commandDoSpell", c =>
        {
            int a = Resolve(c, 0);
            var spell = Ids.Spells.Name(c.Int(1)) ?? throw c.Error($"Unit \"{c.Str(0)}\" can`t cast spell #{c.Int(1)}");
            int? target = null;
            Cell? cell = null;
            if (c.Has(2) && c.Arg(2).IsString) target = Resolve(c, 2);
            else if (c.Has(2)) cell = new Cell(c.Int(2), c.Int(3));
            Command(c, new CastSpellCmd(a, spell, target, cell) { Side = SideOf(a) });
        });

        Reg("combatStarted", c => c.ReturnBool(Started));
        Reg("combatReadyPerson", c =>
        {
            // 0x0056cd60: nothing while the battle is finished (world vf98) or not started (vf124); else the ready unit.
            if (Started && S.Phase != BattlePhase.Finished && S.Active is { } a) c.Return(Actor.IsHero(a) ? HeroName(S.Heroes[Actor.HeroSide(a)]) : S.Unit(a) is { } u ? NameOf(u) : LuaValue.Nil);
            else c.Return(LuaValue.Nil);
        });
        Reg("combatSetPause", c => { Paused = c.Bool(0); Emit("pause", ("on", Paused)); });
        Reg("combatPlayEmotion", c => Emit("emotion", ("unit", c.Str(0)), ("emotion", c.Str(1))));
        Reg("playAnimation", c =>
        {
            ResolveOnArena(c, 0);
            int action = c.Int(2);
            if (action is < 0 or > 5) throw c.Error($"Non valid action type {action}");
            Emit("unit_animation", ("unit", c.Str(0)), ("animation", c.Str(1)), ("action", action));
        });
        Reg("showHighlighting", c => Emit("highlight", ("unit", c.Str(0)), ("on", c.Bool(1))));
        Reg("postEvent", c => Emit("post_event", ("event", c.Str(0)), ("arg", c.Str(1))));
        Reg("GetRagePoints", c => { var u = RageUnit(c); c.Return(u?.RagePoints ?? 0); });
        Reg("GetRageLevel", c => { var u = RageUnit(c); c.Return(u?.RageLevel ?? 0); });

        // Game variables (0x0056a3f0/0x0056a660): ICombatArenaCommon vf40 = the arena's table (+0x2e8), which is the
        // adventure map's own table object (IAdventureMap vf8) handed over by reference when the adventure starts the
        // battle (0x005a789b → CCreateCombatArenaScreen +0x20) — shared, never copied; a battle created without one
        // (FUN_00547310) gets a fresh empty table (FUN_006bca50).
        Reg("GetGameVar", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Game variable name is not specified");
            c.Return(GameVars.TryGetValue(name, out var v) ? v : "");
        });
        Reg("SetGameVar", c =>
        {
            string name = c.Str(0), value = c.Str(1);
            if (name.Length == 0) throw c.Error("Game variable name is not specified");
            if (value.Length == 0) GameVars.Remove(name);   // table vfc (remove) for an empty value, else vf8 (set)
            else GameVars[name] = value;
        });
        Reg("MessageBox", c => Emit("message_box", ("text", c.Str(0)), ("callback", c.Str(1))));
        Reg("ShowFlyingSign", c => Emit("flying_sign", ("text", c.Str(0)), ("unit", c.Str(1)), ("time", c.Has(2) ? c.Number(2) : 1.0)));
        Reg("Play2DSound", c => Emit("sound_2d", ("sound", c.Str(0))));
        Reg("Play3DSound", c => Emit("sound_3d", ("sound", c.Str(0)), ("x", c.Number(1)), ("y", c.Number(2))));

        // Shared functions (0x00f39a0c) and the combat tutorial (0x00f33040, 0x00f3e004).
        Reg("GetDifficulty", c => c.Return(GameVars.TryGetValue("__difficulty", out var d) && int.TryParse(d, out var i) ? i : 1));
        Reg("Save", c => Emit("save_game", ("file", c.Str(0))));
        Reg("Load", c => Emit("load_game", ("file", c.Str(0))));
        Reg("GetMapDataPath", c => c.Return(GameVars.TryGetValue("__map_path", out var p) ? p : "/"));
        Reg("GetSoundTimeInSleeps", c => c.Return(0));
        Reg("toggleTutorialMode", c => TutorialMode = !TutorialMode);
        Reg("showMessage", c => Emit("tutorial_message", ("text", c.Str(0))));
        Reg("clearMessage", c => Emit("tutorial_clear"));
        Reg("changeSubject", c => Emit("tutorial_subject", ("subject", c.Str(0))));
        Reg("subject", c => c.Return(""));
        Reg("shots", c => c.Return(UnitArg(c, 0).Shots));
        Reg("shotsNumber", c => c.Return(UnitArg(c, 0).MaxShots));
        Reg("IsTutorialEnabled", c => c.ReturnBool(TutorialMode));
        Reg("IsTutorialItemEnabled", c => c.ReturnBool(TutorialMode));
        Reg("TutorialActivateHint", c => Emit("tutorial_hint", ("id", c.Str(0))));
        Reg("TutorialSetBlink", c => Emit("tutorial_blink", ("id", c.Str(0)), ("on", c.Bool(1))));
        Reg("TutorialMessageBox", c => Emit("tutorial_message_box", ("id", c.Str(0))));
        Reg("IsTutorialMessageBoxOpen", c => c.ReturnBool(false));
    }

    /// <summary>
    /// <paramref name="exe"/>: AddCreature/SummonCreature checks in the exe's order (side, creature type, count, cell
    /// x ∈ {-1} ∪ [0,width), y ∈ {-1} ∪ [0,height)) with its texts; an empty name means an engine-chosen name.
    /// </summary>
    void AddUnit(HostContext c, string? name, int sideArg, int typeArg, int countArg, int xArg, int yArg, bool summoned, bool exe = false)
    {
        int side = c.Int(sideArg);
        string cr;
        int count;
        if (exe)
        {
            if (side is < 0 or > 1) throw c.Error($"Invalid side #{side}");
            int type = c.Int(typeArg);
            cr = (type > 0 ? Ids.Creatures.Name(type) : null) ?? throw c.Error($"Invalid creature type #{type}");
            count = c.Int(countArg);
            if (count < 1) throw c.Error($"Invalid creature number #{count}");
            int x = c.Int(xArg), y = c.Int(yArg);
            if (!(x == -1 || x >= 0 && x < S.Width) || !(y == -1 || y >= 0 && y < S.Height)) throw c.Error($"Invalid cell ({x},{y})");
            if (name == "") name = null;
        }
        else
        {
            if (side is < 0 or > 1) throw c.Error($"Invalid side ({side})");
            cr = Ids.Creatures.Name(c.Int(typeArg)) ?? throw c.Error($"Invalid creature type ({c.Int(typeArg)})");
            count = Math.Max(1, c.Int(countArg));
            if (name != null && _byName.ContainsKey(name)) throw c.Error($"Unit with name \"{name}\" already exist in combat arena");
        }
        Cell pos;
        if (c.Has(xArg) && c.Int(xArg) >= 0 && c.Has(yArg) && c.Int(yArg) >= 0) pos = new Cell(c.Int(xArg), c.Int(yArg));
        else pos = FreeCell(side);
        if (S.At(pos) != null) pos = FreeCell(side);
        var u = BattleSetup.AddCreature(Db, S, side, cr, count, pos, summoned);
        if (name != null) Bind(u.Id, name); else NameOf(u);
        Emit("unit_added", ("unit", NameOf(u)), ("creature", cr), ("count", count), ("side", side), ("x", pos.X), ("y", pos.Y));
    }

    Cell FreeCell(int side)
    {
        int[] cols = side == 0 ? Enumerable.Range(0, S.Width).ToArray() : Enumerable.Range(0, S.Width).Reverse().ToArray();
        foreach (var x in cols)
            for (int y = 0; y < S.Height; y++)
            {
                var cell = new Cell(x, y);
                if (S.At(cell) == null && !S.Obstacles.Contains(cell)) return cell;
            }
        return new Cell(side == 0 ? 0 : S.Width - 1, 0);
    }

    /// <summary>
    /// UnitCast*Spell: the exe validates the cast (FUN_00566940 → FUN_00982010) and posts it; a refused cast is the
    /// script error the caller raises. Caster may be a hero or any unit.
    /// </summary>
    bool CastSpell(int caster, int spellId, int? target, Cell? cell)
    {
        var spell = Ids.Spells.Name(spellId);
        if (spell == null) return false;
        var cmd = new CastSpellCmd(caster, spell, target, cell) { Side = SideOf(caster) };
        var r = Battle.Apply(cmd);
        if (!r.Ok) { Log.Add($"script command rejected: {cmd.GetType().Name}: {r.Error}"); return false; }
        Emit("script_command", ("command", cmd.GetType().Name));
        ScriptBattleEvents.AddRange(r.Events);
        OnEvents(r.Events);
        return true;
    }

    /// <summary>
    /// GetRagePoints/GetRageLevel: any unit ("Unit \"%s\" is not exists"); without rage (FUN_008a16b0: a creature whose
    /// army hero is a Stronghold hero) the exe only logs "Unit &lt;name&gt; has no rage points" and returns 0.
    /// </summary>
    Unit? RageUnit(HostContext c)
    {
        int a = Resolve(c, 0);
        if (!Actor.IsHero(a) && S.Unit(a) is { Kind: UnitKind.Creature } u && S.Heroes[u.Side] is { Present: true, Town: Towns.Stronghold })
            return u;
        Log.Add($"Unit {c.Str(0)} has no rage points");
        return null;
    }

    void Command(HostContext c, BattleCommand cmd)
    {
        var r = Battle.Apply(cmd);
        if (!r.Ok) { Log.Add($"script command rejected: {cmd.GetType().Name}: {r.Error}"); return; }
        Emit("script_command", ("command", cmd.GetType().Name));
        ScriptBattleEvents.AddRange(r.Events);
        OnEvents(r.Events);
    }
}
