using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

// Adventure-map script API (registration tables 0x00f34c28 and 0x00f35000 in H5_Game.exe, 212
// functions). Semantics follow the official reference shipped with the game (Editor
// Documentation/HOMM5_Script_Functions.pdf and HOMM5_A2_Script_Functions.pdf); error texts are
// the engine's own strings. Engine errors abort the calling thread (lua_error), like Lua errors.
public sealed partial class AdventureScripting
{
    /// <summary>Names of every engine function this host registers (for coverage reports).</summary>
    public readonly SortedSet<string> Registered = new(StringComparer.Ordinal);

    void Reg(string name, HostCall call)
    {
        L.Register(name, call);
        Registered.Add(name);
    }

    // ---- argument helpers ----------------------------------------------------------------------

    HeroState Hero(HostContext c, int i)
    {
        var name = c.Str(i);
        return Game.HeroByNameAnywhere(name) ?? throw c.Error($"Hero \"{name}\" doesn't exist");
    }

    HeroState? HeroOrNull(HostContext c, int i) => Game.HeroByName(c.Str(i));

    /// <summary>A named map object; heroes are objects too (returned via <paramref name="hero"/>).</summary>
    MapObject? ObjOrNull(HostContext c, int i, out HeroState? hero)
    {
        var name = c.Str(i);
        hero = Game.HeroByName(name);
        return hero != null ? null : Game.ObjectByName(name);
    }

    MapObject Obj(HostContext c, int i)
    {
        var name = c.Str(i);
        return Game.ObjectByName(name) ?? throw c.Error($"Object \"{name}\" is not exists");
    }

    TownState Town(HostContext c, int i)
    {
        var name = c.Str(i);
        var o = Game.ObjectByName(name);
        return (o != null ? S.Town(o.Index) : null) ?? throw c.Error($"Town \"{name}\" is not exists");
    }

    MapRegion Region(HostContext c, int i)
    {
        var name = c.Str(i);
        return Data.RegionByName.TryGetValue(name, out var r) ? r : throw c.Error($"Region \"{name}\" is not exists");
    }

    /// <summary>Script player id (1..8) → engine player index; validates like the original.</summary>
    int PlayerArg(HostContext c, int i)
    {
        int p = c.Int(i);
        if (p < 1 || p > 8) throw c.Error($"Incorrect player ID ({p}), must be -1 or defined constant!");
        return p - 1;
    }

    static int ScriptPlayer(int engineIndex) => engineIndex < 0 ? 0 : engineIndex + 1;

    string CreatureName(HostContext c, int i)
    {
        int id = c.Int(i);
        return Ids.Creatures.Name(id) is { } n && id > 0 ? n : throw c.Error($"Invalid monster type id ({id})");
    }

    Tile TileArg(HostContext c, int ix, int iy, int ifloor, int defaultFloor = 0) =>
        new(c.Int(ix), c.Int(iy), c.Has(ifloor) && c.Int(ifloor) >= 0 ? c.Int(ifloor) : defaultFloor);

    LuaTable StringArray(IEnumerable<string> names)
    {
        // Engine arrays are 0-based (length() in common.lua counts from 0) and built in order.
        var t = L.NewTable();
        int i = 0;
        foreach (var n in names) t.Set(i++, n);
        return t;
    }

    Tile PositionOf(MapObject o) => o.Pos;

    // ---- registration --------------------------------------------------------------------------

    void RegisterAdventureApi()
    {
        // advmap overrides the base random(): it uses the game's synchronised generator.
        // 0x005dfc30: top < 1 is an error; otherwise IAdventureMap vf0xc(top).
        Reg("random", c =>
        {
            int top = c.Int(0);
            if (top < 1) throw c.Error("Random`s top must be positive");
            c.Return((double)(int)((uint)S.Rng.NextU64() % (uint)top));
        });

        RegisterGameApi();
        RegisterHeroApi();
        RegisterObjectApi();
        RegisterTownApi();
        RegisterPresentationApi();
        RegisterAiApi();
        RegisterSharedApi();
    }

