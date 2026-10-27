using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

// Hero natives (tables 0x00f34c28 / 0x00f35000 of H5_Game.exe). Every wrapper looks the hero up with
// IAdventureMap vf 0x14 (any hero object: on the map, in a reserve or dead) and checks it with
// FUN_004e9280 (non-null, not destroyed); each wrapper has its own error text for that. Values the
// engine keeps per world (counts of skills/artifacts/spells) are the exe's hard-coded table sizes.
public sealed partial class AdventureScripting
{
    // FUN_008478b0 / FUN_008478a0 / FUN_00847890: valid ids are [0, count). The stock exe hard-codes
    // the RefTables sizes (0xdd skills, 0x61 artifacts, 0x161 spells); the MMH55 DLL patches these
    // constants to its larger tables, so the loaded table sizes are used.
    int ExeSkillCount => Ids.Skills.Count;
    int ExeArtifactCount => Ids.Artifacts.Count;
    int ExeSpellCount => Ids.Spells.Count;

    // ---- per-native error texts for the hero lookup -------------------------------------------
    const string HeroIsNotExists = "Hero \"{0}\" is not exists";
    const string HeroNotExists = "Hero \"{0}\" not exists";
    const string HeroWithNameDoesntExist = "Hero with name \"{0}\" doesn't exist";
    const string HeroDoesntExist = "Hero \"{0}\" doesn't exist";
    const string HeroDoesntExistBacktick = "Hero \"{0}\" doesn`t exist";

    /// <summary>The wrappers' common prologue: empty name → "Hero name is not specified", unknown → <paramref name="notExists"/>.</summary>
    HeroState HeroArg(HostContext c, int i, string notExists, bool checkEmpty = true)
    {
        var name = c.Str(i);
        if (checkEmpty && name.Length == 0) throw c.Error("Hero name is not specified");
        return Game.HeroByNameAnywhere(name) ?? throw c.Error(string.Format(notExists, name));
    }

    /// <summary>The adventure-map hero object (IWorld vf 0x100): only deployed, living heroes.</summary>
    bool OnMap(HeroState h) => h.Alive && S.Heroes.Contains(h);

