using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using HeroState = H5.Adventure.State.HeroState;
using H5.Core.Data;
using H5.Core.Numerics;

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
        g._campaignExtras = setup.Campaign ? setup.CampaignExtras : new();
        g.SetupHeroes();
        g.ApplyStartBonuses(setup);
        g.SetupBoats();
        g.SetupHeroPool();
        g.SetupRandomPrisoners();
        g.RefillTavernsWeekly();
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
        g.LogMapStarted();
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
            var p = new PlayerState { Index = mp.Index, Team = teams[mp.Index] >= 0 ? teams[mp.Index] : mp.Team, Race = race, Colour = PlayerColour(mp), Human = ps.Human && mp.CanBeHuman };
            // Starting resources by difficulty (DefaultStats adventure/StartHuman|ComputerResources, fan manual p.325).
            var list = Db.Stats.Node(p.Human ? "adventure/StartHumanResources" : "adventure/StartComputerResources")?.Elements("Item").ToList();
            if (list is { Count: > 0 }) p.Purse = new Purse(Resources.Parse(list[Math.Min(S.Difficulty, list.Count - 1)]));
            foreach (var f in Map.Floors)
                p.Explored.Add(f == null ? Array.Empty<bool>() : new bool[f.Width * f.Height]);
            S.Players.Add(p);
        }
    }

    /// <summary>Player colours by player number, PCOLOR_RED = 1 … PCOLOR_BROWN = 8 (UI/MPWait/PlayersList/Item TeamColors order).</summary>
    static readonly string[] NumberColours =
        { "PCOLOR_RED", "PCOLOR_BLUE", "PCOLOR_GREEN", "PCOLOR_YELLOW", "PCOLOR_ORANGE", "PCOLOR_TEAL", "PCOLOR_PURPLE", "PCOLOR_BROWN" };

    /// <summary>
    /// 00ca0240 (the game's player slots): a slot's colour starts as its player number and the map player's Colour
    /// (+0x84) replaces it only when it is not 0 (PCOLOR_NEUTRAL). Multiplayer and RMG maps leave every player NEUTRAL.
    /// </summary>
    public static string PlayerColour(MapPlayer mp) =>
        NumberColours.Contains(mp.Colour, StringComparer.OrdinalIgnoreCase) ? mp.Colour.ToUpperInvariant()
            : NumberColours[Math.Clamp(mp.Index, 0, NumberColours.Length - 1)];

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
                    // CAdvMapMonster ctor 004effd0: the main stack by 004ee750(shared creature, Custom, Amount, Amount2,
                    // random factor: 0 for a placed creature, 1 / 2 from the random-monster path 0041ca20), then every
                    // AdditionalStacks item (Creature, CustomAmount, Amount, Amount2) by 004ee750(..., 0).
                    var os = S.Obj(o.Index);
                    int mult = 0;
                    var creature = o.Creature ?? RandomMonster(o.MonsterRandomLevel, out mult);
                    os.Creature = creature;
                    os.Guards.Add(new Stack(creature, MonsterStackCount(creature, o.CustomAmount, o.Amount, o.Amount2, mult)));
                    for (int i = 0; i < o.AdditionalStacks.Count; i++)
                    {
                        var a = o.AdditionalStacks[i];
                        var (custom, amount2) = i < o.AdditionalStackRolls.Count ? o.AdditionalStackRolls[i] : (true, 0);
                        os.Guards.Add(new Stack(a.Creature, MonsterStackCount(a.Creature, custom, a.Count, amount2, 0)));
                    }
                    if (!o.ScriptCreated) ApplyNeutralCountCoef(o, os);
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
            // 0041ce20: a map hero whose shared hero has Class 0 (HERO_CLASS_NONE, MapObjects/Utility/EntryPoint.xdb) is
            // a placeholder for the next campaign extra hero (0041cc60); with none left it makes no hero.
            if (o.Hero.Class == "HERO_CLASS_NONE")
            {
                if (NextCampaignExtra(owner, o.Pos) is { } extra)
                {
                    if (TownAt(o.Pos) is { } et && et.Owner == owner) extra.InTown = et.Object;
                    S.Heroes.Add(extra);
                }
                continue;
            }
            var h = HeroFromInstance(o.Hero, owner, o.Pos);
            if (TownAt(o.Pos) is { } t && t.Owner == owner) h.InTown = t.Object;
            S.Heroes.Add(h);
        }
        // Player reserves exist from the start but stay off the map until scripts deploy them. 0041c600 (game start
        // 0041ecd0): each player's ReserveHeroes (map player +0x34, stride 8) are built through 0041c4b0, handed to the
        // player (vf 0x16c) and then given hero vfunc 0x1f0(true): a generated full army replaces the editor's army.
        foreach (var p in S.Players)
        {
            if (p.Index >= Map.Players.Count) continue;
            foreach (var hi in Map.Players[p.Index].ReserveHeroInstances)
            {
                if (hi.Shared == null || S.Heroes.Any(x => x.DefId == hi.Shared.Id) || S.OffMapHeroes.Any(x => x.DefId == hi.Shared.Id)) continue;
                var h = HeroFromInstance(hi, p.Index, default);
                GiveStartingArmy(h);
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

    /// <summary>
    /// Game start (0041ecd0 → 0041ad90, after the reserve and start heroes, before the first tavern refill 00990550): for
    /// floor 0 then 1, every object of the floor whose query slot 0x5c (the +0xec table; 009cbf00 returns the IAdvMapPrison
    /// at +0xc8) answers gets that interface's vf0(world) = 004557b0: a RandomHero prison (+0xd4) without a live prisoner
    /// (+0xd0) takes a hero from the pool (vf 0x30 = 00ce77f0 → vf 0x34 = 00ce7f50 with race 2 = any: heroes passing vf 0x1c =
    /// 00ce7920 — in the pool, no player, not taken — one at FUN_008e7840(n − 1)), marks him taken (vf 0x2c = 00ce7bc0),
    /// gives him his full army (vf 0x1f0(true)) and clears the flag.
    /// UNVERIFIED: the floor's object order (taken as the map's) and the RNG (the exe seeds world stream 0 with the object
    /// through 008e7860; ours is the game RNG).
    /// </summary>
    void SetupRandomPrisoners()
    {
        foreach (int floor in new[] { 0, 1 })
            foreach (var o in Map.Objects.Where(o => o.Kind == ObjectKind.Prison && o.RandomHero && o.Pos.Floor == floor))
            {
                var os = S.Obj(o.Index);
                if (os.Removed || os.PrisonerDef != null) continue;
                var cand = S.HeroPool.Where(id => Core.Heroes.ContainsKey(id) && !S.Players.Any(p => p.Tavern.Contains(id))).ToList();
                if (cand.Count == 0) continue;
                var id = cand[S.Rng.Next(cand.Count)];
                S.HeroPool.Remove(id);
                var d = Core.Heroes[id];
                var tmp = new HeroState { DefId = id, Town = d.Town, Specialization = d.Specialization };
                GiveStartingArmy(tmp, full: true);
                os.PrisonerDef = id;
                os.PrisonerArmy = tmp.Army;
            }
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
    /// One start-army roll, FUN_00b513e0, under the game's x87 control word (_PC_24 | _RC_CHOP, see <see cref="X87"/>):
    /// hi = growth · max and lo = growth · min (FILD growth, FMUL, FSTP float); u = FILD rand32, which loads it signed,
    /// so a value ≥ 2^31 gets FADD 2^32 (00e0d2e0) and is chopped to 24 bits; then ((hi − lo) · u) · 2^-32 (00e0d2d8),
    /// + lo, every step chopped, and FISTP with the control word | 0xc00 (chop) truncates.
    /// </summary>
    public static int StartArmyCount(int weeklyGrowth, float min, float max, uint rand32)
    {
        float hi = X87.Mul(max, weeklyGrowth), lo = X87.Mul(weeklyGrowth, min);
        double u = rand32 < 0x80000000u ? rand32 : X87.Chop(rand32);
        float r = X87.Mul(X87.Mul(X87.Sub(hi, lo), u), 1.0 / 4294967296.0);
        return (int)X87.Add(lo, r);
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
    /// Random monster (map loader 0041d360 → 0041ca20): MONSTER_RANDOM_Ln takes the RPGRoot group MONSTER_n, and when
    /// a world draw u satisfies u·2⁻³² &lt; Respawn/MonsterDowngradeProb (the float at RPGStats +0x9c) the group
    /// MONSTER_(n−1) instead, passing 2 instead of 1 as the random factor (004effd0 param 5 → 004ee750 param 5, see
    /// <see cref="MonsterStackCount"/>); L1 and MONSTER_RANDOM_ANY (group MONSTER_ANY) have no lower group. The creature
    /// is drawn as u % n among the group's creatures with SubjectOfRandomGeneration (<see cref="AdventureDb.MonsterGroups"/>).
    /// TODO(exe): the map's RacesRandomGroups filter (object RacesRandomGroupID → map +0x2d0) is not modelled.
    /// UNVERIFIED: the RNG — the exe uses its world stream, ours the game RNG.
    /// </summary>
    string RandomMonster(int level, out int mult)
    {
        mult = 1;
        string primary = level is >= 1 and <= 7 ? $"MONSTER_{level}" : "MONSTER_ANY";
        string? lower = level is >= 2 and <= 7 ? $"MONSTER_{level - 1}" : null;
        var pool = Db.MonsterGroups.GetValueOrDefault(primary);
        if (lower != null && Db.MonsterGroups.TryGetValue(lower, out var low) &&
            UnitDraw(NextU32()) < (float)Db.Stats.Get("adventure/Respawn/MonsterDowngradeProb", 0.05))
        {
            pool = low;
            mult = 2;
        }
        if (pool is { Count: > 0 }) return pool[(int)(NextU32() % (uint)pool.Count)];
        // No group data: any stock creature of the tier with a map model.
        var any = Core.Creatures.Values
            .Where(c => c.Stock && c.WeeklyGrowth > 0 && c.Id != "CREATURE_UNKNOWN" && c.MonsterSharedPath != null && (level is 0 or 8 || c.Tier == level))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        mult = 1;
        return any.Count == 0 ? "CREATURE_PEASANT" : any[S.Rng.Next(any.Count)].Id;
    }

    uint NextU32() => (uint)S.Rng.NextU64();

    /// <summary>
    /// The exe's u32 → [0, 1) conversion (0041ca20, 004ee750, 009919a0): FILD of the draw as a signed int, + 2^32
    /// (double 00e0d2e0) when negative, × 2⁻³² (00e0d2d8), under the game's 24-bit chop precision (<see cref="X87"/>).
    /// </summary>
    public static float UnitDraw(uint u) => X87.Mul(DrawValue(u), 1.0 / 4294967296.0);

    /// <summary>FILD of a u32 draw read as a signed int, plus 2^32 when negative (FADD under 24-bit chop).</summary>
    static double DrawValue(uint u) => (int)u < 0 ? X87.Chop(u) : (int)u;

    /// <summary>
    /// Stack size of a map monster's stack, FUN_004ee750(creature, custom, amount, amount2, random): with Custom set, the
    /// map's Amount, or Amount + u % (Amount2 − Amount + 1) when Amount2 is larger. Otherwise, from the Respawn block
    /// (RPGStats +0x90 MonstersMin, +0x94 MonstersMax, +0x98 MonstersRandomUpgradeSub, +0xa0 MonsterDowngradeMult):
    /// t = (max − min)·u·2⁻³² + min; sub = MonstersRandomUpgradeSub when the random factor is non-zero and the creature
    /// is an upgrade (BaseCreature, creature +0x100) or of TOWN_NO_TYPE (CreatureTown +0x98 = 2), else 0;
    /// n = (int)((√CreatureTier · t − sub) · WeeklyGrowth) (creature +0x8c, +0xa8; √ by the table 0046ed30), at least 1;
    /// with random factor 2 (a downgraded random monster) n = (int)(n · MonsterDowngradeMult). A result below 1 is 1
    /// ("Zero count monster at …"). All x87 steps under the game's 24-bit chop (<see cref="X87"/>).
    /// </summary>
    int MonsterStackCount(string creature, bool custom, int amount, int amount2, int random)
    {
        var cd = Core.Creatures.GetValueOrDefault(creature);
        if (cd == null) return 1;   // 0089fbf0 finds no creature: 004ee750 returns 1
        float Stat(string k, double d) => (float)Db.Stats.Get("adventure/Respawn/" + k, d);
        uint u = custom && amount2 <= amount ? 0u : NextU32();
        return NeutralStackCount(cd, custom, amount, amount2, random, u,
            Stat("MonstersMin", 2), Stat("MonstersMax", 3), Stat("MonstersRandomUpgradeSub", 0.3), Stat("MonsterDowngradeMult", 2));
    }

    /// <summary>The arithmetic of <see cref="MonsterStackCount"/> for one draw <paramref name="u"/> (FUN_004ee750).</summary>
    public static int NeutralStackCount(CreatureDef cd, bool custom, int amount, int amount2, int random, uint u,
        float min, float max, float upgradeSub, float downgradeMult)
    {
        int n;
        if (custom)
        {
            n = amount;
            if (amount < amount2) n = amount + (int)(u % ((uint)(amount2 - amount) + 1));
        }
        else
        {
            bool upgradeOrNeutral = cd.BaseCreature is { Length: > 0 } and not "CREATURE_UNKNOWN" || cd.Town == "TOWN_NO_TYPE";
            float sub = random != 0 && upgradeOrNeutral ? upgradeSub : 0f;
            float t = X87.Add(X87.Mul(X87.Mul(X87.Sub(max, min), DrawValue(u)), 1.0 / 4294967296.0), min);
            float g = X87.Mul(X87.Sub(X87.Mul(TableSqrt(cd.Tier), t), sub), cd.WeeklyGrowth);
            n = (int)g;
            if (n <= 1) n = 1;
            if (random == 2) n = (int)X87.Mul(n, downgradeMult);
        }
        return n < 1 ? 1 : n;
    }

    /// <summary>
    /// The exe's table square root FUN_0046ed30: 0 for +0.0; otherwise the exponent ((bits + 0xc0800000) &gt;&gt; 1) +
    /// 0x3f800000 masked to 0x7f800000, the mantissa from the table 00f92810[(bits &gt;&gt; 8) &amp; 0xffff] that the static
    /// initialiser 0046ee40 fills for x = 2..4 (index 0..0x7fff) and 1..2 (0x8000..) with the mantissa of the bisection
    /// root 0046ebb0 (64 halvings of [0, x + 1], keeping the low end) stored as a float. It runs before WinMain sets the
    /// chop control word, so rounded to nearest: (float)√x of x cut to a 15-bit mantissa.
    /// </summary>
    public static float TableSqrt(float x)
    {
        uint bits = BitConverter.SingleToUInt32Bits(x);
        if (bits == 0) return 0f;
        uint idx = (bits >> 8) & 0xffff;
        float xm = BitConverter.UInt32BitsToSingle(((idx & 0x8000) != 0 ? 0x3f800000u : 0x40000000u) | ((idx & 0x7fff) << 8));
        uint mant = BitConverter.SingleToUInt32Bits((float)Math.Sqrt(xm)) & 0x7fffff;
        uint exp = (((bits + 0xc0800000u) >> 1) + 0x3f800000u) & 0x7f800000u;
        return BitConverter.UInt32BitsToSingle(exp | mant);
    }

    /// <summary>
    /// IAdvMapMonster vf0 = 004ef500, run when the monster enters the world (0041aad0 → 0041aa40 calls vf0 of the first
    /// of the object's query slots 4 / 0x20 / 0x1c that answers; the monster's 0x1c returns its +0xc8): unless the map
    /// monster has DoesNotDependOnDifficulty (+0xdd), every stack's count becomes (int)(count · CountCoef) (chop), at
    /// least 1. The coefficient (0098ded0 → 004f6850) is the difficulty's item of NeutralsInCampaign/CountCoefs
    /// (RPGStats +0xc8) in a campaign (game settings +9) or NeutralsNotInCampaign/CountCoefs (+0xf0), 1 past the list;
    /// 004f6850 maps the difficulty enum 0 → item 1, 1 → item 0, 2 → 2, 3 → 3, i.e. items easy, normal, hard, heroic
    /// as our <see cref="AdvState.Difficulty"/>. Monsters entering with the hook: the map load (0041e600 → 0041e570) and
    /// the creature-week spawn (009919a0). UNVERIFIED: script-created monsters (CreateMonster 005c8aa0 queues a command)
    /// are left unscaled. The vf's first part (world vf 0xd4 → 0063ee50) is not ported.
    /// </summary>
    void ApplyNeutralCountCoef(MapObject o, ObjState os)
    {
        if (o.DoesNotDependOnDifficulty) return;
        float k = NeutralCoef("CountCoefs");
        foreach (var g in os.Guards)
        {
            if (g.Count <= 0) continue;
            int n = (int)X87.Mul(g.Count, k);
            g.Count = n <= 1 ? 1 : n;
        }
    }

    /// <summary>The difficulty's item of adventure/Neutrals[Not]InCampaign/&lt;list&gt; (004f6850 / 004f67b0 / 004f68f0), 1 past the list.</summary>
    float NeutralCoef(string list)
    {
        var coefs = Db.Stats.Node((S.Campaign ? "adventure/NeutralsInCampaign/" : "adventure/NeutralsNotInCampaign/") + list)?.Elements("Item").ToList();
        return coefs != null && S.Difficulty >= 0 && S.Difficulty < coefs.Count ? (float)XmlHelpers.ParseDouble(coefs[S.Difficulty].Value, 1) : 1f;
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
