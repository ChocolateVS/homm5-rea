using H5.Core.Data;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Caravans (TotE). H5_Game.exe 3.1:
//  * CAdvMapCaravan (vtables 00e96494, interface 00e96434 at +0x150), created by 00ce5b30 → 00ce5880: owner +0x174
//    (vf 0x24), destination tile +0x170 (vf 0x20), days left +0x178, stuck counter +0x198, army +0x164 (7 slots).
//  * CPurchaseCreaturesToCaravan 00428f20: +0xc world, +0x10 player, +0x14 destination town, +0x18 source,
//    +0x1c creature, +0x20 count. CMoveTownOrTownHeroCreaturesToCaravan 0042c630 (+0x1c slot 0..6),
//    CPurchaseAll / Multiple 0042cb30 / 0042cbf0 (→ 0042c910: one 00428f20 per plan entry), CDismissCaravanCmd
//    004291d0, CMeetCaravanCmd 0042f870 (→ CCreateCaravanMeetScreen 0043cdb0), CCreateCustomCaravanCmd 00429250.
//  * Daily walk 00ce4de0 (interface vf 0x28), its route 00ce49b0 (path mode 9), days 00ce4d40, the stuck merge
//    00ce3a20, the find 00ce4270 → 00ce4030, the start tiles 00ce5f40 → 00cec660.

public sealed record BuyToCaravanCmd(int Source, int DestTown, string Creature, int Count) : AdvCommand;
public sealed record MoveToCaravanCmd(int SourceTown, int DestTown, int Slot, int Count) : AdvCommand;
public sealed record BuyAllToCaravanCmd(int Source, int DestTown, int[]? Variants = null) : AdvCommand;
public sealed record BuyMultipleToCaravanCmd(int Source, int DestTown, List<ArmySlot> Creatures) : AdvCommand;
public sealed record DismissCaravanCmd(int CaravanId) : AdvCommand;

public sealed record CaravanCreated(int CaravanId, int Owner, Tile Pos) : AdvEvent;
public sealed record CaravanMoved(int CaravanId, IReadOnlyList<Tile> Path) : AdvEvent;
public sealed record CaravanChanged(int CaravanId) : AdvEvent;
public sealed record CaravanRemoved(int CaravanId, Tile Pos, string Reason) : AdvEvent;

public sealed partial class Adventure
{
    public const string CaravanShared = "MapObjects/Caravans/Caravan.xdb";

    /// <summary>
    /// A caravan's movement points per day, 00ce38c0 / 00ce6db0: BaseHeroMovement (RPGStats +0x5c) × 130 / 100 (2500
    /// without the stats).
    /// </summary>
    public int CaravanSpeed()
    {
        int b = Db.Stats.Root.Element("adventure").Int("BaseHeroMovement", 2500);
        return b * 0x82 / 100;
    }

    /// <summary>
    /// Days a caravan needs for a route of the given movement cost: (cost − 1) / speed + 1 (unsigned), as shown on the
    /// hire rows (00ce6110) and kept as the caravan's days left (00ce4d40, +0x178).
    /// </summary>
    public int CaravanDays(int cost) => (int)((uint)(cost - 1) / (uint)CaravanSpeed()) + 1;

    /// <summary>A place the destination town can hire from by caravan (00ce6110 entry: source object and days).</summary>
    public readonly record struct CaravanSource(int Object, bool IsTown, int Days);

    public CaravanState? CaravanAt(Tile t) => S.Caravans.Find(c => c.Pos == t);
    public CaravanState? Caravan(int id) => S.Caravans.Find(c => c.Id == id);

    static readonly Tile Nowhere = new(-1, -1, 0);

