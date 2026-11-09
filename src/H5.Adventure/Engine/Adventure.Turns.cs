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
            CaravansTurn(Current.Index);   // 00998060 → 00995f40: the caravans of the player getting the turn
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
        CaravansTurn(Current.Index);       // 00998060 → 00995f40: the caravans of the player getting the turn
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
            RefillTavernsWeekly();
            RestockTownArtifactMerchants();
            if (S.WeekOfMonth == 1) StockSpellShops();
            // 00993cd0 after the growth: IAdvMapWeeklyUpdate vf 0 = FUN_00cd2b90 per town (extras, the Trade Guild's
            // next resource, training points).
            foreach (var t in S.Towns) TownWeeklyUpdate(t);
        }
        RefillTavernsDaily();
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

    /// <summary>The daily income the player can expect (Kingdom Overview): as paid, minus the Dwarven Warren's and the Horn of Plenty's random resource.</summary>
    public Purse ExpectedIncome(PlayerState p) => Income(p, display: true);

    /// <summary>
    /// The new day's payment (world new day 009949d0): the income is rebuilt by 0098ef10 after the moon roll, then
    /// every player's purse (vf 0xd8) gains the stored income (vf 0xe8, CPlayer +0x58).
    /// </summary>
    void DailyIncome(PlayerState p)
    {
        var inc = Income(p, display: false);
        if (inc.R.Any(v => v != 0))
        {
            for (int i = 0; i < 7; i++) p.Purse[i] += inc[i];
            Emit(new ResourcesChanged(p.Index, inc.R, (int[])p.Purse.R.Clone()));
        }
    }

    /// <summary>
    /// 0098ef10: the player's income = Σ hero income (player vf 0xc0 heroes, hero vf 0x14c = 00b4e100, not halved) +
    /// Σ flagged-object income (IAdvMapFlagged slot 0x18: town 00cd43f0, mine 00b08b20, abandoned mine 008b54a0,
    /// Dwarven Warren 00457940), each object's purse halved component-wise in WEEK_OF_FESTIVALS (00bcd9a0 → 009d0e90(2)
    /// → 009d0e00, integer division); negative parts are clamped to 0 (009d0770). The moon factors are applied
    /// per part inside each income function (<see cref="MoonFactor"/>).
    /// </summary>
    Purse Income(PlayerState p, bool display)
    {
        var inc = new Purse();
        string week = S.MoonWeek ?? "";
        void AddObject(Purse part)
        {
            for (int i = 0; i < 7; i++)
            {
                int v = MoonFactor(i, part[i], week);
                if (week == "WEEK_OF_FESTIVALS") v /= 2;
                inc[i] += v;
            }
        }
        foreach (var t in S.Towns.Where(t => t.Owner == p.Index)) AddObject(TownIncome(t));
        foreach (var (idx, os) in S.Objects)
        {
            if (os.Owner != p.Index || os.Removed) continue;
            var o = O(idx);
            switch (o.Kind)
            {
                case ObjectKind.Mine:
                {
                    // 00b08b20: SAdvMapMineShared +0xec 1 mercury 1, 2 crystal 1, 3 gems 1, 4 gold 1000, 5 ore 2,
                    // 6 wood 2, 7 sulfur 1 (jump table 00b08d18).
                    var (kind, amount) = MineYield(o.Shared.Type);
                    if (kind >= 0 && MineIncomeHook != null && !display) amount = MineIncomeHook(idx, p.Index, kind, amount);
                    if (kind < 0) break;
                    var part = new Purse();
                    part[kind] = amount;
                    AddObject(part);
                    break;
                }
                case ObjectKind.AbandonedMine:
                {
                    // 008b54a0: the resource at +0x50 of the flagged part: 2 mercury 1, 3 crystal 1, 4 sulfur 1, 5 gems 1,
                    // 6 gold 1000 (jump table 008b55e4); none otherwise.
                    var (kind, amount) = AbandonedMineYield(os.ResourceKind);
                    if (kind < 0) break;
                    var part = new Purse();
                    part[kind] = amount;
                    AddObject(part);
                    break;
                }
                case ObjectKind.DwarvenWarren:
                {
                    // 00457940: one unit of the resource held at +0x60 of the flagged part (1..5: ore, mercury,
                    // crystal, sulfur, gems; 7 = none), moon factors by 00bcdaa0. 00457830 draws it 008e7840 over
                    // [1, 5] while the warren has an owner (7 otherwise).
                    if (display) break;
                    var part = new Purse();
                    part[Purse.Ore + S.Rng.Next(5)] = 1;
                    AddObject(part);
                    break;
                }
            }
        }
        foreach (var h in S.Heroes.Where(h => h.Alive && h.Owner == p.Index))
        {
            var hi = HeroIncome(h, display);
            for (int i = 0; i < 7; i++) inc[i] += hi[i];
        }
        for (int i = 0; i < 7; i++) if (inc[i] < 0) inc[i] = 0;
        return inc;
    }

    /// <summary>
    /// The moon week factor of one income part: 00bcda40 gold (0x3a WEEK_OF_GOLD ×2), 00bcda60 / 00bcda80 wood / ore
    /// (0x3c WEEK_OF_HARVEST ×2), 00bcd9c0 / 00bcd9e0 crystal / gems (0x38 WEEK_OF_JEWELS ×2), 00bcda20 / 00bcda00
    /// mercury / sulfur (0x39 WEEK_OF_ALCHEMY ×2), 00bcdaa0 all of them; every one ×3 in 0x37 WEEK_OF_CONJUNCTION.
    /// </summary>
    public static int MoonFactor(int resource, int amount, string? week)
    {
        if (week == "WEEK_OF_CONJUNCTION") return amount * 3;
        string doubled = resource switch
        {
            Purse.Gold => "WEEK_OF_GOLD",
            Purse.Wood or Purse.Ore => "WEEK_OF_HARVEST",
            Purse.Crystal or Purse.Gem => "WEEK_OF_JEWELS",
            _ => "WEEK_OF_ALCHEMY",
        };
        return week == doubled ? amount * 2 : amount;
    }

    /// <summary>
    /// A hero's daily income, CHero 00b4e100 (hero vf 0x14c): HERO_SPEC_SUZERAIN (spec 1, hero +0x8c via vf 0x190)
    /// 250 gold; Estates (skill 0x1d) 250 gold, ×2 with the Crown of Leadership (vf 0x310); per worn
    /// ENDLESS_SACK_OF_GOLD (artifact 0x1c, 00ca6680) 1000 gold and per ENDLESS_BAG_OF_GOLD (0x1d) 250 gold; with
    /// ARTIFACT_HORN_OF_PLENTY (0x5c) worn, by the day counter (world vf 0xa8) % 7 (jump table 00b4e434) wood 2..4, ore
    /// 2..4, sulfur 1..2, gems 1..2, mercury 1..2, crystal 1..2 or gold 500..750: (rand[min·1000, max·1000] + 382) / 1000,
    /// gold rounded down to 25, no moon factor; gold per creature with ABILITY_TAXPAYER (0x46, 00b61ba0) in the hero's
    /// army, ×2 for HERO_SPEC_PEASANTS (spec 4). Gold parts go through 00bcda40; heroes are not halved by festivals.
    /// </summary>
    public Purse HeroIncome(HeroState h, bool display = true)
    {
        var inc = new Purse();
        string week = S.MoonWeek ?? "";
        int Gold(int v) => MoonFactor(Purse.Gold, v, week);
        if (h.Specialization == "HERO_SPEC_SUZERAIN") inc[Purse.Gold] += Gold(250);
        if (h.HasPerk("HERO_SKILL_ESTATES")) inc[Purse.Gold] += Gold(250) * (h.HasArtifact("ARTIFACT_CROWN_OF_LEADER") ? 2 : 1);
        inc[Purse.Gold] += Gold(1000 * h.Artifacts.Count(a => a == "ENDLESS_SACK_OF_GOLD"))
                         + Gold(250 * h.Artifacts.Count(a => a == "ENDLESS_BAG_OF_GOLD"));
        if (!display && h.HasArtifact("ARTIFACT_HORN_OF_PLENTY"))
        {
            var (lo, hi, res, step) = ((S.Day - 1) % 7) switch
            {
                0 => (2, 4, Purse.Wood, 1),
                1 => (2, 4, Purse.Ore, 1),
                2 => (1, 2, Purse.Sulfur, 1),
                3 => (1, 2, Purse.Gem, 1),
                4 => (1, 2, Purse.Mercury, 1),
                5 => (1, 2, Purse.Crystal, 1),
                _ => (500, 750, Purse.Gold, 25),
            };
            int v = (lo * 1000 + S.Rng.Next((hi - lo) * 1000 + 1) + 0x17e) / 1000;
            if (step > 1) v -= v % step;
            inc[res] += v;
        }
        int tax = 0;
        foreach (var s in h.Army)
            if (s is { Count: > 0 } && Core.Creatures.TryGetValue(s.Creature, out var cd) && cd.Has("ABILITY_TAXPAYER"))
                tax += s.Count * (h.Specialization == "HERO_SPEC_PEASANTS" ? 2 : 1);
        inc[Purse.Gold] += Gold(tax);
        return inc;
    }

    /// <summary>Daily yield of a mine type (00b08b20).</summary>
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
                if (os.DwellingOff is { } off && i < off.Length && off[i]) continue;   // not in the dwelling's list (009a6040)
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
        h.MovePoints = h.MaxMovePoints;
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
    /// Maximum movement, H5_Game.exe 00b4dd40 (x87 products truncated, FISTP with the chop control word). B =
    /// BaseHeroMovement (RPGStats +0x5c); a specialization (hero vf 0x190 = 00b4b5a0, +0x8c) replaces it first:
    /// HERO_SPEC_LOGISTICS (9) and HERO_SPEC_BEATER (0xd) take p = (float)(⌈level / max(div, 1)⌉ · PerLevel + Base),
    /// clamped to 0..100 (00465610 / 00465640), and B = chop((p + 1.0001f) · B) / chop((p + 1.0f) · B) with
    /// HeroSpecializations Knight/Wanderer_* (+0x818..+0x820) / Demonlord/Beater_* (+0x838..+0x840);
    /// HERO_SPEC_RASH (0x48): B = chop((level · Barbarian/Rash_MovePointsIncreasePerPevel (+0x95c) + 1.0001f) · B).
    /// The week (00bcdc30): WEEK_OF_FLAME adds B / 2 to land and sea, WEEK_OF_WINDS only to sea. Land (+0x14c):
    /// chop((Logistics mastery (vf 0x174(1)) · 0.1f + 1.0001f) · L), then chop((worn BOOTS_OF_SPEED (artifact 0x18,
    /// 00ca6680) · 0.25f + 1.0f) · L), + ForWeekStablesMaxLandMovementBonus (+0x80) while the stables flag is set
    /// (vf 0x210). Sea (+0x150): Navigation (vf 0x174(0x15) ≠ 0) chop(S · 1.5f), chop((worn GOLDEN_SEXTANT (10) ·
    /// 0.25f + 1.0f) · S), + LighthouseMaxSeaMovementBonus (+0x84) × the player's lighthouses (player vf 0x130 =
    /// 004fb750, +0x13c; 00bca4f0 counts captures ±1). The third value (+0x154, movement type 2) is not used here.
    /// </summary>
    public int ComputeMaxMovePoints(HeroState h)
    {
        var adv = Db.Stats.Root.Element("adventure");
        int b = adv.Int("BaseHeroMovement", 2500);
        const float Eps = 1.0001f;   // 00e4f9c0
        float Spec(string path, double def) => (float)Core.Stats.Get("combat/HeroSpecializations/" + path, def);
        int SpecBase(string race, string name, float add)
        {
            int div = Math.Max((int)Spec($"{race}/{name}_LevelDivisor", 1), 1);
            int q = (h.Level + div - 1) / div;
            float p = (float)(q * (double)Spec($"{race}/{name}_MPBonusPerLevel", 0) + Spec($"{race}/{name}_MPBonusBase", 0));
            p = Math.Max(0f, Math.Min(p, 100f));
            return Chop(((double)p + add) * b);
        }
        int start = h.Specialization switch
        {
            "HERO_SPEC_LOGISTICS" => SpecBase("Knight", "Wanderer", Eps),
            "HERO_SPEC_BEATER" => SpecBase("Demonlord", "Beater", 1.0f),
            "HERO_SPEC_RASH" => Chop((h.Level * (double)Spec("Barbarian/Rash_MovePointsIncreasePerPevel", 0.01) + Eps) * b),
            _ => b,
        };
        int Week(int mp, bool sea) => S.MoonWeek == "WEEK_OF_FLAME" || (sea && S.MoonWeek == "WEEK_OF_WINDS") ? mp + mp / 2 : mp;
        if (h.InBoat)
        {
            int sea = Week(start, true);
            if (h.HasPerk("HERO_SKILL_NAVIGATION") || EffectiveMastery(h, "HERO_SKILL_NAVIGATION") != 0) sea = Chop(sea * (double)1.5f);
            sea = Chop((h.Artifacts.Count(a => a == "GOLDEN_SEXTANT") * (double)0.25f + 1.0f) * sea);
            int lighthouses = S.Objects.Count(kv => kv.Value.Owner == h.Owner && h.Owner >= 0 && !kv.Value.Removed && O(kv.Key).Shared.Type == "BUILDING_LIGHTHOUSE");
            return sea + lighthouses * adv.Int("LighthouseMaxSeaMovementBonus", 500);
        }
        int land = Week(start, false);
        land = Chop((EffectiveMastery(h, "HERO_SKILL_LOGISTICS") * (double)0.1f + Eps) * land);
        land = Chop((h.Artifacts.Count(a => a == "BOOTS_OF_SPEED") * (double)0.25f + 1.0f) * land);
        if (h.StablesThisWeek) land += adv.Int("ForWeekStablesMaxLandMovementBonus", 600);
        return land;
    }

    static int Chop(double x) => (int)Math.Truncate(x);

    /// <summary>
    /// The weekly and initial tavern refill, FUN_00990550 (from 00993cd0 each new week and from 0041ecd0 at the game
    /// start): every playing player (vfunc 0x6c == 1, players 1..8 in order) first releases his offers (vfunc 0x154 =
    /// 004fd6b0: each slot's hero back to the pool, pool vf 0x24(hero, 0, 0), slot cleared); then, while players remain,
    /// a random one (world vfunc 0x94 → vf 0x20, % count) gets one hero (vfunc 0x158 = 004fd600 with full = true) and
    /// leaves the list when he takes none (both slots full or no hero for him). GAP: 004fd6b0 keeps a hero whose flag
    /// +0x1e4 (vf 0x1e0) is set or who is in the pool's list +0x60 (vf 0x4c); neither is identified, so all are released.
    /// </summary>
    void RefillTavernsWeekly()
    {
        var players = S.Players.Where(p => p.Alive).ToList();
        foreach (var p in players)
            for (int i = 0; i < p.Tavern.Length; i++) p.Tavern[i] = null;
        while (players.Count > 0 && S.HeroPool.Count > 0)
        {
            int k = (int)(NextRand32() % (uint)players.Count);
            if (!FillTavernSlot(players[k], full: true)) players.RemoveAt(k);
        }
    }

    /// <summary>
    /// Every new day (009949d0) each playing player's vfunc 0x158 = 004fd600 (pool, false) runs twice: an emptied slot
    /// gets a hero with the one-creature army (hero vfunc 0x1f0(false), <see cref="GiveStartingArmy"/>). On the first
    /// day of a week the weekly refill (00990550) has already filled them.
    /// </summary>
    void RefillTavernsDaily()
    {
        foreach (var p in S.Players.Where(p => p.Alive))
        {
            FillTavernSlot(p, full: false);
            FillTavernSlot(p, full: false);
        }
    }

    /// <summary>
    /// CPlayer vfunc 0x158 = FUN_004fd600(pool, full): nothing when both slots hold a hero; otherwise FUN_004fc770 draws
    /// a hero for the first empty slot (slot 0 empty: pool vf 0x38 = 00ce8050 with the player's race (+0x198) first,
    /// then with 2 = any race; slot 1: any race), the hero gets his army at once (vfunc 0x1f0(full)) and the pool gives
    /// him to the player (vf 0x20 = 00ce7980 → hero vf 0x40(player)). Returns false when no hero was placed.
    /// </summary>
    bool FillTavernSlot(PlayerState p, bool full)
    {
        int i = Array.IndexOf(p.Tavern, null);
        if (i < 0) return false;
        var id = DrawTavernHero(p, raceFirst: i == 0);
        if (id == null) return false;
        PlaceTavernHero(p, i, id, full);
        return true;
    }

    /// <summary>
    /// The pool draw (00ce8050): among the pool's heroes the player may get (vf 0x1c = 00ce7920: the hero has no player,
    /// i.e. is in no tavern and not hired; the player's tavern filter, CHeroesFilter rule of player vf 0x224) of the asked
    /// race, one at FUN_008e7840(n − 1) (inclusive). <paramref name="raceFirst"/>: the player's race, then any race.
    /// </summary>
    string? DrawTavernHero(PlayerState p, bool raceFirst)
    {
        var mp = Map.Players[p.Index];
        List<string> Candidates(bool race) => S.HeroPool.Where(id => Core.Heroes.TryGetValue(id, out var d) && IsHireable(d, mp) &&
            !mp.BannedHeroRaces.Contains(d.Town) && (!race || d.Town == p.Race) &&
            !S.Players.Any(o => o.Tavern.Contains(id)) && TavernAccepts(p, id)).ToList();
        var pool = raceFirst ? Candidates(true) : new List<string>();
        if (pool.Count == 0) pool = Candidates(false);
        return pool.Count == 0 ? null : pool[S.Rng.Next(pool.Count)];
    }

    /// <summary>
    /// A hero entering tavern slot <paramref name="i"/> gets his army there (004fd600: vfunc 0x1f0(full) right after the
    /// draw). A hero that fled or surrendered earlier is the same object again, so his own army is replaced.
    /// </summary>
    void PlaceTavernHero(PlayerState p, int i, string defId, bool full)
    {
        p.Tavern[i] = defId;
        var back = S.Heroes.Find(x => !x.Alive && x.Retreat != 0 && x.DefId == defId);
        var h = back ?? new HeroState { DefId = defId, Town = Core.Heroes[defId].Town, Specialization = Core.Heroes[defId].Specialization };
        GiveStartingArmy(h, full);
        p.TavernArmy[i] = back != null ? State.Army.Empty() : h.Army;
    }

    /// <summary>Re-rolls one slot (an offer the player's new tavern filter rejects).</summary>
    void RollTavernSlot(PlayerState p, int i)
    {
        p.Tavern[i] = null;
        if (DrawTavernHero(p, raceFirst: i == 0) is { } id) PlaceTavernHero(p, i, id, full: true);
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
