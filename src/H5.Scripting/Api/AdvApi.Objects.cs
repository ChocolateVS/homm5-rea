using System.Text.RegularExpressions;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

public sealed partial class AdventureScripting
{
    void RegisterObjectApi()
    {
        Reg("IsObjectExists", c =>
        {
            var name = c.Str(0);
            c.ReturnBool(Game.HeroByName(name) != null || Game.ObjectByName(name) != null);
        });
        Reg("GetObjectPosition", c =>
        {
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { c.Return(h.Pos.X, h.Pos.Y, h.Pos.Floor); return; }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" is not exists");
            var p = PositionOf(o);
            c.Return(p.X, p.Y, p.Floor);
        });
        Reg("SetObjectPosition", c =>
        {
            var o = ObjOrNull(c, 0, out var h);
            if (h != null)
            {
                var t = TileArg(c, 1, 2, 3, h.Pos.Floor);
                Change(() => Game.ScriptTeleportHero(h, t));
                return;
            }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" is not exists");
            var to = TileArg(c, 1, 2, 3, o.Pos.Floor);
            World.ObjectPlacements[o.Index] = new ObjectPlacement { X = to.X, Y = to.Y, Floor = to.Floor, Rot = o.Rot };
            Change(() => Game.ScriptMoveObject(o.Index, to));
        });
        Reg("SetObjectRotation", c =>
        {
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { Emit("hero_rotation", ("hero", h.DefId), ("rotation", c.Number(1))); return; }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" not exists");
            float rot = (float)(c.Number(1) * Math.PI / 180.0);
            World.ObjectPlacements[o.Index] = new ObjectPlacement { X = o.Pos.X, Y = o.Pos.Y, Floor = o.Pos.Floor, Rot = rot };
            Change(() => Game.ScriptRotateObject(o.Index, rot));
        });
        Reg("GetObjectOwner", c =>
        {
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { c.Return(ScriptPlayer(h.Owner)); return; }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" doesn`t exist");
            c.Return(ScriptPlayer(OwnerOf(o)));
        });
        Reg("SetObjectOwner", c =>
        {
            var o = ObjOrNull(c, 0, out var h);
            int p = c.Int(1);
            int player = p <= 0 ? -1 : p - 1;
            if (h != null)
            {
                if (player < 0) throw c.Error($"Can`t make hero \"{h.DefId}\" neutral");
                Change(() => Game.ScriptSetHeroOwner(h, player));
                return;
            }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" is not a flagged object or hero");
            if (S.Town(o.Index) is { } town) Change(() => Game.ScriptCaptureTown(null, town, player));
            else Change(() => Game.ScriptCaptureObject(o, player));
        });
        Reg("RemoveObject", c =>
        {
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { Change(() => Game.ScriptRemoveHero(h, "script")); return; }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" is not exists");
            Change(() => Game.ScriptRemoveObjectAndUnblock(o.Index));
        });
        Reg("IsObjectInRegion", c =>
        {
            var reg = Region(c, 1);
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { c.ReturnBool(reg.Contains(h.Pos)); return; }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" is not exists");
            c.ReturnBool(reg.Contains(o.Pos));
        });
        Reg("GetObjectsInRegion", c =>
        {
            var reg = Region(c, 0);
            int type = c.Int(1);
            IEnumerable<string> names = type == 0
                ? S.Heroes.Where(h => h.Alive && reg.Contains(h.Pos)).Select(h => h.DefId)
                : Map.Objects.Where(o => o.Name.Length > 0 && reg.Contains(o.Pos) && !Removed(o)).Select(o => o.Name);
            c.Return(StringArray(names));
        });
        Reg("IsObjectVisible", c =>
        {
            int p = PlayerArg(c, 0);
            var name = c.Str(1);
            Tile? pos = Game.HeroByName(name)?.Pos ?? Game.ObjectByName(name)?.Pos;
            if (pos == null) throw c.Error($"Object \"{name}\" is not exists");
            c.ReturnBool(Explored(p, pos.Value));
        });
        Reg("GetObjectNamesByType", c =>
        {
            var type = c.Str(0);
            c.Return(StringArray(Map.Objects.Where(o => o.Name.Length > 0 && !Removed(o) &&
                (o.Shared.Type == type || o.Kind.ToString().Equals(type, StringComparison.OrdinalIgnoreCase) || o.Tag == type))
                .Select(o => o.Name)));
        });

        // Armies of objects (monsters, towns, garrisons, heroes)
        Reg("GetObjectCreatures", c =>
        {
            var cr = CreatureName(c, 1);
            c.Return(ArmyOf(c).Where(s => s.Creature == cr).Sum(s => s.Count));
        });
        Reg("AddObjectCreatures", c =>
        {
            var cr = CreatureName(c, 1);
            int n = c.Int(2);
            var o = ObjOrNull(c, 0, out var h);
            if (n <= 0) return;
            if (h != null) { if (!h.Army.TryAdd(cr, n)) throw c.Error($"Hero \"{h.DefId}\" has no army slot"); return; }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" has no army");
            if (S.Town(o.Index) is { } town) { if (!town.Garrison.TryAdd(cr, n)) throw c.Error("Town garrison is full"); return; }
            var guards = S.Obj(o.Index).Guards;
            var stack = guards.Find(g => g.Creature == cr);
            if (o.Kind == ObjectKind.Monster && stack == null && guards.Count > 0)
                throw c.Error($"Monster \"{o.Name}\" can only get creatures of its own type");
            if (stack != null) stack.Count += n; else guards.Add(new Stack(cr, n));
        });
        Reg("RemoveObjectCreatures", c =>
        {
            var cr = CreatureName(c, 1);
            int n = c.Int(2);
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { RemoveCreatures(h.Army, cr, n, keepLast: true); return; }
            if (o == null) throw c.Error($"Object \"{c.Str(0)}\" has no army");
            if (S.Town(o.Index) is { } town) { RemoveCreatures(town.Garrison, cr, n, keepLast: false); return; }
            var guards = S.Obj(o.Index).Guards;
            foreach (var g in guards.Where(g => g.Creature == cr))
            {
                int take = Math.Min(n, g.Count);
                g.Count -= take;
                n -= take;
            }
            guards.RemoveAll(g => g.Count <= 0);
            if (o.Kind == ObjectKind.Monster && guards.Count == 0) Change(() => Game.ScriptRemoveObjectAndUnblock(o.Index));
        });
        Reg("GetObjectCreaturesTypes", c =>
        {
            var army = ArmyOf(c).ToList();
            var res = new LuaValue[7];
            for (int i = 0; i < 7; i++) res[i] = i < army.Count ? Ids.Creatures.Id(army[i].Creature) : 0;
            c.Return(res);
        });
        Reg("GetObjectArmySlotCreature", c =>
        {
            var army = ArmyOf(c).ToList();
            int slot = c.Int(1);
            if (slot < 0 || slot >= army.Count) { c.Return(0, 0); return; }
            c.Return(Ids.Creatures.Id(army[slot].Creature), army[slot].Count);
        });
        Reg("DenyGarrisonCreaturesTakeAway", c => World.Settings[$"deny_garrison:{Obj(c, 0).Index}"] = c.Bool(1) ? "1" : "0");
        Reg("GetObjectDwellingCreatures", c =>
        {
            var o = Obj(c, 0);
            var cr = CreatureName(c, 1);
            c.Return(DwellingAvailable(o, cr, null));
        });
        Reg("SetObjectDwellingCreatures", c =>
        {
            var o = Obj(c, 0);
            var cr = CreatureName(c, 1);
            DwellingAvailable(o, cr, Math.Max(0, c.Int(2)));
        });

        // Enabling / visiting
        Reg("IsObjectEnabled", c => c.ReturnBool(!World.DisabledObjects.ContainsKey(Obj(c, 0).Index)));
        Reg("SetObjectEnabled", c =>
        {
            var name = c.Str(0);
            var o = Game.ObjectByName(name);
            if (o == null)
            {
                // Heroes can be disabled too (HERO_TOUCH_TRIGGER works only for disabled heroes).
                var mapHero = Map.Objects.Find(x => x.Kind == ObjectKind.Hero && (x.Name == name || x.Hero?.Shared?.Id == name));
                if (mapHero == null && Game.HeroByName(name) == null) throw c.Error($"Object \"{name}\" is not exists or is not interactive");
                o = mapHero;
            }
            if (o == null) return;
            if (c.Bool(1)) World.DisabledObjects.Remove(o.Index);
            else World.DisabledObjects[o.Index] = World.DisabledObjects.GetValueOrDefault(o.Index, 0);
        });
        Reg("SetDisabledObjectMode", c =>
        {
            var o = Obj(c, 0);
            int mode = c.Int(1);
            if (World.DisabledObjects.ContainsKey(o.Index) || mode != 0) World.DisabledObjects[o.Index] = mode;
        });
        Reg("MarkObjectAsVisited", c =>
        {
            var o = Obj(c, 0);
            var h = Hero(c, 1);
            if (!World.DisabledObjects.ContainsKey(o.Index)) throw c.Error($"Object \"{o.Name}\" is enabled, so can`t mark it as visited manualy");
            h.VisitedOnce.Add(o.Index);
            Emit("object_visited_mark", ("object", o.Name), ("hero", h.DefId));
        });
        Reg("SetRegionAutoObjectEnable", c =>
        {
            var reg = Region(c, 0);
            World.Settings[$"region_auto:{reg.Name}:{c.Int(1)}"] = $"{c.Int(2)}|{(c.Has(3) ? c.Int(3) : -1)}";
        });
        Reg("OverrideObjectTooltipNameAndDescription", c =>
        {
            var o = Obj(c, 0);
            World.Settings[$"tooltip:{o.Index}"] = c.Str(1) + "|" + c.Str(2);
            Emit("tooltip_override", ("object", o.Name), ("name", c.Str(1)), ("description", c.Str(2)));
        });

        // Monsters
        Reg("CreateMonster", c =>
        {
            var name = c.Str(0);
            var cr = CreatureName(c, 1);
            int count = Math.Max(1, c.Int(2));
            var tile = FreeTileNear(TileArg(c, 3, 4, 5));
            int mood = c.Has(6) ? c.Int(6) : 1, courage = c.Has(7) ? c.Int(7) : 2;
            float rot = (float)((c.Has(8) ? c.Number(8) : 0) * Math.PI / 180.0);
            if (Game.ObjectByName(name) != null && name.Length > 0) throw c.Error($"Object with name \"{name}\" already exists");
            if (!Core.Creatures.TryGetValue(cr, out var cd) || cd.MonsterSharedPath == null) throw c.Error($"Invalid monster type id ({c.Int(1)})");
            var o = new MapObject
            {
                Kind = ObjectKind.Monster, Tag = "AdvMapMonster", Pos = tile, Rot = rot, Name = name,
                SharedPath = cd.MonsterSharedPath, Shared = _catalog.Get(cd.MonsterSharedPath),
                Creature = cr, Amount = count, CustomAmount = true,
                Mood = MoodName(mood), Courage = CourageName(courage),
            };
            World.CreatedObjects.Add(new CreatedObject { Kind = "monster", Name = name, SharedPath = o.SharedPath, X = tile.X, Y = tile.Y, Floor = tile.Floor, Rot = rot, Creature = cr, Count = count, Mood = o.Mood, Courage = o.Courage });
            Change(() => Game.ScriptAddObject(o));
        });
        Reg("SetMonsterSelectionType", c =>
        {
            var o = Obj(c, 0);
            if (!World.DisabledObjects.ContainsKey(o.Index)) throw c.Error($"Monster \"{o.Name}\" is not disabled, cant change selection type");
            World.Settings[$"monster_selection:{o.Index}"] = c.Int(1).ToString();
        });
        Reg("SetMonsterNames", c => World.Settings[$"monster_names:{Obj(c, 0).Index}"] = $"{c.Int(1)}|{c.Str(2)}");
        Reg("SetMonsterCourageAndMood", c =>
        {
            var o = Obj(c, 0);
            if (o.Kind != ObjectKind.Monster) throw c.Error($"Object \"{o.Name}\" is not exists or is not monster");
            o.Mood = MoodName(c.Int(2));
            o.Courage = CourageName(c.Int(3));
            World.MonsterMoods[o.Index] = o.Mood + "|" + o.Courage;
        });
        Reg("GenerateMonsters", c =>
        {
            var cr = CreatureName(c, 0);
            int gmin = c.Int(1), gmax = Math.Max(gmin, c.Int(2)), nmin = c.Int(3), nmax = Math.Max(nmin, c.Int(4));
            int groups = gmin + (int)((uint)S.Rng.NextU64() % (uint)(gmax - gmin + 1));
            for (int g = 0; g < groups; g++)
            {
                var f = Map.Floor(0);
                if (f == null) break;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    var t = new Tile((int)((uint)S.Rng.NextU64() % (uint)f.Width), (int)((uint)S.Rng.NextU64() % (uint)f.Height), 0);
                    if (f.IsBlocked(t.X, t.Y) || f.IsWater(t.X, t.Y) || Game.HeroAtTile(t) != null || Game.ObjectsAtTile(t).Any()) continue;
                    int count = nmin + (int)((uint)S.Rng.NextU64() % (uint)(nmax - nmin + 1));
                    if (!Core.Creatures.TryGetValue(cr, out var cd) || cd.MonsterSharedPath == null) break;
                    var o = new MapObject
                    {
                        Kind = ObjectKind.Monster, Tag = "AdvMapMonster", Pos = t, SharedPath = cd.MonsterSharedPath,
                        Shared = _catalog.Get(cd.MonsterSharedPath), Creature = cr, Amount = Math.Max(1, count), CustomAmount = true,
                        Mood = "MONSTER_MOOD_AGGRESSIVE", Courage = "MONSTER_COURAGE_CAN_FLEE_JOIN",
                    };
                    World.CreatedObjects.Add(new CreatedObject { Kind = "monster", SharedPath = o.SharedPath, X = t.X, Y = t.Y, Floor = 0, Creature = cr, Count = o.Amount, Mood = o.Mood, Courage = o.Courage });
                    Change(() => Game.ScriptAddObject(o));
                    break;
                }
            }
        });
        Reg("RemoveAllMonsters", c =>
        {
            foreach (var o in Map.Objects.Where(o => o.Kind == ObjectKind.Monster && !Removed(o)).ToList())
                Change(() => Game.ScriptRemoveObjectAndUnblock(o.Index));
        });
        Reg("CalcAverageMonstersTier", c =>
        {
            var tiers = Map.Objects.Where(o => o.Kind == ObjectKind.Monster && !Removed(o))
                .SelectMany(o => S.Obj(o.Index).Guards).Select(g => Core.Creatures.TryGetValue(g.Creature, out var d) ? d.Tier : 0).ToList();
            c.Return(tiers.Count == 0 ? 0 : tiers.Average());
        });
        Reg("GetGuardsTier", c =>
        {
            var o = Obj(c, 0);
            var tiers = S.Obj(o.Index).Guards.Select(g => Core.Creatures.TryGetValue(g.Creature, out var d) ? d.Tier : 0);
            var t = L.NewTable();
            int i = 0;
            foreach (var tier in tiers) t.Set(i++, tier);
            c.Return(t);
        });

        // Object creation
        Reg("CreateArtifact", c =>
        {
            var name = c.Str(0);
            var art = Ids.Artifacts.Name(c.Int(1)) ?? throw c.Error($"Invalid artifact ID ({c.Int(1)})");
            var tile = FreeTileNear(TileArg(c, 2, 3, 4));
            var shared = ArtifactSharedPath(art) ?? throw c.Error($"Artifact {art} has no adventure map object");
            var o = new MapObject { Kind = ObjectKind.Artifact, Tag = "AdvMapArtifact", Pos = tile, Name = name, SharedPath = shared, Shared = _catalog.Get(shared), ArtifactId = art };
            World.CreatedObjects.Add(new CreatedObject { Kind = "artifact", Name = name, SharedPath = shared, X = tile.X, Y = tile.Y, Floor = tile.Floor, Artifact = art });
            Change(() => Game.ScriptAddObject(o));
        });
        Reg("CreateTreasure", c =>
        {
            var name = c.Str(0);
            int type = c.Int(1), amount = c.Int(2);
            var tile = FreeTileNear(TileArg(c, 3, 4, 5));
            var shared = TreasureSharedPath(type) ?? throw c.Error($"Invalid treasure type ({type})");
            var o = new MapObject { Kind = ObjectKind.Treasure, Tag = "AdvMapTreasure", Pos = tile, Name = name, SharedPath = shared, Shared = _catalog.Get(shared), TreasureCustom = amount > 0, TreasureAmount = amount };
            World.CreatedObjects.Add(new CreatedObject { Kind = "treasure", Name = name, SharedPath = shared, X = tile.X, Y = tile.Y, Floor = tile.Floor, Count = amount });
            Change(() => Game.ScriptAddObject(o));
        });
        Reg("CreateStatic", c =>
        {
            var name = c.Str(0);
            var shared = H5.Core.Xdb.XdbRef.Parse(c.Str(1)).Path;
            var tile = TileArg(c, 2, 3, 4);
            var o = new MapObject { Kind = ObjectKind.Static, Tag = "AdvMapStatic", Pos = tile, Name = name, SharedPath = shared, Shared = _catalog.Get(shared) };
            World.CreatedObjects.Add(new CreatedObject { Kind = "static", Name = name, SharedPath = shared, X = tile.X, Y = tile.Y, Floor = tile.Floor });
            Change(() => Game.ScriptAddObject(o));
        });
        Reg("CreateDwelling", c =>
        {
            var name = c.Str(0);
            var shared = H5.Core.Xdb.XdbRef.Parse(c.Str(1)).Path;
            var tile = TileArg(c, 2, 3, 4);
            var o = new MapObject { Kind = ObjectKind.Dwelling, Tag = "AdvMapDwelling", Pos = tile, Name = name, SharedPath = shared, Shared = _catalog.Get(shared), Owner = c.Has(5) ? c.Int(5) - 1 : -1 };
            World.CreatedObjects.Add(new CreatedObject { Kind = "dwelling", Name = name, SharedPath = shared, X = tile.X, Y = tile.Y, Floor = tile.Floor });
            Change(() => Game.ScriptAddObject(o));
        });
        Reg("ReplaceDwelling", c =>
        {
            var o = Obj(c, 0);
            var town = EnumTables.Town(c.Int(1)) ?? "";
            var creatures = Enumerable.Range(2, Math.Max(0, c.Count - 2)).Select(i => Ids.Creatures.Name(c.Int(i)) ?? "").Where(n => n.Length > 0).ToArray();
            World.Settings[$"dwelling_replaced:{o.Index}"] = town + "|" + string.Join(",", creatures);
            var os = S.Obj(o.Index);
            os.Available = creatures.Select(cr => Core.Creatures.TryGetValue(cr, out var d) ? d.WeeklyGrowth : 0).ToArray();
            Emit("dwelling_replaced", ("object", o.Name), ("town", town), ("creatures", string.Join(",", creatures)));
        });
        Reg("CreateCaravan", c =>
        {
            var name = c.Str(0);
            int player = PlayerArg(c, 1);
            var from = new Tile(c.Int(3), c.Int(4), c.Int(2));
            var to = new Tile(c.Int(6), c.Int(7), c.Int(5));
            World.Settings[$"caravan:{name}"] = $"{player}|{from}|{to}";
            Emit("caravan_created", ("name", name), ("player", player + 1), ("from", from), ("to", to));
        });
        Reg("GetStandStatesCount", c => c.Return(2));
        Reg("GetStandState", c => c.Return(World.Settings.TryGetValue($"stand:{c.Str(0)}", out var v) && int.TryParse(v, out var i) ? i : 0));
        Reg("SetStandState", c => World.Settings[$"stand:{c.Str(0)}"] = c.Int(1).ToString());

        // Regions and fog
        Reg("RegionToPoint", c =>
        {
            var r = Region(c, 0);
            c.Return((r.X1 + r.X2) / 2, (r.Y1 + r.Y2) / 2, r.Floor);
        });
        Reg("IsRegionBlocked", c =>
        {
            var r = Region(c, 0);
            int p = c.Has(1) ? c.Int(1) : -1;
            c.ReturnBool(World.BlockedRegions.TryGetValue(r.Name, out var set) && (set.Contains(0) || (p > 0 && set.Contains(p))));
        });
        Reg("SetRegionBlocked", c =>
        {
            var r = Region(c, 0);
            bool block = c.Bool(1);
            int p = c.Has(2) ? c.Int(2) : -1;
            int who = p <= 0 ? 0 : p;
            if (!World.BlockedRegions.TryGetValue(r.Name, out var set)) World.BlockedRegions[r.Name] = set = new HashSet<int>();
            if (block) set.Add(who);
            else if (who == 0) set.Clear();
            else set.Remove(who);
            if (set.Count == 0) World.BlockedRegions.Remove(r.Name);
        });
        Reg("OpenRegionFog", c =>
        {
            int p = PlayerArg(c, 0);
            var r = Region(c, 1);
            int cx = (r.X1 + r.X2) / 2, cy = (r.Y1 + r.Y2) / 2;
            int radius = Math.Max(r.X2 - r.X1, r.Y2 - r.Y1) / 2 + 1;
            Change(() => Game.ScriptReveal(p, new Tile(cx, cy, r.Floor), radius));
        });
        Reg("OpenCircleFog", c =>
        {
            var t = TileArg(c, 0, 1, 2);
            int range = c.Int(3);
            int p = PlayerArg(c, 4);
            Change(() => Game.ScriptReveal(p, t, range));
        });
        Reg("SetWarfogBehaviour", c => { World.Settings["warfog_land"] = c.Int(0).ToString(); World.Settings["warfog_sea"] = c.Int(1).ToString(); });
        Reg("AllowOpenFogOfWarForAlly", c => World.Settings[$"ally_fog:{c.Int(0)}"] = c.Bool(1) ? "1" : "0");
    }

    bool Removed(MapObject o) => S.Objects.TryGetValue(o.Index, out var os) && os.Removed;

    int OwnerOf(MapObject o)
    {
        if (S.Town(o.Index) is { } t) return t.Owner;
        return S.Objects.TryGetValue(o.Index, out var os) ? os.Owner : o.Owner;
    }

    bool Explored(int player, Tile t)
    {
        var p = Game.Player(player);
        var f = Map.Floor(t.Floor);
        if (p == null || f == null || t.Floor >= p.Explored.Count) return false;
        int i = t.Y * f.Width + t.X;
        return i >= 0 && i < p.Explored[t.Floor].Length && p.Explored[t.Floor][i];
    }

    IEnumerable<Stack> ArmyOf(HostContext c)
    {
        var o = ObjOrNull(c, 0, out var h);
        if (h != null) return h.Army.Live();
        if (o == null) throw c.Error($"Object \"{c.Str(0)}\" has no army");
        if (S.Town(o.Index) is { } t) return t.Garrison.Live();
        return S.Obj(o.Index).Guards.Where(g => g.Count > 0);
    }

    /// <summary>Gets (value null) or sets the available creatures of a type in a town or dwelling.</summary>
    int DwellingAvailable(MapObject o, string creature, int? set)
    {
        if (S.Town(o.Index) is { } town)
        {
            for (int tier = 1; tier <= 7; tier++)
                for (int variant = 0; variant < 3; variant++)
                    if (Game.DwellingCreature(town, tier, variant) == creature)
                    {
                        if (set != null) town.Available[tier - 1] = set.Value;
                        return town.Available[tier - 1];
                    }
            return 0;
        }
        var os = S.Obj(o.Index);
        var list = o.Shared.DwellingCreatures;
        int idx = list.IndexOf(creature);
        if (idx < 0 || idx >= os.Available.Length) return 0;
        if (set != null) os.Available[idx] = set.Value;
        return os.Available[idx];
    }

    static string MoodName(int m) => m switch
    {
        0 => "MONSTER_MOOD_FRIENDLY", 1 => "MONSTER_MOOD_AGGRESSIVE", 2 => "MONSTER_MOOD_HOSTILE", _ => "MONSTER_MOOD_WILD",
    };

    static string CourageName(int c) => c switch
    {
        0 => "MONSTER_COURAGE_ALWAYS_JOIN", 1 => "MONSTER_COURAGE_ALWAYS_FIGHT", _ => "MONSTER_COURAGE_CAN_FLEE_JOIN",
    };

    readonly Dictionary<string, string?> _artifactShared = new(StringComparer.Ordinal);

    string? ArtifactSharedPath(string artifact)
    {
        if (_artifactShared.Count == 0)
        {
            var text = System.Text.Encoding.UTF8.GetString(_game.ReadAllBytes("GameMechanics/RefTables/Artifacts.xdb"));
            foreach (Match m in Regex.Matches(text, @"<ID>([A-Z_0-9]+)</ID>(.*?)</obj>", RegexOptions.Singleline))
            {
                var sh = Regex.Match(m.Groups[2].Value, "<ArtifactShared href=\"([^\"#]*)");
                _artifactShared[m.Groups[1].Value] = sh.Success && sh.Groups[1].Value.Length > 0 ? H5.Core.IO.PakFileSystem.Normalize(sh.Groups[1].Value) : null;
            }
        }
        return _artifactShared.GetValueOrDefault(artifact);
    }

    string? TreasureSharedPath(int type)
    {
        var want = type switch
        {
            0 => "TREASURE_CRYSTAL", 1 => "TREASURE_GEMS", 2 => "TREASURE_GOLD", 3 => "TREASURE_MERCURY", 4 => "TREASURE_ORE",
            5 => "TREASURE_SULFUR", 6 => "TREASURE_WOOD", 8 => "TREASURE_CAMPFIRE", 9 => "TREASURE_CHEST",
            10 => "TREASURE_SEA_CHEST", 11 => "TREASURE_FLOATSAM", 13 => "TREASURE_SHIPWRECK", _ => null,
        };
        if (want == null) return null;
        // Reuse a placed treasure's descriptor of that type when the map has one, else the stock one.
        var placed = Map.Objects.Find(o => o.Kind == ObjectKind.Treasure && o.Shared.Type == want);
        return placed?.SharedPath ?? _game.Paths.FirstOrDefault(p => p.StartsWith("MapObjects/", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".(AdvMapTreasureShared).xdb", StringComparison.OrdinalIgnoreCase)
            && _catalog.Get(p).Type == want);
    }
}
