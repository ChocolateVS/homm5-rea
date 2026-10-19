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
        var s = new AdvState { MapId = map.Id, Rng = new Rng(setup.Seed), Difficulty = Math.Clamp(setup.Difficulty, 0, 3), Campaign = setup.Campaign };
        var g = new Adventure(db, map, s);
        g._ev = new List<AdvEvent>();
        g.SetupPlayers(setup);
        g.SetupTowns();
        g.SetupObjects();
        g.SetupHeroes();
        g.SetupBoats();
        g.SetupHeroPool();
        foreach (var p in s.Players) g.RollTavern(p);
        g.ChooseMoonWeek();
        g.StockArtifactMerchants();
        g.ResetTrainingPoints();
        g.PlaceGrail();
        foreach (var h in s.Heroes) g.ResetHeroDay(h, newWeek: true);
        foreach (var p in s.Players) g.RevealOwned(p);
        s.CurrentPlayer = 0;
        g.Emit(new TurnStarted(s.Players[0].Index, s.Day));
        return (g, g._ev);
    }

    void SetupPlayers(GameSetup setup)
    {
        foreach (var ps in setup.Players.OrderBy(p => p.MapPlayer))
        {
            var mp = Map.Players[ps.MapPlayer];
            var race = ps.Race ?? mp.Race;
            if (!PlayableRaces.Contains(race)) race = PlayableRaces[S.Rng.Next(PlayableRaces.Length)];
            var p = new PlayerState { Index = mp.Index, Team = mp.Team, Race = race, Colour = mp.Colour, Human = ps.Human && mp.CanBeHuman };
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
            var t = new TownState { Object = o.Index, Type = type, Owner = owner, Pos = o.Pos, Specialization = o.Specialization };
            foreach (var b in o.Buildings)
            {
                t.Built[b.Type] = b.Initial;
                t.MaxLevel[b.Type] = b.Max == 0 ? Db.MaxLevel(type, b.Type) : Math.Min(b.Max, Db.MaxLevel(type, b.Type));
            }
            // Buildings the map doesn't mention are allowed up to the town type's maximum.
            if (Db.Buildings.TryGetValue(type, out var defs))
                foreach (var d in defs)
                    if (!t.MaxLevel.ContainsKey(d.Type)) { t.MaxLevel[d.Type] = Db.MaxLevel(type, d.Type); t.Built.TryAdd(d.Type, 0); }
            foreach (var g in o.Guards) t.Garrison.TryAdd(g.Creature, g.Count);
            // Initial creature pools: one week of growth for built dwellings.
            for (int tier = 1; tier <= 7; tier++)
                t.Available[tier - 1] = t.Level($"TB_DWELLING_{tier}") > 0 ? WeeklyGrowth(t, tier) : 0;
            RollMageGuild(t, o);
            S.Towns.Add(t);
        }
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
                    var creature = o.Creature ?? RandomMonster(o.MonsterRandomLevel);
                    os.Creature = creature;
                    int count = o.CustomAmount && o.Amount > 0 ? o.Amount : RandomMonsterCount(creature, o);
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
                    os.SpellId = o.SpellId;
                    foreach (var g in o.Guards) os.Guards.Add(new Stack(g.Creature, g.Count));
                    break;
                }
                case ObjectKind.Mine:
                case ObjectKind.DwarvenWarren:
                {
                    var os = S.Obj(o.Index);
                    os.Owner = PlayerByIndex(o.Owner) != null ? o.Owner : -1;
                    foreach (var g in o.Guards) os.Guards.Add(new Stack(g.Creature, g.Count));
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
                    for (int i = 0; i < creatures.Count; i++)
                        if (i >= o.CreaturesEnabled.Length || o.CreaturesEnabled[i])
                            os.Available[i] = Core.Creatures.TryGetValue(creatures[i], out var cd) ? cd.WeeklyGrowth : 0;
                    break;
                }
                case ObjectKind.Building when AdventureDb.BankFor(o.Shared.Type) is { } bankId:
                    RollBank(o, bankId);
                    break;
                case ObjectKind.Shrine:
                {
                    var os = S.Obj(o.Index);
                    os.SpellId = o.SpellId ?? RandomSpell(o.Shared.RunicMagic ? "MAGIC_SCHOOL_RUNIC" : null,
                        o.Shared.RunicMagic ? 1 + S.Rng.Next(Math.Max(1, o.Shared.MaxRunicMagicLevel)) : Math.Max(1, o.Shared.ShrineLevel));
                    break;
                }
            }
        }
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
            var def = Core.Heroes.Values.Where(d => d.Town == town.Type && IsHireable(d, mp)).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            if (def.Count == 0) continue;
            var pick = def[S.Rng.Next(def.Count)];
            var h = HeroFromDef(pick, p.Index, EntranceOf(town));
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
        return h;
    }

    /// <summary>
    /// A freshly hired or auto-created hero brings tier 1–3 creatures of their faction scaled by
    /// weekly growth (DefaultStats HeroArmyGeneration: Min/MaxWeekGrowthCoef_TierN).
    /// </summary>
    void GiveStartingArmy(HeroState h)
    {
        var gen = Db.Stats.Node("HeroArmyGeneration");
        for (int tier = 1; tier <= 3; tier++)
        {
            var cr = Core.Creatures.Values.Where(c => c.Town == h.Town && c.Tier == tier && !c.Upgrade).OrderBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
            if (cr == null) continue;
            double lo = gen.Dbl($"MinWeekGrowthCoef_Tier{tier}"), hi = gen.Dbl($"MaxWeekGrowthCoef_Tier{tier}");
            double coef = lo + (hi - lo) * S.Rng.NextDouble();
            int count = (int)Math.Round(cr.WeeklyGrowth * coef);
            if (count > 0) h.Army.TryAdd(cr.Id, count);
        }
    }

    // ------------------------------------------------------------------ random content

    int PickWeighted(IReadOnlyList<int> weights)
    {
        int total = weights.Sum();
        if (total <= 0) return weights.Count > 0 ? 0 : -1;
        int r = S.Rng.Next(total);
        for (int i = 0; i < weights.Count; i++) { if (r < weights[i]) return i; r -= weights[i]; }
        return weights.Count - 1;
    }

    string RandomMonster(int level)
    {
        var pool = Core.Creatures.Values
            .Where(c => c.Stock && c.WeeklyGrowth > 0 && c.Id != "CREATURE_UNKNOWN" && c.MonsterSharedPath != null && (level is 0 or 8 || c.Tier == level))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        return pool.Count == 0 ? "CREATURE_PEASANT" : pool[S.Rng.Next(pool.Count)].Id;
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

    string? RandomArtifact(string? cls)
    {
        var pool = Db.ArtifactClass.Where(kv => kv.Value != "ARTF_CLASS_GRAIL" && (cls == null || kv.Value == cls))
            .Select(kv => kv.Key).Where(a => Map.ArtifactIds.Count == 0 || !Map.ArtifactIds.Contains(a))
            .OrderBy(a => a, StringComparer.Ordinal).ToList();
        return pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)];
    }

    string? RandomSpell(string? school, int level)
    {
        var pool = Core.Spells.Values.Where(s => s.Stock && s.Level == level && s.ManaCost > 0 &&
                (school == null ? s.School is "MAGIC_SCHOOL_DESTRUCTIVE" or "MAGIC_SCHOOL_DARK" or "MAGIC_SCHOOL_LIGHT" or "MAGIC_SCHOOL_SUMMONING" : s.School == school))
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        return pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)];
    }

    /// <summary>Treasure contents (fan manual p.269-270); custom amounts from the map win.</summary>
    void RollTreasure(MapObject o)
    {
        var os = S.Obj(o.Index);
        int min = o.Shared.MinResource, max = Math.Max(o.Shared.MinResource, o.Shared.MaxResource);
        int Roll() => o.TreasureCustom && o.TreasureAmount > 0 ? o.TreasureAmount : S.Rng.Range(min, max);
        switch (o.Shared.Type)
        {
            case "TREASURE_WOOD": os.ResourceKind = Purse.Wood; os.Amount = Roll(); break;
            case "TREASURE_ORE": os.ResourceKind = Purse.Ore; os.Amount = Roll(); break;
            case "TREASURE_MERCURY": os.ResourceKind = Purse.Mercury; os.Amount = Roll(); break;
            case "TREASURE_CRYSTAL": os.ResourceKind = Purse.Crystal; os.Amount = Roll(); break;
            case "TREASURE_SULFUR": os.ResourceKind = Purse.Sulfur; os.Amount = Roll(); break;
            case "TREASURE_GEMS": os.ResourceKind = Purse.Gem; os.Amount = Roll(); break;
            case "TREASURE_GOLD":
                os.ResourceKind = Purse.Gold;
                // Gold piles are 500–1000 in steps of 100 (manual p.269); data stores 5..10.
                os.Amount = o.TreasureCustom && o.TreasureAmount > 0 ? o.TreasureAmount : S.Rng.Range(min, max) * (max <= 20 ? 100 : 1);
                break;
            case "TREASURE_RANDOM":
            {
                os.ResourceKind = S.Rng.Next(7);
                os.Amount = os.ResourceKind switch
                {
                    Purse.Gold => S.Rng.Range(5, 10) * 100,
                    Purse.Wood or Purse.Ore => S.Rng.Range(4, 7),
                    _ => S.Rng.Range(2, 4),
                };
                break;
            }
            case "TREASURE_CAMPFIRE":
                // N of a random resource and 100·N gold; N is 4–6 for wood/ore, 4–5 otherwise (p.269).
                os.ResourceKind = S.Rng.Next(6);
                os.Amount = os.ResourceKind is Purse.Wood or Purse.Ore ? S.Rng.Range(4, 6) : S.Rng.Range(4, 5);
                break;
            case "TREASURE_CHEST":
            {
                // 4% minor artifact (DefaultStats ChestMinorArtifactChance), else gold or experience:
                // 1000/500, 1500/1000 or 2000/1500 (p.270).
                int chance = Db.Stats.Root.Element("adventure").Int("ChestMinorArtifactChance", 4);
                if (S.Rng.Next(100) < chance) os.ArtifactId = RandomArtifact("ARTF_CLASS_MINOR");
                os.Variant = S.Rng.Range(Math.Max(1, min), Math.Max(1, max)) - 1;   // data: MinResource 1, MaxResource 3
                break;
            }
            // Variants 1..4 (data Min/MaxResource): nothing / 5 wood / 5 wood+200 gold / 10 wood+500 gold (p.270).
            case "TREASURE_FLOATSAM": os.Variant = S.Rng.Range(Math.Max(1, min), Math.Max(1, max)) - 1; break;
            // TODO(exe): sea chest variants 1..4; the manual only documents "may provide 1500 gold".
            case "TREASURE_SEA_CHEST": os.Variant = S.Rng.Range(Math.Max(1, min), Math.Max(1, max)) - 1; break;
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

    void RollMageGuild(TownState t, MapObject o)
    {
        // Spells per circle (fan manual p.225): 3,3,3,2,2. Map-provided spells take precedence.
        int[] perLevel = { 3, 3, 3, 2, 2 };
        var schools = TownSchools(t.Type);
        for (int lvl = 1; lvl <= 5; lvl++)
        {
            var fromMap = o.TownSpells.Where(id => Core.Spell(id)?.Level == lvl).ToList();
            var pool = Core.Spells.Values.Where(s => s.Stock && s.Level == lvl && s.ManaCost > 0 && schools.Contains(s.School))
                .Select(s => s.Id).Where(id => !fromMap.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var list = fromMap.Take(perLevel[lvl - 1]).ToList();
            while (list.Count < perLevel[lvl - 1] && pool.Count > 0)
            {
                var i = S.Rng.Next(pool.Count);
                list.Add(pool[i]);
                pool.RemoveAt(i);
            }
            t.MageGuild[lvl - 1] = list;
        }
    }

    /// <summary>
    /// Magic schools offered by a town's guild. TODO(exe/data): the town shared descriptor's
    /// MagicSchool_0/1 fields bias the selection; here every combat school is allowed.
    /// </summary>
    static string[] TownSchools(string town) => town == "TOWN_STRONGHOLD"
        ? new[] { "MAGIC_SCHOOL_WARCRIES" }
        : new[] { "MAGIC_SCHOOL_DESTRUCTIVE", "MAGIC_SCHOOL_DARK", "MAGIC_SCHOOL_LIGHT", "MAGIC_SCHOOL_SUMMONING", "MAGIC_SCHOOL_ADVENTURE" };
}
