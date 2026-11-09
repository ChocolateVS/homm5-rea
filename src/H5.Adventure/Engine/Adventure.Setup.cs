using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using HeroState = H5.Adventure.State.HeroState;
using H5.Core.Data;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    static readonly string[] PlayableRaces =
        { "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD" };

    /// <summary>Creates the initial state for a map and returns the engine plus the opening events.</summary>
    public static (Adventure Game, List<AdvEvent> Opening) NewGame(AdventureDb db, AdvMap map, GameSetup setup)
    {
        var s = new AdvState { MapId = map.Id, Rng = new Rng(setup.Seed), WorldSeed = unchecked((int)setup.Seed), Difficulty = Math.Clamp(setup.Difficulty, 0, 3), Campaign = setup.Campaign };
        var g = new Adventure(db, map, s);
        g._ev = new List<AdvEvent>();
        g.SetupPlayers(setup);
        g.ResolveRandomObjects();
        g.SetupTowns();
        g.SetupObjects();
        g.SetupHeroes();
        g.ApplyStartBonuses(setup);
        g.SetupBoats();
        g.SetupHeroPool();
        foreach (var p in s.Players) g.RollTavern(p);
        g.SetupMoonCalendar();
        g.StockArtifactMerchants();
        g.StockSpellShops();
        g.ResetTrainingPoints();
        g.PlaceGrail();
        g.SetupRumours();
        g.SyncArtifactInstances();
        foreach (var h in s.Heroes) g.ResetHeroDay(h, newWeek: true);
        foreach (var p in s.Players) g.RevealOwned(p);
        s.CurrentPlayer = 0;
        g.EmitTurnStarted();
        return (g, g._ev);
    }

    readonly Dictionary<int, string> _startHeroes = new();

    /// <summary>
    /// The players' teams as the game-setup model builds them (H5_Game.exe 00ca0240, used by every game start through
    /// 00ca0c10 / 00ca0c70): per active player in map order the map's <c>Team</c> when the map has <c>CustomTeams</c>
    /// (+0x100), else a new team of its own; the distinct teams are then renumbered 0.. in first-seen order.
    /// Inactive players get -1.
    /// </summary>
    public static int[] SlotTeams(AdvMap map)
    {
        var result = new int[map.Players.Count];
        var seen = new List<int>();
        for (int i = 0; i < map.Players.Count; i++)
        {
            var mp = map.Players[i];
            if (!mp.Active) { result[i] = -1; continue; }
            int team = map.CustomTeams ? mp.Team : seen.Count;
            int k = seen.IndexOf(team);
            if (k < 0) { k = seen.Count; seen.Add(team); }
            result[i] = k;
        }
        return result;
    }

    void SetupPlayers(GameSetup setup)
    {
        foreach (var ps in setup.Players) if (ps.StartHero != null) _startHeroes[ps.MapPlayer] = ps.StartHero;
        var teams = SlotTeams(Map);
        foreach (var ps in setup.Players.OrderBy(p => p.MapPlayer))
        {
            var mp = Map.Players[ps.MapPlayer];
            var race = ps.Race ?? mp.Race;
            if (!PlayableRaces.Contains(race)) race = PlayableRaces[S.Rng.Next(PlayableRaces.Length)];
            var p = new PlayerState { Index = mp.Index, Team = teams[mp.Index] >= 0 ? teams[mp.Index] : mp.Team, Race = race, Colour = mp.Colour, Human = ps.Human && mp.CanBeHuman };
            // Starting resources by difficulty (DefaultStats adventure/StartHuman|ComputerResources, fan manual p.325).
            var list = Db.Stats.Node(p.Human ? "adventure/StartHumanResources" : "adventure/StartComputerResources")?.Elements("Item").ToList();
            if (list is { Count: > 0 }) p.Purse = new Purse(Resources.Parse(list[Math.Min(S.Difficulty, list.Count - 1)]));
            foreach (var f in Map.Floors)
                p.Explored.Add(f == null ? Array.Empty<bool>() : new bool[f.Width * f.Height]);
            S.Players.Add(p);
        }
    }

    void SetupTowns()
    {
        foreach (var o in Map.OfKind(ObjectKind.Town))
        {
            var type = o.TownType ?? "TOWN_HEAVEN";
            var owner = PlayerByIndex(o.Owner) != null ? o.Owner : -1;
            // A random town owned by a player becomes that player's race.
            if (!PlayableRaces.Contains(type))
                type = owner >= 0 ? PlayerByIndex(owner)!.Race : PlayableRaces[S.Rng.Next(PlayableRaces.Length)];
            var t = InitTown(o, type, owner, o.Specialization, o.Buildings, o.Guards);
            S.Towns.Add(t);
        }
    }

    /// <summary>
    /// Town init FUN_00cd5be0 from its init record: the map load fills the record from the AdvMapTown (FUN_00cd1650),
    /// TransformTown from the old town (vf 0xf4 = FUN_00cd3ce0, no buildings, army or spells).
    /// </summary>
    TownState InitTown(MapObject o, string type, int owner, string? specialization, IEnumerable<TownBuildingInit> buildings, IEnumerable<ArmySlot> garrison)
    {
        var t = new TownState { Object = o.Index, Type = type, Owner = owner, Pos = o.Pos, Specialization = specialization };
        // Town init 00cd6460 over the map's buildings list (AdvMapTown +0x1a4: Type, InitialUpgrade, MaxUpgrade):
        // limit (record +0x28) = min(MaxUpgrade, data maximum) — BLD_UPG_NONE forbids the building — and the
        // start level = min(InitialUpgrade, limit); the town hall is raised to level 1 when lower (any town type).
        foreach (var b in buildings)
        {
            int limit = Math.Min(b.Max, Db.MaxLevel(type, b.Type));
            t.MaxLevel[b.Type] = limit;
            t.Built[b.Type] = Math.Min(b.Initial, limit);
        }
        if (t.Level("TB_TOWN_HALL") < 1) t.Built["TB_TOWN_HALL"] = 1;
        // Buildings the map doesn't mention are allowed up to the town type's maximum.
        if (Db.Buildings.TryGetValue(type, out var defs))
            foreach (var d in defs)
                if (!t.MaxLevel.ContainsKey(d.Type)) { t.MaxLevel[d.Type] = Db.MaxLevel(type, d.Type); t.Built.TryAdd(d.Type, 0); }
        foreach (var g in garrison) t.Garrison.TryAdd(g.Creature, g.Count);
        // Initial creature pools: the start levels go through the build path (00cd6460 → vf 0x6c → 00cd4a40), whose
        // new pool (FUN_00cd0710 at 00cd4e42) holds the base creature's WeeklyGrowth. No weekly update runs at map
        // start: 00993bf0 (IAdvMapWeeklyUpdate vf 4 on every object) is reached only from the new-day function
        // 009949d0 (GiveTurnToNextPlayers 00998060), so fort, horde and week bonuses come with the first new week.
        for (int tier = 1; tier <= 7; tier++)
            t.Available[tier - 1] = t.Level($"TB_DWELLING_{tier}") > 0
                && Db.Building(t.Type, $"TB_DWELLING_{tier}", 1)?.Creature is { } dc && Core.Creatures.TryGetValue(dc, out var dcd)
                ? dcd.WeeklyGrowth : 0;
        // FUN_00cd79d0: the guild, runes and warcries are rolled after the initial buildings. The map's per-town
        // spellIDs (AdvMapTown +0xf4) are copied by FUN_00cd1650 into the init record but FUN_00cd5be0 never reads them.
        RollTownSpells(t);
        RollTradeGuild(t);   // the start levels go through 00cd4a40 (00cd6460), which draws for ability 0x1d
        return t;
    }

    void SetupObjects()
    {
        foreach (var o in Map.Objects) SetupObject(o);
    }

    /// <summary>Rolls the dynamic state of one placed object (also used for script-created objects).</summary>
    void SetupObject(MapObject o)
    {
        {
            switch (o.Kind)
            {
                case ObjectKind.Monster:
                {
                    var os = S.Obj(o.Index);
                    int mult = 1;
                    var creature = o.Creature ?? RandomMonster(o.MonsterRandomLevel, out mult);
                    os.Creature = creature;
                    int count = o.CustomAmount && o.Amount > 0 ? o.Amount : RandomMonsterCount(creature, o) * mult;
                    os.Guards.Add(new Stack(creature, Math.Max(1, count)));
                    foreach (var a in o.AdditionalStacks) os.Guards.Add(new Stack(a.Creature, Math.Max(1, a.Count)));
                    break;
                }
                case ObjectKind.Treasure:
                    RollTreasure(o);
                    break;
                case ObjectKind.Artifact:
                {
                    var os = S.Obj(o.Index);
                    os.ArtifactId = o.ArtifactId ?? RandomArtifact(o.Shared.Type switch
                    {
                        "ARTF_RANDOM_MINOR" => "ARTF_CLASS_MINOR",
                        "ARTF_RANDOM_MAJOR" => "ARTF_CLASS_MAJOR",
                        "ARTF_RANDOM_RELIC" => "ARTF_CLASS_RELIC",
                        _ => null,
                    });
                    // 004ec660 -> 00ca8300 -> 00ca7e40: a wand / scroll keeps the map's spellID, else rolls one; a Rune of
                    // Flame always rolls (NewArtifactInstance); other artifacts carry none.
                    os.SpellId = os.ArtifactId != null && HasInstanceSpell(os.ArtifactId)
                        ? NewArtifactInstance(os.ArtifactId, worn: false, os.ArtifactId is WandArtifact or ScrollArtifact ? o.SpellId : null).Spell
                        : null;
                    foreach (var g in o.Guards) os.Guards.Add(new Stack(g.Creature, g.Count));
                    break;
                }
                case ObjectKind.Mine:
                case ObjectKind.DwarvenWarren:
                case ObjectKind.Garrison:
                {
                    // Map armySlots: guards of a neutral building, the owner's troops of a flagged one.
                    var os = S.Obj(o.Index);
                    os.Owner = PlayerByIndex(o.Owner) != null ? o.Owner : -1;
                    foreach (var g in o.Guards)
                        if (os.Owner >= 0) os.Army.TryAdd(g.Creature, g.Count);
                        else os.Guards.Add(new Stack(g.Creature, g.Count));
                    break;
                }
                case ObjectKind.AbandonedMine:
                {
                    var os = S.Obj(o.Index);
                    var v = PickWeighted(o.Shared.GuardVariants.Select(gv => gv.Chance).ToList());
                    if (v >= 0) foreach (var g in o.Shared.GuardVariants[v].Guards) os.Guards.Add(new Stack(g.Creature, g.Count));
                    break;
                }
                case ObjectKind.Dwelling:
                {
                    var os = S.Obj(o.Index);
                    os.Owner = PlayerByIndex(o.Owner) != null ? o.Owner : -1;
                    var creatures = o.Shared.DwellingCreatures;
                    os.Available = new int[creatures.Count];
                    var on = DwellingOffer(o);
                    os.DwellingOff = on.Select(x => !x).ToArray();
                    for (int i = 0; i < creatures.Count; i++)
                        if (on[i])
                            os.Available[i] = Core.Creatures.TryGetValue(creatures[i], out var cd) ? cd.WeeklyGrowth : 0;
                    break;
                }
                case ObjectKind.Building when AdventureDb.BankFor(o.Shared.Type) is { } bankId:
                    RollBank(o, bankId);
                    break;
                case ObjectKind.Shrine:
                {
                    var os = S.Obj(o.Index);
                    os.SpellId = ShrineSpell(o);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The creatures a map dwelling offers, H5_Game.exe 009a6040 (from the constructor's 009a6950; dwelling types 0x54,
    /// 0x5e, 0x5f take another path, not ported) over the shared's creature list: with a creature level (+0x128, the
    /// random dwelling's 4..7) the creatures of that tier (creature +0x8c); otherwise, when the instance's
    /// RandomCreatures (+0x9c) is set or its creaturesEnabled is empty, each creature with a chance that starts at 20
    /// and rises by 10 per creature (rand % 100 + 1 ≤ chance, the dwelling's generator — here the game RNG), else the
    /// creaturesEnabled flags. A list left empty takes the first creature.
    /// </summary>
    bool[] DwellingOffer(MapObject o)
    {
        var creatures = o.Shared.DwellingCreatures;
        var on = new bool[creatures.Count];
        int level = o.DwellingTier >= 1 ? o.DwellingTier : 0;
        bool random = o.RandomCreatures || o.CreaturesEnabled.Length == 0;
        int chance = 10;
        for (int i = 0; i < creatures.Count; i++)
        {
            if (!Core.Creatures.TryGetValue(creatures[i], out var cd)) continue;
            if (level >= 1) on[i] = cd.Tier == level;
            else if (random) { chance += 10; on[i] = S.Rng.Range(0, 99) + 1 <= chance; }
            else on[i] = i < o.CreaturesEnabled.Length && o.CreaturesEnabled[i];
        }
        if (creatures.Count > 0 && !on.Any(x => x)) on[0] = true;
        return on;
    }

    void SetupHeroes()
    {
        foreach (var o in Map.OfKind(ObjectKind.Hero))
        {
            if (o.Hero == null) continue;
            var owner = PlayerByIndex(o.Owner) != null ? o.Owner : -1;
            if (owner < 0) continue;   // heroes of absent players are not placed
            var h = HeroFromInstance(o.Hero, owner, o.Pos);
            if (TownAt(o.Pos) is { } t && t.Owner == owner) h.InTown = t.Object;
            S.Heroes.Add(h);
        }
        // Player reserves exist from the start but stay off the map until scripts deploy them.
        foreach (var p in S.Players)
        {
            if (p.Index >= Map.Players.Count) continue;
            foreach (var hi in Map.Players[p.Index].ReserveHeroInstances)
            {
                if (hi.Shared == null || S.Heroes.Any(x => x.DefId == hi.Shared.Id) || S.OffMapHeroes.Any(x => x.DefId == hi.Shared.Id)) continue;
                var h = HeroFromInstance(hi, p.Index, default);
                S.OffMapHeroes.Add(h);
                S.HeroPool.Remove(h.DefId);
            }
        }
        // Heroes the editor placed in town garrisons (AdvMapTown/GarrisonHero) belong to the town's owner.
        foreach (var o in Map.OfKind(ObjectKind.Town))
        {
            if (o.GarrisonHero == null || S.Town(o.Index) is not { } town || PlayerByIndex(town.Owner) == null) continue;
            if (S.Heroes.Any(x => x.DefId == o.GarrisonHero.Shared?.Id)) continue;
            var h = HeroFromInstance(o.GarrisonHero, town.Owner, EntranceOf(town));
            h.InTown = town.Object;
            h.Garrisoned = true;
            S.Heroes.Add(h);
            S.HeroPool.Remove(h.DefId);
        }
        // Players without a hero start with one in their main town (map "HeroInTown"), drawn from their race.
        foreach (var p in S.Players)
        {
            if (S.Heroes.Any(h => h.Owner == p.Index)) continue;
            var mp = Map.Players[p.Index];
            if (!mp.HeroInTown) continue;
            var town = S.Towns.FirstOrDefault(t => Map.Objects[t.Object].Uid == mp.MainTownUid) ?? S.Towns.FirstOrDefault(t => t.Owner == p.Index);
            if (town == null) continue;
            // Candidates as the exe's hero lists (00c9f330): HEROES_ANY members of the race that are not ScenarioHero.
            var def = Core.Heroes.Values.Where(d => d.Town == town.Type && IsHireable(d, mp) && Db.InHeroesAny(d) && !Db.IsScenarioHero(d))
                .OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            if (def.Count == 0) continue;
            // Custom game: the starting hero chosen on UI/CustomGameWait (00c9e1e0) replaces the random pick.
            var chosen = _startHeroes.GetValueOrDefault(p.Index) is { } sh ? def.FirstOrDefault(d => d.Id == sh) : null;
            var pick = chosen ?? def[S.Rng.Next(def.Count)];
            var h = HeroFromDef(pick, p.Index, EntranceOf(town));
            SetUpHeroBook(h);
            h.InTown = town.Object;
            GiveStartingArmy(h);
            S.Heroes.Add(h);
        }
        foreach (var h in S.Heroes) h.Mana = MaxMana(h);
    }

    static bool IsHireable(HeroDef d, MapPlayer mp) =>
        !d.Id.StartsWith("TEST", StringComparison.OrdinalIgnoreCase) && d.Class.Length > 0 &&
        !mp.BannedHeroes.Contains(d.Id) && (mp.AllowedHeroes.Count == 0 || mp.AllowedHeroes.Contains(d.Id));

    void SetupHeroPool()
    {
        var used = new HashSet<string>(S.Heroes.Select(h => h.DefId));
        foreach (var d in Core.Heroes.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
            if (!used.Contains(d.Id) && d.Class.Length > 0 && PlayableRaces.Contains(d.Town) &&
                (Map.AvailableHeroes.Count == 0 ? !Db.IsScenarioHero(d) : Map.AvailableHeroes.Contains(d.Id)))
                S.HeroPool.Add(d.Id);
    }

    HeroState HeroFromDef(HeroDef d, int owner, Tile pos)
    {
        var h = new HeroState
        {
            Id = S.NextHeroId++, DefId = d.Id, Name = d.Name.Length > 0 ? d.Name : d.Id, Owner = owner, Pos = pos,
            Class = d.Class, Town = d.Town, Specialization = d.Specialization, Level = 1, Experience = 0,
            Attack = d.Attack, Defence = d.Defence, SpellPower = d.SpellPower, Knowledge = d.Knowledge,
            Skills = d.Skills.ToList(), Perks = d.Perks.ToList(), Spells = d.Spells.ToList(),
            Ballista = d.Ballista, FirstAidTent = d.FirstAidTent, AmmoCart = d.AmmoCart,
            FavoriteEnemies = d.FavoriteEnemies.ToList(),
        };
        if (d.PrimarySkill is { } ps && h.Skills.All(s => s.SkillId != ps.SkillId)) h.Skills.Insert(0, ps);
        h.Mana = MaxMana(h);
        return h;
    }

    HeroState HeroFromInstance(HeroInstance hi, int owner, Tile pos)
    {
        var h = new HeroState
        {
            Id = S.NextHeroId++, DefId = hi.Shared?.Id ?? "", Name = hi.DisplayName.Length > 0 ? hi.DisplayName : hi.Shared?.Name ?? "",
            Owner = owner, Pos = pos, Class = hi.Class, Town = hi.Town, Specialization = hi.Shared?.Specialization ?? "",
            Level = Math.Max(1, hi.Level), Experience = hi.Experience,
            Attack = hi.Attack, Defence = hi.Defence, SpellPower = hi.SpellPower, Knowledge = hi.Knowledge,
            Skills = hi.Skills.ToList(), Perks = hi.Perks.ToList(), Spells = hi.Spells.ToList(), Artifacts = hi.Artifacts.ToList(),
            Ballista = hi.Ballista, FirstAidTent = hi.FirstAidTent, AmmoCart = hi.AmmoCart,
            FavoriteEnemies = hi.FavoriteEnemies.ToList(),
        };
        foreach (var a in hi.Army) h.Army.TryAdd(a.Creature, a.Count);
        if (h.Army.StackCount() == 0) GiveStartingArmy(h);
        // 009aacb0 after creating the hero: vf 0x304 (00b4ce30, clamped) with the combined Editable's TalismanLevel, then
        // vf 0x384 (00b56b50) grants the talisman spells.
        SetTalismanLevel(h, hi.TalismanLevel);
        GrantTalismanSpells(h);
        SetUpHeroBook(h);
        return h;
    }

    /// <summary>
    /// The hero army generator, CAdvMapHero vfunc 0x1f0 = FUN_00b57de0(full). The army is first emptied (7 empty slots).
    /// Base creature of tier 1..3: the LAST creature id 1..179 (FUN_00acb380 = 0xb4) whose CreatureTown (record +0x98)
    /// is the hero's town (vfunc 0x254 → +0x84) and whose BaseCreature (+0x100) is CREATURE_UNKNOWN; tier = +0x8c.
    /// <paramref name="full"/> false: one creature of the tier-1 base (FUN_004bd4d0; a missing creature or count &lt; 1
    /// becomes {1 = CREATURE_PEASANT, 1}). True: the specialisation creature (vfunc 0x298 = FUN_00b4bf10, see
    /// <see cref="SpecialisationCreatures"/>); none → one stack each of the tier 1, 2, 3 bases with HeroArmyGeneration
    /// Min/MaxWeekGrowthCoef_TierN (RPGStats +0xb10..+0xb24); a tier-1/2/3 specialisation creature → only
    /// NumSuchStacks_SpecTierN stacks of it with the SpecTierN coefficients (+0xb28..+0xb48); any other tier → the
    /// tier 1 and 2 bases and one forced stack of it (FUN_00b513e0(0, 0, 1, 1)). Each roll is <see cref="StartArmyCount"/>;
    /// every stack goes into the first empty slot (FUN_0045a290), never merged with another.
    /// </summary>
    public void GiveStartingArmy(HeroState h, bool full = true)
    {
        for (int i = 0; i < h.Army.Length; i++) h.Army[i] = null;
        var tierBase = StartArmyBases(h.Town);
        if (!full)
        {
            PutStack(h, tierBase[1] ?? "CREATURE_PEASANT", 1);
            return;
        }
        var gen = Db.Stats.Node("HeroArmyGeneration");
        float Coef(string name) => (float)gen.Dbl(name);
        void Roll(string? creature, string tierKey, bool forced)
        {
            // GAP: the exe has no guard here (a town without that tier's base dereferences record 0); we skip it.
            if (creature == null || !Core.Creatures.TryGetValue(creature, out var cr)) return;
            int n = tierKey.Length == 0 ? StartArmyCount(cr.WeeklyGrowth, 0, 0, NextRand32())
                : StartArmyCount(cr.WeeklyGrowth, Coef($"MinWeekGrowthCoef_{tierKey}"), Coef($"MaxWeekGrowthCoef_{tierKey}"), NextRand32());
            if (n > 0 || forced) PutStack(h, creature, Math.Max(1, n));
        }
        var spec = SpecialisationCreatures.GetValueOrDefault(h.Specialization);
        int specTier = spec != null && Core.Creatures.TryGetValue(spec, out var sc) ? sc.Tier : 0;
        if (spec == null)
            for (int t = 1; t <= 3; t++) Roll(tierBase[t], $"Tier{t}", false);
        else if (specTier is >= 1 and <= 3)
        {
            int stacks = gen.Int($"NumSuchStacks_SpecTier{specTier}");
            for (int i = 0; i < stacks; i++) Roll(spec, $"SpecTier{specTier}", true);
        }
        else
        {
            Roll(tierBase[1], "Tier1", false);
            Roll(tierBase[2], "Tier2", false);
            Roll(spec, "", true);
        }
    }

    /// <summary>FUN_00b57de0's table: [tier] = the last creature id 1..179 of the town with tier 1..3 and no BaseCreature.</summary>
    public string?[] StartArmyBases(string town)
    {
        var tierBase = new string?[4];
        foreach (var c in Core.Creatures.Values.Where(c => c.Number is > 0 and < 180).OrderBy(c => c.Number))
            if (c.Town == town && c.BaseCreature is "" or "CREATURE_UNKNOWN" && c.Tier is >= 1 and <= 3) tierBase[c.Tier] = c.Id;
        return tierBase;
    }

    uint NextRand32() => (uint)(S.Rng.NextU64() >> 32);

    void PutStack(HeroState h, string creature, int count)
    {
        int slot = Array.FindIndex(h.Army, s => s is not { Count: > 0 });
        if (slot >= 0) h.Army[slot] = new Stack(creature, count);
    }

    /// <summary>
    /// One start-army roll, FUN_00b513e0: lo = (float)(growth · min), hi = (float)(growth · max) (FSTP float),
    /// count = trunc(lo + (hi − lo) · rand32 · 2^-32) (x87, 53-bit precision, control word | 0xc00 = chop).
    /// </summary>
    public static int StartArmyCount(int weeklyGrowth, float min, float max, uint rand32)
    {
        double lo = (float)(weeklyGrowth * (double)min), hi = (float)(weeklyGrowth * (double)max);
        return (int)(lo + (hi - lo) * rand32 * (1.0 / 4294967296.0));
    }

    /// <summary>
    /// FUN_00b4bf10 (hero vfunc 0x298): the specialisation (hero +0x8c) looked up in the 24 {spec, creature, …} entries
    /// at 00f75648 (HeroSpecialization / CreatureType enum values in the comments); other specialisations have none.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SpecialisationCreatures = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["HERO_SPEC_PEASANTS"] = "CREATURE_PEASANT",            // 4 → 1
        ["HERO_SPEC_ARCHERS"] = "CREATURE_ARCHER",              // 5 → 3
        ["HERO_SPEC_FOOTMEN"] = "CREATURE_FOOTMAN",             // 6 → 5
        ["HERO_SPEC_GRIFFINS"] = "CREATURE_GRIFFIN",            // 7 → 7
        ["HERO_SPEC_HOUNDS"] = "CREATURE_HELL_HOUND",           // 18 → 19
        ["HERO_SPEC_SUCCUBUSES"] = "CREATURE_SUCCUBUS",         // 19 → 21
        ["HERO_SPEC_BLADE_MASTER"] = "CREATURE_BLADE_JUGGLER",  // 21 → 45
        ["HERO_SPEC_ELVES"] = "CREATURE_WOOD_ELF",              // 22 → 47
        ["HERO_SPEC_UNICORNS"] = "CREATURE_UNICORN",            // 23 → 51
        ["HERO_SPEC_ZOMBIES"] = "CREATURE_WALKING_DEAD",        // 30 → 31
        ["HERO_SPEC_VAMPIRES"] = "CREATURE_VAMPIRE",            // 31 → 35
        ["HERO_SPEC_GREMLINS"] = "CREATURE_GREMLIN",            // 44 → 57
        ["HERO_SPEC_GOLEMS"] = "CREATURE_IRON_GOLEM",           // 45 → 61
        ["HERO_SPEC_MAGES"] = "CREATURE_MAGI",                  // 46 → 63
        ["HERO_SPEC_SAVAGE"] = "CREATURE_SCOUT",                // 54 → 71
        ["HERO_SPEC_WITCHES"] = "CREATURE_WITCH",               // 55 → 73
        ["HERO_SPEC_MINOTAURS"] = "CREATURE_MINOTAUR",          // 56 → 75
        ["HERO_SPEC_AXE_MASTER"] = "CREATURE_AXE_FIGHTER",      // 61 → 94
        ["HERO_SPEC_BEAR_RIDERS"] = "CREATURE_BEAR_RIDER",      // 62 → 96
        ["HERO_SPEC_MAGISTER"] = "CREATURE_RUNE_MAGE",          // 63 → 100
        ["HERO_SPEC_DEFENDER"] = "CREATURE_DEFENDER",           // 67 → 92
        ["HERO_SPEC_GRIM_FIGHTER"] = "CREATURE_ORC_WARRIOR",    // 71 → 121
        ["HERO_SPEC_CENTAUR_MISTRESS"] = "CREATURE_CENTAUR",    // 73 → 119
        ["HERO_SPEC_ORC_ELDER"] = "CREATURE_ORCCHIEF_BUTCHER",  // 78 → 125
    };

    // ------------------------------------------------------------------ random content

    int PickWeighted(IReadOnlyList<int> weights)
    {
        int total = weights.Sum();
        if (total <= 0) return weights.Count > 0 ? 0 : -1;
        int r = S.Rng.Next(total);
        for (int i = 0; i < weights.Count; i++) { if (r < weights[i]) return i; r -= weights[i]; }
        return weights.Count - 1;
    }

    /// <summary>
    /// Random monster (map loader 0041d360 → 0041ca20): MONSTER_RANDOM_Ln takes the RPGRoot group MONSTER_n, and with
    /// the chance Respawn/MonsterDowngradeProb (the float at RPGStats +0x9c, compared with rng / 2^32) the group
    /// MONSTER_(n−1) instead, passing 2 (Respawn/MonsterDowngradeMult) instead of 1 to the monster (004effd0 param 5,
    /// used here as the count factor); L1 and MONSTER_RANDOM_ANY (group MONSTER_ANY) have no lower group. The
    /// creature is drawn uniformly among the group's creatures with SubjectOfRandomGeneration (<see cref="AdventureDb.MonsterGroups"/>).
    /// TODO(exe): the map's RacesRandomGroups filter (object RacesRandomGroupID → map +0x2d0) is not modelled.
    /// </summary>
    string RandomMonster(int level, out int mult)
    {
        mult = 1;
        string primary = level is >= 1 and <= 7 ? $"MONSTER_{level}" : "MONSTER_ANY";
        string? lower = level is >= 2 and <= 7 ? $"MONSTER_{level - 1}" : null;
        var pool = Db.MonsterGroups.GetValueOrDefault(primary);
        if (lower != null && Db.MonsterGroups.TryGetValue(lower, out var low) &&
            S.Rng.NextDouble() < Db.Stats.Get("adventure/Respawn/MonsterDowngradeProb", 0.05))
        {
            pool = low;
            mult = (int)Db.Stats.Get("adventure/Respawn/MonsterDowngradeMult", 2);
        }
        if (pool is { Count: > 0 }) return pool[S.Rng.Next(pool.Count)];
        // No group data: any stock creature of the tier with a map model.
        var any = Core.Creatures.Values
            .Where(c => c.Stock && c.WeeklyGrowth > 0 && c.Id != "CREATURE_UNKNOWN" && c.MonsterSharedPath != null && (level is 0 or 8 || c.Tier == level))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        mult = 1;
        return any.Count == 0 ? "CREATURE_PEASANT" : any[S.Rng.Next(any.Count)].Id;
    }

    /// <summary>
    /// Neutral stack size when the map leaves it random.
    /// TODO(exe): the original generator is not yet decoded; this uses one to two weeks of
    /// growth of the creature, scaled by the difficulty CountCoefs (DefaultStats
    /// adventure/Neutrals[Not]InCampaign/CountCoefs, fan manual p.325).
    /// </summary>
    int RandomMonsterCount(string creature, MapObject o)
    {
        int growth = Core.Creatures.TryGetValue(creature, out var c) ? Math.Max(1, c.WeeklyGrowth) : 5;
        double weeks = 1 + S.Rng.NextDouble();
        var coefs = Db.Stats.Node(S.Campaign ? "adventure/NeutralsInCampaign/CountCoefs" : "adventure/NeutralsNotInCampaign/CountCoefs")?.Elements("Item").ToList();
        double k = coefs is { Count: > 0 } && !o.Props.ContainsKey("DoesNotDependOnDifficulty")
            ? XmlHelpers.ParseDouble(coefs[Math.Min(S.Difficulty, coefs.Count - 1)].Value, 1) : 1;
        if (o.Props.TryGetValue("DoesNotDependOnDifficulty", out var dnd) && dnd == "true") k = 1;
        return Math.Max(1, (int)(growth * 4 * weeks * k));
    }

    /// <summary>A random artifact of the class (null = any) from the map's generator (<see cref="GenerateArtifact(int, int)"/>).</summary>
    string? RandomArtifact(string? cls) => GenerateArtifact(cls);

    /// <summary>
    /// Treasure contents as the exe's reward roller FUN_00a82720 reads them: the amount is the object's own
    /// (IsCustom / Amount, object +0xc8 / +0xcc) or a roll in the shared MinResource..MaxResource (+0xf0 / +0xf4);
    /// the campfire always rolls. Per type: resource piles give the amount (gold × 100); the campfire's roll r gives
    /// r / 6 of resource r % 6 and 100 gold per unit; the chest, sea chest and flotsam keep the amount
    /// (<see cref="ObjState.Variant"/> = amount − 1) and resolve it on the visit (<see cref="VisitTreasure"/>).
    /// The exe rolls on the visit; the hero's Fortunate Adventurer bonus is applied there.
    /// </summary>
    void RollTreasure(MapObject o)
    {
        ResolveTreasure(o);
        var os = S.Obj(o.Index);
        int min = o.Shared.MinResource, max = Math.Max(o.Shared.MinResource, o.Shared.MaxResource);
        int Roll() => o.TreasureCustom ? o.TreasureAmount : S.Rng.Range(min, max);
        switch (o.Shared.Type)
        {
            case "TREASURE_WOOD": os.ResourceKind = Purse.Wood; os.Amount = Roll(); break;
            case "TREASURE_ORE": os.ResourceKind = Purse.Ore; os.Amount = Roll(); break;
            case "TREASURE_MERCURY": os.ResourceKind = Purse.Mercury; os.Amount = Roll(); break;
            case "TREASURE_CRYSTAL": os.ResourceKind = Purse.Crystal; os.Amount = Roll(); break;
            case "TREASURE_SULFUR": os.ResourceKind = Purse.Sulfur; os.Amount = Roll(); break;
            case "TREASURE_GEMS": os.ResourceKind = Purse.Gem; os.Amount = Roll(); break;
            // 00a82a64: the amount (data 5..10, maps' custom amounts 1..60) is in hundreds.
            case "TREASURE_GOLD": os.ResourceKind = Purse.Gold; os.Amount = Roll() * 100; break;
            // TREASURE_RANDOM became one of the TREASURE_ANY shareds at creation (ResolveTreasure, 0041b350); one
            // still random here (no group in the data) holds nothing.
            case "TREASURE_CAMPFIRE":
            {
                // 00a82a75: r in MinResource..MaxResource (data 24..36), custom amount ignored (00a8289d);
                // resource r % 6 (wood, ore, mercury, crystal, sulfur, gems), count r / 6.
                int r = S.Rng.Range(min, max);
                os.ResourceKind = r % 6;
                os.Amount = r / 6;
                break;
            }
            case "TREASURE_CHEST":
            {
                // 00a829bd: a minor artifact when a roll of 0..99 is below ChestMinorArtifactChance (RPGStats +0xc0,
                // 2 without stats); otherwise gold or experience by the amount (data 1..3).
                int chance = Db.Stats.Root.Element("adventure").Int("ChestMinorArtifactChance", 2);
                if (S.Rng.Range(0, 99) < chance) os.ArtifactId = RandomArtifact("ARTF_CLASS_MINOR");
                os.Variant = Roll() - 1;
                break;
            }
            case "TREASURE_FLOATSAM": os.Variant = Roll() - 1; break;
            case "TREASURE_SEA_CHEST": os.Variant = Roll() - 1; break;
            case "TREASURE_SHIPWRECK": RollShipwreck(os, footman: min == 1); break;
            default: os.Amount = Roll(); break;
        }
    }

    void RollBank(MapObject o, string bankId)
    {
        if (!Db.Banks.TryGetValue(bankId, out var bank) || bank.Variants.Count == 0) return;
        var os = S.Obj(o.Index);
        os.Variant = PickWeighted(bank.Variants.Select(v => v.Chance).ToList());
        os.Guards.Clear();
        foreach (var g in bank.Variants[os.Variant].Guards)
        {
            bool first = S.Rng.Next(100) < g.Chance1;
            var cr = first ? g.Creature1 : g.Creature2;
            int n = first ? S.Rng.Range(g.Min1, g.Max1) : S.Rng.Range(g.Min2, g.Max2);
            if (cr.Length > 0 && cr != "CREATURE_UNKNOWN" && n > 0) os.Guards.Add(new Stack(cr, n));
        }
    }
}
