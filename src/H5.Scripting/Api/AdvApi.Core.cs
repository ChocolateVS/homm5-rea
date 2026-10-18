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
        Reg("random", c =>
        {
            int top = c.Int(0);
            c.Return(top <= 0 ? 0 : (double)(int)((uint)S.Rng.NextU64() % (uint)top));
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
        Reg("Win", c => WinFor(c.Count > 0 ? PlayerArg(c, 0) : S.CurrentPlayer));
        Reg("WinTeam", c =>
        {
            int team = c.Int(0) - 1;
            Change(() => Game.ScriptEndGame(team, "script"));
        });
        Reg("Loose", c => LoseFor(c.Count > 0 ? PlayerArg(c, 0) : S.CurrentPlayer));
        Reg("GetDate", c =>
        {
            int type = c.Has(0) ? c.Int(0) : 0;
            c.Return(type switch
            {
                0 => S.Day,
                1 => S.Week,
                2 => S.Month,
                3 => S.DayOfWeek,
                _ => throw c.Error($"Incorrect date type ID ({type})"),
            });
        });
        Reg("GetCurrentPlayer", c => c.Return(ScriptPlayer(S.CurrentPlayer)));
        Reg("IsPlayerCurrent", c => c.ReturnBool(PlayerArg(c, 0) == S.CurrentPlayer));
        Reg("IsTeamCurrent", c => c.ReturnBool(Game.Player(S.CurrentPlayer)?.Team == c.Int(0) - 1));
        Reg("IsPlayerWaitingForTurn", c => c.ReturnBool(PlayerArg(c, 0) != S.CurrentPlayer));
        Reg("IsPlayerLost", c => c.ReturnBool(Game.Player(PlayerArg(c, 0)) is { Alive: false }));
        Reg("IsPlayerInGhostMode", c => c.ReturnBool(false));
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
        Reg("GetGameVar", c => c.Return(World.GameVars.TryGetValue(c.Str(0), out var v) ? v : ""));
        Reg("SetGameVar", c => World.GameVars[c.Str(0)] = c.Str(1));
        Reg("Save", c => Emit("save_game", ("file", c.Str(0))));
        Reg("Load", c => Emit("load_game", ("file", c.Str(0))));
        Reg("GetCurrentMoonWeek", c => c.Return(MoonWeekId(S.MoonWeek)));
        Reg("GetTerrainSize", c => c.Return(Map.TilesX, Map.TilesY));
        Reg("IsTilePassable", c =>
        {
            var t = TileArg(c, 0, 1, 2);
            var f = Map.Floor(t.Floor);
            c.ReturnBool(f != null && f.InBounds(t.X, t.Y) && !f.IsBlocked(t.X, t.Y));
        });
        Reg("WarpToMap", c => Emit("warp_to_map", ("map", c.Str(0))));
        Reg("WarpHeroExp", c => Emit("warp_hero_exp", ("hero", c.Str(0))));
        Reg("GetPlayerSelectedCampaignBonusIndex", c => c.Return(World.GameVars.TryGetValue("__campaign_bonus", out var v) && int.TryParse(v, out var i) ? i : 0));

        // Players
        Reg("GetPlayerState", c =>
        {
            var p = Game.Player(PlayerArg(c, 0));
            c.Return(p == null ? 0 : !p.Alive ? 3 : S.Finished && S.WinnerTeam == p.Team ? 2 : 1);
        });
        Reg("GetPlayerHeroes", c =>
        {
            int p = PlayerArg(c, 0);
            c.Return(StringArray(S.Heroes.Where(h => h.Alive && h.Owner == p).Select(h => h.DefId)));
        });
        Reg("GetPlayerResource", c =>
        {
            var p = Game.Player(PlayerArg(c, 0)) ?? throw c.Error("Player is not in game");
            int r = c.Int(1);
            if (r < 0 || r > 6) throw c.Error($"Invalid resource kind ({r})");
            c.Return(p.Purse[r]);
        });
        Reg("SetPlayerResource", c =>
        {
            int pi = PlayerArg(c, 0);
            var p = Game.Player(pi) ?? throw c.Error("Player is not in game");
            int r = c.Int(1), q = c.Int(2);
            if (r < 0 || r > 6) throw c.Error($"Invalid resource kind ({r})");
            if (q < 0) throw c.Error("Resource quantity can`t be negative");
            Change(() => Game.ScriptGive(pi, r, q - p.Purse[r]));
        });
        Reg("SetPlayerStartResources", c =>
        {
            int pi = PlayerArg(c, 0);
            var p = Game.Player(pi) ?? throw c.Error("Player is not in game");
            for (int r = 0; r < 7; r++) { int q = c.Int(r + 1); int cur = p.Purse[r]; Change(() => Game.ScriptGive(pi, r, q - cur)); }
        });
        Reg("SetPlayerTeam", c =>
        {
            var p = Game.Player(PlayerArg(c, 0));
            if (p != null) p.Team = c.Int(1) - 1;
        });
        Reg("GetPlayerTeam", c => c.Return((Game.Player(PlayerArg(c, 0))?.Team ?? -1) + 1));
        Reg("GetPlayerRace", c => c.Return(EnumTables.TownIndex(Game.Player(PlayerArg(c, 0))?.Race ?? "")));
        Reg("IsAIPlayer", c => c.ReturnBool(Game.Player(PlayerArg(c, 0)) is { Human: false }));
        Reg("GetPlayerNecroEnergy", c => c.Return(0));
        Reg("HasBorderguardKey", c => c.ReturnBool(Game.Player(PlayerArg(c, 0))?.Keys.Contains(c.Int(1)) == true));
        Reg("GiveBorderguardKey", c => Game.Player(PlayerArg(c, 0))?.Keys.Add(c.Int(1)));

        // Objectives
        Reg("IsObjectiveVisible", c => c.ReturnBool(Objective(c).Visible));
        Reg("SetObjectiveVisible", c => { var o = Objective(c, 2); o.Visible = c.Bool(1); Emit("objective_visible", ("name", c.Str(0)), ("visible", o.Visible)); });
        Reg("GetObjectiveState", c => c.Return(Objective(c).State));
        Reg("SetObjectiveState", c => SetObjectiveState(c));
        Reg("GetObjectiveProgress", c => c.Return(Objective(c).Progress));
        Reg("SetObjectiveProgress", c => { var o = Objective(c, 2); o.Progress = c.Int(1); Emit("objective_progress", ("name", c.Str(0)), ("step", o.Progress)); });

        Reg("SetTrigger", TriggerCall);
        Reg("Trigger", TriggerCall);
        Reg("StopTrigger", c => SetTrigger(c.Int(0), c.Count > 1 ? c.Str(1) : "", null));

        Reg("BlockGame", c => { World.GameBlocks++; Emit("block_game", ("count", World.GameBlocks)); });
        Reg("UnblockGame", c => { if (World.GameBlocks > 0) World.GameBlocks--; Emit("unblock_game", ("count", World.GameBlocks)); });

        // Saved combats (GetSavedCombat*)
        Reg("GetLastSavedCombatIndex", c => c.Return(World.SavedCombats.Count > 0 ? World.SavedCombats[^1].Index : -1));
        Reg("GetSavedCombatResult", c => c.Return(SavedCombat(c).Winner == 0 ? 1 : 0));
        Reg("GetSavedCombatArmyPlayer", c => c.Return(SavedCombat(c).Players[c.Int(1) == 0 ? 0 : 1]));
        Reg("GetSavedCombatArmyHero", c => c.Return(SavedCombat(c).Heroes[c.Int(1) == 0 ? 0 : 1] ?? ""));
        Reg("GetSavedCombatArmyCreaturesCount", c => c.Return(SavedCombat(c).Armies[c.Int(1) == 0 ? 0 : 1].Count));
        Reg("GetSavedCombatArmyCreatureInfo", c =>
        {
            var army = SavedCombat(c).Armies[c.Int(1) == 0 ? 0 : 1];
            int i = c.Int(2);
            if (i < 0 || i >= army.Count) throw c.Error("Invalid army slot");
            c.Return(army[i].Creature, army[i].Count, army[i].Died);
        });
    }

    SavedCombat SavedCombat(HostContext c)
    {
        int i = c.Int(0);
        return World.SavedCombats.Find(s => s.Index == i) ?? throw c.Error($"Saved combat #{i} not found");
    }

    /// <summary>Moon week id: the WEEK_OF_* constant defined by advmap-startup.lua.</summary>
    int MoonWeekId(string week) => L.GetGlobal(week) is { IsNumber: true } v ? (int)v.N : 0;

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

    void SetObjectiveState(HostContext c)
    {
        var st = Objective(c, 2);
        int state = c.Int(1);
        if (state < 0 || state > 4) throw c.Error($"Invalid objective state ({state})");
        if (st.State == state) return;
        st.State = state;
        var name = c.Str(0);
        var def = Data.Objectives.Find(o => o.Name == name);
        int player = def != null && def.Player >= 0 ? def.Player : c.Has(2) ? c.Int(2) - 1 : 0;
        Emit("objective_state", ("name", name), ("state", state), ("player", player + 1));
        Fire(TriggerTypes.ObjectiveStateChange, name, null, player + 1);
        Fire(TriggerTypes.ObjectiveStateChange, $"{name}|{player + 1}", null, player + 1);
    }

    /// <summary>Trigger(type, filter..., functionName): see SetTrigger (0x005dd530).</summary>
    void TriggerCall(HostContext c)
    {
        if (c.Count < 1) throw c.Error("Parameter lost: type of trigger");
        if (!c.Arg(0).IsNumber) throw c.Error("Invalid parameter type: type of trigger must be number");
        int type = c.Int(0);
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
                int pid = c.Int(1);
                if (pid < 1 || pid > 8) throw c.Error("Invalid parameter value: player ID must be in [1,8]");
                filter = pid.ToString(); fnArg = 2;
                break;
            case 3:
                if (c.Count < 2) throw c.Error("Parameter lost: objective name");
                filter = c.Str(1);
                if (c.Count >= 4 && c.Arg(2).IsNumber) { filter = $"{filter}|{c.Int(2)}"; fnArg = 3; }
                else fnArg = 2;
                break;
            case 4: case 5: case 10:
                if (c.Count < 2) throw c.Error("Parameter lost: object name");
                filter = c.Str(1);
                if (Game.ObjectByName(filter) == null && Game.HeroByName(filter) == null)
                    throw c.Error($"Object with name \"{filter}\" is not exists");
                fnArg = 2;
                break;
            case 6: case 7: case 13:
                if (c.Count < 2) throw c.Error("Parameter lost: region name");
                filter = c.Str(1);
                if (!Data.RegionByName.ContainsKey(filter)) throw c.Error($"Region with name \"{filter}\" is not exists");
                fnArg = 2;
                break;
            default: // 8, 14, 15, 16: hero
                if (c.Count < 2) throw c.Error("Parameter lost: hero name");
                filter = c.Str(1);
                if (Game.HeroByName(filter) == null && !Core.Heroes.ContainsKey(filter))
                    throw c.Error($"Hero with name \"{filter}\" is not exists");
                fnArg = 2;
                break;
        }
        if (c.Count <= fnArg) throw c.Error("Parameter lost: function name");
        var fn = c.Arg(fnArg);
        if (!fn.IsString && !fn.IsNil && !fn.IsNumber) throw c.Error("Invalid parameter type: function name must be string or nil");
        SetTrigger(type, filter, fn.IsNil ? null : c.Str(fnArg));
    }

    H5.Core.Data.GameDatabase Core => Game.Core;
}