    /// <summary>
    /// The route of a caravan-like walker of <paramref name="owner"/> and its cumulative costs (index 0 = start, cost 0).
    /// Both caravan path modes (00c076d0 mode 8 for the sources 00ce6110, mode 9 for the walk 00ce49b0) start from
    /// 00ca9c50(race 0): no Pathfinding, no boots, no Rash, native ground TT_NONE (00b4c2a0(0)), so every ground penalty
    /// applies; then 00ce4880 / 00ce4960 set the owner (+0x18) and the day's movement (+0x28 = +0x30 = 00ce38c0). The
    /// expansion 00c07f80 prices each step with 00ca9a70 (mode 0 on land) on the step's destination tile, with "left" =
    /// settings +0x30 − the node's cost (param 5 from 00c08890), so the diagonal rule only acts within the first day;
    /// the node cost (+8) is the sum of the step costs. Our hero Dijkstra with a skill-less proxy hero whose movement
    /// points are the caravan speed computes exactly that. The modes' blocking flags (+0x43 .. +0x65: e.g. mode 9
    /// clears +0x41, so 009d19a0 blocks a step onto any object) are left to our hero passability (UNVERIFIED in detail).
    /// </summary>
    public (List<Tile> Path, List<int> Cost)? CaravanRoute(int owner, Tile from, Tile to)
    {
        if (to.X < 0) return null;
        int speed = CaravanSpeed();
        var proxy = new HeroState { Id = -1, Owner = owner, Pos = from, Alive = true, MovePoints = speed };
        var path = FindPath(proxy, to);
        if (path == null || path.Count == 0) return null;
        var cost = new List<int> { 0 };
        for (int i = 1; i < path.Count; i++) cost.Add(cost[i - 1] + StepCost(proxy, path[i - 1], path[i], speed - cost[i - 1]));
        return (path, cost);
    }

    /// <summary>
    /// The caravan sources of a town (00ce6110, used by the hire screen 006e23a0, EnterFort 0061a390, the script
    /// query 00598970 and the AI 0086a350): every map dwelling or town of the town's owner (dwelling: owner of the
    /// flagged part; town: vf 0xbc) other than the town itself (its entrance vf 0xc4 is not the flood's start) reached by
    /// the flood; days = <see cref="CaravanDays"/> of the node cost (+8). The exe floods FROM the town's entrance (path
    /// mode 8, 00c08be0(param 4 = the town's tile)) to every object node (00c07100), so the cost is that of the walk
    /// from the town to the source (each step priced on its destination tile, see <see cref="CaravanRoute"/>), not of
    /// the caravan's way back. GAP-detail: the flood's army estimator (00c07ad0(00c27740), strength of no army) and the
    /// holder replacement through vf 0x2c → vf 0x1c are not modelled.
    /// </summary>
    public List<CaravanSource> CaravanSources(TownState dest)
    {
        var list = new List<CaravanSource>();
        if (dest.Owner < 0) return list;
        var target = EntranceOf(dest);
        int? Days(Tile at) => CaravanRoute(dest.Owner, target, at) is { } r ? CaravanDays(r.Cost[^1]) : null;
        foreach (var t in S.Towns)
        {
            if (t == dest || t.Owner != dest.Owner) continue;
            if (Days(EntranceOf(t)) is { } d) list.Add(new CaravanSource(t.Object, true, d));
        }
        foreach (var (idx, os) in S.Objects)
        {
            if (os.Removed || os.Owner != dest.Owner) continue;
            var o = O(idx);
            if (o.Kind != ObjectKind.Dwelling) continue;
            if (Days(o.ActiveTiles.FirstOrDefault(o.Pos)) is { } d) list.Add(new CaravanSource(idx, false, d));
        }
        return list;
    }

    /// <summary>
    /// EnterFort's caravan alternative (0061a390): the fort (hire screen) opens when the town has any dwelling built
    /// (building interface vf 0x34 over the dwelling types 7..13), or when a caravan of the town's owner (world vf 0x90
    /// list, caravan vf 0x24 = player vf 0x2c) is heading to the town (caravan vf 0x20 = the town's tile, vf 0xc4), or
    /// when a caravan source of the town (00ce6110) has creatures to hire (its list 00ce43a0 has a non-empty entry;
    /// taken as a pool above zero, UNVERIFIED).
    /// </summary>
    public bool CanEnterFort(TownState t)
    {
        for (int tier = 1; tier <= 7; tier++) if (t.Level($"TB_DWELLING_{tier}") > 0) return true;
        var at = EntranceOf(t);
        if (S.Caravans.Any(c => c.Owner == t.Owner && c.Dest == at)) return true;
        foreach (var s in CaravanSources(t))
        {
            int[] avail = s.IsTown ? S.Towns.First(x => x.Object == s.Object).Available : S.Objects[s.Object].Available;
            if (avail.Any(n => n > 0)) return true;
        }
        return false;
    }

