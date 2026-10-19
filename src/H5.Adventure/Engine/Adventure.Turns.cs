using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    string? DoEndTurn()
    {
        if (S.Heroes.Any(h => h.Owner == Current.Index && h.Alive && h.PendingLevelUps.Count > 0))
            return "a hero has a pending level-up";
        if (TurnOrder != null)
        {
            int next = TurnOrder(S.CurrentPlayer);
            if (next < 0) { NewDay(); next = TurnOrder(-1); }
            if (next >= 0) S.CurrentPlayer = next;
            if (!S.Finished) Emit(new TurnStarted(Current.Index, S.Day));
            return null;
        }
        // Next living player; wrapping around starts a new day.
        int idx = S.CurrentPlayer;
        do
        {
            idx++;
            if (idx >= S.Players.Count) { idx = 0; NewDay(); }
        } while (!S.Players[idx].Alive && S.Players.Any(p => p.Alive));
        S.CurrentPlayer = idx;
        if (!S.Finished) Emit(new TurnStarted(Current.Index, S.Day));
        return null;
    }

    void NewDay()
    {
        S.Day++;
        bool newWeek = S.DayOfWeek == 1;
        Emit(new DayStarted(S.Day));
        if (newWeek)
        {
            ChooseMoonWeek();
            Emit(new WeekStarted(S.Week, S.MoonWeek));
            WeeklyGrowthAll();
            foreach (var p in S.Players) RollTavern(p);
            RestockTownArtifactMerchants();
            ResetTrainingPoints();
        }
        foreach (var t in S.Towns) t.BuiltToday = false;
        foreach (var p in S.Players.Where(p => p.Alive)) DailyIncome(p);
        foreach (var h in S.Heroes.Where(h => h.Alive)) ResetHeroDay(h, newWeek);
        CheckEliminations();
    }

    /// <summary>
    /// Optional: adjusts a mine's daily yield (object index, owner, resource kind, amount) → amount.
    /// Used by multiplayer Ghost Mode (haunted mines).
    /// </summary>
    public Func<int, int, int, int, int>? MineIncomeHook { get; set; }

    /// <summary>Daily gold of a town's hall (Town Info "Income").</summary>
    public static int TownGoldIncome(TownState t) => t.Level("TB_TOWN_HALL") switch { 1 => 500, 2 => 1000, 3 => 2000, >= 4 => 4000, _ => 0 };

    /// <summary>The daily income the player can expect (Kingdom Overview): as paid, minus the Dwarven Warren's random resource.</summary>
    public Purse ExpectedIncome(PlayerState p) => Income(p, display: true);

    /// <summary>Gold from town halls, resources from silos and mines (fan manual p.224, p.269).</summary>
    void DailyIncome(PlayerState p)
    {
        var inc = Income(p, display: false);
        if (inc.R.Any(v => v != 0))
        {
            for (int i = 0; i < 7; i++) p.Purse[i] += inc[i];
            Emit(new ResourcesChanged(p.Index, inc.R, (int[])p.Purse.R.Clone()));
        }
    }

    Purse Income(PlayerState p, bool display)
    {
        var inc = new Purse();
        foreach (var t in S.Towns.Where(t => t.Owner == p.Index))
        {
            inc[Purse.Gold] += TownGoldIncome(t);
            if (t.Level("TB_MARKETPLACE") >= 2) inc.Add(SiloIncome(t.Type));
        }
        foreach (var (idx, os) in S.Objects)
        {
            if (os.Owner != p.Index || os.Removed) continue;
            var o = O(idx);
            switch (o.Kind)
            {
                case ObjectKind.Mine:
                    var (kind, amount) = MineYield(o.Shared.Type);
                    if (kind >= 0 && MineIncomeHook != null && !display) amount = MineIncomeHook(idx, p.Index, kind, amount);
                    if (kind >= 0) inc[kind] += amount;
                    break;
                case ObjectKind.DwarvenWarren:
                    // One random resource excluding wood and gold (p.269).
                    if (!display) inc[Purse.Ore + 1 + S.Rng.Next(5)] += 1;
                    break;
            }
        }
        // Moon weeks affecting income (p.271).
        switch (S.MoonWeek)
        {
            case "WEEK_OF_GOLD": inc[Purse.Gold] *= 2; break;
            case "WEEK_OF_HARVEST": inc[Purse.Wood] *= 2; inc[Purse.Ore] *= 2; break;
            case "WEEK_OF_JEWELS": inc[Purse.Gem] *= 2; inc[Purse.Crystal] *= 2; break;
            case "WEEK_OF_ALCHEMY": inc[Purse.Mercury] *= 2; inc[Purse.Sulfur] *= 2; break;
            case "WEEK_OF_CONJUNCTION": for (int i = 0; i < 7; i++) inc[i] *= 3; break;
            case "WEEK_OF_FESTIVALS":
                inc[Purse.Gold] /= 2; inc[Purse.Wood] /= 2; inc[Purse.Ore] /= 2;
                inc[Purse.Mercury] = inc[Purse.Crystal] = inc[Purse.Sulfur] = inc[Purse.Gem] = 0;
                break;
        }
        return inc;
    }

    /// <summary>Daily yield of a mine type (p.269).</summary>
    public static (int Kind, int Amount) MineYield(string type) => type switch
    {
        "BUILDING_SAWMILL" => (Purse.Wood, 2),
        "BUILDING_ORE_PIT" => (Purse.Ore, 2),
        "BUILDING_ALCHEMIST_LAB" => (Purse.Mercury, 1),
        "BUILDING_CRYSTAL_CAVERN" => (Purse.Crystal, 1),
        "BUILDING_SULFUR_DUNE" => (Purse.Sulfur, 1),
        "BUILDING_GEM_POND" => (Purse.Gem, 1),
        "BUILDING_GOLD_MINE" => (Purse.Gold, 1000),
        _ => (-1, 0),
    };

    Resources SiloIncome(string town) =>
        Db.SiloIncome.TryGetValue(town, out var r) ? r : new Resources(0, 0, 0, 0, 0, 0, 0);

    void WeeklyGrowthAll()
    {
        double plague = S.MoonWeek switch
        {
            "WEEK_OF_PLAGUE" => 0, "WEEK_OF_DISEASE" => 1.0 / 3, "WEEK_OF_FEVER" => 0.5, _ => 1,
        };
        foreach (var t in S.Towns)
            for (int tier = 1; tier <= 7; tier++)
            {
                if (t.Level($"TB_DWELLING_{tier}") <= 0) continue;
                if (plague < 1) t.Available[tier - 1] = plague == 0 ? 0 : Math.Max(1, (int)(t.Available[tier - 1] * plague));
                t.Available[tier - 1] += WeeklyGrowth(t, tier);
            }
        foreach (var (idx, os) in S.Objects)
        {
            var o = O(idx);
            if (o.Kind == ObjectKind.Dwelling && !os.Removed)
                for (int i = 0; i < os.Available.Length && i < o.Shared.DwellingCreatures.Count; i++)
                    if (Core.Creatures.TryGetValue(o.Shared.DwellingCreatures[i], out var cd)) os.Available[i] += cd.WeeklyGrowth;
            // Neutral stacks grow weekly by the difficulty coefficient (DefaultStats WeeklyGrowCoefs, p.325).
            if (o.Kind == ObjectKind.Monster && !os.Removed && !o.DoesNotGrow)
            {
                var coefs = Db.Stats.Node(S.Campaign ? "adventure/NeutralsInCampaign/WeeklyGrowCoefs" : "adventure/NeutralsNotInCampaign/WeeklyGrowCoefs")?.Elements("Item").ToList();
                double k = coefs is { Count: > 0 } ? XmlHelpers.ParseDouble(coefs[Math.Min(S.Difficulty, coefs.Count - 1)].Value, 1) : 1.09;
                foreach (var g in os.Guards) g.Count = Math.Max(g.Count + 1, (int)(g.Count * k));
            }
        }
    }

    /// <summary>
    /// Weekly growth of a town dwelling tier: the creature's base growth, +50% with a Citadel,
    /// +100% with a Castle (fan manual p.224), plus moon-week multipliers (p.272).
    /// TODO(exe): owned external dwellings' growth bonus and town specialisations.
    /// </summary>
    int WeeklyGrowth(TownState t, int tier)
    {
        var def = Db.Building(t.Type, $"TB_DWELLING_{tier}", 1);
        if (def?.Creature == null || !Core.Creatures.TryGetValue(def.Creature, out var cr)) return 0;
        double g = cr.WeeklyGrowth;
        int fort = t.Level("TB_FORT");
        if (fort >= 3) g *= 2; else if (fort >= 2) g *= 1.5;
        g *= S.MoonWeek switch
        {
            "WEEK_OF_LIFE" => 2, "WEEK_OF_CONJUNCTION" => 3, "WEEK_OF_DISEASE" => 1.0 / 3, "WEEK_OF_FEVER" => 0.5, "WEEK_OF_PLAGUE" => 0,
            _ => 1,
        };
        return (int)g;
    }

    /// <summary>
    /// Picks the next week. TODO(exe): the original draw weights/rules (and the map's MoonCalendar
    /// overrides) are not decoded; weeks are drawn uniformly, with the first week neutral.
    /// </summary>
    void ChooseMoonWeek()
    {
        if (S.Week == 1 || Db.MoonWeeks.Count == 0) { S.MoonWeek = "WEEK_OF_NOTHING"; return; }
        S.MoonWeek = Db.MoonWeeks[S.Rng.Next(Db.MoonWeeks.Count)].Id;
    }

    void ResetHeroDay(HeroState h, bool newWeek)
    {
        if (newWeek) { h.VisitedWeek.Clear(); h.StablesThisWeek = false; }
        h.VisitedDay.Clear();
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        h.MovePoints = h.MaxMovePoints + (h.StablesThisWeek && !h.InBoat ? Db.Stats.Root.Element("adventure").Int("VisitStablesMovementBonus", 600) : 0);
        // Mana regeneration. TODO(exe): exact daily regen (base + Mana Regeneration perk + Week of Meditation).
        int max = MaxMana(h);
        int regen = Math.Max(1, h.Knowledge) * (S.MoonWeek == "WEEK_OF_MEDITATION" ? 2 : 1);
        h.Mana = Math.Min(max, h.Mana + regen);
        if (h.InTown >= 0 && S.Town(h.InTown) is { } t && t.Level("TB_MAGIC_GUILD") > 0) h.Mana = Math.Max(h.Mana, max);
    }

    /// <summary>Maximum mana: 10 per Knowledge (DefaultStats HeroManaPerKnowledge), ×1.5 with Intelligence.</summary>
    public int MaxMana(HeroState h)
    {
        int per = Db.Stats.Root.Element("adventure").Int("HeroManaPerKnowledge", 10);
        double m = h.Knowledge * per;
        if (h.HasPerk("HERO_SKILL_INTELLIGENCE"))
            m *= Db.Stats.Root.Element("adventure")!.Element("HeroSkills").Dbl("Intelligence_ManaPointsBonus", 1.5);
        return (int)m;
    }

    /// <summary>
    /// Starting movement points (fan manual p.300): 2500, Logistics +10/20/30% with Nival rounding,
    /// then Boots of the Swift Journey +25% (floored); Week of Flame +50%.
    /// Reproduces the manual's table: 2500/2749/2999/3249 and 3125/3436/3748/4061.
    /// </summary>
    public int ComputeMaxMovePoints(HeroState h)
    {
        int baseMp = Db.Stats.Root.Element("adventure").Int("BaseHeroMovement", 2500);
        double mp = baseMp;
        if (h.InBoat)
        {
            if (h.HasPerk("HERO_SKILL_NAVIGATION")) mp *= 1.5;
            if (h.HasArtifact("GOLDEN_SEXTANT")) mp *= 1.25;
            int lighthouses = S.Objects.Count(kv => kv.Value.Owner == h.Owner && O(kv.Key).Shared.Type == "BUILDING_LIGHTHOUSE");
            mp += lighthouses * Db.Stats.Root.Element("adventure").Int("LighthouseMaxSeaMovementBonus", 500);
            if (S.MoonWeek == "WEEK_OF_WINDS") mp *= 1.5;
            return (int)mp;
        }
        int log = (int)h.MasteryOf("HERO_SKILL_LOGISTICS");
        if (log > 0) mp = NivalFloor(baseMp * (1 + 0.1 * Math.Min(log, 3)));
        if (h.HasArtifact("BOOTS_OF_SPEED")) mp = Math.Floor(mp * 1.25);
        if (S.MoonWeek == "WEEK_OF_FLAME") mp = Math.Floor(mp * 1.5);
        return (int)mp;
    }

    void RollTavern(PlayerState p)
    {
        var mp = Map.Players[p.Index];
        for (int i = 0; i < p.Tavern.Length; i++)
        {
            if (p.Tavern[i] != null && S.HeroPool.Contains(p.Tavern[i]!) && S.Week > 1) continue;
            // Slot 0 prefers the player's race, slot 1 any race (TODO(exe): exact tavern rules).
            var pool = S.HeroPool.Where(id => Core.Heroes.TryGetValue(id, out var d) && IsHireable(d, mp) &&
                                             !mp.BannedHeroRaces.Contains(d.Town) && (i != 0 || d.Town == p.Race) &&
                                             !p.Tavern.Contains(id)).ToList();
            if (pool.Count == 0) pool = S.HeroPool.Where(id => !p.Tavern.Contains(id)).ToList();
            p.Tavern[i] = pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)];
        }
    }

    void CheckEliminations()
    {
        foreach (var p in S.Players.Where(p => p.Alive))
        {
            bool hasTown = S.Towns.Any(t => t.Owner == p.Index);
            bool hasHero = S.Heroes.Any(h => h.Alive && h.Owner == p.Index);
            if (hasTown) p.DaysWithoutTown = -1;
            else if (p.DaysWithoutTown < 0) p.DaysWithoutTown = 0;
            else p.DaysWithoutTown++;
            // A player with no town and no hero is out; on maps with "DieInWeekWithoutTowns" losing
            // every town also eliminates the player after 7 days.
            if ((DefaultVictoryRules && !hasTown && !hasHero) || (Map.DieInWeekWithoutTowns && p.DaysWithoutTown >= 7))
                Eliminate(p);
        }
        CheckVictory();
    }

    /// <summary>
    /// The automatic outcome rules (a player with no town and no hero is out; the last team standing
    /// wins) belong to OBJECTIVE_KIND_DEFEAT_ALL. Maps whose primary objectives are all scripted
    /// (OBJECTIVE_KIND_MANUAL, 18 of the stock campaign maps) decide victory and defeat in Lua
    /// (Win/Loose) — their AI players often start with nothing until scripts deploy them.
    /// </summary>
    public bool DefaultVictoryRules =>
        !Map.Objectives.Any(o => o.Primary) || Map.Objectives.Any(o => o.Primary && o.Kind is "OBJECTIVE_KIND_DEFEAT_ALL" or "OBJECTIVE_KIND_STAY_UNDEFEATED");

    void Eliminate(PlayerState p)
    {
        if (!p.Alive) return;
        p.Alive = false;
        foreach (var h in S.Heroes.Where(h => h.Owner == p.Index && h.Alive)) { h.Alive = false; Emit(new HeroRemoved(h.Id, "player eliminated")); }
        foreach (var t in S.Towns.Where(t => t.Owner == p.Index)) t.Owner = -1;
        foreach (var os in S.Objects.Values.Where(o => o.Owner == p.Index)) os.Owner = -1;
        Emit(new PlayerEliminated(p.Index));
    }

    /// <summary>Default victory: the last team standing (OBJECTIVE_KIND_DEFEAT_ALL). Scripted objectives need the Lua layer.</summary>
    void CheckVictory()
    {
        var teams = S.Players.Where(p => p.Alive).Select(p => p.Team).Distinct().ToList();
        if (teams.Count <= 1 && !S.Finished && (DefaultVictoryRules || teams.Count == 0))
        {
            S.Finished = true;
            S.WinnerTeam = teams.Count == 1 ? teams[0] : null;
            Emit(new GameEnded(S.WinnerTeam, "all enemies defeated"));
        }
    }
}