    void RegisterGameApi()
    {
        Reg("GetMaxFloor", c => c.Return(Map.HasUnderground ? 1 : 0));
        Reg("ExitGame", c => Emit("exit_game"));
        Reg("Win", c => WinFor(EndGamePlayer(c, win: true)));
        Reg("WinTeam", c =>
        {
            int team = c.Int(0) - 1;
            Change(() => Game.ScriptEndGame(team, "script"));
        });
        Reg("Loose", c => LoseFor(EndGamePlayer(c, win: false)));
        // 0x005d7170: day counter d (0-based): DAY d+1, WEEK (d/7)%4+1 (week of the month),
        // MONTH (d/28)%12+1, DAY_OF_WEEK d%7+1.
        Reg("GetDate", c =>
        {
            int type = c.Int(0), d = S.Day - 1;
            c.Return(type switch
            {
                0 => d + 1,
                1 => d / 7 % 4 + 1,
                2 => d / 28 % 12 + 1,
                3 => d % 7 + 1,
                _ => throw c.Error($"Incorrect date type ID ({type})"),
            });
        });
        // 0x005d73d0: the player whose turn it is, -1 when there is none.
        Reg("GetCurrentPlayer", c => c.Return(Game.Player(S.CurrentPlayer) != null ? S.CurrentPlayer + 1 : -1));
        // 0x005d75c0 / 0x005d7810: player turn status vf0x44 == 0 (current) / == 2 (ghost mode, not modelled).
        Reg("IsPlayerCurrent", c => c.ReturnBool(TurnPlayerArg(c) == S.CurrentPlayer));
        Reg("IsPlayerInGhostMode", c => { TurnPlayerArg(c); c.ReturnBool(false); });
        // 0x005d7cb0: the team has players and every active one (state 1) is on its turn.
        Reg("IsTeamCurrent", c =>
        {
            int team = c.Int(0) - 1;
            var members = S.Players.Where(p => p.Team == team).ToList();
            c.ReturnBool(members.Count > 0 && !members.Any(p => PlayerStateOf(p.Index) == 1 && p.Index != S.CurrentPlayer));
        });
        Reg("IsPlayerWaitingForTurn", c => c.ReturnBool(PlayerArg(c, 0) != S.CurrentPlayer));
        Reg("IsPlayerLost", c => c.ReturnBool(Game.Player(PlayerArg(c, 0)) is { Alive: false }));
        Reg("CanShowToPlayer", c => c.ReturnBool(Game.Player(PlayerArg(c, 0)) is { Human: true }));
        Reg("GetTurnTimeLeft", c => c.Return(-1));
        Reg("GetAllNames", c =>
        {
            int filter = c.Has(0) ? c.Int(0) : 0;
            IEnumerable<string> names = filter switch
            {
                0 => S.Heroes.Where(h => h.Alive).Select(h => h.DefId),
                1 => Map.Objects.Where(o => o.Name.Length > 0 && !(S.Objects.TryGetValue(o.Index, out var os) && os.Removed)).Select(o => o.Name),
                2 => Data.Regions.Select(r => r.Name),
                3 => World.Objectives.Keys.Select(k => k.Split('|')[0]).Distinct(),
                _ => Enumerable.Empty<string>(),
            };
            c.Return(string.Join(" ", names));
        });
        Reg("GetDifficulty", c => c.Return(S.Difficulty));
        // 0x005dea90 / 0x005decb0: an empty name is an error; setting "" removes the variable.
        Reg("GetGameVar", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Game variable name is not specified");
            c.Return(World.GameVars.TryGetValue(name, out var v) ? v : "");
        });
        Reg("SetGameVar", c =>
        {
            string name = c.Str(0), value = c.Str(1);
            if (name.Length == 0) throw c.Error("Game variable name is not specified");
            if (value.Length == 0) World.GameVars.Remove(name);
            else World.GameVars[name] = value;
        });
        Reg("Save", c => Emit("save_game", ("file", c.Str(0))));
        Reg("Load", c => Emit("load_game", ("file", c.Str(0))));
        Reg("GetCurrentMoonWeek", c => c.Return(MoonWeekId(S.MoonWeek)));
        Reg("GetTerrainSize", c => c.Return(Map.TilesX, Map.TilesY));
        // 0x005e11a0: floor first, then x/y ranges (the messages lack the closing parenthesis).
        Reg("IsTilePassable", c =>
        {
            int x = c.Int(0), y = c.Int(1);
            var f = Map.Floor(c.Int(2)) ?? throw c.Error("Can`t run: cant get ground floor");
            if (x < 0 || x >= Map.TilesX) throw c.Error($"X is out of range (must be in [0,{Map.TilesX - 1}], now is {x}");
            if (y < 0 || y >= Map.TilesY) throw c.Error($"Y is out of range (must be in [0,{Map.TilesY - 1}], now is {y}");
            c.ReturnBool(!f.IsBlocked(x, y));
        });
        Reg("WarpToMap", c => Emit("warp_to_map", ("map", c.Str(0))));
        Reg("WarpHeroExp", c => Emit("warp_hero_exp", ("hero", c.Str(0))));
        Reg("GetPlayerSelectedCampaignBonusIndex", c => c.Return(World.GameVars.TryGetValue("__campaign_bonus", out var v) && int.TryParse(v, out var i) ? i : 0));