    /// <summary>The entrance tiles of a caravan source (00ce4180: position + the object's entrance offsets, vf 0xb8).</summary>
    public List<Tile> CaravanSourceTiles(int source)
    {
        if (S.Town(source) is { } t) { var l = O(t.Object).ActiveTiles.ToList(); return l.Count > 0 ? l : new List<Tile> { t.Pos }; }
        var o = O(source);
        var ol = o.ActiveTiles.ToList(); return ol.Count > 0 ? ol : new List<Tile> { o.Pos };
    }

    // 00cec660 neighbour order (DAT_00f7ce34 dx / DAT_00f7ce54 dy; 8 directions for land, the first 4 for water).
    static readonly (int dx, int dy)[] CaravanStartDirs = { (0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1) };

    /// <summary>
    /// Where a new caravan may stand (00ce5f40 → 00cec660(…, land)): for each entrance tile of the source, its eight
    /// neighbours in the order above, without repeats, that hold no object, hero, ship or caravan (00cea350 / 00cea010 /
    /// 00ceab70) and whose passability is land (00ce9ed0: cell +8 is 1 or 4; taken as not blocked and not water).
    /// </summary>
    public List<Tile> CaravanStartTiles(int source)
    {
        var list = new List<Tile>();
        foreach (var e in CaravanSourceTiles(source))
            foreach (var (dx, dy) in CaravanStartDirs)
            {
                var n = e.Offset(dx, dy);
                if (list.Contains(n)) continue;
                var f = Map.Floor(n.Floor);
                if (f == null || !f.InBounds(n.X, n.Y) || f.IsBlocked(n.X, n.Y) || f.IsWater(n.X, n.Y)) continue;
                if (HeroAt(n) != null || CaravanAt(n) != null || BoatAt(n) != null || AnyObjectAt(n)) continue;
                list.Add(n);
            }
        return list;
    }

    /// <summary>
    /// The caravan a purchase joins (00ce4270 → 00ce4030 per entrance tile of the source): a caravan of the player
    /// within one tile of it whose destination is the town's tile.
    /// </summary>
    public CaravanState? CaravanAtSource(int owner, int source, Tile dest)
    {
        foreach (var e in CaravanSourceTiles(source))
            foreach (var c in S.Caravans)
                if (c.Owner == owner && c.Dest == dest && c.Pos.Floor == e.Floor && Math.Max(Math.Abs(c.Pos.X - e.X), Math.Abs(c.Pos.Y - e.Y)) <= 1)
                    return c;
        return null;
    }

    /// <summary>0045b240: the army holds that creature already (0045b0e0) or has a free slot.</summary>
    static bool ArmyTakes(Stack?[] a, string creature) =>
        a.Any(s => s is { Count: > 0 } && s.Creature == creature) || a.Any(s => s is not { Count: > 0 });

    /// <summary>0045add0(dest, src): every stack of src matches a creature type of dest or takes one of its free slots.</summary>
    static bool ArmyFits(Stack?[] dest, Stack?[] src)
    {
        int free = dest.Count(s => s is not { Count: > 0 });
        var types = dest.Where(s => s is { Count: > 0 }).Select(s => s!.Creature).ToHashSet();
        foreach (var s in src.Live())
        {
            if (types.Contains(s.Creature)) continue;
            if (free-- <= 0) return false;
            types.Add(s.Creature);
        }
        return true;
    }

