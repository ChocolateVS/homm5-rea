using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

public sealed partial class AdventureScripting
{
    string BuildingArg(HostContext c, int i)
    {
        int id = c.Int(i);
        return EnumTables.Building(id) ?? throw c.Error($"Town \"{c.Str(0)}\" is not containing building #{id}");
    }

    int BuildingMaxLevel(TownState t, string building) =>
        Game.Db.Buildings.TryGetValue(t.Type, out var defs) ? defs.Where(d => d.Type == building).Select(d => d.Level).DefaultIfEmpty(0).Max() : 0;

    /// <summary>Town vf 0x5c: the town's building record for a TOWN_BUILDING_* id, if the town has one.</summary>
    string TownBuildingArg(HostContext c, TownState t, int i)
    {
        int id = c.Int(i);
        var b = EnumTables.Building(id);
        if (b == null || !Game.Db.Buildings.TryGetValue(t.Type, out var defs) || !defs.Any(d => d.Type == b))
            throw c.Error($"Town \"{c.Str(0)}\" is not containing building #{id}");
        return b;
    }

    /// <summary>The (RazedStatic) of a shared, or null when the object is not razeable.</summary>
    static string? RazedStaticOf(SharedDef sd)
    {
        var href = (string?)sd.Raw?.Element("RazedStatic")?.Attribute("href");
        var path = H5.Core.Xdb.XdbRef.Parse(href, sd.Path).Path;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>The towns' "Object" lookups (DisableAutoEnterTown, MakeTownMovable).</summary>
    TownState TownObjectArg(HostContext c, int i)
    {
        var name = c.Str(i);
        var o = Game.ObjectByName(name) ?? throw c.Error($"Object \"{name}\" does not exist!");
        return S.Town(o.Index) ?? throw c.Error($"Object \"{name}\" is not town!");
    }


    void RegisterTownApi()
    {
        // 0x005c7590
        Reg("GetTownBuildingLevel", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" is not specified");
            c.Return(t.Level(TownBuildingArg(c, t, 1)));
        });
        Reg("GetTownBuildingMaxLevel", c => { var t = Town(c, 0); c.Return(BuildingMaxLevel(t, BuildingArg(c, 1))); });
        Reg("GetTownBuildingLimitLevel", c => { var t = Town(c, 0); c.Return(t.MaxLevel.GetValueOrDefault(BuildingArg(c, 1))); });
        // 0x005c7dd0: current level <= limit <= maximum level.
        Reg("SetTownBuildingLimitLevel", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" is not specified");
            var b = TownBuildingArg(c, t, 1);
            int limit = c.Int(2), cur = t.Level(b), max = BuildingMaxLevel(t, b);
            if (limit < cur) throw c.Error($"Limit ({limit}) is lesser then actual towns \"{c.Str(0)}\" building \"{b}\" level ({cur})");
            if (limit > max) throw c.Error($"Limit ({limit}) is greater then maximum towns \"{c.Str(0)}\" building \"{b}\" level ({max})");
            t.MaxLevel[b] = limit;
        });
        // 0x005c8140: (town, building, level, canRebuild = 1); canRebuild 0 also caps the limit at level.
        Reg("DestroyTownBuildingToLevel", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" is not specified");
            var b = TownBuildingArg(c, t, 1);
            int level = c.Int(2), max = BuildingMaxLevel(t, b);
            if (level < 0) throw c.Error($"Level ({level}) is lesser than 0");
            if (level > max) throw c.Error($"Level ({level}) is greater then maximum towns \"{c.Str(0)}\" building \"{b}\" level ({max})");
            if (level < t.Level(b)) Change(() => Game.ScriptSetBuildingLevel(t, b, level));
            if (c.Int(3) == 0) t.MaxLevel[b] = level;
        });
        // 0x005e7a30 (format "sn"): the next level, free.
        Reg("UpgradeTownBuilding", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" is not registered!");
            var b = TownBuildingArg(c, t, 1);
            if (t.Level(b) >= BuildingMaxLevel(t, b)) return;
            Change(() => Game.ScriptUpgradeBuilding(t, b));
        });
        // 0x005c9ee0: the exe replaces the town object (CRemoveObjectCmd + CCreateTownCmd) keeping
        // only name and owner; the engine only changes the type.
        Reg("TransformTown", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" is not exists", checkEmpty: false);
            int id = c.Int(1);
            var type = EnumTables.Town(id) ?? throw c.Error($"Invalid town type #{id}");
            t.Type = type;
            Emit("town_transformed", ("town", c.Str(0)), ("type", type));
        });
        // 0x005ca3f0: the town is removed and its (RazedStatic) placed with the town's name.
        Reg("RazeTown", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" is not exists", checkEmpty: false);
            var o = Map.Objects[t.Object];
            var razed = RazedStaticOf(o.Shared) ?? throw c.Error($"Town \"{c.Str(0)}\" is not razeable");
            Change(() => Game.ScriptRazeTown(t));
            AddStatic(o.Name, razed, o.Pos, o.Rot);
        });
        // 0x005ca830: any object with a (RazedStatic) (towns included) becomes that static.
        Reg("RazeBuilding", c =>
        {
            var name = c.Str(0);
            var o = Game.ObjectByName(name) ?? throw c.Error($"Building \"{name}\" does not exists");
            var razed = RazedStaticOf(o.Shared) ?? throw c.Error($"Building \"{name}\" is not razeable");
            if (S.Town(o.Index) is { } t) Change(() => Game.ScriptRazeTown(t));
            else Change(() => Game.ScriptRemoveObjectAndUnblock(o.Index));
            AddStatic(o.Name, razed, o.Pos, o.Rot);
        });
        // 0x005d0840: the garrison hero (town vf 0x18(1)); no value when there is none.
        Reg("GetTownHero", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" is not exists");
            var h = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && x.Garrisoned);
            if (h != null) c.Return(h.DefId);
        });
        Reg("GetTownRace", c => c.Return(EnumTables.TownIndex(Town(c, 0).Type)));
        // 0x005e6a00: CDisableAutoEnterTownCmd is always built with true (the argument is not read).
        Reg("DisableAutoEnterTown", c => World.Settings[$"no_auto_enter:{TownObjectArg(c, 0).Object}"] = "1");
        // 0x005e5770
        Reg("MakeTownMovable", c => World.Settings[$"movable_town:{TownObjectArg(c, 0).Object}"] = "1");

        // 0x005d4460: (hero, town, arena = ""); the hero must be on the map and owned; allied towns refused.
        Reg("SiegeTown", c =>
        {
            var heroName = c.Str(0);
            if (heroName.Length == 0) throw c.Error("Hero name is not specified");
            var h = Game.HeroByName(heroName) ?? throw c.Error($"Hero \"{heroName}\" is not exists");
            if (h.Owner < 0) throw c.Error($"Hero \"{heroName}\" is not belongs to any player");
            var t = TownArg(c, 1, "Town \"{0}\" is not exists");
            if (t.Owner >= 0 && Game.ArePlayersAllied(h.Owner, t.Owner)) throw c.Error("Can`t siege allied town");
            var arena = c.Str(2);
            if (arena.Length > 0 && !_game.TryReadAllBytes(H5.Core.Xdb.XdbRef.Parse(arena).Path, out _))
                throw c.Error($"Arena \"{arena}\" is not exists");
            var army = t.Garrison.Live().Select(s => new Stack(s.Creature, s.Count)).ToList();
            var defender = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && x.Garrisoned);
            if (defender != null) army = defender.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList();
            Change(() => Game.ScriptStartBattle(h, defender?.Id ?? -1, t.Object, t.Pos, army, siege: true));
        });
        // 0x005d3b90 (format "s."): StartCombat(hero, enemyHero|nil, count, type1, amount1, …,
        // combatScript|nil, finishTrigger|nil, arena|nil, allowQuickCombat); the parameters are
        // checked by the native itself in this order.
        Reg("StartCombat", c =>
        {
            var heroName = c.Str(0);
            if (heroName.Length == 0) throw c.Error("Hero name is not specified");
            var h = Game.HeroByName(heroName) ?? throw c.Error($"Hero \"{heroName}\" is not exists");
            int left = c.Count;
            if (left - 1 < 1) throw c.Error("Parameter missed: enemy hero name");
            var e = c.Arg(1);
            if (!IsLuaString(e) && !e.IsNil) throw c.Error("Invalid parameter type: enemy hero name (must be string or nil)");
            HeroState? enemy = null;
            if (IsLuaString(e))
            {
                var en = c.Str(1);
                if (en.Length == 0) throw c.Error("Invalid parameter value: enemy hero name (is empty)");
                enemy = Game.HeroByNameAnywhere(en) ?? throw c.Error($"Invalid parameter value: enemy hero name (hero with name \"{en}\" is not exists)");
                if (enemy.Owner < 0) throw c.Error($"Invalid parameter value: enemy hero name (hero with name \"{en}\" not belongs to any player)");
                if (!S.OffMapHeroes.Contains(enemy)) throw c.Error($"Invalid parameter value: enemy hero name (hero with name \"{en}\" is not reserved)");
            }
            if (left - 2 < 1) throw c.Error("Parameter missed: creatures count");
            if (!c.Arg(2).TryToNumber(out _)) throw c.Error("Invalid parameter type: creatures count (must be number)");
            int count = c.Int(2);
            if (count < 1) throw c.Error("Invalid parameter value: creatures count (must be positive)");
            left -= 3;
            int at = 3;
            var army = new List<Stack>();
            for (int i = 0; i < count; i++)
            {
                if (left < 1) throw c.Error($"Parameter missed: creature #{i + 1} type");
                if (!c.Arg(at).TryToNumber(out _)) throw c.Error($"Invalid parameter type: creature #{i + 1} type (must be number)");
                int type = c.Int(at);
                var cr = type != 0 ? Ids.Creatures.Name(type) : null;
                if (type != 0 && (cr == null || type < 0)) throw c.Error($"Creature type #{type} is not exists");
                if (left - 1 < 1) throw c.Error($"Parameter missed: creature #{i + 1} amount");
                if (!c.Arg(at + 1).TryToNumber(out _)) throw c.Error($"Invalid parameter type: creature #{i + 1} amount (must be number)");
                int amount = c.Int(at + 1);
                // The exe formats this message with the amount, not the creature number.
                if (amount < 1) throw c.Error($"Invalid parameter value: creature #{amount} amount (must be positive)");
                if (cr != null) army.Add(new Stack(cr, amount));
                at += 2;
                left -= 2;
            }
            string script = "", finish = "", arena = "";
            if (left > 0)
            {
                var v = c.Arg(at);
                if (!IsLuaString(v) && !v.IsNil) throw c.Error("Invalid parameter type: combat script name (must be string or nil)");
                if (IsLuaString(v))
                {
                    script = c.Str(at);
                    if (!_game.TryReadAllBytes(H5.Core.Xdb.XdbRef.Parse(script).Path, out _)) throw c.Error($"Script \"{script}\" is not exists");
                }
                at++; left--;
            }
            if (left > 0)
            {
                var v = c.Arg(at);
                if (!IsLuaString(v) && !v.IsNil) throw c.Error("Invalid parameter type: finish trigger (must be string or nil)");
                if (IsLuaString(v)) finish = c.Str(at);
                at++; left--;
            }
            if (left > 0)
            {
                var v = c.Arg(at);
                if (!IsLuaString(v) && !v.IsNil) throw c.Error("Invalid parameter type: arena (must be string or nil)");
                if (IsLuaString(v))
                {
                    arena = c.Str(at);
                    if (!_game.TryReadAllBytes(H5.Core.Xdb.XdbRef.Parse(arena).Path, out _)) throw c.Error($"Arena \"{arena}\" is not exists");
                }
                at++; left--;
            }
            bool quick = left > 0 && !c.Arg(at).IsNil;
            World.PendingCombat = new PendingScriptCombat { Hero = h.DefId, FinishTrigger = finish, CombatScript = script, Arena = arena, AllowQuickCombat = quick };
            Change(() => Game.ScriptStartBattle(h, enemy?.Id ?? -1, -1, h.Pos, army, siege: false));
            Emit("start_combat", ("hero", h.DefId), ("script", script), ("arena", arena));
        });
    }
}
