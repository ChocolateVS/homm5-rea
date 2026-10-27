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
            if (!S.Finished) EmitTurnStarted();
            return null;
        }
        // Next living player; wrapping around starts a new day.
        int idx = S.CurrentPlayer;
        do
        {
            idx++;
            if (idx >= S.Players.Count) { idx = 0; NewDay(); }
        } while (!GetsTurn(S.Players[idx]) && S.Players.Any(GetsTurn));   // exe FUN_0098e410 (Adventure.AiDirectives.cs)
        S.CurrentPlayer = idx;
        if (!S.Finished) EmitTurnStarted();
        return null;
    }

    void NewDay()
    {
        S.Day++;
        bool newWeek = S.DayOfWeek == 1;
        Emit(new DayStarted(S.Day));
        MoonNewDay(newWeek, out bool boughtMidWeek);
        // 009949d0: a week bought at a Tower of Astrologer that takes effect on days 2..7 runs the towns' weekly
        // update with full = false (00994a5c → 00993bf0(0) → IAdvMapWeeklyUpdate vf 4 = 00cd27b0): pool reduction and
        // the moon part only.
        if (boughtMidWeek) { TownWeeklyGrowth(full: false); MapDwellingWeeklyGrowth(full: false); }
        if (newWeek)
        {
            WeeklyGrowthAll();
            foreach (var p in S.Players) RollTavern(p);
            RestockTownArtifactMerchants();
            if (S.WeekOfMonth == 1) StockSpellShops();
            // 00993cd0 after the growth: IAdvMapWeeklyUpdate vf 0 = FUN_00cd2b90 per town (extras, the Trade Guild's
            // next resource, training points).
            foreach (var t in S.Towns) TownWeeklyUpdate(t);
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
            var ti = TownIncome(t);
            for (int i = 0; i < 7; i++) inc[i] += ti[i];
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
        TownWeeklyGrowth(full: true);
        MapDwellingWeeklyGrowth(full: true);
        foreach (var (idx, os) in S.Objects)
        {
            var o = O(idx);
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
    /// Town pools (00cd27b0, called by 00993bf0): the week's pool reduction (00bce0d0), then the growth record's total
    /// when <paramref name="full"/> (the regular new week, 00993cd0 → 00993bf0(1)), else only its moon part (record
    /// +0x1c; a bought week taking effect mid-week, 00994a5c → 00993bf0(0)); owner adjustment; positive amounts added.
    /// </summary>
    void TownWeeklyGrowth(bool full)
    {
        int week = Db.MoonWeekIndex(S.MoonWeek);
        foreach (var t in S.Towns)
            for (int tier = 1; tier <= 7; tier++)
            {
                if (t.Level($"TB_DWELLING_{tier}") <= 0) continue;
                t.Available[tier - 1] = MoonWeekPool(week, t.Available[tier - 1]);
                var g = GrowthOf(t, tier);
                int n = OwnerAdjustedGrowth(t, full ? g.Total : g.MoonWeek);
                if (n > 0) t.Available[tier - 1] += n;
            }
    }

    /// <summary>
    /// Map dwellings (CAdvMapDwelling IAdvMapWeeklyUpdate vf 4 = 009a76d0 → 009a71f0(calendar, full), called by the
    /// same 00993bf0(full) as the towns): the week's pool reduction (00bce0d0), then per creature a growth record whose
    /// only filled part is weekly = creature +0xa8 (009a64c0) plus the moon part (00bce140, <see cref="MoonWeekGrowth"/>
    /// of that weekly growth); full adds the record total, otherwise only the moon part; positive amounts are added.
    /// No owner adjustment. TODO(exe): dwelling types 0x54 / 0x5e / 0x5f (SAdvMapDwellingShared +0xec, path 009a7080)
    /// and the full update's other work (+0x110 list, 009a63a0 for an unowned dwelling) are not ported.
    /// </summary>
    void MapDwellingWeeklyGrowth(bool full)
    {
        int week = Db.MoonWeekIndex(S.MoonWeek);
        foreach (var (idx, os) in S.Objects)
        {
            var o = O(idx);
            if (o.Kind != ObjectKind.Dwelling || os.Removed) continue;
            for (int i = 0; i < os.Available.Length && i < o.Shared.DwellingCreatures.Count; i++)
            {
                var creature = o.Shared.DwellingCreatures[i];
                if (!Core.Creatures.TryGetValue(creature, out var cd)) continue;
                os.Available[i] = MoonWeekPool(week, os.Available[i]);
                int total = MoonWeekGrowth(week, cd.WeeklyGrowth, creature);
                int n = full ? total : total - cd.WeeklyGrowth;
                if (n > 0) os.Available[i] += n;
            }
        }
    }

    AI.AdventureAiStats? _aiStats;
    AI.AdventureAiStats AiStats => _aiStats ??= AI.AdventureAiStats.Load(Core);

    /// <summary>
    /// Owner adjustment of a town's weekly growth (exe FUN_00cd27b0, town vtable slot 0x00e95828): for a town of a
    /// computer player (player vf 0x184 false) n = round((float)n·c + ((week)·c − round(0.01f + week·c))) with
    /// c = clamp(AI/Adventure Difficulty*/TownCreatureGrowthCoef, 0, 10) (FUN_00465610 / FUN_00465640; stock Easy
    /// 0.5, others 1) and week = day counter / 7 + 1 (x87 rounding, half to even); then a positive count of a
    /// neutral town (owner 0) is halved. GAP-detail: the exe's day counter is taken as 0-based (= S.Week here).
    /// </summary>
    public int OwnerAdjustedGrowth(TownState t, int n)
    {
        if (t.Owner >= 0 && PlayerByIndex(t.Owner) is { Human: false })
            n = ComputerTownGrowth(n, AiStats.Difficulty[Math.Clamp(S.Difficulty, 0, 3)].TownCreatureGrowthCoef, S.Week);
        if (n > 0 && t.Owner < 0) n /= 2;
        return n;
    }

    /// <summary>FUN_00cd27b0's computer-town formula (see <see cref="OwnerAdjustedGrowth"/>).</summary>
    public static int ComputerTownGrowth(int n, double coef, int week)
    {
        float c = Math.Clamp((float)coef, 0f, 10f);
        float f2 = week * c;
        float carry = f2 - (float)Math.Round(0.01f + f2, MidpointRounding.ToEven);
        return (int)Math.Round((float)n * c + carry, MidpointRounding.ToEven);
    }

    /// <summary>Weekly growth of a town dwelling tier: the total of its growth record (<see cref="GrowthOf"/>).</summary>
    int WeeklyGrowth(TownState t, int tier) => GrowthOf(t, tier).Total;

    /// <summary>
    /// A player's turn starts. A human player (player vfunc 0x44 == 0) then gets the start-of-week notice when the
    /// day of the week equals the notice day (world +0x1dc: 1, or the day a bought week took effect) — 00994920 →
    /// 009946e0: START_OF_WEEK with weektype = the week's name, the creature icon of a creature week (00bcd640) or
    /// TEXTURE_INFO, and NEW_WEEK_NEUTRAL/GOOD/BAD_SOUND by the week's Type.
    /// </summary>
    void EmitTurnStarted()
    {
        Emit(new TurnStarted(Current.Index, S.Day));
        if (Current.Human && S.DayOfWeek == S.WeekNoticeDay) Emit(new WeekStarted(S.Week, S.MoonWeek));
    }

    void ResetHeroDay(HeroState h, bool newWeek)
    {
        // The hero's new-week handler 00b50880 clears the stables flag (+0x18c), the new-day handler 00b50470 the well's
        // (+0x174). The stables flag adds ForWeekStablesMaxLandMovementBonus (RPGStats +0x80) to the maximum land
        // movement (00b4dd40); 00b50880 sets it again when the hero's town (+0x17c) has ability 3 (the Stables).
        if (newWeek)
        {
            h.VisitedWeek.Clear();
            h.StablesThisWeek = h.InTown >= 0 && S.Town(h.InTown) is { } st && HasTownStables(st);
        }
        h.VisitedDay.Clear();
        h.DrankFromWellToday = false;
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        h.MovePoints = h.MaxMovePoints + (h.StablesThisWeek && !h.InBoat ? Db.Stats.Root.Element("adventure").Int("ForWeekStablesMaxLandMovementBonus", 600) : 0);
        // Mana regeneration, 00b50470 (00b504a2..00b504e0): below the maximum (vf 0x12c = +0x140, set from vf 0x130 of the
        // same knowledge) mana += knowledge (vf 0x1c = 00b4cd80, results below 2 read 1) ×2 when the hero's week (+0x1d0,
        // set by the new-week handler 00b50880) is 0x35 WEEK_OF_MEDITATION (00bcdc80), ×2 more when hero vf 0x174(0x28)
        // HERO_SKILL_MYSTICISM > 0; capped at the maximum.
        int max = MaxMana(h);
        int regen = Math.Max(1, h.Knowledge) * (S.MoonWeek == "WEEK_OF_MEDITATION" ? 2 : 1);
        if (RawMastery(h, "HERO_SKILL_MYSTICISM") > Mastery.None) regen *= 2;
        // 00b50470 regenerates only below the maximum: mana above it (an Eldritch Spring's) stays.
        if (h.Mana < max) h.Mana = Math.Min(max, h.Mana + regen);
        // 00b50470: in a town (+0x17c) with a mage guild (building 6) or ability 0x27 (Stronghold TB_SPECIAL_1) the
        // mana is raised to the maximum.
        if (h.InTown >= 0 && S.Town(h.InTown) is { } t
            && (t.Level("TB_MAGIC_GUILD") > 0 || (t.Type == H5.Combat.Towns.Stronghold && t.Level("TB_SPECIAL_1") > 0)))
            h.Mana = Math.Max(h.Mana, max);
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
        for (int i = 0; i < p.Tavern.Length; i++)
        {
            if (p.Tavern[i] != null && S.HeroPool.Contains(p.Tavern[i]!) && S.Week > 1) continue;
            RollTavernSlot(p, i);
        }
    }

    void RollTavernSlot(PlayerState p, int i)
    {
        var mp = Map.Players[p.Index];
        // Slot 0 prefers the player's race, slot 1 any race (TODO(exe): exact tavern rules).
        // AllowPlayerTavernRace/Hero (p.TavernFilter) keep heroes out of this player's taverns.
        var pool = S.HeroPool.Where(id => Core.Heroes.TryGetValue(id, out var d) && IsHireable(d, mp) &&
                                         !mp.BannedHeroRaces.Contains(d.Town) && (i != 0 || d.Town == p.Race) &&
                                         !p.Tavern.Contains(id) && TavernAccepts(p, id)).ToList();
        if (pool.Count == 0) pool = S.HeroPool.Where(id => !p.Tavern.Contains(id) && TavernAccepts(p, id)).ToList();
        p.Tavern[i] = pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)];
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
        foreach (var t in S.Towns.Where(t => t.Owner == p.Index)) { t.Owner = -1; t.OwnedSinceDay = S.Day; }
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