    /// <summary>The pool a caravan source hires a creature from (source vf 0xc(creature)): town tier pool or dwelling row.</summary>
    int[]? CaravanPool(int source, string creature, out int slot)
    {
        slot = -1;
        if (S.Town(source) is { } t)
        {
            for (int tier = 1; tier <= 7; tier++)
                for (int v = 0; v < 3; v++)
                    if (DwellingCreature(t, tier, v) == creature) { slot = tier - 1; return t.Available; }
            return null;
        }
        if (!S.Objects.TryGetValue(source, out var os) || O(source).Kind != ObjectKind.Dwelling) return null;
        var list = O(source).Shared.DwellingCreatures;
        foreach (int row in DwellingRows(source))
            if (row < list.Count && list[row] == creature && row < os.Available.Length) { slot = row; return os.Available; }
        return null;
    }

    int CaravanSourceOwner(int source) =>
        S.Town(source) is { } t ? t.Owner
        : S.Objects.TryGetValue(source, out var os) && O(source).Kind == ObjectKind.Dwelling ? os.Owner : -1;

    /// <summary>00ce5b30 → 00ce5880: a new caravan of the player at the tile, heading to the destination; days by 00ce4d40.</summary>
    CaravanState NewCaravan(int owner, Tile pos, Tile dest, int destTown, int source, string name = "")
    {
        var c = new CaravanState { Id = S.NextCaravanId++, Owner = owner, Pos = pos, Dest = dest, DestTown = destTown, Source = source, Name = name };
        S.Caravans.Add(c);
        UpdateCaravanDays(c);
        Emit(new CaravanCreated(c.Id, owner, pos));
        return c;
    }

    /// <summary>00ce4d40: days left from the route's last cost, or −1 and one more stuck day when there is no route.</summary>
    void UpdateCaravanDays(CaravanState c)
    {
        if (CaravanRoute(c.Owner, c.Pos, c.Dest) is { Path.Count: > 1 } r) c.DaysLeft = CaravanDays(r.Cost[^1]);
        else { c.Stuck++; c.DaysLeft = -1; }
    }

    // 009a5870 (IAdvMapDwelling vf 0, the part at +0xf8 returned by the object's vf 0x18 = 009a80d0): true for these
    // SAdvMapDwellingShared Type values (+0xec, enum 00ad0650: 0x40 0x49 0x4c 0x53 0x55 0x58 0x6f 0x7b).
    static readonly HashSet<string> FreeCaravanDwellingTypes = new()
    {
        "BUILDING_PEASANT_HUT", "BUILDING_IMP_CRUCIBLE", "BUILDING_GRAVEYARD", "BUILDING_FAIRIE_TREE",
        "BUILDING_WORKSHOP", "BUILDING_BATTLE_ACADEMY", "BUILDING_FORTRESS_DEFENDERS", "BUILDING_STRONGHOLD_GOBLINS",
    };

    /// <summary>
    /// The payment skip of 00428f20 ([ESP+0x13], 00428fb1 / 0042903c): 0 for a town source; for a dwelling the result
    /// of its IAdvMapDwelling vf 0 (009a5870): the shared Type is one of the eight first-level dwellings above.
    /// </summary>
    public bool CaravanPurchaseFree(int source) =>
        S.Town(source) == null && source >= 0 && source < Map.Objects.Count && O(source).Kind == ObjectKind.Dwelling
        && FreeCaravanDwellingTypes.Contains(O(source).Shared.Type);

