using System.Text.RegularExpressions;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

// Object natives: validation order, error texts and return values follow the wrappers in
// H5_Game.exe (addresses in each comment; audit ledger in the W2 natives audit).
public sealed partial class AdventureScripting
{
    void RegisterObjectApi()
    {
        // 0x005c8520: "Object name is not specified"; heroes and map objects (FUN_005eaba0 finders).
        Reg("IsObjectExists", c =>
        {
            var name = ObjectNameArg(c, 0);
            c.ReturnBool(Game.HeroByName(name) != null || Game.ObjectByName(name) != null);
        });
        // 0x005caf40: returns x, y, floor of the packed position.
        Reg("GetObjectPosition", c =>
        {
            var name = ObjectNameArg(c, 0);
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { c.Return(h.Pos.X, h.Pos.Y, h.Pos.Floor); return; }
            // The buried Grail is a world object named OBJECT_GRAIL = 'grail' (advmap-startup.lua)
            // until it is dug up.
            if (o == null && name == "grail" && S.Grail is { } g && !S.GrailFound) { c.Return(g.X, g.Y, g.Floor); return; }
            if (o == null) throw c.Error($"Object \"{name}\" is not exists");
            var p = PositionOf(o);
            c.Return(p.X, p.Y, p.Floor);
        });
        // 0x005cb1b0: object vf4(world, result, pos, arg5); failure reasons of vf4 are not traced.
        Reg("SetObjectPosition", c =>
        {
            var name = ObjectNameArg(c, 0);
            var o = ObjOrNull(c, 0, out var h);
            if (h != null)
            {
                var t = TileArg(c, 1, 2, 3, h.Pos.Floor);
                Change(() => Game.ScriptTeleportHero(h, t));
                return;
            }
            if (o == null) throw c.Error($"Object \"{name}\" does not exist");
            var to = TileArg(c, 1, 2, 3, o.Pos.Floor);
            World.ObjectPlacements[o.Index] = new ObjectPlacement { X = to.X, Y = to.Y, Floor = to.Floor, Rot = o.Rot };
            Change(() => Game.ScriptMoveObject(o.Index, to));
        });
        // 0x005cb440
        Reg("SetObjectRotation", c =>
        {
            var name = ObjectNameArg(c, 0);
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { Emit("hero_rotation", ("hero", h.DefId), ("rotation", c.Number(1))); return; }
            if (o == null) throw c.Error($"Object \"{name}\" does not exist");
            float rot = (float)(c.Number(1) * Math.PI / 180.0);
            World.ObjectPlacements[o.Index] = new ObjectPlacement { X = o.Pos.X, Y = o.Pos.Y, Floor = o.Pos.Floor, Rot = rot };
            Change(() => Game.ScriptRotateObject(o.Index, rot));
        });
        // 0x005cb6d0
        Reg("GetObjectOwner", c =>
        {
            var name = ObjectNameArg(c, 0);
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { c.Return(ScriptPlayer(h.Owner)); return; }
            if (o == null) throw c.Error($"Object \"{name}\" not exists");
            c.Return(ScriptPlayer(OwnerOf(o)));
        });
        // 0x005cb910: player 0..8 (0 = neutral); flagged objects first, then heroes.
        Reg("SetObjectOwner", c =>
        {
            var name = ObjectNameArg(c, 0);
            var o = ObjOrNull(c, 0, out var h);
            if (h == null && o == null) throw c.Error($"Object \"{name}\" not exists");
            int p = c.Int(1);
            if (p < 0 || p > 8) throw c.Error($"Invalid player ID #{p}");
            int player = p - 1;
            if (o != null && IsFlagged(o))
            {
                if (S.Town(o.Index) is { } town) Change(() => Game.ScriptCaptureTown(null, town, player));
                else Change(() => Game.ScriptCaptureObject(o, player));
                return;
            }
            if (h == null) throw c.Error($"Object \"{name}\" is not a flagged object or hero");
            if (p == 0) throw c.Error($"Can`t make hero \"{name}\" neutral");
            Change(() => Game.ScriptSetHeroOwner(h, player));
        });
        // 0x005cacb0: failure reasons of the object's remove method are not traced.
        Reg("RemoveObject", c =>
        {
            var name = ObjectNameArg(c, 0);
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { Change(() => Game.ScriptRemoveHero(h, "script")); return; }
            if (o == null) throw c.Error($"Object \"{name}\" is not exists");
            Change(() => Game.ScriptRemoveObjectAndUnblock(o.Index));
        });
        // 0x005cbcd0: the object is checked before the region.
        Reg("IsObjectInRegion", c =>
        {
            var name = ObjectNameArg(c, 0);
            var o = ObjOrNull(c, 0, out var h);
            if (h == null && o == null) throw c.Error($"Object \"{name}\" is not exists");
            var reg = RegionArg(c, 1);
            c.ReturnBool(reg.Contains(h != null ? h.Pos : o!.Pos));
        });
        // 0x005cbf60: only type 0 (OBJECT_HERO) is accepted: the map's heroes in list order, unnamed ones skipped.
        Reg("GetObjectsInRegion", c =>
        {
            var reg = RegionArg(c, 0);
            int type = c.Int(1);
            if (type != 0) throw c.Error($"Invalid objects type #{type}");
            c.Return(StringArray(S.Heroes.Where(h => h.Alive && h.DefId.Length > 0 && reg.Contains(h.Pos)).Select(h => h.DefId)));
        });
        // 0x005cc2a0: the player's fog cell under the object.
        Reg("IsObjectVisible", c =>
        {
            int p = PlayerInGameArg(c, 0);
            var name = ObjectNameArg(c, 1);
            Tile? pos = Game.HeroByName(name)?.Pos ?? Game.ObjectByName(name)?.Pos;
            if (pos == null) throw c.Error($"Object \"{name}\" is not exists");
            c.ReturnBool(Explored(p, pos.Value));
        });
        // 0x005e0660: floors 0 then 1, objects in floor order; an object matches when the type name
        // of its shared contains the argument (std::search). Names are pushed unfiltered.
        Reg("GetObjectNamesByType", c =>
        {
            var type = c.Str(0);
            var names = new List<string>();
            for (int floor = 0; floor < 2; floor++)
            {
                foreach (var o in Map.Objects)
                {
                    if (o.Pos.Floor != floor || o.Kind == ObjectKind.Hero || Removed(o)) continue;
                    if (TypeNameMatches(SharedTypeName(o), type)) names.Add(o.Name);
                }
                foreach (var h in S.Heroes)
                    if (h.Alive && h.Pos.Floor == floor && TypeNameMatches(HeroClassName(h.Class), type)) names.Add(h.DefId);
            }
            c.Return(StringArray(names));
        });

        // Armies of objects (monsters, towns, garrisons, heroes)
        // 0x005cc590: total of the creature type in the army.
        Reg("GetObjectCreatures", c =>
        {
            var name = ObjectNameArg(c, 0);
            if (!ObjectExists(name)) throw c.Error($"Object \"{name}\" doesn`t exist");
            var cr = CreatureIdArg(c, 1);
            c.Return(ArmyOf(c, "Object \"{0}\" has no army").Where(s => s.Creature == cr).Sum(s => s.Count));
        });
        // 0x005ccec0: the creature is added through CGiveCreaturesCmd (slot argument not modelled).
        Reg("AddObjectCreatures", c =>
        {
            var name = ObjectNameArg(c, 0);
            if (!ObjectExists(name)) throw c.Error($"Object \"{name}\" doesn't exist");
            var cr = CreatureIdArg(c, 1);
            int n = c.Int(2);
            if (n < 1) throw c.Error("Quantity cannot be negative");
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { if (!h.Army.TryAdd(cr, n)) Log.Add($"AddObjectCreatures: {h.DefId} has no free slot"); return; }
            if (!HasArmy(o!)) throw c.Error($"Object \"{name}\" has no army");
            if (S.Town(o!.Index) is { } town) { if (!town.Garrison.TryAdd(cr, n)) Log.Add($"AddObjectCreatures: {o.Name} garrison is full"); return; }
            var guards = S.Obj(o.Index).Guards;
            var stack = guards.Find(g => g.Creature == cr);
            if (stack != null) stack.Count += n; else guards.Add(new Stack(cr, n));
        });
        // 0x005cd7b0: at most the present amount is removed; a hero keeps its last creature; a
        // monster losing all its creatures is removed from the map.
        Reg("RemoveObjectCreatures", c =>
        {
            var name = ObjectNameArg(c, 0);
            if (!ObjectExists(name)) throw c.Error($"Object \"{name}\" doesn't exist");
            var cr = CreatureIdArg(c, 1);
            int n = c.Int(2);
            if (n < 1) throw c.Error("Quantity cannot be negative");
            var o = ObjOrNull(c, 0, out var h);
            if (h != null) { RemoveCreatures(h.Army, cr, n, keepLast: true); return; }
            if (!HasArmy(o!)) throw c.Error($"Object \"{name}\" has no army");
            if (S.Town(o!.Index) is { } town) { RemoveCreatures(town.Garrison, cr, n, keepLast: false); return; }
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
        // 0x005cc820: the distinct creature types in slot order, padded with 0 to 7 return values.
        Reg("GetObjectCreaturesTypes", c =>
        {
            var name = ObjectNameArg(c, 0);
            if (!ObjectExists(name)) throw c.Error($"Object \"{name}\" doesn`t exist");
            var types = ArmyOf(c, "Object \"{0}\" has no army").Select(s => s.Creature).Distinct().ToList();
            var res = new LuaValue[7];
            for (int i = 0; i < 7; i++) res[i] = i < types.Count ? Ids.Creatures.Id(types[i]) : 0;
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
        // 0x005cd530: towns only (object vf 0x3c); the second argument is a creature id in [0, count).
        Reg("GetObjectDwellingCreatures", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" doesn't exist");
            int id = c.Int(1);
            if (id < 0 || id >= Ids.Creatures.Count) throw c.Error($"Invalid creature ID #{id}");
            c.Return(DwellingAvailable(Map.Objects[t.Object], Ids.Creatures.Name(id) ?? "", null));
        });
        // 0x005cd270: CSetDwellingCreaturesCmd.
        Reg("SetObjectDwellingCreatures", c =>
        {
            var t = TownArg(c, 0, "Town \"{0}\" doesn't exist");
            int id = c.Int(1);
            if (id < 0 || id >= Ids.Creatures.Count) throw c.Error($"Invalid creature ID #{id}");
            int n = c.Int(2);
            if (n < 0) throw c.Error("Quantity cannot be negative");
            DwellingAvailable(Map.Objects[t.Object], Ids.Creatures.Name(id) ?? "", n);
        });

        // Enabling / visiting
        Reg("IsObjectEnabled", c => c.ReturnBool(!World.DisabledObjects.ContainsKey(Obj(c, 0).Index)));
        // 0x005cde30: interactive objects (CEnableInteractiveObjectCmd), else heroes (CEnableHeroInteractionsCmd).
        Reg("SetObjectEnabled", c =>
        {
            var name = c.Str(0);
            var o = Game.ObjectByName(name);
            if (o == null || !IsInteractive(o))
            {
                // Heroes can be disabled too (HERO_TOUCH_TRIGGER works only for disabled heroes).
                if (Game.HeroByName(name) == null) throw c.Error($"Object \"{name}\" is not exists or is not interactive");
                o = Map.Objects.Find(x => x.Kind == ObjectKind.Hero && (x.Name == name || x.Hero?.Shared?.Id == name));
            }
            if (o == null) return;
            if (c.Bool(1)) World.DisabledObjects.Remove(o.Index);
            else World.DisabledObjects[o.Index] = 0;
        });
        // 0x005e54b0: CSetDisabledObjectModeCmd only sets the cursor mode; it does not disable the object.
        Reg("SetDisabledObjectMode", c =>
        {
            var name = c.Str(0);
            var o = Game.ObjectByName(name) ?? throw c.Error($"Object \"{name}\" does not exist!");
            if (!IsInteractive(o)) throw c.Error($"Object \"{name}\" is interactive!");
            int mode = c.Int(1);
            if (mode < 0 || mode > 3) throw c.Error($"Invalid disabled object mode #{mode}!");
            World.DisabledModes[o.Index] = mode;
        });
        // 0x005ce0f0: the object (or hero) must be disabled; the hero must be on the map.
        Reg("MarkObjectAsVisited", c =>
        {
            var name = c.Str(0);
            var o = Game.ObjectByName(name);
            if (o != null && !IsInteractive(o)) o = null;
            var asHero = Game.HeroByName(name);
            if (o == null && asHero == null) throw c.Error($"Object \"{name}\" is not exists or is not interactive");
            bool enabled = o != null ? !World.DisabledObjects.ContainsKey(o.Index) : HeroInteractionsEnabled(name);
            if (enabled) throw c.Error($"Object \"{name}\" is enabled, so can`t mark it as visited manualy");
            var heroName = c.Str(1);
            var h = Game.HeroByName(heroName) ?? throw c.Error($"Hero \"{heroName}\" is not on map");
            if (o == null) return;
            h.VisitedOnce.Add(o.Index);
            Emit("object_visited_mark", ("object", o.Name), ("hero", h.DefId));
        });
        // 0x005e6ca0: (region, autoMode 1..2, heroTownType, heroPlayer, heroName, objectName, enableType -1..1).
        Reg("SetRegionAutoObjectEnable", c =>
        {
            var reg = RegionArg(c, 0);
            int mode = c.Int(1), townType = c.Int(2), player = c.Int(3), enable = c.Int(6);
            if (mode < 1 || mode > 2) throw c.Error($"Invalid region auto action mode ({mode})!");
            if (townType != -1 && EnumTables.Town(townType) == null)
                throw c.Error($"Invalid hero town type ({townType}), must be -1 or defined constant!");
            if (player != -1 && (player < 1 || player > 8)) throw c.Error($"Incorrect player ID ({player}), must be -1 or defined constant!");
            if (enable < -1 || enable > 1) throw c.Error($"Invalid region auto action enable type ({enable}), must be -1, or 0, or 1!");
            World.Settings[$"region_auto:{reg.Name}:{mode}"] = $"{townType}|{player}|{c.Str(4)}|{c.Str(5)}|{enable}";
        });
        // 0x005e74e0: an empty name passes a null object to the command (no error, no effect).
        Reg("OverrideObjectTooltipNameAndDescription", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) return;
            var o = Game.ObjectByName(name) ?? throw c.Error($"Object \"{name}\" does not exist!");
            World.Settings[$"tooltip:{o.Index}"] = c.Str(1) + "|" + c.Str(2);
            Emit("tooltip_override", ("object", o.Name), ("name", c.Str(1)), ("description", c.Str(2)));
        });

        // Monsters
        // 0x005c8aa0: (name, type, count, x, y, floor, mood = 1, courage = 2, rotation = 0, …); no name check.
        Reg("CreateMonster", c =>
        {
            var name = c.Str(0);
            int typeId = c.Int(1);
            var cr = Ids.Creatures.Name(typeId) is { } n && typeId > 0 ? n : throw c.Error($"Invalid monster type id ({typeId})");
            int count = c.Int(2);
            var tile = PlacementCell(c, 3, 4, 5, "Can`t find an empty cell to place monster");
            int mood = c.Int(6), courage = c.Int(7);
            if (mood < 0 || mood > 3) throw c.Error($"Invalid monster`s mood #{mood}");
            if (courage < 0 || courage > 2) throw c.Error($"Invalid monster`s courage #{courage}");
            float rot = (float)(c.Int(8) * Math.PI / 180.0);
            if (!Core.Creatures.TryGetValue(cr, out var cd) || cd.MonsterSharedPath == null) throw c.Error($"Invalid monster type id ({typeId})");
            var o = new MapObject
            {
                Kind = ObjectKind.Monster, Tag = "AdvMapMonster", Pos = tile, Rot = rot, Name = name,
                SharedPath = cd.MonsterSharedPath, Shared = _catalog.Get(cd.MonsterSharedPath),
                Creature = cr, Amount = count, CustomAmount = true,
                Mood = MoodName(mood), Courage = CourageName(courage),
            };
            AddCreated(o, new CreatedObject { Kind = "monster", Name = name, SharedPath = o.SharedPath, X = tile.X, Y = tile.Y, Floor = tile.Floor, Rot = rot, Creature = cr, Count = count, Mood = o.Mood, Courage = o.Courage });
        });
        // 0x005ce490: an invalid selection type is only logged (not a script error).
        Reg("SetMonsterSelectionType", c =>
        {
            var name = c.Str(0);
            var o = Game.ObjectByName(name);
            if (o == null || o.Kind != ObjectKind.Monster) throw c.Error($"Object \"{name}\" is not exists or is not monster");
            if (!World.DisabledObjects.ContainsKey(o.Index)) throw c.Error($"Monster \"{name}\" is not disabled, cant change selection type");
            int type = c.Int(1);
            if (type < -1 || type > 2) { Log.Add($"Script error (SetMonsterSelectionType): Invalid selection type {type} , must be -1 to 2"); return; }
            World.Settings[$"monster_selection:{o.Index}"] = type.ToString();
        });
        Reg("SetMonsterNames", c => World.Settings[$"monster_names:{Obj(c, 0).Index}"] = $"{c.Int(1)}|{c.Str(2)}");
        // 0x005c9240: (monster, player -1..8, courage 0..2, mood 0..3).
        Reg("SetMonsterCourageAndMood", c =>
        {
            var name = c.Str(0);
            var o = Game.ObjectByName(name);
            if (o == null || o.Kind != ObjectKind.Monster) throw c.Error($"Monster \"{name}\" does not exist!");
            int player = c.Int(1);
            if (player < -1 || player > 8) throw c.Error($"Invalid player {player}!");
            int courage = c.Int(2);
            if (courage < 0 || courage > 2) throw c.Error($"Invalid monster`s courage #{courage}");
            int mood = c.Int(3);
            if (mood < 0 || mood > 3) throw c.Error($"Invalid monster`s mood #{mood}");
            if (player == -1)
            {
                o.Mood = MoodName(mood);
                o.Courage = CourageName(courage);
                World.MonsterMoods[o.Index] = o.Mood + "|" + o.Courage;
            }
            else World.Settings[$"monster_attitude:{o.Index}:{player}"] = MoodName(mood) + "|" + CourageName(courage);
        });
        // 0x005cf8c0: CGenerateMonstersCmd (0x0042b260): group count in [min, max] from the world
        // generator, each group on a random existing floor (rng & 1).
        Reg("GenerateMonsters", c =>
        {
            int typeId = c.Int(0);
            var cr = Ids.Creatures.Name(typeId) is { } crn && typeId > 0 ? crn : throw c.Error($"Invalid monster type id ({typeId})");
            int gmin = c.Int(1), gmax = c.Int(2), nmin = c.Int(3), nmax = c.Int(4);
            if (gmin < 0) throw c.Error("Minimum groups count can`t be negative");
            if (gmax < gmin) throw c.Error("Maximum groups count can`t be lesser then minimum");
            if (nmin < 1) throw c.Error("Minimum monsters count in group must pe positive");
            if (nmax < nmin) throw c.Error("Maximum monsters count in group can`t be lesser then minimum");
            if (!Core.Creatures.TryGetValue(cr, out var cd) || cd.MonsterSharedPath == null) return;
            int groups = gmin + (int)((uint)S.Rng.NextU64() % (uint)(gmax - gmin + 1));
            if (groups == 0 || (Map.Floor(0) == null && Map.Floor(1) == null)) return;
            var perFloor = new int[2];
            for (int left = groups; left > 0;)
            {
                int fl = (int)(S.Rng.NextU64() & 1);
                if (Map.Floor(fl) == null) continue;
                perFloor[fl]++;
                left--;
            }
            for (int fl = 0; fl < 2; fl++)
            {
                var f = Map.Floor(fl);
                if (f == null) continue;
                for (int g = 0; g < perFloor[fl]; g++)
                    for (int attempt = 0; attempt < 1000; attempt++)
                    {
                        var t = new Tile((int)((uint)S.Rng.NextU64() % (uint)f.Width), (int)((uint)S.Rng.NextU64() % (uint)f.Height), fl);
                        if (f.IsBlocked(t.X, t.Y) || f.IsWater(t.X, t.Y) || Game.HeroAtTile(t) != null || Game.ObjectsAtTile(t).Any()) continue;
                        int count = nmin + (int)((uint)S.Rng.NextU64() % (uint)(nmax - nmin + 1));
                        var o = new MapObject
                        {
                            Kind = ObjectKind.Monster, Tag = "AdvMapMonster", Pos = t, SharedPath = cd.MonsterSharedPath,
                            Shared = _catalog.Get(cd.MonsterSharedPath), Creature = cr, Amount = count, CustomAmount = true,
                            Mood = "MONSTER_MOOD_AGGRESSIVE", Courage = "MONSTER_COURAGE_CAN_FLEE_JOIN",
                        };
                        AddCreated(o, new CreatedObject { Kind = "monster", SharedPath = o.SharedPath, X = t.X, Y = t.Y, Floor = fl, Creature = cr, Count = o.Amount, Mood = o.Mood, Courage = o.Courage });
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
        // 0x005c8730: (name, artifact, x, y, floor).
        Reg("CreateArtifact", c =>
        {
            var name = c.Str(0);
            int id = c.Int(1);
            var art = (id > 0 ? Ids.Artifacts.Name(id) : null) ?? throw c.Error($"Invalid artifact ID #{id}");
            var tile = PlacementCell(c, 2, 3, 4, "Can`t find an empty cell to place artifact");
            var shared = ArtifactSharedPath(art) ?? throw c.Error($"Artifact {art} has no adventure map object");
            var o = new MapObject { Kind = ObjectKind.Artifact, Tag = "AdvMapArtifact", Pos = tile, Name = name, SharedPath = shared, Shared = _catalog.Get(shared), ArtifactId = art };
            AddCreated(o, new CreatedObject { Kind = "artifact", Name = name, SharedPath = shared, X = tile.X, Y = tile.Y, Floor = tile.Floor, Artifact = art });
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
        // 0x005d1810: (name, shared, x, y, floor, rotation = 0, …); the cell is not checked for emptiness.
        Reg("CreateStatic", c =>
        {
            var name = c.Str(0);
            var href = c.Str(1);
            var shared = H5.Core.Xdb.XdbRef.Parse(href).Path;
            if (shared.Length == 0 || _catalog.Get(shared).Tag == "missing") throw c.Error($"Not found static shared '{href}'!");
            var tile = FloorCell(c, 2, 3, 4);
            float rot = (float)(c.Int(5) * Math.PI / 180.0);
            AddStatic(name, shared, tile, rot);
        });
        // 0x005c9530: (name, townType, tier, owner, x, y, floor, rotation = 0); the dwelling comes from
        // the RPGRoot group DWELLINGS_<town>: tiers 1..3 → links 0..2, 4..8 → link 3 (military post,
        // tier 8 = all tiers).
        Reg("CreateDwelling", c =>
        {
            var name = c.Str(0);
            int townId = c.Int(1);
            var town = EnumTables.Town(townId) ?? throw c.Error($"Invalid town type {townId}!");
            int tier = c.Int(2), owner = c.Int(3);
            if (owner < -1 || owner > 8) throw c.Error($"Invalid player {owner}!");
            var tile = FloorCell(c, 4, 5, 6);
            var group = DwellingGroup(town);
            if (group.Count == 0) throw c.Error($"No dwellings shareds group for town {townId}!");
            int link = Math.Clamp(tier - 1, 0, 3);
            if (link >= group.Count) throw c.Error("Invalid group (not enought links)!");
            var sd = _catalog.Get(group[link]);
            if (sd.Tag != "AdvMapDwellingShared") throw c.Error("Non-dwelling shared link!");
            foreach (var off in sd.Blocked.Concat(sd.Active))
            {
                var cell = new Tile(tile.X + off.X, tile.Y + off.Y, tile.Floor);
                if (!CellEmpty(cell)) throw c.Error($"Cell is not empty: ({cell.X}, {cell.Y}, {cell.Floor})");
            }
            int postTier = tier < 1 ? 1 : tier >= 8 ? -1 : tier;
            var o = new MapObject
            {
                Kind = ObjectKind.Dwelling, Tag = "AdvMapDwelling", Pos = tile, Name = name, SharedPath = group[link], Shared = sd,
                Rot = (float)(c.Int(7) * Math.PI / 180.0), Owner = owner <= 0 ? -1 : owner - 1,
            };
            if (link == 3 && postTier > 0)
                o.CreaturesEnabled = Enumerable.Range(0, sd.DwellingCreatures.Count).Select(i => i == postTier - 4).ToArray();
            AddCreated(o, new CreatedObject { Kind = "dwelling", Name = name, SharedPath = o.SharedPath, X = tile.X, Y = tile.Y, Floor = tile.Floor, Rot = o.Rot });
        });
        // 0x005d1c50: (dwelling, townType, creature1..4 = 0); the dwelling takes the same link of the
        // new town's DWELLINGS group (the creature overrides are not modelled by the engine).
        Reg("ReplaceDwelling", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Dwelling name was not specified!");
            var o = Game.ObjectByName(name);
            if (o == null || o.Kind != ObjectKind.Dwelling) throw c.Error($"Dwelling \"{name}\" does not exist!");
            int townId = c.Int(1);
            var town = EnumTables.Town(townId) ?? throw c.Error($"Invalid town type {townId}!");
            var creatures = Enumerable.Range(2, 4).Select(i => c.Int(i)).Where(id => id > 0).Select(id => Ids.Creatures.Name(id) ?? "").Where(n => n.Length > 0).ToArray();
            int link = DwellingLink(o.SharedPath);
            if (link >= 0 && DwellingGroup(town) is { } g && link < g.Count) ApplyDwellingShared(o, g[link], reset: true);
            World.Settings[$"dwelling_replaced:{o.Index}"] = o.SharedPath + "|" + string.Join(",", creatures);
            Emit("dwelling_replaced", ("object", o.Name), ("town", town), ("creatures", string.Join(",", creatures)));
        });
        // 0x005e32c0: (name, player 1..8, floor, x, y, destFloor, destX, destY); no caravan engine yet.
        Reg("CreateCaravan", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Caravan name is not specified! You must specify caravan name to add creatures later!");
            int p = c.Int(1);
            if (p < 1 || p > 8) throw c.Error($"Incorrect player ID #{p}!");
            var from = new Tile(c.Int(3), c.Int(4), c.Int(2));
            var to = new Tile(c.Int(6), c.Int(7), c.Int(5));
            World.Settings[$"caravan:{name}"] = $"{p - 1}|{from}|{to}";
            Emit("caravan_created", ("name", name), ("player", p), ("from", from), ("to", to));
        });
        Reg("GetStandStatesCount", c => c.Return(2));
        Reg("GetStandState", c => c.Return(World.Settings.TryGetValue($"stand:{c.Str(0)}", out var v) && int.TryParse(v, out var i) ? i : 0));
        // 0x005d0570: the state index must be below the stand's <States> count.
        Reg("SetStandState", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Stand name is not specified");
            var o = Game.ObjectByName(name);
            if (o == null || o.Kind != ObjectKind.Stand) throw c.Error($"Stand \"{name}\" is not exists");
            int state = c.Int(1), states = o.Shared.Raw?.Element("States")?.Elements("Item").Count() ?? 0;
            if (state < 0 || state >= states) throw c.Error($"Invalid stand state index #{state}, stand \"{name}\" has only {states} states");
            World.Settings[$"stand:{name}"] = state.ToString();
        });

        // Regions and fog
        // 0x005d9540: the region must be a single cell.
        Reg("RegionToPoint", c =>
        {
            var r = RegionArg(c, 0);
            if (r.X1 != r.X2 || r.Y1 != r.Y2) throw c.Error($"Region \"{r.Name}\" is not a point");
            c.Return(r.X1, r.Y1, r.Floor);
        });
        Reg("IsRegionBlocked", c =>
        {
            var r = Region(c, 0);
            int p = c.Has(1) ? c.Int(1) : -1;
            c.ReturnBool(World.BlockedRegions.TryGetValue(r.Name, out var set) &&
                (set.Contains(0) || (p > 0 ? set.Contains(p) : set.Count >= 8)));
        });
        // 0x005d9a50: player -1 = every player (CSetRegionBlockForAllCmd), else 1..8.
        Reg("SetRegionBlocked", c =>
        {
            var r = RegionArg(c, 0);
            bool block = c.Bool(1);
            int p = c.Int(2);
            if (p != -1 && (p < 1 || p > 8)) throw c.Error($"Invalid player number #{p}");
            if (!World.BlockedRegions.TryGetValue(r.Name, out var set)) World.BlockedRegions[r.Name] = set = new HashSet<int>();
            if (p == -1)
            {
                set.Clear();
                if (block) for (int i = 1; i <= 8; i++) set.Add(i);
            }
            else if (block) set.Add(p);
            else { if (set.Remove(0)) for (int i = 1; i <= 8; i++) set.Add(i); set.Remove(p); }
            if (set.Count == 0) World.BlockedRegions.Remove(r.Name);
        });
        // 0x005d9df0: CModifyPlayerFogCmd over the region's cells.
        Reg("OpenRegionFog", c =>
        {
            int p = PlayerInGameArg(c, 0);
            var r = RegionArg(c, 1);
            var f = Map.Floor(r.Floor);
            if (f == null) throw c.Error($"Region \"{r.Name}\" is placed on non-existent floor #{r.Floor}");
            if (r.X1 < 0 || r.X2 > f.Width - 1 || r.Y1 < 0 || r.Y2 > f.Height - 1)
                throw c.Error($"Region \"{r.Name}\" is out of floor`s #{r.Floor} border");
            Change(() => Game.ScriptRevealTiles(p, r.Tiles()));
        });
        // 0x005da1e0: (x, y, floor, range, player); CModifyPlayerFogInCircleCmd.
        Reg("OpenCircleFog", c =>
        {
            int p = PlayerInGameArg(c, 4);
            int floor = c.Int(2);
            if (Map.Floor(floor) == null) throw c.Error($"Floor #{floor} is not exists");
            int range = c.Int(3);
            if (range < 0) throw c.Error("Range is negative");
            var t = new Tile(c.Int(0), c.Int(1), floor);
            Change(() => Game.ScriptReveal(p, t, range));
        });
        // 0x005df9d0: (onLand, onSea, player = -1); -1 = the local player; human players only.
        Reg("SetWarfogBehaviour", c =>
        {
            int arg = c.Int(2);
            var player = arg >= 0 ? (arg >= 1 && arg <= 8 ? Game.Player(arg - 1) : null) : Game.Player(S.CurrentPlayer);
            if (player == null) throw c.Error("No player!");
            if (!player.Human) throw c.Error("Specified player is not human player!");
            World.Settings[$"warfog_land:{player.Index + 1}"] = c.Bool(0) ? "1" : "0";
            World.Settings[$"warfog_sea:{player.Index + 1}"] = c.Bool(1) ? "1" : "0";
        });
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

    /// <summary>The army of a hero or object; with <paramref name="noArmy"/> only objects that have one (FUN_005c0710).</summary>
    IEnumerable<Stack> ArmyOf(HostContext c, string? noArmy = null)
    {
        var o = ObjOrNull(c, 0, out var h);
        if (h != null) return h.Army.Live();
        if (o == null || (noArmy != null && !HasArmy(o))) throw c.Error(string.Format(noArmy ?? "Object \"{0}\" has no army", c.Str(0)));
        if (S.Town(o.Index) is { } t) return t.Garrison.Live();
        return S.Obj(o.Index).Guards.Where(g => g.Count > 0);
    }

    // ---- exe argument checks shared by the object and town natives --------------------------

    /// <summary>The wrappers' first check: an empty name is "Object name is not specified".</summary>
    string ObjectNameArg(HostContext c, int i)
    {
        var name = c.Str(i);
        return name.Length > 0 ? name : throw c.Error("Object name is not specified");
    }

    bool ObjectExists(string name) => Game.HeroByName(name) != null || Game.ObjectByName(name) != null;

    /// <summary>IAdventureMap vf 0x24 lookup with the wrappers' two messages.</summary>
    MapRegion RegionArg(HostContext c, int i)
    {
        var name = c.Str(i);
        if (name.Length == 0) throw c.Error("Region name is not specified");
        return Data.RegionByName.TryGetValue(name, out var r) ? r : throw c.Error($"Region \"{name}\" is not exists");
    }

    /// <summary>Player 1..8 that takes part in the game (IsObjectVisible, Open*Fog); engine index.</summary>
    int PlayerInGameArg(HostContext c, int i)
    {
        int p = c.Int(i);
        if (p < 1 || p > 8) throw c.Error($"Invalid player ID #{p} (must be in [1,8] )");
        if (Game.Player(p - 1) == null) throw c.Error($"Player #{p} doesn`t taking part in the current game");
        return p - 1;
    }

    /// <summary>FUN_0089fbf0: a creature descriptor exists for the id.</summary>
    string CreatureIdArg(HostContext c, int i)
    {
        int id = c.Int(i);
        return id > 0 && Ids.Creatures.Name(id) is { } n ? n : throw c.Error($"Invalid creature ID #{id}");
    }

    /// <summary>Town by name (object vf 0x3c) with the native's own messages ({0} = name).</summary>
    TownState TownArg(HostContext c, int i, string notFound, bool checkEmpty = true)
    {
        var name = c.Str(i);
        if (checkEmpty && name.Length == 0) throw c.Error("Town name is not specified");
        var o = Game.ObjectByName(name);
        return (o != null ? S.Town(o.Index) : null) ?? throw c.Error(string.Format(notFound, name));
    }

    /// <summary>Floor 0..1 that exists and a cell inside it (creation natives).</summary>
    Tile FloorCell(HostContext c, int ix, int iy, int ifloor)
    {
        int x = c.Int(ix), y = c.Int(iy), floor = c.Int(ifloor);
        if (floor < 0 || floor > 1) throw c.Error($"Invalid floor ID #{floor}");
        var f = Map.Floor(floor) ?? throw c.Error($"Floor #{floor} is not exists");
        if (!f.InBounds(x, y)) throw c.Error($"Cell ({x},{y}) is not inside floor #{floor} borders");
        return new Tile(x, y, floor);
    }

    /// <summary>FloorCell, then the nearest empty cell (FUN_005c0f00) or the given error.</summary>
    Tile PlacementCell(HostContext c, int ix, int iy, int ifloor, string noCell)
    {
        var t = FreeTileNear(FloorCell(c, ix, iy, ifloor));
        var f = Map.Floor(t.Floor)!;
        if (f.IsBlocked(t.X, t.Y) || !CellEmpty(t)) throw c.Error(noCell);
        return t;
    }

    bool CellEmpty(Tile t)
    {
        var f = Map.Floor(t.Floor);
        return f != null && f.InBounds(t.X, t.Y) && Game.HeroAtTile(t) == null && !Game.ObjectsAtTile(t).Any();
    }

    /// <summary>Objects with an IAdvMapInteractive part (everything a hero can visit).</summary>
    static bool IsInteractive(MapObject o) => o.Kind is not (ObjectKind.Static or ObjectKind.Stand or ObjectKind.Unknown or ObjectKind.Hero);

    /// <summary>Objects with an IAdvMapFlagged part (SetObjectOwner).</summary>
    static bool IsFlagged(MapObject o) => o.Kind is ObjectKind.Town or ObjectKind.Mine or ObjectKind.AbandonedMine or ObjectKind.Dwelling
        or ObjectKind.Shipyard or ObjectKind.DwarvenWarren or ObjectKind.Garrison
        || (o.Kind == ObjectKind.Building && o.Shared.Type == "BUILDING_LIGHTHOUSE");

    /// <summary>Objects with an army (FUN_005c0710): monsters, towns, garrisons and guarded objects.</summary>
    // Army holders (FUN_005c0710 picks the object's army interface): monsters, towns, garrisons and mines
    // (H5 mines take a garrison; MMH55's H55_MineVisit reads mine armies), plus anything still guarded.
    bool HasArmy(MapObject o) => o.Kind is ObjectKind.Monster or ObjectKind.Town or ObjectKind.Garrison or ObjectKind.Mine or ObjectKind.AbandonedMine
        || S.Obj(o.Index).Guards.Count > 0;

    bool HeroInteractionsEnabled(string heroName)
    {
        var mapHero = Map.Objects.Find(x => x.Kind == ObjectKind.Hero && (x.Name == heroName || x.Hero?.Shared?.Id == heroName));
        return mapHero == null || !World.DisabledObjects.ContainsKey(mapHero.Index);
    }

    /// <summary>std::search of the argument in the type name; an empty type name never matches.</summary>
    static bool TypeNameMatches(string typeName, string arg) =>
        typeName.Length > 0 && arg.Length <= typeName.Length && typeName.Contains(arg, StringComparison.Ordinal);

    /// <summary>
    /// GetObjectNamesByType's type name of an object's shared: the building type of Mine, Building
    /// and Dwelling shareds (first enum value when absent), fixed names for Abandoned Mine, Prison
    /// and Seer Hut, the monster's creature, the town type; other shareds have none. Random towns and
    /// monsters are replaced by concrete ones when the world is created, so their resolved type is used.
    /// </summary>
    string SharedTypeName(MapObject o)
    {
        var raw = o.Shared.Raw;
        string Field(string name, string dflt)
        {
            var v = raw?.Elements(name).LastOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(v) ? dflt : v;
        }
        return o.Shared.Tag switch
        {
            "AdvMapMineShared" or "AdvMapBuildingShared" or "AdvMapDwellingShared" => Field("Type", "BUILDING_ABANDONED_MINE"),
            "AdvMapAbanMineShared" => "BUILDING_ABANDONED_MINE",
            "AdvMapPrisonShared" => "BUILDING_PRISON",
            "AdvMapSeerHutShared" => "BUILDING_SEER_HUT",
            "AdvMapMonsterShared" => S.Objects.TryGetValue(o.Index, out var os) && os.Creature is { Length: > 0 } cr ? cr : Field("Creature", "CREATURE_UNKNOWN"),
            "AdvMapTownShared" => S.Town(o.Index) is { Type.Length: > 0 } t ? t.Type : Field("Type", "TOWN_SPECIAL"),
            _ => "",
        };
    }

    static string HeroClassName(string cls) => cls.StartsWith("HERO_CLASS_", StringComparison.Ordinal) ? cls : "HERO_CLASS_NONE";

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

    // ---- dwelling groups (RPGRoot DWELLINGS_<town> → MapObjects/_(AdvMapSharedGroup)/Dwellings) ----

    readonly Dictionary<string, List<string>> _dwellingGroups = new(StringComparer.Ordinal);

    List<string> DwellingGroup(string town)
    {
        if (_dwellingGroups.Count == 0)
        {
            var root = System.Text.Encoding.UTF8.GetString(_game.ReadAllBytes("GameMechanics/RPGStats/RPGRoot.xdb"));
            foreach (Match m in Regex.Matches(root, "<ID>DWELLINGS_([A-Z_]+)</ID>\\s*<Group href=\"([^\"#]*)"))
            {
                var path = H5.Core.IO.PakFileSystem.Normalize(m.Groups[2].Value);
                var links = new List<string>();
                if (_game.TryReadAllBytes(path, out var g))
                    foreach (Match l in Regex.Matches(System.Text.Encoding.UTF8.GetString(g), "<Item href=\"([^\"#]*)"))
                        links.Add(H5.Core.Xdb.XdbRef.Parse(l.Groups[1].Value, path).Path);
                _dwellingGroups[m.Groups[1].Value] = links;
            }
        }
        var key = town switch
        {
            "TOWN_HEAVEN" => "HAVEN", "TOWN_NECROMANCY" => "NECROPOLIS", _ => town.Replace("TOWN_", ""),
        };
        return _dwellingGroups.GetValueOrDefault(key) ?? new List<string>();
    }

    /// <summary>Position of a dwelling shared in its town's group (its "level"), or -1.</summary>
    int DwellingLink(string sharedPath)
    {
        DwellingGroup("TOWN_HEAVEN");
        foreach (var links in _dwellingGroups.Values)
        {
            int i = links.FindIndex(l => string.Equals(l, sharedPath, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    /// <summary>Gives a dwelling another shared (ReplaceDwelling, also when loading a save).</summary>
    void ApplyDwellingShared(MapObject o, string sharedPath, bool reset)
    {
        o.SharedPath = sharedPath;
        o.Shared = _catalog.Get(sharedPath);
        if (!reset) return;
        var os = S.Obj(o.Index);
        var creatures = o.Shared.DwellingCreatures;
        os.Available = creatures.Select(cr => Core.Creatures.TryGetValue(cr, out var d) ? d.WeeklyGrowth : 0).ToArray();
    }

    /// <summary>CCreateStaticCmd: a static named like the source (CreateStatic, RazeTown, RazeBuilding).</summary>
    void AddStatic(string name, string sharedPath, Tile tile, float rot)
    {
        var o = new MapObject { Kind = ObjectKind.Static, Tag = "AdvMapStatic", Pos = tile, Rot = rot, Name = name, SharedPath = sharedPath, Shared = _catalog.Get(sharedPath) };
        AddCreated(o, new CreatedObject { Kind = "static", Name = name, SharedPath = sharedPath, X = tile.X, Y = tile.Y, Floor = tile.Floor, Rot = rot });
    }

    /// <summary>Adds a script-created object; an unnamed one gets the world's automatic name (no DBID part).</summary>
    void AddCreated(MapObject o, CreatedObject record)
    {
        World.CreatedObjects.Add(record);
        Change(() => Game.ScriptAddObject(o));
        if (o.Name.Length == 0) record.Name = o.Name = AutoName(o, "");
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
