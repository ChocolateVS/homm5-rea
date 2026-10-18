using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

public sealed partial class AdventureScripting
{
    void RegisterHeroApi()
    {
        Reg("IsHeroAlive", c => c.ReturnBool(HeroOrNull(c, 0) is { } h && Game.Player(h.Owner) is { Alive: true }));
        Reg("GetHeroLevel", c => c.Return(Hero(c, 0).Level));
        Reg("GetHeroStat", c =>
        {
            var h = Hero(c, 0);
            c.Return(c.Int(1) switch
            {
                0 => (double)h.Experience,
                1 => h.Attack,
                2 => h.Defence,
                3 => h.SpellPower,
                4 => h.Knowledge,
                5 => Game.ScriptHeroLuck(h),
                6 => Game.ScriptHeroMorale(h),
                7 => h.MovePoints,
                8 => h.Mana,
                var s => throw c.Error($"Invalid stat ID ({s})"),
            });
        });
        Reg("ChangeHeroStat", c =>
        {
            var h = Hero(c, 0);
            int stat = c.Int(1), delta = c.Int(2);
            Change(() =>
            {
                switch (stat)
                {
                    case 0:
                        if (delta < 0) throw c.Error("Experience can only grow");
                        Game.ScriptAddExperienceRaw(h, (long)(delta * World.HeroesExpCoef));
                        break;
                    case 1: Game.ScriptAddStat(h, "Attack", Math.Max(-h.Attack, delta)); break;
                    case 2: Game.ScriptAddStat(h, "Defence", Math.Max(-h.Defence, delta)); break;
                    case 3: Game.ScriptAddStat(h, "SpellPower", Math.Max(-h.SpellPower, delta)); break;
                    case 4: Game.ScriptAddStat(h, "Knowledge", Math.Max(-h.Knowledge, delta)); break;
                    case 5: h.ScriptLuck += delta; break;
                    case 6: h.ScriptMorale += delta; break;
                    case 7: h.MovePoints = Math.Clamp(h.MovePoints + delta, 0, Math.Max(h.MaxMovePoints, h.MovePoints)); break;
                    case 8: h.Mana = Math.Clamp(h.Mana + delta, 0, Math.Max(Game.ScriptMaxMana(h), h.Mana)); break;
                    default: throw c.Error($"Invalid stat ID ({stat})");
                }
            });
        });
        Reg("TakeAwayHeroExp", c =>
        {
            var h = Hero(c, 0);
            h.Experience = Math.Max(0, h.Experience - c.Int(1));
        });
        Reg("LevelUpHero", c => { var h = Hero(c, 0); Change(() => Game.ScriptLevelUp(h)); });
        Reg("SetHeroesExpCoef", c => World.HeroesExpCoef = c.Number(0));

        Reg("HasHeroSkill", c =>
        {
            var h = Hero(c, 0);
            var id = Ids.Skills.Name(c.Int(1)) ?? "";
            c.ReturnBool(h.Perks.Contains(id) || h.Skills.Any(s => s.SkillId == id && s.Mastery > Mastery.None));
        });
        Reg("GetHeroSkillMastery", c => c.Return((int)Hero(c, 0).MasteryOf(Ids.Skills.Name(c.Int(1)) ?? "")));
        Reg("GiveHeroSkill", c =>
        {
            var h = Hero(c, 0);
            var id = Ids.Skills.Name(c.Int(1)) ?? throw c.Error($"Invalid skill ID ({c.Int(1)})");
            Change(() =>
            {
                if (Core.Skills.TryGetValue(id, out var sd) && sd.IsPerk) Game.ScriptLearnPerk(h, id);
                else
                {
                    var m = h.MasteryOf(id);
                    if (m < Mastery.Expert) Game.ScriptSetSkill(h, id, m + 1);
                }
            });
        });
        Reg("KnowHeroSpell", c => c.ReturnBool(Hero(c, 0).Spells.Contains(Ids.Spells.Name(c.Int(1)) ?? "")));
        Reg("TeachHeroSpell", c =>
        {
            var h = Hero(c, 0);
            var spell = Ids.Spells.Name(c.Int(1)) ?? throw c.Error($"Invalid spell ID ({c.Int(1)})");
            Change(() => Game.ScriptLearnSpell(h, spell));
        });

        Reg("HasArtefact", c =>
        {
            var h = Hero(c, 0);
            var art = Ids.Artifacts.Name(c.Int(1)) ?? "";
            bool needEquipped = c.Has(2) && c.Bool(2);
            c.ReturnBool(h.Artifacts.Contains(art));
        });
        Reg("GetHeroArtifactsCount", c => c.Return(Hero(c, 0).Artifacts.Count(a => a == (Ids.Artifacts.Name(c.Int(1)) ?? ""))));
        Reg("GetArtifactSetItemsCount", c =>
        {
            var h = Hero(c, 0);
            int set = c.Int(1);
            var setName = Core.ArtifactSets.Keys.ElementAtOrDefault(set);
            int n = setName != null ? Core.ArtifactSets[setName].Count(a => h.Artifacts.Contains(a)) : 0;
            c.Return(n);
        });
        Reg("GiveArtefact", c =>
        {
            var h = Hero(c, 0);
            var art = Ids.Artifacts.Name(c.Int(1)) ?? throw c.Error($"Invalid artifact ID ({c.Int(1)})");
            Change(() => Game.ScriptGiveArtifact(h, art));
            if (c.Has(2) && c.Bool(2)) World.Settings[$"bound_artifact:{h.Id}:{art}"] = "1";
        });
        Reg("RemoveArtefact", c =>
        {
            var h = Hero(c, 0);
            var art = Ids.Artifacts.Name(c.Int(1)) ?? "";
            Change(() => Game.ScriptRemoveArtifact(h, art));
        });

        Reg("GetHeroCreatures", c =>
        {
            var h = Hero(c, 0);
            var cr = CreatureName(c, 1);
            c.Return(h.Army.Live().Where(s => s.Creature == cr).Sum(s => s.Count));
        });
        Reg("AddHeroCreatures", c =>
        {
            var h = Hero(c, 0);
            var cr = CreatureName(c, 1);
            int n = c.Int(2);
            if (n <= 0) return;
            if (!h.Army.TryAdd(cr, n)) throw c.Error($"Hero \"{h.DefId}\" has no free army slot");
            GameEvents.Add(new H5.Adventure.Engine.ArmyChanged(h.Id, -1));
        });
        Reg("RemoveHeroCreatures", c =>
        {
            var h = Hero(c, 0);
            var cr = CreatureName(c, 1);
            RemoveCreatures(h.Army, cr, c.Int(2), keepLast: true);
            GameEvents.Add(new H5.Adventure.Engine.ArmyChanged(h.Id, -1));
        });
        Reg("GetHeroCreaturesTypes", c =>
        {
            var h = Hero(c, 0);
            var res = new List<LuaValue>();
            for (int i = 0; i < 7; i++)
            {
                var s = h.Army[i];
                res.Add(s is { Count: > 0 } ? Ids.Creatures.Id(s.Creature) : 0);
            }
            c.Return(res.ToArray());
        });

        Reg("IsHeroLootable", c => c.ReturnBool(!World.Settings.ContainsKey($"unlootable:{Hero(c, 0).Id}")));
        Reg("SetHeroLootable", c =>
        {
            var h = Hero(c, 0);
            if (c.Bool(1)) World.Settings.Remove($"unlootable:{h.Id}");
            else World.Settings[$"unlootable:{h.Id}"] = "1";
        });
        Reg("HasHeroWarMachine", c =>
        {
            var h = Hero(c, 0);
            c.ReturnBool(c.Int(1) switch { 1 => h.Ballista, 2 => true, 3 => h.FirstAidTent, 4 => h.AmmoCart, _ => false });
        });
        Reg("GiveHeroWarMachine", c => SetWarMachine(Hero(c, 0), c.Int(1), true));
        Reg("RemoveHeroWarMachine", c => SetWarMachine(Hero(c, 0), c.Int(1), false));
        Reg("SetHeroCombatScript", c => World.Settings[$"combat_script:{Hero(c, 0).Id}"] = c.Str(1));
        Reg("ResetHeroCombatScript", c => World.Settings.Remove($"combat_script:{Hero(c, 0).Id}"));
        Reg("GiveHeroBattleBonus", c =>
        {
            var h = Hero(c, 0);
            int type = c.Int(1), amount = c.Int(2);
            if (type == 0) h.NextBattleLuck += amount;
            else if (type == 1) h.NextBattleMorale += amount;
            else World.Settings[$"battle_bonus:{h.Id}:{type}"] = amount.ToString();
        });

        Reg("DeployReserveHero", c =>
        {
            var name = c.Str(0);
            var tile = TileArg(c, 1, 2, 3);
            if (Game.HeroByName(name) != null) throw c.Error($"Hero \"{name}\" is already deployed");
            var owner = ReserveOwner(name);
            if (owner < 0) throw c.Error($"Hero \"{name}\" is not belong to any player");
            if (!Core.Heroes.TryGetValue(name, out var def)) throw c.Error($"Hero with name \"{name}\" doesn't exist");
            Change(() => Game.ScriptDeployHero(def, owner, FreeTileNear(tile)));
        });
        Reg("UnreserveHero", c =>
        {
            var name = c.Str(0);
            foreach (var mp in Map.Players) mp.ReserveHeroes.Remove(name);
            if (!S.HeroPool.Contains(name) && Game.HeroByName(name) == null) S.HeroPool.Add(name);
        });
        Reg("GetHeroTown", c =>
        {
            var h = Hero(c, 0);
            c.Return(h.InTown >= 0 && Map.Objects[h.InTown].Name.Length > 0 ? Map.Objects[h.InTown].Name : LuaValue.Nil);
        });
        Reg("IsHeroInTown", c =>
        {
            var h = Hero(c, 0);
            bool garrisonOnly = c.Has(2) && c.Bool(2), visitorOnly = c.Has(1) && !c.Bool(1);
            c.ReturnBool(h.InTown >= 0 && (!garrisonOnly || h.Garrisoned) && (!visitorOnly || !h.Garrisoned));
        });
        Reg("IsHeroInBoat", c => c.ReturnBool(Hero(c, 0).InBoat));
        Reg("SinkHero", c =>
        {
            var h = Hero(c, 0);
            Change(() => Game.ScriptRemoveHero(h, "sunk"));
        });
        Reg("MakeHeroReturnToTavernAfterDeath", c => World.Settings[$"tavern_after_death:{c.Str(0)}"] = c.Bool(1) ? "1" : "0");
        Reg("MakeHeroNecromancer", c => World.Settings[$"necromancer:{Hero(c, 0).Id}"] = "1");
        Reg("LockMinHeroSkillsAndAttributes", c => World.Settings[$"lock_min_skills:{Hero(c, 0).Id}"] = "1");
        Reg("SetHeroRoleMode", c => World.Settings[$"role_mode:{Hero(c, 0).Id}"] = c.Int(1).ToString());
        Reg("SetHeroBiography", c => World.Settings[$"biography:{Hero(c, 0).Id}"] = c.Str(1));
        Reg("ControlHeroCustomAbility", c => World.Settings[$"custom_ability:{Hero(c, 0).Id}:{c.Int(1)}"] = c.Int(2).ToString());
        Reg("MakeHeroInteractWithObject", c =>
        {
            var h = Hero(c, 0);
            var o = Obj(c, 1);
            var at = o.ActiveTiles.FirstOrDefault(o.Pos);
            Change(() => Game.ScriptMoveHero(h, at, ignoreMovePoints: true));
        });

        Reg("CanMoveHero", c =>
        {
            var h = Hero(c, 0);
            var t = TileArg(c, 1, 2, 3, h.Pos.Floor);
            c.ReturnBool(Game.FindPath(h, t) != null);
        });
        Reg("CalcHeroMoveCost", c =>
        {
            var h = Hero(c, 0);
            var t = TileArg(c, 1, 2, 3, h.Pos.Floor);
            var path = Game.FindPath(h, t);
            if (path == null) { c.Return(-1); return; }
            int cost = 0;
            for (int i = 1; i < path.Count; i++) cost += Game.StepCost(h, path[i - 1], path[i]);
            c.Return(cost);
        });
        Reg("GetObjectsFromPath", c =>
        {
            var h = Hero(c, 0);
            var t = TileArg(c, 1, 2, 3, h.Pos.Floor);
            var path = Game.FindPath(h, t) ?? new List<Tile>();
            var names = path.SelectMany(p => Game.ObjectsAtTile(p)).Where(o => o.Name.Length > 0).Select(o => o.Name).Distinct();
            c.Return(StringArray(names));
        });
        Reg("MoveHero", c => MoveHeroCall(c, realTime: false));
        Reg("MoveHeroRealTime", c => MoveHeroCall(c, realTime: true));
    }