    /// <summary>
    /// CPurchaseCreaturesToCaravan (00428f20): the source (a town of the buyer, vf 0x3c → vf 0xbc, or a dwelling whose
    /// flagged owner is the buyer) must offer the creature; the count is clamped to the pool (vf 0xc); 00ce64a0 finds a
    /// caravan from the source to the town: found and taking the creature (0045b240) → join it; found and full → fail
    /// (cant_but_caravan_not_fit); none → a start tile must be free (00ce5f40, cant_buy_no_place_for_caravan) and a new
    /// caravan is made there (00ce5b30). Cost × count is paid (player vf 0xe0, refused when short) unless
    /// <see cref="CaravanPurchaseFree"/>, the pool loses the count (vf 8) and the creatures join the caravan (00ce4450).
    /// </summary>
    string? DoBuyToCaravan(BuyToCaravanCmd c)
    {
        var p = Current;
        if (S.Town(c.DestTown) is not { } dest) return "no such town";
        if (c.Count < 1 || string.IsNullOrEmpty(c.Creature)) return "invalid count";
        if (CaravanSourceOwner(c.Source) != p.Index) return "not your source";
        if (!Core.Creatures.TryGetValue(c.Creature, out var cd)) return "creature not available";
        var pool = CaravanPool(c.Source, c.Creature, out int slot);
        if (pool == null || pool[slot] <= 0) return "creature not available";
        int n = Math.Min(c.Count, pool[slot]);
        var to = EntranceOf(dest);
        var car = CaravanAtSource(p.Index, c.Source, to);
        List<Tile>? start = null;
        if (car != null) { if (!ArmyTakes(car.Army, c.Creature)) return "caravan is full"; }
        else if ((start = CaravanStartTiles(c.Source)).Count == 0) return "nowhere to place caravan";
        if (!CaravanPurchaseFree(c.Source))
        {
            var cost = Purse.Scale(cd.Cost, n);
            if (!p.Purse.CanAfford(cost)) return "not enough resources";
            Give(p, Purse.Scale(cost, -1));
        }
        car ??= NewCaravan(p.Index, start![0], to, dest.Object, c.Source);
        pool[slot] -= n;
        car.Army.TryAdd(c.Creature, n);
        Emit(new CreaturesRecruited(c.Source, c.Creature, n));
        RumourCreaturesBought(p.Index, c.Creature);
        Emit(new CaravanChanged(car.Id));
        return null;
    }

    /// <summary>
    /// CMoveTownOrTownHeroCreaturesToCaravan (0042c630): slot 0..6 of the source town's army (town vf 0x18(1) hero's
    /// army vf 0x48, else the town army vf 0x2c) goes whole to a NEW caravan from that town to the destination: 00ce64a0
    /// (flag 0) returning 0 — a caravan of the player beside the town heading there that takes the creature — fails
    /// the command (0042c7b8), as does 2 (that caravan is full); only 1 (none) goes on: a free start tile (00ce5f40)
    /// and 00ce5b30. The stack is then removed (army vf 0x14 = 00c66e40) and the army's vf 0 asked; CArmy (vtable
    /// 00e8f43c, the only one with that vf 0x14) answers 00412bd0 = true, so the stack always goes (00ce4450) — the
    /// army may be left empty; the put-back branch (vf 0x10) is unreachable.
    /// </summary>
    string? DoMoveToCaravan(MoveToCaravanCmd c)
    {
        var p = Current;
        if (S.Town(c.DestTown) is not { } dest || S.Town(c.SourceTown) is not { } src) return "no such town";
        if (src.Owner != p.Index) return "not your town";
        if (c.Slot is < 0 or > 6 || c.Count < 1) return "invalid slot";
        var hero = GarrisonHeroOf(src);
        var army = TownRecruitArmy(src);
        if (army[c.Slot] is not { Count: > 0 } stack) return "no creatures in that slot";
        var to = EntranceOf(dest);
        if (CaravanAtSource(p.Index, src.Object, to) != null) return "a caravan is already there";
        var start = CaravanStartTiles(src.Object);
        if (start.Count == 0) return "nowhere to place caravan";
        var car = NewCaravan(p.Index, start[0], to, dest.Object, src.Object);
        army[c.Slot] = null;
        car.Army.TryAdd(stack.Creature, stack.Count);
        Emit(new ArmyChanged(hero?.Id ?? -1, hero == null ? src.Object : -1));
        Emit(new CaravanChanged(car.Id));
        return null;
    }