        // Players (table 0x00f34c28)
        Reg("GetPlayerState", c => c.Return(PlayerStateOf(PlayerId18(c))));
        Reg("GetPlayerHeroes", c =>
        {
            int id = c.Int(0);
            if (id < 1 || id > 8) throw c.Error($"Player ID #{id} is not valid");
            if (PlayerStateOf(id - 1) == 0) throw c.Error($"Player #{id} is not exists");
            c.Return(StringArray(S.Heroes.Where(h => h.Alive && h.Owner == id - 1).Select(h => h.DefId)));
        });
        Reg("GetPlayerResource", c =>
        {
            var p = PlayerInGame(c, PlayerId18(c));
            int r = c.Int(1);
            if (r < 0 || r > 6) throw c.Error($"Invalid resource specified: {r}");
            c.Return(p.Purse[r]);
        });
        // 0x005c1c30: (player, resource, quantity, hero = ""); the hero only places the flying text.
        Reg("SetPlayerResource", c =>
        {
            var p = PlayerInGame(c, PlayerId18(c));
            int pi = p.Index, r = c.Int(1), q = c.Int(2);
            if (r < 0 || r > 6) throw c.Error($"Invalid resource specified: {r}");
            if (q < 0) throw c.Error("Quantity cannot be negative");
            var hero = c.Str(3);
            if (hero.Length > 0 && Game.HeroByName(hero) == null)
                Log.Add($"Script warning (SetPlayerResource): hero {hero} is not on map.");
            Change(() => Game.ScriptGive(pi, r, q - p.Purse[r]));
        });
        Reg("SetPlayerStartResources", c =>
        {
            var p = PlayerInGame(c, PlayerId18(c));
            int pi = p.Index;
            var q = new int[7];
            for (int r = 0; r < 7; r++) q[r] = c.Int(r + 1);
            if (q.Any(v => v < 0)) throw c.Error("All resource values must be >0");
            Change(() => { for (int r = 0; r < 7; r++) Game.ScriptGive(pi, r, q[r] - p.Purse[r]); });
        });
        // 0x005e2030: player and team are both checked against 1..9.
        Reg("SetPlayerTeam", c =>
        {
            int id = c.Int(0);
            if (id < 1 || id > 9) throw c.Error($"Invalid player number {id}! (must be 1..9)");
            // The world has the 8 player slots: only id 9 is not found. Slots not in the game have no state here.
            if (id == 9) throw c.Error($"Not found player {id} in world!");
            int team = c.Int(1);
            if (team < 1 || team > 9) throw c.Error($"Invalid player team number {team}! (must be 1..9)");
            if (Game.Player(id - 1) is { } p) p.Team = team - 1;
        });
        Reg("GetPlayerTeam", c => c.Return((Game.Player(PlayerArg(c, 0))?.Team ?? -1) + 1));
        Reg("GetPlayerRace", c => c.Return(EnumTables.TownIndex(Game.Player(PlayerArg(c, 0))?.Race ?? "")));
        Reg("IsAIPlayer", c => c.ReturnBool(Game.Player(PlayerArg(c, 0)) is { Human: false }));
        Reg("GetPlayerNecroEnergy", c => c.Return(0));
        Reg("HasBorderguardKey", c => { var p = KeyPlayer(c, out int key); c.ReturnBool(p.Keys.Contains(key)); });
        Reg("GiveBorderguardKey", c => { var p = KeyPlayer(c, out int key); p.Keys.Add(key); });