    void RegisterHeroApi()
    {
        // 0x005c2f80: no argument errors; true iff the hero is in its owner's hero list (owner vf 0xc0).
        Reg("IsHeroAlive", c => c.ReturnBool(Game.HeroByName(c.Str(0)) is { Owner: >= 0 }));
        // 0x005c3190: no empty-name check (an empty name fails the lookup).
        Reg("GetHeroLevel", c => c.Return(HeroArg(c, 0, HeroWithNameDoesntExist, checkEmpty: false).Level));
        // 0x005c33a0: getter table 0x00fd9100 (10 entries, set up at 0x00dac670).
        Reg("GetHeroStat", c =>
        {
            var h = HeroArg(c, 0, HeroNotExists);
            int stat = c.Int(1);
            if (stat < 0 || stat >= 10) throw c.Error($"Incorrect stat id #{stat}");
            c.Return(stat switch
            {
                0 => (double)h.Experience,                                         // vf 0x1a4
                1 => Math.Max(0, h.Attack + WornBonus(h, a => a.Attack)),          // vf 0x10 = 0x00b4cc50 (>= 0)
                2 => Math.Max(0, h.Defence + WornBonus(h, a => a.Defence)),        // vf 0x14 = 0x00b4ccc0 (>= 0)
                3 => Math.Max(1, h.SpellPower + WornBonus(h, a => a.SpellPower)),  // vf 0x18 = 0x00b4ccf0 (>= 1)
                4 => Math.Max(1, h.Knowledge + WornBonus(h, a => a.Knowledge)),    // vf 0x1c = 0x00b4cd80 (>= 1)
                5 => Math.Clamp(Game.ScriptHeroLuck(h), -5, 5),                    // vf 0x13c = 0x00b4cb40
                6 => Math.Clamp(Game.ScriptHeroMorale(h), -5, 5),                  // vf 0x140 = 0x00b4cb60
                7 => h.MovePoints,                                                 // vf 0xf4
                8 => h.Mana,                                                       // vf 0x4
                _ => h.MaxMovePoints,                                              // 9: vf 0xfc (max for land/sea)
            });
        });
        // 0x005c3620: one world command per stat; the clamps are done in the wrapper.
        Reg("ChangeHeroStat", c =>
        {
            var h = HeroArg(c, 0, HeroNotExists);
            int stat = c.Int(1), delta = c.Int(2);
            switch (stat)
            {
                case 0:
                    // CGiveHeroExperienceCmd → hero vf 0x198 (0x00b50220): no HeroesExpCoef, capped at the
                    // experience of the world's maximum level.
                    if (delta < 0) throw c.Error("Experience can`t be negative");
                    Change(() => AddExperienceCapped(h, delta));
                    break;
                // CChangeHeroAttributeCmd → vf 0x1bc (0x00b4bdc0): plain addition to the base value.
                case 1: Change(() => Game.ScriptAddStat(h, "Attack", delta)); break;
                case 2: Change(() => Game.ScriptAddStat(h, "Defence", delta)); break;
                case 3: Change(() => Game.ScriptAddStat(h, "SpellPower", delta)); break;
                case 4: Change(() => Game.ScriptAddStat(h, "Knowledge", delta)); break;
                case 5: h.ScriptLuck += delta; break;    // vf 0x144: +0x40 += delta
                case 6: h.ScriptMorale += delta; break;  // vf 0x148: +0x44 += delta
                case 7: h.MovePoints = Math.Max(0, Math.Min(h.MaxMovePoints, h.MovePoints + delta)); break;
                case 8: h.Mana = Math.Max(0, Math.Min(Game.ScriptMaxMana(h), h.Mana + delta)); break;
                default: throw c.Error($"Invalid stat ID #{stat}");
            }
        });
        // 0x005e5a00 → hero vf 0x354 (0x00b56430): removes experience level by level.
        Reg("TakeAwayHeroExp", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            TakeAwayExperience(h, c.Int(1));
        });
        // 0x005c3c00: below level 40 the hero gets exactly the experience missing for the next level;
        // returns whether it was below 40.
        Reg("LevelUpHero", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            bool below = h.Level < 0x28;
            if (below) Change(() => AddExperienceCapped(h, Core.Experience.For(h.Level + 1) - h.Experience));
            c.ReturnBool(below);
        });
        // 0x005e2c70: (int)(coef * 100 + 0.5) posted as CSetHeroesExpCoefCmd (Execute 0x00428ef0 → world vf 0x1b0, clamp
        // 0..1000). Its only consumer is CMonsterFleeCmd (Adventure.ApplyHeroesExpCoef).
        Reg("SetHeroesExpCoef", c =>
        {
            int percent = (int)((float)c.Number(0) * 100f + 0.5f);
            Change(() => Game.SetHeroesExpCoef(percent));
        });

        // 0x005c3ee0 / 0x005c4140: hero vf 0x174 (0x00b4d150).
        Reg("HasHeroSkill", c =>
        {
            var h = HeroArg(c, 0, HeroNotExists);
            c.ReturnBool(SkillMastery(h, SkillArg(c, 1)) > 0);
        });
        Reg("GetHeroSkillMastery", c =>
        {
            var h = HeroArg(c, 0, HeroNotExists);
            c.Return(SkillMastery(h, SkillArg(c, 1)));
        });
        // 0x005c43a0: perks need vf 0x18c (learnable), skills a free slot (vf 0x1b8) or room to improve
        // (vf 0x1c4: below Expert); returns whether the skill was given.
        Reg("GiveHeroSkill", c =>
        {
            var h = HeroArg(c, 0, HeroNotExists);
            int sid = SkillArg(c, 1);
            var id = Ids.Skills.Name(sid) ?? "";
            bool isPerk = Core.Skills.TryGetValue(id, out var sd) && sd.IsPerk;
            bool can;
            if (isPerk) can = PerkLearnable(h, sd!);
            else if (h.Skills.Any(s => s.SkillId == id && s.Mastery > Mastery.None)) can = h.MasteryOf(id) < Mastery.Expert;
            else can = FreeSkillSlots(h) > 0;
            if (can && sid != 0)
                Change(() =>
                {
                    if (isPerk) Game.ScriptLearnPerk(h, id);
                    else Game.ScriptSetSkill(h, id, (Mastery)Math.Min(3, (int)h.MasteryOf(id) + 1));
                });
            c.ReturnBool(can);
        });
        // 0x005c4770: an out-of-range id is checked as 0; FUN_00a349f0 validates the spell.
        Reg("KnowHeroSpell", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            var spell = SpellArg(c, 1);
            c.ReturnBool(h.Spells.Contains(spell));
        });
        // 0x005c49e0: always sends CAddHeroSpellCmd; returns !vf 0xdc (spell not yet taught).
        Reg("TeachHeroSpell", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            var spell = SpellArg(c, 1);
            bool had = h.Spells.Contains(spell);
            Change(() => Game.ScriptLearnSpell(h, spell));
            c.ReturnBool(!had);
        });

        // 0x005c4cd0: worn (vf 0x74), and unless the 3rd argument is non-zero also the backpack (vf 0x78).
        Reg("HasArtefact", c =>
        {
            var h = HeroArg(c, 0, HeroWithNameDoesntExist);
            var art = ArtifactArg(c, 1, "Invalid artefact ID {0}");
            bool equippedOnly = c.Int(2) != 0;
            c.ReturnBool(h.Artifacts.Contains(art) || !equippedOnly && h.Backpack.Contains(art));
        });
        // 0x005c4f70: worn count (+ backpack count unless the 3rd argument is non-zero).
        Reg("GetHeroArtifactsCount", c =>
        {
            var h = HeroArg(c, 0, HeroWithNameDoesntExist);
            var art = ArtifactArg(c, 1, "Invalid artefact ID {0}");
            bool equippedOnly = c.Int(2) != 0;
            c.Return(h.Artifacts.Count(a => a == art) + (equippedOnly ? 0 : h.Backpack.Count(a => a == art)));
        });
        Reg("GetArtifactSetItemsCount", c =>
        {
            var h = Hero(c, 0);
            int set = c.Int(1);
            var setName = Core.ArtifactSets.Keys.ElementAtOrDefault(set);
            int n = setName != null ? Core.ArtifactSets[setName].Count(a => h.Artifacts.Contains(a)) : 0;
            c.Return(n);
        });
        // 0x005c5200: the 3rd argument marks the artifact bound to the hero (artifact +0x24).
        Reg("GiveArtefact", c =>
        {
            var h = HeroArg(c, 0, HeroWithNameDoesntExist);
            var art = ArtifactArg(c, 1, "Invalid artefact #{0}");
            Change(() => Game.ScriptGiveArtifact(h, art));
            if (c.Int(2) != 0) World.Settings[$"bound_artifact:{h.Id}:{art}"] = "1";
        });
        // 0x005c54d0: errors when the hero carries none (vf 0x7c).
        Reg("RemoveArtefact", c =>
        {
            var h = HeroArg(c, 0, HeroWithNameDoesntExist);
            var art = ArtifactArg(c, 1, "Invalid artefact #{0}");
            if (!h.Artifacts.Contains(art) && !h.Backpack.Contains(art)) throw c.Error($"Hero {c.Str(0)} hasn't such artefact!");
            Change(() => Game.ScriptRemoveArtifact(h, art));
        });

        // 0x005c5ed0: FUN_0045a390 sums every stack of the type.
        Reg("GetHeroCreatures", c =>
        {
            var h = HeroArg(c, 0, HeroDoesntExistBacktick);
            int id = c.Int(1);
            var cr = CreatureOrNull(id) ?? throw c.Error($"Invalid combat unit ID {id} [arg1]");
            c.Return(h.Army.Live().Where(s => s.Creature == cr).Sum(s => s.Count));
        });
        // 0x005c6140: count must be > 0; the slot (4th arg) is used when it is empty or holds the
        // same type (FUN_0045a520), else the army's default placement. Returns true.
        Reg("AddHeroCreatures", c =>
        {
            var h = HeroArg(c, 0, HeroDoesntExist);
            int id = c.Int(1);
            var cr = CreatureOrNull(id) ?? throw c.Error($"Invalid creature ID {id}");
            int n = c.Int(2), slot = c.Int(3);
            if (n <= 0) throw c.Error("Quantity cannot be negative");
            bool added;
            if (slot >= 0 && slot < h.Army.Length && h.Army[slot] is not { Count: > 0 })
            {
                h.Army[slot] = new Stack(cr, n);
                added = true;
            }
            else if (slot >= 0 && slot < h.Army.Length && h.Army[slot]!.Creature == cr)
            {
                h.Army[slot]!.Count += n;
                added = true;
            }
            else added = h.Army.TryAdd(cr, n);
            if (added) GameEvents.Add(new H5.Adventure.Engine.ArmyChanged(h.Id, -1));
            else Log.Add($"AddHeroCreatures: hero {h.DefId} has no free slot for {cr}");
            c.ReturnBool(true);
        });
        // 0x005c64a0: count must be > 0; never takes the last creature of an army made only of this
        // type; removal starts at the given slot (FUN_0045a440). Returns the number removed.
        Reg("RemoveHeroCreatures", c =>
        {
            var h = HeroArg(c, 0, HeroDoesntExist);
            int id = c.Int(1);
            var cr = CreatureOrNull(id) ?? throw c.Error($"Invalid creature ID {id}");
            int n = c.Int(2), slot = c.Int(3);
            if (n <= 0) throw c.Error("Quantity cannot be negative");
            int have = h.Army.Live().Where(s => s.Creature == cr).Sum(s => s.Count);
            if (n >= have)
            {
                n = have;
                int stacksOfType = h.Army.Count(s => s is { Count: > 0 } && s.Creature == cr);
                if (stacksOfType == h.Army.StackCount()) n--;
            }
            if (n > 0)
            {
                RemoveCreaturesFrom(h.Army, cr, n, slot);
                GameEvents.Add(new H5.Adventure.Engine.ArmyChanged(h.Id, -1));
            }
            c.Return(n);
        });
        // 0x005ccb70: the distinct creature types in slot order, padded with 0 to 7 values.
        Reg("GetHeroCreaturesTypes", c =>
        {
            var h = HeroArg(c, 0, HeroDoesntExistBacktick);
            var res = new List<LuaValue>();
            foreach (var s in h.Army)
            {
                if (s is not { Count: > 0 }) continue;
                int t = Ids.Creatures.Id(s.Creature);
                if (!res.Any(v => v.TryToNumber(out var d) && (int)d == t)) res.Add(t);
            }
            while (res.Count < 7) res.Add(0);
            c.Return(res.ToArray());
        });

        Reg("IsHeroLootable", c => c.ReturnBool(Hero(c, 0).Lootable));
        Reg("SetHeroLootable", c =>
        {
            var h = HeroArg(c, 0, HeroWithNameDoesntExist);
            // CSetHeroLootableCmd (Execute 0x00427a00) → hero vf 0xb0 (0x00b4b740): CHero +0x218, read by vf 0xac.
            bool lootable = c.Bool(1);
            Change(() => h.Lootable = lootable);
        });
        // 0x005c67f0 / 0x005c6a50: war machine ids 1..4 (FUN_0089fc10); Give returns whether the hero
        // did not have it (and gives it).
        Reg("HasHeroWarMachine", c =>
        {
            var h = HeroArg(c, 0, HeroDoesntExistBacktick);
            int wm = c.Int(1);
            if (wm <= 0 || wm >= EnumTables.WarMachines.Length) throw c.Error($"Invalid war machine ID #{wm}");
            c.ReturnBool(HasWarMachine(h, wm));
        });
        Reg("GiveHeroWarMachine", c =>
        {
            var h = HeroArg(c, 0, HeroDoesntExist);
            int wm = c.Int(1);
            if (wm <= 0 || wm >= EnumTables.WarMachines.Length) throw c.Error($"Invalid war machine ID #{wm}");
            bool give = !HasWarMachine(h, wm);
            if (give) SetWarMachine(h, wm, true);
            c.ReturnBool(give);
        });
        Reg("RemoveHeroWarMachine", c => SetWarMachine(Hero(c, 0), c.Int(1), false));
        // 0x005c6fd0 / 0x005c72e0. TODO(exe): the script resource lookup (FUN_00bf6c30/FUN_005d4a70,
        // "Script \"%s\" is not exists") is not reproduced.
        Reg("SetHeroCombatScript", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            if (h.Owner < 0) throw c.Error($"Hero \"{c.Str(0)}\" not belongs to any player");
            World.Settings[$"combat_script:{h.Id}"] = c.Str(1);
        });
        Reg("ResetHeroCombatScript", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            if (h.Owner < 0) throw c.Error($"Hero \"{c.Str(0)}\" not belongs to any player");
            World.Settings.Remove($"combat_script:{h.Id}");
        });
        Reg("GiveHeroBattleBonus", c =>
        {
            var h = Hero(c, 0);
            int type = c.Int(1), amount = c.Int(2);
            // HERO_BATTLE_BONUS_* index the hero's next-battle struct (luck, morale, attack, defence, hit points, initiative, speed).
            switch (type)
            {
                case 0: h.NextBattleLuck += amount; break;
                case 1: h.NextBattleMorale += amount; break;
                case 2: h.NextBattleAttack += amount; break;
                case 3: h.NextBattleDefence += amount; break;
                case 4: h.NextBattleHitPoints += amount; break;
                case 5: h.NextBattleInitiative += amount; break;
                case 6: h.NextBattleSpeed += amount; break;
            }
        });

        // 0x005c27f0: (hero, x, y, floor=0, rotation=0).
        Reg("DeployReserveHero", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Hero name is not specified");
            if (!Core.Heroes.TryGetValue(name, out var def) && Game.HeroByNameAnywhere(name) == null)
                throw c.Error($"Hero \"{name}\" is not exists");
            int owner = ReserveOwner(name);
            var deployed = Game.HeroByName(name);
            if (owner < 0 && deployed == null) throw c.Error($"Hero \"{name}\" is not belong to any player");
            if (owner < 0) throw c.Error($"Hero \"{name}\" is not in reserve of player #{ScriptPlayer(deployed!.Owner)}, to which he belongs");
            if (deployed != null) throw c.Error($"Hero \"{name}\" is already deployed");
            int floor = c.Int(3);
            if (floor < 0 || floor > 1) throw c.Error($"Invalid floor number #{floor}");
            if (Map.Floor(floor) == null) throw c.Error($"Floor #{floor} is not exists");
            var at = FreeTileOrNull(new Tile(c.Int(1), c.Int(2), floor));
            if (at is not { } tile) throw c.Error("Can`t find an empty cell to deploy hero");
            def ??= Core.Heroes[Game.HeroByNameAnywhere(name)!.DefId];
            Change(() => Game.ScriptDeployHero(def, owner, tile));
        });
        // 0x005c2c90: owner player vf 0x170 takes the hero out of its reserve.
        Reg("UnreserveHero", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Hero name is not specified");
            if (!Core.Heroes.ContainsKey(name) && Game.HeroByNameAnywhere(name) == null)
                throw c.Error($"Hero \"{name}\" is not exists");
            int owner = ReserveOwner(name);
            var deployed = Game.HeroByName(name);
            if (owner < 0 && deployed == null) throw c.Error($"Hero \"{name}\" is not belong to any player");
            if (owner < 0) throw c.Error($"Hero \"{name}\" is not in reserve of player #{ScriptPlayer(deployed!.Owner)}, to which he belongs");
            foreach (var mp in Map.Players) mp.ReserveHeroes.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        });
        // 0x005d0a90: the town (hero vf 0x54) only when the hero is its garrison hero (town slot 1).
        Reg("GetHeroTown", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            if (OnMap(h) && h.InTown >= 0 && h.Garrisoned && Map.Objects[h.InTown].Name.Length > 0)
                c.Return(Map.Objects[h.InTown].Name);
        });
        // 0x005c9c50 (hero, town, checkGate=1, checkGarrison=1): the hero name is only compared with the
        // town's gate (slot 0) and garrison (slot 1) heroes, it is not validated.
        Reg("IsHeroInTown", c =>
        {
            var townName = c.Str(1);
            var o = Game.ObjectByName(townName);
            var t = o != null ? S.Town(o.Index) : null;
            if (t == null) throw c.Error($"Town \"{townName}\" does not exist!");
            bool gate = c.Int(2) != 0, garrison = c.Int(3) != 0;
            var name = c.Str(0);
            bool Is(HeroState? x) => x != null && (x.DefId == name || x.Name == name);
            var gateHero = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && !x.Garrisoned);
            var garrisonHero = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && x.Garrisoned);
            c.ReturnBool(gate && Is(gateHero) || garrison && Is(garrisonHero));
        });
        // 0x005e3580: false for a hero that is not on the map.
        Reg("IsHeroInBoat", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            c.ReturnBool(OnMap(h) && h.InBoat);
        });
        // 0x005e37f0: only a hero on the map sinks; otherwise nothing happens.
        Reg("SinkHero", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            if (OnMap(h)) Change(() => Game.ScriptRemoveHero(h, "sunk"));
        });
        // 0x005e4fc0 (name, enable, 3rd flag=0): no hero validation.
        Reg("MakeHeroReturnToTavernAfterDeath", c =>
            World.Settings[$"tavern_after_death:{c.Str(0)}"] = (c.Int(1) != 0 ? "1" : "0") + (c.Int(2) != 0 ? ",1" : ",0"));
        // 0x005e4d50: necromancy level 0..4.
        Reg("MakeHeroNecromancer", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            int level = c.Int(1);
            if (level < 0 || level > 4) throw c.Error($"Invalid necromancy level {level}!");
            // CMakeHeroNecromancerCmd (Execute 0x0042a140) → hero vf 0x364 (0x00b4c9a0, +0x2a8): read as the
            // necromancy level of heroes without the skill (Adventure.NecromancyLevel). GAP: the Execute only sets it
            // when two bytes of the owner's record (player vf 0x14 → +0x1cc) are 0 — meaning not traced, ignored.
            Change(() => h.ScriptNecromancyLevel = level);
        });
        Reg("LockMinHeroSkillsAndAttributes", c => World.Settings[$"lock_min_skills:{Hero(c, 0).Id}"] = "1");
        // 0x005e7260: modes other than 1 and 2 become 0.
        Reg("SetHeroRoleMode", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            int mode = c.Int(1);
            World.Settings[$"role_mode:{h.Id}"] = (mode == 1 ? 1 : mode == 2 ? 2 : 0).ToString();
        });
        Reg("SetHeroBiography", c => World.Settings[$"biography:{Hero(c, 0).Id}"] = c.Str(1));
        // 0x005d2560 (hero, ability 1..4, mode -1..1): the mode is checked first, then the index.
        Reg("ControlHeroCustomAbility", c =>
        {
            int ability = c.Int(1), mode = c.Int(2);
            if (mode < -1 || mode > 1) throw c.Error($"Invalid hero custom ability mode {mode}!");
            if (ability < 1 || ability > 4) throw c.Error($"Invalid her custom ability index {ability}!");
            var h = HeroArg(c, 0, HeroIsNotExists);
            World.Settings[$"custom_ability:{h.Id}:{ability}"] = mode.ToString();
        });
        // 0x005d2220: CMakeHeroInteractWithObjectCmd → adventure hero vf 0x74 (visit without moving);
        // nothing happens for a hero that is not on the map.
        Reg("MakeHeroInteractWithObject", c =>
        {
            var h = HeroArg(c, 0, HeroIsNotExists);
            var objName = c.Str(1);
            if (objName.Length == 0) throw c.Error("Object name is not specified");
            var o = Game.ObjectByName(objName);
            var oh = o == null ? Game.HeroByName(objName) : null;
            if (o == null && oh == null) throw c.Error($"Object \"{objName}\" does not exist!");
            if (!OnMap(h) || o == null) return;
            Change(() => Game.InteractWithObject(h, o.Index));
        });

        // 0x005ce790: AI heroes only; floor -1 = the hero's floor.
        Reg("CanMoveHero", c =>
        {
            var h = MovableHero(c, aiOnly: true);
            var t = new Tile(c.Int(1), c.Int(2), FloorArg(c, 3, h, "Incorrect floor number #{0}"));
            c.ReturnBool(Game.FindPath(h, t) != null);
        });
        // 0x005ceaf0: the hero is looked up as a map object; X/Y are range checked; -1 without a path.
        Reg("CalcHeroMoveCost", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0) throw c.Error("Hero name is not specified");
            var h = Game.HeroByName(name);
            if (h == null)
            {
                if (Game.ObjectByName(name) != null) throw c.Error($"Object \"{name}\" in not a hero");
                throw c.Error($"Object \"{name}\" is not exists");
            }
            int floor = FloorArg(c, 3, h, "Incorrect floor number #{0}");
            var f = Map.Floor(floor)!;
            int x = c.Int(1), y = c.Int(2);
            if (x < 0 || x >= f.Width) throw c.Error($"X is out of range (must be in [0,{f.Width - 1}], now is {x}");
            if (y < 0 || y >= f.Height) throw c.Error($"Y is out of range (must be in [0,{f.Height - 1}], now is {y}");
            var path = Game.FindPath(h, new Tile(x, y, floor));
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

    // ---- helpers ------------------------------------------------------------------------------

    int WornBonus(HeroState h, Func<ArtifactDef, int> stat) =>
        h.Artifacts.Sum(a => Core.Artifacts.TryGetValue(a, out var d) ? stat(d) : 0);

    /// <summary>Skill argument of HasHeroSkill/GetHeroSkillMastery/GiveHeroSkill: [0, 0xdd).</summary>
    int SkillArg(HostContext c, int i)
    {
        int id = c.Int(i);
        if (id < 0 || id >= ExeSkillCount) throw c.Error($"Skill with id #{id} not exists");
        return id;
    }

    /// <summary>Spell argument of KnowHeroSpell/TeachHeroSpell: out-of-range ids are checked as 0, then FUN_00a349f0.</summary>
    string SpellArg(HostContext c, int i)
    {
        int raw = c.Int(i);
        int id = raw < 0 || raw >= ExeSpellCount ? 0 : raw;
        var name = Ids.Spells.Name(id);
        if (name == null || !SpellValid(id, name)) throw c.Error($"Invalid spell id #{raw}");
        return name;
    }

    /// <summary>
    /// FUN_00a349f0: runic and warcry spells are valid; otherwise spell 67 (Dark Ritual ability) and
    /// the SPECIAL / NONE schools are not.
    /// </summary>
    bool SpellValid(int id, string name)
    {
        var school = Core.Spell(name)?.School ?? (id == 0 ? "MAGIC_SCHOOL_NONE" : "");
        if (school is "MAGIC_SCHOOL_RUNIC" or "MAGIC_SCHOOL_WARCRIES") return true;
        if (id == 0x43) return false;
        return school is not ("MAGIC_SCHOOL_SPECIAL" or "MAGIC_SCHOOL_NONE");
    }

    string ArtifactArg(HostContext c, int i, string invalid)
    {
        int id = c.Int(i);
        if (id < 0 || id >= ExeArtifactCount) throw c.Error(string.Format(invalid, id));
        return Ids.Artifacts.Name(id) ?? "";
    }

    /// <summary>FUN_0089fbf0: 0 &lt; id &lt; count and the creature record exists.</summary>
    string? CreatureOrNull(int id) => id > 0 && Ids.Creatures.Name(id) is { } n && n.Length > 0 ? n : null;

    static readonly string[] RaceTowns =
        { "", "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_INFERNO", "TOWN_NECROMANCY", "TOWN_DUNGEON", "TOWN_FORTRESS", "TOWN_STRONGHOLD" };
    // FUN_00875550: race → racial skill id.
    static readonly int[] RacialSkill = { 0, 0xd, 0x10, 0x11, 0xe, 0xf, 0x12, 0x97, 0xac };

    /// <summary>
    /// Hero vf 0x174 (0x00b4d150): mastery of a skill, 1 for a learned perk; the racial skill gets +1
    /// with the Pendant of Mastery (artifact 15) worn; values outside 0..4 read as 0.
    /// </summary>
    int SkillMastery(HeroState h, int sid)
    {
        if (sid <= 0 || h.Skills.Count + h.Perks.Count == 0) return 0;
        var id = Ids.Skills.Name(sid) ?? "";
        int m = h.Perks.Contains(id) ? 1 : (int)h.MasteryOf(id);
        int race = Array.IndexOf(RaceTowns, h.Town);
        if (race > 0 && sid == RacialSkill[race] && Ids.Artifacts.Name(15) is { } pendant && h.Artifacts.Contains(pendant)) m++;
        return m < 0 || m > 4 ? 0 : m;
    }

    /// <summary>Hero vf 0x1b8 (0x00b4d380): 5 minus the known non-perk skills, racial skills not counted.</summary>
    int FreeSkillSlots(HeroState h)
    {
        var racial = RacialSkill.Where(r => r > 0).Select(r => Ids.Skills.Name(r)).ToHashSet();
        return 5 - h.Skills.Count(s => s.Mastery > Mastery.None && !racial.Contains(s.SkillId));
    }

    /// <summary>
    /// Hero vf 0x18c (0x00b4e510): the perk is in the hero's learnable list (vf 0x188). Approximation:
    /// not yet learned, its basic skill known, and the class prerequisites met.
    /// </summary>
    bool PerkLearnable(HeroState h, SkillDef perk)
    {
        if (h.Perks.Contains(perk.Id)) return false;
        if (perk.BasicSkillId.Length > 0 && h.MasteryOf(perk.BasicSkillId) == Mastery.None) return false;
        return perk.Prerequisites.All(h.Perks.Contains);
    }

    /// <summary>GiveHeroExperience: hero vf 0x198 (0x00b50220) caps the total at the max-level experience.</summary>
    void AddExperienceCapped(HeroState h, long amount)
    {
        int maxLevel = Map.HeroMaxLevel > 0 ? Map.HeroMaxLevel : 40;
        long cap = Core.Experience.For(maxLevel);
        long target = Math.Min(h.Experience + amount, Math.Max(cap, h.Experience));
        if (target > h.Experience) Game.ScriptAddExperienceRaw(h, target - h.Experience);
    }

    /// <summary>
    /// TakeAwayHeroExp (hero vf 0x354 = 0x00b56430): experience is removed level by level; each level
    /// lost lowers the level. TODO(exe): the skill and attribute lost per level (vf 0x338/0x340/0x308)
    /// are not reproduced.
    /// </summary>
    static void TakeAwayExperience(HeroState h, int amount)
    {
        var table = new ExperienceTable();
        while (amount > 0)
        {
            if (h.Level < 2 && h.Experience <= amount) amount = (int)h.Experience;
            long drop = Math.Max(0, h.Experience - table.For(h.Level));
            if (amount <= drop) { h.Experience -= amount; return; }
            h.Level--;
            h.Experience -= drop;
            amount -= (int)drop;
        }
    }

    /// <summary>Hero argument of MoveHero/MoveHeroRealTime/CanMoveHero (0x005cef50 / 0x005cf310 / 0x005ce790).</summary>
    HeroState MovableHero(HostContext c, bool aiOnly)
    {
        var h = HeroArg(c, 0, HeroIsNotExists);
        if (h.Owner < 0 || Game.Player(h.Owner) == null) throw c.Error($"Hero \"{c.Str(0)}\" is not belong to any player");
        if (aiOnly && Game.Player(h.Owner) is { Human: true }) throw c.Error("Can`t use function for heroes, that belong to human player");
        if (!OnMap(h)) throw c.Error($"Hero \"{c.Str(0)}\" is not on map");
        return h;
    }

    /// <summary>Floor argument: -1..1, -1 = the hero's floor, and the floor must exist.</summary>
    int FloorArg(HostContext c, int i, HeroState h, string incorrect)
    {
        int floor = c.Int(i);
        if (floor < -1 || floor > 1) throw c.Error(string.Format(incorrect, floor));
        if (floor == -1) floor = h.Pos.Floor;
        if (Map.Floor(floor) == null) throw c.Error($"Floor #{floor} is not exists");
        return floor;
    }

    void MoveHeroCall(HostContext c, bool realTime)
    {
        // MoveHero sets an AI movement target (AI heroes only); MoveHeroRealTime moves any hero along
        // its path at once. A missing path is only logged.
        var h = MovableHero(c, aiOnly: !realTime);
        var t = new Tile(c.Int(1), c.Int(2), FloorArg(c, 3, h, "Incorrect floor number #{0}"));
        if (Game.FindPath(h, t) == null)
        {
            Log.Add(realTime
                ? $"Can`t find suitable real-time path for hero \"{c.Str(0)}\" to cell [{t.X},{t.Y}] on floor #{t.Floor}"
                : $"Can`t find suitable path (just now, but set target) for hero \"{c.Str(0)}\" to cell [{t.X},{t.Y}] on floor #{t.Floor}");
            if (realTime) return;
        }
        if (t == h.Pos) return;
        Change(() => Game.ScriptMoveHero(h, t, ignoreMovePoints: realTime));
        Emit(realTime ? "hero_move_realtime" : "hero_move", ("hero", h.DefId), ("x", t.X), ("y", t.Y), ("floor", t.Floor));
    }

    /// <summary>Hero vf 0x70 (0x00b4cfd0): the hero's war machine list holds the type; every hero has a catapult.</summary>
    static bool HasWarMachine(HeroState h, int type) =>
        type switch { 1 => h.Ballista, 2 => true, 3 => h.FirstAidTent, 4 => h.AmmoCart, _ => false };

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

    /// <summary>
    /// FUN_0045a440: removes up to n creatures of a type, starting with <paramref name="slot"/> (-1 or
    /// out of range = the last slot) and then from the last slot down; a stack the army may not lose
    /// entirely keeps one creature.
    /// </summary>
    static void RemoveCreaturesFrom(Stack?[] army, string creature, int n, int slot)
    {
        int last = army.Length - 1;
        if (slot < 0 || slot > last) slot = last;
        for (int k = last; k >= 0 && n > 0; k--)
        {
            int i = k == last ? slot : k < slot ? k : k + 1;
            var s = army[i];
            if (s is not { Count: > 0 } || s.Creature != creature) continue;
            int take = Math.Min(n, s.Count);
            if (take == s.Count && army.StackCount() == 1) take = s.Count - 1;
            s.Count -= take;
            n -= take;
            if (s.Count == 0) army[i] = null;
        }
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
    Tile FreeTileNear(Tile t) => FreeTileOrNull(t) ?? t;

    /// <summary>The tile, or the nearest free passable one (FUN_005c0f00); null when there is none.</summary>
    Tile? FreeTileOrNull(Tile t)
    {
        var f = Map.Floor(t.Floor);
        if (f == null) return null;
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
        return null;
    }
}