    /// <summary>
    /// CPurchaseAllCreaturesToCaravan (0042cb30): the source's hire list (00ce43a0, vf 0) planned by FUN_009a52a0 with
    /// the screen's selection and no army (free slots not limited: an empty army is used, UNVERIFIED), then 0042c910
    /// runs one CPurchaseCreaturesToCaravan per entry; succeeds when any did.
    /// </summary>
    string? DoBuyAllToCaravan(BuyAllToCaravanCmd c)
    {
        var p = Current;
        List<ArmySlot> plan;
        if (S.Town(c.Source) is { } t)
            plan = HireAllPlan(t, p.Purse, State.Army.Empty(), c.Variants).Select(x => new ArmySlot(x.Creature, x.Count)).ToList();
        else if (S.Objects.ContainsKey(c.Source) && O(c.Source).Kind == ObjectKind.Dwelling)
            plan = DwellingHireAllPlan(c.Source, p.Purse, State.Army.Empty()).Select(x => new ArmySlot(x.Creature, x.Count)).ToList();
        else return "no such town or dwelling";
        return BuyEachToCaravan(c.Source, c.DestTown, plan);
    }

    /// <summary>CPurchaseMultipleCreaturesToCaravan (0042cbf0 → 0042c910): one purchase per (creature, count).</summary>
    string? DoBuyMultipleToCaravan(BuyMultipleToCaravanCmd c) => BuyEachToCaravan(c.Source, c.DestTown, c.Creatures);

    string? BuyEachToCaravan(int source, int destTown, List<ArmySlot> items)
    {
        bool any = false;
        string? err = "nothing to hire";
        foreach (var it in items)
        {
            var e = DoBuyToCaravan(new BuyToCaravanCmd(source, destTown, it.Creature, it.Count) { Player = Current.Index });
            if (e == null) any = true; else err = e;
        }
        return any ? null : err;
    }

    /// <summary>CDismissCaravanCmd (004291d0): a caravan in the world's list is removed (world vf 0xec). The exe does not
    /// check the owner; the network command does (only the owner may dismiss).</summary>
    string? DoDismissCaravan(DismissCaravanCmd c)
    {
        if (Caravan(c.CaravanId) is not { } car) return "no such caravan";
        if (car.Owner != Current.Index) return "not your caravan";
        RemoveCaravan(car, "dismissed");
        return null;
    }

    void RemoveCaravan(CaravanState c, string reason)
    {
        S.Caravans.Remove(c);
        Emit(new CaravanRemoved(c.Id, c.Pos, reason));
    }

    /// <summary>CCreateCustomCaravanCmd (00429250, script CreateCaravan 005e32c0): a named caravan of the player at a tile.</summary>
    public CaravanState? CreateScriptCaravan(string name, int owner, Tile at, Tile dest)
    {
        if (S.Caravans.Any(c => c.Name == name) || CaravanAt(at) != null) return null;
        return NewCaravan(owner, at, dest, TownAt(dest)?.Object ?? -1, -1, name);
    }

    /// <summary>
    /// The caravans' walk when the turn is handed over (00998060 GiveTurnToNextPlayers → 00995f40, after the new-day
    /// work): with one player getting the turn only that player's caravans (owner vf 0x24 = player vf 0x2c), else all
    /// (null). Passes over the world's caravan list (+0x174): a caravan not yet "moved" walks (interface vf 0x28 =
    /// 00ce4de0); one that did not stop (vf 0x38: days ≠ −1) is moved and skipped by later passes; then an empty one
    /// (vf 0x2c = 00ce3de0) is removed (world vf 0xec) and the pass counter restarts. The walker returns after the
    /// second pass in a row without a removal. UNVERIFIED-detail: world vf 0xec is taken to erase the entry at once
    /// (the pass then skips the next caravan, as the exe's index loop would).
    /// </summary>
    void CaravansTurn(int? player)
    {
        var moved = new HashSet<CaravanState>();
        int passes = 0;
        while (true)
        {
            for (int i = 0; i < S.Caravans.Count; i++)
            {
                var c = S.Caravans[i];
                if (player is { } pl && c.Owner != pl) continue;
                if (moved.Contains(c)) continue;
                CaravanWalk(c);
                if (!c.Stopped) moved.Add(c);
                if (!c.Army.Live().Any())
                {
                    RemoveCaravan(c, c.Dest.X < 0 ? "arrived" : "empty");
                    passes = 0;
                }
            }
            if (++passes > 1) return;
        }
    }