        // Objectives
        Reg("IsObjectiveVisible", c => c.ReturnBool(Objective(c).Visible));
        // 0x005d83f0: objectives are looked up per player (default 1); IsHidden ones refuse.
        Reg("SetObjectiveVisible", c =>
        {
            var o = PlayerObjective(c, 2, out var name, out var def, out _);
            if (def is { Hidden: true }) throw c.Error($"Objective \"{name}\" is hidden");
            o.Visible = c.Bool(1);
            Emit("objective_visible", ("name", name), ("visible", o.Visible));
        });
        Reg("GetObjectiveState", c => c.Return(PlayerObjective(c, 1, out _, out _, out _).State));
        Reg("SetObjectiveState", c => SetObjectiveState(c));
        Reg("GetObjectiveProgress", c => c.Return(Objective(c).Progress));
        // 0x005d9170: only manual, active objectives; 0 <= step < number of progress comments.
        Reg("SetObjectiveProgress", c =>
        {
            var o = PlayerObjective(c, 2, out var name, out var def, out _);
            if (def?.Kind != ManualObjective) throw c.Error($"Objective {name} is not manualy controlled");
            if (o.State != ObjectiveStates.Active) throw c.Error($"Objective {name} is not active");
            int step = c.Int(1);
            if (step < 0) throw c.Error("Progress step can`t be negative");
            if (step >= def.ProgressSteps) throw c.Error($"Invalid progress step #{step} (objective {name} has only {def.ProgressSteps} progress steps)");
            o.Progress = step;
            Emit("objective_progress", ("name", name), ("step", o.Progress));
        });

        Reg("SetTrigger", TriggerCall);
        Reg("Trigger", TriggerCall);
        Reg("StopTrigger", c => SetTrigger(c.Int(0), c.Count > 1 ? c.Str(1) : "", null));

        Reg("BlockGame", c => { World.GameBlocks++; Emit("block_game", ("count", World.GameBlocks)); });
        Reg("UnblockGame", c => { if (World.GameBlocks > 0) World.GameBlocks--; Emit("unblock_game", ("count", World.GameBlocks)); });