    void MoveHeroCall(HostContext c, bool realTime)
    {
        var h = Hero(c, 0);
        var t = TileArg(c, 1, 2, 3, h.Pos.Floor);
        if (t == h.Pos) return;
        // MoveHero uses the hero's movement points; MoveHeroRealTime moves regardless of them.
        Change(() => Game.ScriptMoveHero(h, t, ignoreMovePoints: realTime));
        Emit(realTime ? "hero_move_realtime" : "hero_move", ("hero", h.DefId), ("x", t.X), ("y", t.Y), ("floor", t.Floor));
    }

    void SetWarMachine(HeroState h, int type, bool give)
    {
        switch (type)
        {
            case 1: h.Ballista = give; break;
            case 3: h.FirstAidTent = give; break;
            case 4: h.AmmoCart = give; break;
        }
        GameEvents.Add(new H5.Adventure.Engine.ArmyChanged(h.Id, -1));
    }

    int ReserveOwner(string heroName)
    {
        foreach (var mp in Map.Players)
            if (mp.ReserveHeroes.Contains(heroName, StringComparer.OrdinalIgnoreCase)) return mp.Index;
        return -1;
    }

    /// <summary>Removes up to n creatures of a type; a hero must keep at least one creature.</summary>
    static void RemoveCreatures(Stack?[] army, string creature, int n, bool keepLast)
    {
        for (int i = army.Length - 1; i >= 0 && n > 0; i--)
        {
            var s = army[i];
            if (s is not { Count: > 0 } || s.Creature != creature) continue;
            int take = Math.Min(n, s.Count);
            if (keepLast && army.Live().Sum(x => x.Count) - take < 1) take = Math.Max(0, take - 1);
            s.Count -= take;
            n -= take;
            if (s.Count == 0) army[i] = null;
        }
    }

    /// <summary>The tile, or the nearest free passable one (heroes and creations are nudged like the original).</summary>
    Tile FreeTileNear(Tile t)
    {
        var f = Map.Floor(t.Floor);
        if (f == null) return t;
        for (int r = 0; r < 8; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var c = new Tile(t.X + dx, t.Y + dy, t.Floor);
                    if (!f.InBounds(c.X, c.Y) || f.IsBlocked(c.X, c.Y)) continue;
                    if (Game.HeroAtTile(c) != null || Game.ObjectsAtTile(c).Any()) continue;
                    return c;
                }
        return t;
    }
}