    /// <summary>
    /// 00ce4de0: an empty caravan (vf 0x2c) does nothing. The route (00ce49b0, path mode 9 from the caravan to its
    /// destination) is walked up to the last node whose cost fits the day's speed (vf 0x30), stopping two nodes before
    /// an occupied node that is not the destination (00ce3c20; occupied = a hero or another caravan, UNVERIFIED). When the
    /// reached node is the destination: the town's owner differs from the caravan's → the route is cut to max(i − 3, 0)
    /// nodes and the caravan stops; same owner → the caravan's army goes into the town hero's army, else the town's
    /// (0045add0 fits → 0045a2c0), the destination is cleared and the walk ends one node short; not fitting → the route is
    /// cut to max(i − 1, 0) nodes and the caravan stops. Moving resets the stuck counter (+0x198); a stopped caravan
    /// gets days −1, one more stuck day and the merge of 00ce3a20; else the days are recomputed (00ce4d40). No route:
    /// days −1, stuck + 1, 00ce3a20.
    /// </summary>
    void CaravanWalk(CaravanState c)
    {
        if (!c.Army.Live().Any()) return;
        if (CaravanRoute(c.Owner, c.Pos, c.Dest) is not { } r || r.Path.Count < 2) { StopCaravan(c); return; }
        var (path, cost) = r;
        int speed = CaravanSpeed(), n = path.Count, idx = Math.Max(n - 1, 0);
        for (int i = 0; i < n; i++)
        {
            if (cost[i] > speed) { idx = Math.Max(i - 1, 0); break; }
            if (i > 0 && path[i] != c.Dest && CaravanNodeBlocked(c, path[i])) { idx = Math.Max(i - 2, 0); break; }
        }
        bool stop = false;
        if (path[idx] == c.Pos) { StopCaravan(c); return; }
        if (path[idx] == c.Dest && TownAt(c.Dest) is { } town)
        {
            if (town.Owner != c.Owner)
            {
                int keep = Math.Max(idx - 3, 0);
                if (keep == 0 || path[keep - 1] == c.Pos) { StopCaravan(c); return; }
                idx = keep - 1; stop = true;
            }
            else
            {
                var hero = GarrisonHeroOf(town);
                var army = TownRecruitArmy(town);
                if (ArmyFits(army, c.Army))
                {
                    foreach (var s in c.Army.Live()) army.TryAdd(s.Creature, s.Count);
                    Array.Fill(c.Army, null);
                    c.Dest = Nowhere;
                    idx = Math.Max(idx - 1, 0);
                    Emit(new ArmyChanged(hero?.Id ?? -1, hero == null ? town.Object : -1));
                }
                else if (n > 1)
                {
                    int keep = Math.Max(idx - 1, 0);
                    if (keep == 0 || path[keep - 1] == c.Pos) { StopCaravan(c); return; }
                    idx = keep - 1; stop = true;
                }
            }
        }
        if (idx > 0)
        {
            c.Stuck = 0;
            c.Pos = path[idx];
            Emit(new CaravanMoved(c.Id, path.Take(idx + 1).ToList()));
        }
        // The caravan that delivered its army is removed by the walker (00995f40: vf 0x2c → world vf 0xec).
        if (stop) StopCaravan(c); else UpdateCaravanDays(c);
    }

