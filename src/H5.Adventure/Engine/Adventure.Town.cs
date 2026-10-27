using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Towns: building, recruiting, taverns, blacksmith, mage guild, armies.
public sealed partial class Adventure
{
    /// <summary>The hire price with one hero (exe 00b4ced0 is 500 × (heroes + 4), see <see cref="HireCost"/>).</summary>
    public const int HeroHireCost = 2500;
    public const int MaxHeroesPerPlayer = 8; // TODO(exe): confirm the TotE hero cap

    void VisitTown(HeroState h, TownState t)
    {
        if (t.Owner == h.Owner || Allied(t.Owner, h.Owner))
        {
            h.InTown = t.Object;
            // IAdvMapTown vf 0x14 = 00cd2680 (the hero put into the town): Stables 00cd2230, the guild 00cced80, the
            // Arcane Forge 00cd2310. No mana here: the refill is the hero's new day (00b50470, ResetHeroDay).
            TownStables(h, t);
            LearnFromGuild(h, t);
            UpgradeMicroArtifacts(h, t);
            Emit(new ObjectVisited(h.Id, t.Object, "town"));
            return;
        }
        var garrisonHero = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && x.Garrisoned);
        if (Refused(h, garrisonHero?.Id ?? -1, t.Object)) return;
        var defenders = garrisonHero != null ? garrisonHero.Army.Live().ToList() : t.Garrison.Live().ToList();
        if (defenders.Count == 0 && garrisonHero == null)
        {
            CaptureTown(h, t);
            return;
        }
        StartBattle(h, garrisonHero?.Id ?? -1, t.Object, t.Pos, defenders.Select(s => new Stack(s.Creature, s.Count)).ToList(),
            siege: t.Level("TB_FORT") > 0);
    }

    void CaptureTown(HeroState h, TownState t)
    {
        var prev = t.Owner;
        t.Owner = h.Owner;
        t.OwnedSinceDay = S.Day;
        h.InTown = t.Object;
        Emit(new ObjectCaptured(t.Object, h.Owner));
        // Conquering a town is worth 500 experience (p.298).
        GainExperience(h, 500);
        if (prev >= 0 && PlayerByIndex(prev) is { } pp) RevealOwned(pp);
        RevealOwned(PlayerByIndex(h.Owner)!);
        CheckEliminations();
    }

    /// <summary>
    /// FUN_00cced80: only with town ability 0x15 (Academy TB_SPECIAL_1, the Library), 0x20 (Fortress TB_SPECIAL_1),
    /// 0x27 (Stronghold TB_SPECIAL_1) or a mage guild (building 6) — hero vf 0xf0 = 00b4e640: circles 1..5 of the town's
    /// offer (town vf 0x24), each spell the hero can learn (vf 0xe4) is learned (vf 0xe8(spell, 0)); then hero vf 0x9c.
    /// </summary>
    void LearnFromGuild(HeroState h, TownState t)
    {
        bool special = t.Level("TB_SPECIAL_1") > 0 && t.Type is H5.Combat.Towns.Academy or H5.Combat.Towns.Fortress or H5.Combat.Towns.Stronghold;
        if (!special && t.Level("TB_MAGIC_GUILD") <= 0) return;
        // Every circle's offer (CTown vf 0x24 = FUN_00ccfab0: guild + Library, runes, warcries).
        for (int lvl = 1; lvl <= 5; lvl++)
            foreach (var sp in TownSpellsAt(t, lvl)) LearnSpell(h, sp, force: false);
    }

    string? DoBuild(BuildCmd c)
    {
        var t = S.Town(c.TownObject);
        if (t == null || t.Owner != Current.Index) return "not your town";
        var p = Current;
        var why = BuildCheck(t, c.Building, p.Purse);
        if (why != BuildDenial.CanBuild) return BuildDenialText(why);
        int next = t.Level(c.Building) + 1;
        var def = Db.Building(t.Type, c.Building, next)!;
        if (c.Building == "TB_GRAIL") ConsumeTearOfAsha(t);
        Give(p, Purse.Scale(def.Cost, -1));
        int trainingBefore = TrainingPointsMax(t);
        TownSpellsOnBuild(t, c.Building, next - 1);
        t.Built[c.Building] = next;
        if (HasTradeGuild(t) && c.Building == "TB_SPECIAL_4") RollTradeGuild(t);   // 00cd4a40 at 00cd4d89: ability 0x1d
        TrainingPointsAfterBuild(t, trainingBefore);
        t.BuiltToday = true;
        // A new dwelling's pool starts with its base creature's WeeklyGrowth (00cd4a40 at 00cd4e2c: when
        // FUN_00cd0710 creates the pool, creature +0xa8); no fort, horde or week bonuses.
        if (c.Building.StartsWith("TB_DWELLING_") && next == 1 && int.TryParse(c.Building[^1..], out var tier)
            && Db.Building(t.Type, c.Building, 1)?.Creature is { } dc && Core.Creatures.TryGetValue(dc, out var dcd))
            t.Available[tier - 1] += dcd.WeeklyGrowth;
        Emit(new BuildingBuilt(t.Object, c.Building, next));
        StockArtifactMerchants();   // 00cd4a40 at 00cd4db7: a merchant town without a CBlackMarket gets one stocked
        BuildingEffectsOnHeroes(t, c.Building);
        return null;
    }

    /// <summary>
    /// FUN_00cd2390 (from 00cd4a40) for the town's heroes, by the building just built: the guild, ability 0x15 / 0x20
    /// / 0x27 (TB_SPECIAL_1 of an Academy / Fortress / Stronghold) → 00cced80 (learning); ability 3 (Haven TB_SPECIAL_4,
    /// the Stables) → 00cd2230; ability 0x16 (the Arcane Forge) → 00cd2310.
    /// </summary>
    void BuildingEffectsOnHeroes(TownState t, string building)
    {
        var heroes = S.Heroes.Where(h => h.Alive && h.InTown == t.Object).ToList();
        bool learn = building == "TB_MAGIC_GUILD"
            || (building == "TB_SPECIAL_1" && t.Type is H5.Combat.Towns.Academy or H5.Combat.Towns.Fortress or H5.Combat.Towns.Stronghold);
        foreach (var h in heroes)
        {
            if (learn) LearnFromGuild(h, t);
            if (building == StablesBuilding && t.Type == H5.Combat.Towns.Haven) TownStables(h, t);
            if (building == ArcaneForgeBuilding) UpgradeMicroArtifacts(h, t);
        }
    }

    public const string StablesBuilding = "TB_SPECIAL_4";

    /// <summary>Town ability 3 (table 00f7c998: Haven TB_SPECIAL_4, the Stables).</summary>
    public static bool HasTownStables(TownState t) => t.Type == H5.Combat.Towns.Haven && t.Level(StablesBuilding) >= 1;

    /// <summary>
    /// FUN_00cd2230: with ability 3 and the hero's stables flag (vf 0x210, +0x18c) clear: the flag is set (vf 0x214)
    /// and the movement points get VisitStablesMovementBonus (RPGStats +0x7c, vf 0x100(vf 0xf4 + bonus)), as the map's
    /// Stables (00b02740 case 0xf).
    /// </summary>
    void TownStables(HeroState h, TownState t)
    {
        if (!HasTownStables(t) || h.StablesThisWeek) return;
        h.StablesThisWeek = true;
        h.MovePoints += Db.Stats.Root.Element("adventure").Int("VisitStablesMovementBonus", 600);
    }

    /// <summary>Town state shown by the hero/town list marker (UI TownHeroPanel: BuildDone, BuildAllComplete, NothingBuild, BuildNoResources).</summary>
    public enum TownBuildState { BuiltToday, AllComplete, CanBuild, NoResources }

    /// <summary>
    /// What the adventure HUD marker shows for a town: already built today, every building at its
    /// maximum level, something affordable and allowed today, or nothing that can be built now.
    /// Uses the same checks as <see cref="DoBuild"/> against the owner's purse.
    /// </summary>
    public TownBuildState BuildState(TownState t)
    {
        if (t.BuiltToday) return TownBuildState.BuiltToday;
        var purse = PlayerByIndex(t.Owner)?.Purse;
        bool anyLeft = false;
        foreach (var building in t.MaxLevel.Keys)
        {
            var why = BuildCheck(t, building, purse);
            if (why == BuildDenial.CanBuild) return TownBuildState.CanBuild;
            if (why is not (BuildDenial.AlreadyBuilt or BuildDenial.LimitUpgrade or BuildDenial.CantBuild or BuildDenial.NoWater)) anyLeft = true;
        }
        return anyLeft ? TownBuildState.NoResources : TownBuildState.AllComplete;
    }

    /// <summary>Creature offered by a town dwelling tier: variant 0 base, 1 upgrade, 2 alternative upgrade (TotE).</summary>
    public string? DwellingCreature(TownState t, int tier, int variant)
    {
        int lvl = t.Level($"TB_DWELLING_{tier}");
        if (lvl <= 0 || (variant > 0 && lvl < 2)) return null;
        var def = Db.Building(t.Type, $"TB_DWELLING_{tier}", variant == 0 ? 1 : 2);
        return variant switch { 0 => def?.Creature, 1 => def?.Creature, 2 => def?.Creature2, _ => null };
    }

    /// <summary>The garrison hero of a town (town hero slot 1), if any.</summary>
    public HeroState? GarrisonHeroOf(TownState t) => S.Heroes.Find(h => h.Alive && h.InTown == t.Object && h.Garrisoned);

    /// <summary>
    /// The army a town's hire screen recruits into (0061af30: town hero slot 1's army when there is one, else the town
    /// army): the garrison hero's army, else the town garrison.
    /// </summary>
    public Stack?[] TownRecruitArmy(TownState t) => GarrisonHeroOf(t)?.Army ?? t.Garrison;

    /// <summary>
    /// "Hire all" (FUN_009a52a0, used by the hire screen's cost preview 006dcfe0 and by CHireAllCreaturesCmd 0042b870):
    /// dwellings from the highest tier down; per dwelling its creature variants (base, upgrade, alternative) or, when
    /// <paramref name="variants"/> gives a column (≥ 0) for the tier, only that variant; variants are tried from the last
    /// down while the dwelling's pool lasts. A creature the army does not hold yet takes one of the army's free slots
    /// (none left → skipped). With <paramref name="limitByMoney"/> each count is min(pool, floor(purse / cost)) (the
    /// exe truncates the float ratio of 009d0500) and the cost comes off a copy of the purse.
    /// </summary>
    public List<(int Tier, int Variant, string Creature, int Count)> HireAllPlan(TownState t, Purse purse, Stack?[] army, int[]? variants, bool limitByMoney = true)
    {
        var plan = new List<(int, int, string, int)>();
        var money = new Purse { R = (int[])purse.R.Clone() };
        int free = army.Count(s => s is not { Count: > 0 });
        for (int tier = 7; tier >= 1; tier--)
        {
            var cands = new List<int>();
            for (int v = 0; v < 3; v++) if (DwellingCreature(t, tier, v) != null) cands.Add(v);
            if (cands.Count == 0) continue;
            if (variants != null && variants.Length == 7 && variants[tier - 1] >= 0) cands.RemoveAll(v => v != variants[tier - 1]);
            int pool = t.Available[tier - 1];
            for (int i = cands.Count - 1; i >= 0 && pool > 0; i--)
            {
                var creature = DwellingCreature(t, tier, cands[i])!;
                if (!Core.Creatures.TryGetValue(creature, out var cd)) continue;
                bool newStack = !army.Any(s => s is { Count: > 0 } && s.Creature == creature) && !plan.Any(x => x.Item3 == creature);
                if (newStack && free <= 0) continue;
                int n = pool;
                if (limitByMoney) n = Math.Min(n, Affordable(money, cd.Cost));
                if (n <= 0) continue;
                plan.Add((tier, cands[i], creature, n));
                pool -= n;
                if (limitByMoney) money.Add(cd.Cost, -n);
                if (newStack) free--;
            }
        }
        return plan;
    }

    /// <summary>
    /// Hire all at a map dwelling: CHireAllCreaturesCmd (0042b870) over the screen's source list (source vfunc 0 — for a
    /// map dwelling its creature rows, one creature each), planned by the same FUN_009a52a0: rows from the last down, a
    /// creature the army does not hold takes a free slot (none left → skipped), count = min(pool, floor(purse / cost)),
    /// paid from a copy of the purse.
    /// </summary>
    public List<(int Row, string Creature, int Count)> DwellingHireAllPlan(int dwelling, Purse purse, Stack?[] army)
    {
        var plan = new List<(int, string, int)>();
        if (!S.Objects.TryGetValue(dwelling, out var os)) return plan;
        var list = O(dwelling).Shared.DwellingCreatures;
        var money = new Purse { R = (int[])purse.R.Clone() };
        int free = army.Count(s => s is not { Count: > 0 });
        for (int row = Math.Min(list.Count, os.Available.Length) - 1; row >= 0; row--)
        {
            var creature = list[row];
            if (os.Available[row] <= 0 || !Core.Creatures.TryGetValue(creature, out var cd)) continue;
            bool newStack = !army.Any(s => s is { Count: > 0 } && s.Creature == creature) && !plan.Any(x => x.Item2 == creature);
            if (newStack && free <= 0) continue;
            int n = Math.Min(os.Available[row], Affordable(money, cd.Cost));
            if (n <= 0) continue;
            plan.Add((row, creature, n));
            money.Add(cd.Cost, -n);
            if (newStack) free--;
        }
        return plan;
    }

    /// <summary>The visiting hero of a map dwelling (the hire screen's recruit army), or null.</summary>
    public HeroState? DwellingVisitor(int dwelling, int player) =>
        S.Heroes.Find(h => h.Alive && h.Owner == player && O(dwelling).ActiveTiles.Contains(h.Pos));

    /// <summary>How many of a cost the purse pays for (FUN_009d0500: the smallest resource ratio, truncated).</summary>
    public static int Affordable(Purse purse, Resources cost)
    {
        int[] c = { cost.Wood, cost.Ore, cost.Mercury, cost.Crystal, cost.Sulfur, cost.Gem, cost.Gold };
        int n = int.MaxValue;
        for (int i = 0; i < 7; i++) if (c[i] != 0) n = Math.Min(n, Math.Max(0, purse[i]) / c[i]);
        return n == int.MaxValue ? int.MaxValue : n;
    }

    /// <summary>CHireAllCreaturesCmd (execute 0042b870): hires the <see cref="HireAllPlan"/> into the town's recruit army; fails when it is empty.</summary>
    string? DoHireAll(HireAllCmd c)
    {
        var p = Current;
        if (S.Town(c.TownObject) is not { } t) return HireAllAtDwelling(c);
        if (t.Owner != p.Index) return "not your town";
        var army = TownRecruitArmy(t);
        var plan = HireAllPlan(t, p.Purse, army, c.Variants);
        if (plan.Count == 0) return "nothing to hire";
        foreach (var (tier, _, creature, n) in plan)
        {
            var cost = Purse.Scale(Core.Creatures[creature].Cost, n);
            if (!p.Purse.CanAfford(cost) || t.Available[tier - 1] < n || !army.TryAdd(creature, n)) continue;
            t.Available[tier - 1] -= n;
            Give(p, Purse.Scale(cost, -1));
            Emit(new CreaturesRecruited(c.TownObject, creature, n));
        }
        var gh = GarrisonHeroOf(t);
        Emit(new ArmyChanged(gh?.Id ?? -1, gh == null ? c.TownObject : -1));
        return null;
    }

    /// <summary>CHireAllCreaturesCmd at a map dwelling: the <see cref="DwellingHireAllPlan"/> into the visiting hero.</summary>
    string? HireAllAtDwelling(HireAllCmd c)
    {
        var p = Current;
        if (!S.Objects.TryGetValue(c.TownObject, out var os) || O(c.TownObject).Kind != ObjectKind.Dwelling) return "no such town or dwelling";
        if (os.Owner != p.Index) return "not your dwelling";
        if (DwellingVisitor(c.TownObject, p.Index) is not { } h) return "a hero must visit the dwelling";
        var plan = DwellingHireAllPlan(c.TownObject, p.Purse, h.Army);
        if (plan.Count == 0) return "nothing to hire";
        foreach (var (row, creature, n) in plan)
        {
            var cost = Purse.Scale(Core.Creatures[creature].Cost, n);
            if (!p.Purse.CanAfford(cost) || os.Available[row] < n || !h.Army.TryAdd(creature, n)) continue;
            os.Available[row] -= n;
            Give(p, Purse.Scale(cost, -1));
            Emit(new CreaturesRecruited(c.TownObject, creature, n));
        }
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    string? DoRecruit(RecruitCmd c)
    {
        if (c.Count <= 0) return "invalid count";
        var p = Current;
        string? creature;
        int[] pool;
        int slot;
        Stack?[] target;
        HeroState? visitor = null;
        if (S.Town(c.TownObject) is { } t)
        {
            if (t.Owner != p.Index) return "not your town";
            creature = DwellingCreature(t, c.Tier, c.Variant);
            pool = t.Available;
            slot = c.Tier - 1;
            // The town hire screen (0061af30 → 006db170) hires into the town hero slot 1 (the garrison hero, town
            // vf 0x18(1) → hero vf 0x48) when there is one, else into the town's own army (town vf 0x2c, the garrison).
            visitor = GarrisonHeroOf(t);
            target = TownRecruitArmy(t);
        }
        else if (S.Objects.TryGetValue(c.TownObject, out var os) && O(c.TownObject).Kind == ObjectKind.Dwelling)
        {
            if (os.Owner != p.Index) return "not your dwelling";
            var list = O(c.TownObject).Shared.DwellingCreatures;
            slot = c.Tier - 1;
            if (slot < 0 || slot >= list.Count) return "invalid tier";
            creature = list[slot];
            pool = os.Available;
            visitor = S.Heroes.Find(h => h.Alive && h.Owner == p.Index && O(c.TownObject).ActiveTiles.Contains(h.Pos));
            if (visitor == null) return "a hero must visit the dwelling";
            target = visitor.Army;
        }
        else return "no such town or dwelling";

        if (creature == null || !Core.Creatures.TryGetValue(creature, out var cd)) return "creature not available";
        if (slot < 0 || slot >= pool.Length || pool[slot] < c.Count) return "not enough creatures";
        var cost = Purse.Scale(cd.Cost, c.Count);
        if (!p.Purse.CanAfford(cost)) return "not enough resources";
        var trial = target.Select(s => s == null ? null : new Stack(s.Creature, s.Count)).ToArray();
        if (!trial.TryAdd(creature, c.Count)) return "no free army slot";
        target.TryAdd(creature, c.Count);
        pool[slot] -= c.Count;
        Give(p, Purse.Scale(cost, -1));
        Emit(new CreaturesRecruited(c.TownObject, creature, c.Count));
        Emit(new ArmyChanged(visitor?.Id ?? -1, visitor == null ? c.TownObject : -1));
        return null;
    }

    /// <summary>
    /// Town hiring status, exe 004f4570 (the town tavern's action button is enabled only at 0, 00636d20): 2 = no
    /// tavern (town vfunc 0x34(4) &lt; 1), 3 = gold below <see cref="HireCost"/> with the town, 4 = no free town hero
    /// slot (004f41b0), 5 = too many heroes. Status 1 (player vfunc 0x150 on the hero) is not modelled.
    /// GAP: 004f41b0 also accepts a free second slot when the first is taken; the hire command (00639060, vtable
    /// 00e1976c) is not traced, so the engine keeps needing a free entrance (<see cref="DoHire"/>).
    /// </summary>
    public int TownHireStatus(PlayerState p, TownState t)
    {
        if (t.Level("TB_TAVERN") < 1) return 2;
        if (p.Purse[Purse.Gold] < HireCost(p, t)) return 3;
        if (HeroAt(EntranceOf(t)) != null) return 4;
        if (S.Heroes.Count(h => h.Alive && h.Owner == p.Index) >= HiringHeroLimit(p)) return 5;
        return 0;
    }

    string? DoHire(HireHeroCmd c)
    {
        var t = S.Town(c.TownObject);
        var p = Current;
        if (t == null || t.Owner != p.Index) return "not your town";
        if (t.Level("TB_TAVERN") <= 0) return "no tavern";
        // The slot indexes the heroes this town's tavern shows (town hiring filter, exe FUN_00636a10).
        var offers = TownTavernOffers(p, t);
        if (c.TavernSlot < 0 || c.TavernSlot >= offers.Length || offers[c.TavernSlot] is not { } defId) return "no hero in that slot";
        if (S.Heroes.Count(h => h.Alive && h.Owner == p.Index) >= HiringHeroLimit(p)) return "too many heroes";
        var entrance = EntranceOf(t);
        if (HeroAt(entrance) != null) return "the town entrance is occupied";
        int cost = HireCost(p, t);
        if (p.Purse[Purse.Gold] < cost) return "not enough gold";
        var h = HireHero(defId, p.Index, entrance, out bool returning);
        h.InTown = t.Object;
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        h.MovePoints = h.MaxMovePoints;
        if (!returning) S.Heroes.Add(h);
        S.HeroPool.Remove(defId);
        foreach (var other in S.Players) for (int i = 0; i < other.Tavern.Length; i++) if (other.Tavern[i] == defId) other.Tavern[i] = null;
        GiveOne(p, Purse.Gold, -cost);
        Emit(new HeroHired(h.Id, defId, t.Object));
        LearnFromGuild(h, t);
        RevealAround(h);
        return null;
    }

    /// <summary>
    /// The hero a tavern offer would become when hired (as <see cref="DoHire"/> builds it, not added to the game). Uses the
    /// game's random stream for the starting army: call it on a copy of the game (the AI's hire score does). A hero that
    /// fled or surrendered is that same hero (<see cref="HireHero"/>; it is revived in the copy).
    /// </summary>
    public HeroState TavernHeroPreview(string defId, int owner, Tile pos)
    {
        var h = HireHero(defId, owner, pos, out _);
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        h.MovePoints = h.MaxMovePoints;
        return h;
    }

    string? DoDismiss(DismissHeroCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || h.Owner != Current.Index) return "not your hero";
        if (S.Heroes.Count(x => x.Alive && x.Owner == h.Owner) == 1 && !S.Towns.Any(t => t.Owner == h.Owner))
            return "cannot dismiss your last hero";
        RemoveHero(h, "dismissed");
        return null;
    }

    void RemoveHero(HeroState h, string reason)
    {
        h.Alive = false;
        if (h.DefId.Length > 0 && !S.HeroPool.Contains(h.DefId)) S.HeroPool.Add(h.DefId);
        Emit(new HeroRemoved(h.Id, reason));
    }

    /// <summary>
    /// Blacksmith price (town vfunc 0xec = FUN_00cceae0, listed by FUN_00cd3f50 for ballista, first aid tent, ammo cart
    /// in that order): the machine's gold cost when it is native to the town (FUN_00ccea20: the town specialisation
    /// TOWN_BONUS_BALLISTA / _FIRST_AID_TENT / _AMMO_CART names it, or TownTypesInfo NativeWarMachine), else ×3.
    /// Only gold is charged (CBuyWarMachineCmd carries a gold amount, execute 004f4d10).
    /// </summary>
    public int WarMachinePrice(TownState t, string machine)
    {
        int gold = Core.WarMachines.TryGetValue(machine, out var wm) ? wm.Cost.Gold : 0;
        bool native = (machine, SpecializationBonus(t)) switch
        {
            ("WAR_MACHINE_BALLISTA", "TOWN_BONUS_BALLISTA") => true,
            ("WAR_MACHINE_AMMO_CART", "TOWN_BONUS_AMMO_CART") => true,
            ("WAR_MACHINE_FIRST_AID_TENT", "TOWN_BONUS_FIRST_AID_TENT") => true,
            _ => Db.NativeWarMachine.GetValueOrDefault(t.Type) == machine,
        };
        return native ? gold : gold * 3;
    }

    /// <summary>Blacksmith: buy a war machine the hero lacks for <see cref="WarMachinePrice"/>.</summary>
    string? DoBuyWarMachine(BuyWarMachineCmd c)
    {
        var t = S.Town(c.TownObject);
        var h = S.Hero(c.HeroId);
        var p = Current;
        if (t == null || t.Owner != p.Index || h == null || h.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
        if (t.Level("TB_BLACKSMITH") <= 0) return "no blacksmith";
        if (!Core.WarMachines.TryGetValue(c.Machine, out var wm)) return "unknown war machine";
        bool owned = c.Machine switch
        {
            "WAR_MACHINE_BALLISTA" => h.Ballista,
            "WAR_MACHINE_FIRST_AID_TENT" => h.FirstAidTent,
            "WAR_MACHINE_AMMO_CART" => h.AmmoCart,
            _ => true,
        };
        if (owned) return "already owned";
        var cost = new Resources(0, 0, 0, 0, 0, 0, WarMachinePrice(t, c.Machine));
        if (!p.Purse.CanAfford(cost)) return "not enough resources";
        Give(p, Purse.Scale(cost, -1));
        switch (c.Machine)
        {
            case "WAR_MACHINE_BALLISTA": h.Ballista = true; break;
            case "WAR_MACHINE_FIRST_AID_TENT": h.FirstAidTent = true; break;
            case "WAR_MACHINE_AMMO_CART": h.AmmoCart = true; break;
        }
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    /// <summary>Moves or splits creatures between two armies that are at the same place.</summary>
    string? DoTransfer(TransferStackCmd c)
    {
        Stack?[]? Army(int hero, int town, out Tile? where)
        {
            where = null;
            if (hero >= 0 && S.Hero(hero) is { } h && h.Owner == Current.Index) { where = h.Pos; return h.Army; }
            if (town >= 0 && S.Town(town) is { } t && t.Owner == Current.Index) { where = EntranceOf(t); return t.Garrison; }
            return BuildingArmy(town, out where);
        }
        var from = Army(c.FromHero, c.FromTown, out var wFrom);
        var to = Army(c.ToHero, c.ToTown, out var wTo);
        if (from == null || to == null) return "invalid armies";
        if (wFrom is { } a && wTo is { } b && Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) > 1) return "armies are not together";
        if (c.FromSlot is < 0 or >= State.Army.Slots || c.ToSlot is < 0 or >= State.Army.Slots) return "invalid slot";
        var src = from[c.FromSlot];
        if (c.Count == 0) return null;   // CSplitStackCmd / CTransferCreaturesCmd (00432160 / 0042ae70): moving 0 succeeds, no change
        if (src is not { Count: > 0 } || c.Count < 0 || c.Count > src.Count) return "invalid count";
        var dst = to[c.ToSlot];
        if (dst is { Count: > 0 } && dst.Creature != src.Creature) return "slot holds other creatures";
        // A hero must keep at least one stack.
        if (ReferenceEquals(from, to) == false && c.FromHero >= 0 && c.Count == src.Count && from.StackCount() == 1) return "a hero cannot be left without troops";
        if (dst is { Count: > 0 }) dst.Count += c.Count; else to[c.ToSlot] = new Stack(src.Creature, c.Count);
        // A whole stack takes its creature artifact along (FUN_00cdd180 / FUN_004bcad0); a split leaves it on the source.
        if (c.Count == src.Count) MergeMicroArtifact(src, to[c.ToSlot]!, S.Hero(c.FromHero), S.Hero(c.ToHero));
        src.Count -= c.Count;
        if (src.Count == 0) from[c.FromSlot] = null;
        Emit(new ArmyChanged(c.FromHero, c.FromTown));
        Emit(new ArmyChanged(c.ToHero, c.ToTown));
        return null;
    }
}
