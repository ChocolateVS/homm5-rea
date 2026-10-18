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

    void RegisterTownApi()
    {
        Reg("GetTownBuildingLevel", c => { var t = Town(c, 0); c.Return(t.Level(BuildingArg(c, 1))); });
        Reg("GetTownBuildingMaxLevel", c => { var t = Town(c, 0); c.Return(BuildingMaxLevel(t, BuildingArg(c, 1))); });
        Reg("GetTownBuildingLimitLevel", c => { var t = Town(c, 0); c.Return(t.MaxLevel.GetValueOrDefault(BuildingArg(c, 1))); });
        Reg("SetTownBuildingLimitLevel", c =>
        {
            var t = Town(c, 0);
            var b = BuildingArg(c, 1);
            int limit = c.Int(2);
            if (limit < 0) throw c.Error("Level (" + limit + ") is lesser than 0");
            if (limit < t.Level(b)) throw c.Error($"Limit ({limit}) is lesser then actual towns \"{c.Str(0)}\" building \"{b}\" level ({t.Level(b)})");
            t.MaxLevel[b] = Math.Min(limit, Math.Max(limit, BuildingMaxLevel(t, b)));
        });
        Reg("DestroyTownBuildingToLevel", c =>
        {
            var t = Town(c, 0);
            var b = BuildingArg(c, 1);
            int level = Math.Max(0, c.Int(2));
            if (level < t.Level(b)) Change(() => Game.ScriptSetBuildingLevel(t, b, level));
        });
        Reg("UpgradeTownBuilding", c =>
        {
            var t = Town(c, 0);
            var b = BuildingArg(c, 1);
            if (t.Level(b) >= BuildingMaxLevel(t, b)) return;
            Change(() => Game.ScriptUpgradeBuilding(t, b));
        });
        Reg("TransformTown", c =>
        {
            var t = Town(c, 0);
            var type = EnumTables.Town(c.Int(1)) ?? throw c.Error($"Invalid town type ({c.Int(1)})");
            t.Type = type;
            Emit("town_transformed", ("town", c.Str(0)), ("type", type));
        });
        Reg("RazeTown", c =>
        {
            var t = Town(c, 0);
            Change(() => Game.ScriptRazeTown(t));
        });
        Reg("RazeBuilding", c =>
        {
            var o = Obj(c, 0);
            World.Settings[$"razed:{o.Index}"] = "1";
            World.DisabledObjects[o.Index] = 3;
            Emit("building_razed", ("object", o.Name));
        });
        Reg("GetTownHero", c =>
        {
            var t = Town(c, 0);
            var h = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && x.Garrisoned) ?? S.Heroes.Find(x => x.Alive && x.InTown == t.Object);
            c.Return(h != null ? h.DefId : LuaValue.Nil);
        });
        Reg("GetTownRace", c => c.Return(EnumTables.TownIndex(Town(c, 0).Type)));
        Reg("DisableAutoEnterTown", c => World.Settings[$"no_auto_enter:{Town(c, 0).Object}"] = c.Bool(1) ? "1" : "0");
        Reg("MakeTownMovable", c => World.Settings[$"movable_town:{Town(c, 0).Object}"] = "1");

        Reg("SiegeTown", c =>
        {
            var h = Hero(c, 0);
            var t = Town(c, 1);
            var army = t.Garrison.Live().Select(s => new Stack(s.Creature, s.Count)).ToList();
            var defender = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && x.Garrisoned);
            if (defender != null) army = defender.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList();
            Change(() => Game.ScriptStartBattle(h, defender?.Id ?? -1, t.Object, t.Pos, army, siege: true));
        });
        Reg("StartCombat", c =>
        {
            // StartCombat(hero, enemyHero, count, type1, amount1, …, combatScript, finishTrigger, arena, allowQuickCombat)
            var h = Hero(c, 0);
            var enemy = c.Has(1) ? Game.HeroByName(c.Str(1)) : null;
            int count = c.Int(2);
            var army = new List<Stack>();
            for (int i = 0; i < count; i++)
            {
                var cr = Ids.Creatures.Name(c.Int(3 + 2 * i));
                int n = c.Int(4 + 2 * i);
                if (cr != null && n > 0) army.Add(new Stack(cr, n));
            }
            int after = 3 + 2 * count;
            var script = c.Has(after) ? c.Str(after) : "";
            var finish = c.Has(after + 1) ? c.Str(after + 1) : "";
            var arena = c.Has(after + 2) ? c.Str(after + 2) : "";
            if (enemy != null && army.Count == 0) army = enemy.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList();
            World.PendingCombat = new PendingScriptCombat { Hero = h.DefId, FinishTrigger = finish, CombatScript = script, Arena = arena };
            Change(() => Game.ScriptStartBattle(h, enemy?.Id ?? -1, -1, h.Pos, army, siege: false));
            Emit("start_combat", ("hero", h.DefId), ("script", script), ("arena", arena));
        });
    }
}