    /// <summary>
    /// 00ce3c20 (ECX = the caravan, arg = the node): no world → occupied; else 00cead80 looks for an object (00cea350),
    /// a hero (00cea010), then a caravan (00ceab70) at the node for the owner: none (or a removed one) → free; one
    /// whose vf 0x50 part passes 00ce3960 for the caravan's owner → free; otherwise occupied. GAP-detail: 00ce3960
    /// (the vf 0x50 part's vf 4 / vf 0x54 / vf 0x20 tests) is not decoded: every hero, ship, other caravan or object
    /// counts as occupying (the caravan itself does not: the walk's node 0 is its own tile).
    /// </summary>
    bool CaravanNodeBlocked(CaravanState c, Tile t) =>
        HeroAt(t) != null || (CaravanAt(t) is { } o && o != c) || BoatAt(t) != null || AnyObjectAt(t);

    // 00ce3a20 neighbour order (DAT_00f757c8).
    static readonly (int dx, int dy)[] CaravanMergeDirs = { (0, -1), (0, 1), (-1, 0), (1, 0), (1, -1), (1, 1), (-1, -1), (-1, 1) };

    /// <summary>
    /// 00ce4420: days −1, one more stuck day, then 00ce3a20: each neighbouring caravan (order above) of the same owner
    /// heading to the same tile whose stuck counter (interface vf 0x44 = 00ce6790: +0x48 of the interface at +0x150 =
    /// +0x198) is at most this one's — and not equal unless this one is stuck for more than a day — gives this caravan
    /// every stack it can take (vf 0xc → vf 0x10). An emptied neighbour stays until the walker's empty test removes it.
    /// </summary>
    void StopCaravan(CaravanState c)
    {
        c.DaysLeft = -1;
        c.Stuck++;
        foreach (var (dx, dy) in CaravanMergeDirs)
        {
            if (CaravanAt(c.Pos.Offset(dx, dy)) is not { } o || o == c || o.Owner != c.Owner || o.Dest != c.Dest) continue;
            if (!(o.Stuck <= c.Stuck && (o.Stuck != c.Stuck || c.Stuck > 1))) continue;
            for (int i = 0; i < o.Army.Length; i++)
                if (o.Army[i] is { Count: > 0 } s && c.Army.TryAdd(s.Creature, s.Count)) o.Army[i] = null;
            Emit(new CaravanChanged(o.Id));
            Emit(new CaravanChanged(c.Id));
        }
    }

    /// <summary>
    /// A hero steps onto a caravan: its owner meets it (CMeetCaravanCmd 0042f870: caravan vf 0x2c owner = the player →
    /// the exchange screen 0043cdb0, UI/Garrison ScreenCaravanDesc); an enemy attacks it (00433440 → the
    /// CCombatHeroVsCaravanArenaAndOpponentsLoader 00436570, the caravan's army defends); an ally's caravan blocks.
    /// </summary>
    string? MeetCaravan(HeroState h, CaravanState c)
    {
        if (c.Owner == h.Owner)
        {
            Emit(new ObjectVisited(h.Id, -1, $"caravan:{c.Id}"));
            return null;
        }
        if (Allied(h.Owner, c.Owner)) return null;
        StartBattle(h, -1, -1, c.Pos, c.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList(), siege: false);
        S.Battle!.DefenderCaravan = c.Id;
        S.Battle.DefenderCaravanOwner = c.Owner;
        return null;
    }

    /// <summary>
    /// After a battle against a caravan: its survivors stay in it. The combat finish 00af6680 removes the losing
    /// side's caravan (side vf 0x24 → world vf 0xec; the attacking side's only when its army count 0045a320 is &lt; 1), so a
    /// caravan left without creatures leaves the map.
    /// </summary>
    void ResolveCaravanBattle(PendingBattle pb, List<ArmySlot> defenderSurvivors)
    {
        if (Caravan(pb.DefenderCaravan) is not { } c) return;
        Array.Fill(c.Army, null);
        for (int i = 0; i < defenderSurvivors.Count && i < c.Army.Length; i++)
            if (defenderSurvivors[i].Count > 0) c.Army[i] = new Stack(defenderSurvivors[i].Creature, defenderSurvivors[i].Count);
        if (!c.Army.Live().Any()) RemoveCaravan(c, "defeated");
        else Emit(new CaravanChanged(c.Id));
    }
}