        // Saved combats (world vf0x1f4 = count - 1, vf0x1f0 = record). forWinner != 0 reads the winner
        // (record army 0), 0 the loser (army 1); the army functions read only records holding both armies.
        Reg("GetLastSavedCombatIndex", c => c.Return(World.SavedCombats.Count - 1));
        Reg("GetSavedCombatResult", c => c.Return(SavedCombat(c).Result));
        Reg("GetSavedCombatArmyPlayer", c => c.Return(SavedArmy(c) is { } a ? a.Player : 0));
        Reg("GetSavedCombatArmyHero", c => { if (SavedArmy(c) is { Hero.Length: > 0 } a) c.Return(a.Hero); });
        Reg("GetSavedCombatArmyCreaturesCount", c => c.Return(SavedArmy(c) is { } a ? a.Stacks.Count : 0));
        Reg("GetSavedCombatArmyCreatureInfo", c =>
        {
            var army = SavedArmy(c);
            if (army == null) { c.Return(0, 0, 0); return; }
            int i = c.Int(2);
            if (i < 0 || i >= army.Stacks.Count) throw c.Error($"Invalid creature index {i}!");
            var s = army.Stacks[i];
            c.Return(s.Creature, s.Count, s.Died);
        });
    }

    static readonly SavedCombat NoSavedCombat = new();

    /// <summary>World vf0x1f0: the record at the index, else the world's empty default record (+0x1c0).</summary>
    SavedCombat SavedCombat(HostContext c)
    {
        int i = c.Int(0);
        return i >= 0 && i < World.SavedCombats.Count ? World.SavedCombats[i] : NoSavedCombat;
    }

    SavedArmy? SavedArmy(HostContext c)
    {
        var sc = SavedCombat(c);
        return sc.Armies.Count == 2 ? sc.Armies[c.Int(1) == 0 ? 1 : 0] : null;
    }

    /// <summary>Moon week id: the WEEK_OF_* constant defined by advmap-startup.lua.</summary>
    int MoonWeekId(string week) => L.GetGlobal(week) is { IsNumber: true } v ? (int)v.N : 0;

    // ---- players ---------------------------------------------------------------------------------

    /// <summary>Player state (player vf0x6c): 0 PLAYER_NOT_IN_GAME, 1 PLAYER_ACTIVE, 2 PLAYER_WON, 3 PLAYER_LOST.</summary>
    int PlayerStateOf(int engineIndex)
    {
        var p = Game.Player(engineIndex);
        return p == null ? 0 : !p.Alive ? 3 : S.Finished && S.WinnerTeam == p.Team ? 2 : 1;
    }

    /// <summary>Player id argument of the 0x00f34c28 player functions (1..8), as a 0-based index.</summary>
    static int PlayerId18(HostContext c)
    {
        int id = c.Int(0);
        if (id < 1 || id > 8) throw c.Error("Player ID must be 1 through 8");
        return id - 1;
    }

    /// <summary>The player, which must take part in the game (state != 0).</summary>
    PlayerState PlayerInGame(HostContext c, int index) =>
        PlayerStateOf(index) != 0 ? Game.Player(index)! : throw c.Error($"Player {index + 1} doesn't taking part in the current game");

    /// <summary>HasBorderguardKey / GiveBorderguardKey (0x005c22a0 / 0x005c2540): player, then key colour 1..8.</summary>
    PlayerState KeyPlayer(HostContext c, out int key)
    {
        int id = c.Int(0);
        key = c.Int(1);
        if (id < 1 || id > 8) throw c.Error($"Invalid player ID #{id}");
        var p = PlayerInGame(c, id - 1);
        if (key < 1 || key > 8) throw c.Error($"Invalid borderguard key color #{key}");
        return p;
    }

    /// <summary>IsPlayerCurrent / IsPlayerInGhostMode player argument.</summary>
    int TurnPlayerArg(HostContext c)
    {
        int id = c.Int(0);
        if (id < 1 || id > 8) throw c.Error($"Invalid player ID {id}!");
        return id - 1;
    }

    /// <summary>
    /// The player Win / Loose act on (0x005d69d0 / 0x005d6ed0). Outside a multiplayer game the argument
    /// is ignored: the first human player with state 1. In a multiplayer game Win checks the id range,
    /// Loose does not. Either way the player must be active.
    /// </summary>
    int EndGamePlayer(HostContext c, bool win)
    {
        int id = c.Int(0);
        PlayerState? p;
        if (S.Players.Count(x => x.Human) <= 1)
            p = S.Players.Where(x => x.Human && PlayerStateOf(x.Index) == 1).OrderBy(x => x.Index).FirstOrDefault();
        else
        {
            if (win && (id < 1 || id > 8)) throw c.Error($"Invalid player ID({id}) in MP game Win() script command!");
            if (id < 1 || id > 8) throw c.Error("No player!");
            // Slots 1..8 always exist in the exe world; one not in the game has state 0.
            p = Game.Player(id - 1);
        }
        if (p == null) throw c.Error(S.Players.Count(x => x.Human) <= 1 ? "No player!" : "Player not active!");
        if (PlayerStateOf(p.Index) != 1) throw c.Error("Player not active!");
        return p.Index;
    }

    void WinFor(int player)
    {
        var p = Game.Player(player);
        Change(() => Game.ScriptEndGame(p?.Team, "script_win"));
    }

    void LoseFor(int player)
    {
        Change(() =>
        {
            Game.ScriptEliminate(player);
            Game.ScriptCheckVictory();
        });
        // A human losing a single-player scenario ends the game.
        if (Game.Player(player) is { Human: true } && S.Players.Count(p => p.Human) == 1)
            Change(() => Game.ScriptEndGame(null, "script_loose"));
    }

    // ---- objectives ----------------------------------------------------------------------------

    ObjectiveState Objective(HostContext c, int playerArg = 1)
    {
        var name = c.Str(0);
        var def = Data.Objectives.Find(o => o.Name == name);
        int player = def != null && def.Player >= 0 ? def.Player : c.Has(playerArg) ? PlayerArg(c, playerArg) : 0;
        var key = ScriptWorld.ObjectiveKey(name, player);
        if (World.Objectives.TryGetValue(key, out var st)) return st;
        if (def == null) throw c.Error($"Objective with name \"{name}\" is not exists");
        return World.Objectives[key] = new ObjectiveState { Visible = def.InitiallyVisible };
    }

    const string ManualObjective = "OBJECTIVE_KIND_MANUAL";

    /// <summary>
    /// The objective named by argument 0 for the player in argument <paramref name="playerArg"/> (script id,
    /// default 1), as the 0x00f35000 objective functions look it up (IAdventureMap vf0x18(player, name)).
    /// </summary>
    ObjectiveState PlayerObjective(HostContext c, int playerArg, out string name, out ObjectiveDef? def, out int player)
    {
        name = c.Str(0);
        if (name.Length == 0) throw c.Error("Objective name is not specified");
        int id = c.Int(playerArg);
        if (id < 1 || id > 8) throw c.Error($"Incorrect player ID #{id}");
        player = id - 1;
        var n = name;
        int pi = player;
        def = Data.Objectives.Find(o => o.Name == n && (o.Player < 0 || o.Player == pi));
        if (!World.Objectives.TryGetValue(ScriptWorld.ObjectiveKey(name, player), out var st))
            throw c.Error($"Objective \"{name}\" is not exists");
        return st;
    }

    /// <summary>SetObjectiveState (0x005d89e0): the transitions the engine allows from each state.</summary>
    void SetObjectiveState(HostContext c)
    {
        var st = PlayerObjective(c, 2, out var name, out var def, out int player);
        int state = c.Int(1);
        if (state < 0 || state > 4) throw c.Error($"Invalid objective state #{state}");
        bool manual = def?.Kind == ManualObjective;
        string? error = st.State switch
        {
            ObjectiveStates.ScenarioInfo => $"Objective \"{name}\" is a scenario info, so it`s state can`t be modified",
            ObjectiveStates.Unknown => state == ObjectiveStates.Active
                ? def is { Dependencies.Count: > 0 } ? $"Objective \"{name}\" depends on other objectives, so can`t be activated manual" : null
                : state == ObjectiveStates.Failed ? null : $"Objective \"{name}\" is in initial state, so can only activate or fail it",
            ObjectiveStates.Active => state is not (ObjectiveStates.Completed or ObjectiveStates.Failed)
                ? $"Objective \"{name}\" is active, so can only complete or fail it"
                : manual ? null : $"Objective \"{name}\" is not manual controlled",
            ObjectiveStates.Completed => state is not (ObjectiveStates.Active or ObjectiveStates.Failed)
                ? $"Objective \"{name}\" is completed, so can only reactivate or fail it"
                : def is not { CanUncomplete: true } ? $"Objective \"{name}\" can`t be uncompleted"
                : manual ? null : $"Objective \"{name}\" is not manual controlled",
            ObjectiveStates.Failed => $"Objective \"{name}\" is failed, so can`t do nothing with it",
            _ => null,
        };
        if (error != null) throw c.Error(error);
        st.State = state;
        Emit("objective_state", ("name", name), ("state", state), ("player", player + 1));
        Fire(TriggerTypes.ObjectiveStateChange, $"{name}|{player + 1}", null, player + 1);
    }

    /// <summary>A trigger number argument: lua_isnumber, then rounded to nearest (FUN_004c0d00, x87 FISTP).</summary>
    static bool TriggerNumber(HostContext c, int i, out int value)
    {
        value = 0;
        if (!c.Arg(i).TryToNumber(out var d)) return false;
        value = (int)Math.Round(d, MidpointRounding.ToEven);
        return true;
    }

    static bool IsLuaString(LuaValue v) => v.IsString || v.IsNumber;

    /// <summary>Trigger(type, filter..., functionName): see SetTrigger (0x005dd530).</summary>
    void TriggerCall(HostContext c)
    {
        if (c.Count < 1) throw c.Error("Parameter lost: type of trigger");
        if (!TriggerNumber(c, 0, out int type)) throw c.Error("Invalid parameter type: type of trigger must be number");
        if (type < 0 || type > 16) throw c.Error("Invalid parameter value: type of trigger must be in [0,16]");
        string filter;
        int fnArg;
        switch (type)
        {
            case 0: case 9: case 11: case 12:
                filter = ""; fnArg = 1;
                break;
            case 1: case 2:
                if (c.Count < 2) throw c.Error("Parameter lost: player ID");
                if (!TriggerNumber(c, 1, out int pid)) throw c.Error("Invalid parameter type: player ID must be number");
                if (pid < 1 || pid > 8) throw c.Error("Invalid parameter value: player ID must be in [1,8]");
                filter = pid.ToString(); fnArg = 2;
                break;
            case 3:
            {
                // The objective of player 1 unless a player id precedes the function (4+ arguments).
                if (c.Count < 2) throw c.Error("Parameter lost: objective name");
                if (!IsLuaString(c.Arg(1))) throw c.Error("Invalid parameter type: objective name must be string");
                var name = c.Str(1);
                int player = 1;
                fnArg = 2;
                if (c.Count >= 4)
                {
                    if (!TriggerNumber(c, 2, out player)) throw c.Error("Invalid parameter type: player id must be integer");
                    if (player < 1 || player > 8) throw c.Error($"Invalid player id #{player}");
                    fnArg = 3;
                }
                if (!World.Objectives.ContainsKey(ScriptWorld.ObjectiveKey(name, player - 1)))
                    throw c.Error($"Objective with name \"{name}\" is not exists");
                filter = $"{name}|{player}";
                break;
            }
            case 4: case 5: case 10:
                if (c.Count < 2) throw c.Error("Parameter lost: object name");
                if (!IsLuaString(c.Arg(1))) throw c.Error("Invalid parameter type: object name must be string");
                filter = c.Str(1);
                var obj = Game.ObjectByName(filter);
                if (obj == null && Game.HeroByName(filter) == null)
                    throw c.Error($"Object with name \"{filter}\" is not exists");
                if (type == 10 && (obj == null || S.Town(obj.Index) == null)) throw c.Error($"Object \"{filter}\" is not a town");
                fnArg = 2;
                break;
            case 6: case 7: case 13:
                if (c.Count < 2) throw c.Error("Parameter lost: region name");
                if (!IsLuaString(c.Arg(1))) throw c.Error("Invalid parameter type: region name must be string");
                filter = c.Str(1);
                if (!Data.RegionByName.ContainsKey(filter)) throw c.Error($"Region with name \"{filter}\" is not exists");
                fnArg = 2;
                break;
            default: // 8, 14, 15, 16: hero
                if (c.Count < 2) throw c.Error("Parameter lost: hero name");
                if (!IsLuaString(c.Arg(1))) throw c.Error("Invalid parameter type: hero name must be string");
                filter = c.Str(1);
                if (Game.HeroByName(filter) == null && !Core.Heroes.ContainsKey(filter))
                    throw c.Error($"Hero with name \"{filter}\" is not exists");
                fnArg = 2;
                break;
        }
        if (c.Count <= fnArg) throw c.Error("Parameter lost: function name");
        var fn = c.Arg(fnArg);
        if (!IsLuaString(fn) && !fn.IsNil) throw c.Error("Invalid parameter type: function name must be string or nil");
        SetTrigger(type, filter, fn.IsNil ? null : c.Str(fnArg));
    }

    H5.Core.Data.GameDatabase Core => Game.Core;
}
