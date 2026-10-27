using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Combat.AI;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

/// <summary>
/// Strategic adventure AI (NWorld::CPlayerAI) driven by the stock AI/Adventure tunables.
///
/// Exe-confirmed parts (docs/rules/AI.md):
///   • hero spread weight = RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel) (FUN_00868a40, FUN_004e9a80);
///   • hero attribute value Σ attr·ValueOfAttribute* (FUN_004e9f60), artifact value by class (FUN_004eb840);
///   • enemy danger reach = move points · SearchDangerEnemyMobilityMultiplier + SearchDangerEnemyMobilityBonus (FUN_00872330);
///   • all thresholds/limits per difficulty come from the data rows the exe reads.
/// Reconstructed parts (structure from the exe's log strings and call graph, weights from data, exact formulas
/// not yet traced): destination scoring, town build/recruit priorities, threat/garrison handling.
///
/// Determinism: random choices use a private generator seeded from the game seed, day, player and hero, so the
/// game's own RNG stream is never consumed and every peer reproduces the same decisions.
/// </summary>
public sealed partial class OriginalAdventureAI
{
    /// <summary>Routes commands through the game's command sink (e.g. the map-script host) when one is set.</summary>
    static AdvResult Send(Engine.Adventure g, AdvCommand c) => g.CommandSink?.Invoke(c) ?? g.Apply(c);

    public int MaxActionsPerTurn { get; init; } = 80;
    /// <summary>Combat AI used to quick-combat the AI's battles.</summary>
    public IBattleAI BattleAI { get; init; } = new OriginalBattleAI();

    AdventureAiStats? _stats;
    readonly Dictionary<int, Tile> _lastTarget = new();

    public void PlayTurn(Engine.Adventure g, ulong battleSeedBase)
    {
        _stats ??= AdventureAiStats.Load(g.Core);
        var st = _stats;
        var me = g.Current.Index;
        var diff = st.Difficulty[Math.Clamp(g.S.Difficulty, 0, 3)];
        int actions = 0;

        ResolvePending(g, battleSeedBase);
        var towns = g.S.Towns.Where(t => t.Owner == me).ToList();
        // Phase 0 (FUN_0086d750): the AI money values (00868bb0, before the puzzle map and threats), then the threat table.
        SetMoneyValues(g, st, me);
        CheckThreats(g, st, diff, me);

        // Phase order of the exe's state machine (CPlayerAI +0x128, dispatcher at 0x0086dea0): day start and
        // threats (0, FUN_0086d750) → roles (12, FUN_00869ad0) → heroes one by one (1..5, FUN_0086b400 …) →
        // hiring (7, FUN_0086b0a0; after a hire back to 12, so the new hero acts the same day) → towns (6,
        // FUN_0086d4e0; UNVERIFIED: build/recruit are taken to live there, FUN_009c5c60) → 13 → 14 → 8 → end
        // turn (9, FUN_00868eb0).
        //
        // Hero order (exe FUN_00869770, called from the move phase FUN_0086b400): the next hero to act is the
        // not-yet-finished one with the LOWEST attack + defence + spell power + knowledge (hero vf 0x10..0x1c;
        // strict <, so the earlier hero in the player's list wins ties). A finished hero is never picked again.
        var all = g.S.Heroes.Where(h => h.Alive && h.Owner == me).ToList();
        var roles = AssignRoles(g, all, st, diff);
        var claimed = new HashSet<int>();
        var done = new HashSet<int>();
        MoveHeroes(g, battleSeedBase, me, diff, st, roles, claimed, done, ref actions);
        if (HireHeroes(g, towns, st, diff))
        {
            // After a hire the exe goes back to the role phase 12 (0x0086b39a), so the new hero gets a role too.
            roles = AssignRoles(g, g.S.Heroes.Where(h => h.Alive && h.Owner == me).ToList(), st, diff);
            MoveHeroes(g, battleSeedBase, me, diff, st, roles, claimed, done, ref actions);
        }
        // Phase 6 (FUN_0086d4e0): threatened own towns without a garrison hero buy into the garrison, money ignored
        // (CAIPurchaseCreaturesInTown, queued before the build commands); then the building pass.
        foreach (var t in towns)
            if (Threats(me).T.ContainsKey(t.Object) && TownHeroes(g, t).Garrison == null) Purchase(g, st, t.Object, null, ignoreMoney: true);
        BuildInTowns(g, me, diff, ThreatenedTownsOf(Threats(me)));
        ResolvePending(g, battleSeedBase + 999);
        foreach (var h in g.S.Heroes.Where(h => h.Alive && h.Owner == me)) LevelUps(g, h, st);
        if (!g.S.Finished) Send(g, new EndTurnCmd { Player = me });
    }

    void MoveHeroes(Engine.Adventure g, ulong battleSeedBase, int me, AdventureDifficulty diff, AdventureAiStats st,
                    Dictionary<int, HeroRole> roles, HashSet<int> claimed, HashSet<int> done, ref int actions)
    {
        while (actions < MaxActionsPerTurn && !g.S.Finished)
        {
            var h = NextHero(g, me, done) ?? HeroOutOfGarrison(g, me, done);
            if (h == null) break;
            done.Add(h.Id);
            if (!roles.ContainsKey(h.Id)) roles[h.Id] = _role.GetValueOrDefault(h.Id, HeroRole.Freelancer);
            for (int step = 0; step < 8 && actions < MaxActionsPerTurn && h.Alive && !g.S.Finished; step++)
            {
                ResolvePending(g, battleSeedBase + (ulong)actions);
                LevelUps(g, h, st);
                var target = ChooseDestination(g, h, roles, diff, st, claimed);
                if (target == null) break;
                var mate = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == me && x.Id != h.Id && !x.Garrisoned && x.Pos == target.Value.Tile);
                var (r, whole) = StepTowards(g, h, target.Value, me);
                actions++;
                if (r is not { Ok: true }) break;
                if (whole) claimed.Add(target.Value.Object);
                _lastTarget[h.Id] = target.Value.Tile;
                _lastCost[h.Id] = target.Value.Cost;
                ResolvePending(g, battleSeedBase + (ulong)actions);
                LevelUps(g, h, st);
                // Visiting an own hero (004e6d30 → FUN_00c5ad90): the army exchange by roles and the role update.
                if (mate is { Alive: true } && h.Alive && Math.Max(Math.Abs(mate.Pos.X - h.Pos.X), Math.Abs(mate.Pos.Y - h.Pos.Y)) <= 1)
                {
                    HeroesMeet(g, st, h, mate);
                    foreach (var x in new[] { h, mate }) if (_role.TryGetValue(x.Id, out var nr)) roles[x.Id] = nr;
                }
                // BlockTownGarrisonForAI: no creature exchange with that garrison (exe reader 0x008687a0).
                if (h.InTown >= 0 && g.S.Town(h.InTown) is { } home && home.Owner == me && !home.AiGarrisonBlocked) TakeGarrison(g, h, home);
                // A hero in an own town buys creatures (009c4710 step 6), at an own external dwelling too (004e6d30 case 5).
                if (h.Alive && h.InTown >= 0 && g.S.Town(h.InTown) is { } shop && shop.Owner == me)
                {
                    Purchase(g, st, shop.Object, h, false);
                    BuyTalismanLevel(g, h, shop);
                    GarrisonHeroInTown(g, st, diff, h, shop);
                    if (h.Garrisoned) break;
                }
                else if (h.Alive && g.Map.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Dwelling && o.ActiveTiles.Contains(h.Pos)) is { } dw
                         && g.S.Objects.TryGetValue(dw.Index, out var dws) && dws.Owner == me) Purchase(g, st, dw.Index, h, false);
                if (h.MovePoints < 100) break;
            }
            if (h.Alive && !g.S.Finished) FinishHero(g, h, roles[h.Id]);
        }
    }

    /// <summary>
    /// With no hero left on the map (exe FUN_00869770 tail): the first of the player's towns, in town order, whose
    /// garrison hero (town vf 0x18(1)) is AI-enabled (vf 0x2ac), not finished, has move points (vf 0xf4 &gt; 0) and at
    /// least one stack (FUN_0045a320) sends it out of the garrison (FUN_00435cc0 with "into garrison" false; executed
    /// by 004f58e0 as the town's hero swap) and makes it the current hero.
    /// GAP-detail: our town order is the map's town list.
    /// </summary>
    public static HeroState? HeroOutOfGarrison(Engine.Adventure g, int me, HashSet<int> done)
    {
        foreach (var t in g.S.Towns.Where(t => t.Owner == me))
        {
            var h = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == me && x.Garrisoned && x.InTown == t.Object);
            if (h == null || !h.AiEnabled || done.Contains(h.Id) || h.MovePoints <= 0 || h.Army.StackCount() == 0) continue;
            if (Send(g, new GarrisonHeroCmd(t.Object, h.Id, false) { Player = me }).Ok) return h;
        }
        return null;
    }

    /// <summary>
    /// A hero ends its turn (exe FUN_00868640 after the search gave up, 0086c010): a collector / supplier hands its
    /// partner logic a command instead (FUN_0086b5e0 returns true, no garrison move); otherwise an AI-enabled hero
    /// standing in its own town whose garrison the scripts did not block (town vf 0x114, BlockTownGarrisonForAI) is
    /// put into the garrison (FUN_00435cc0 with "into garrison" true).
    /// A hero already in the garrison comes out as the visitor (the command swaps, 004f58e0).
    /// GAP-details: our turn ends a hero on our own conditions (no destination, a failed move, under
    /// 100 move points, 8 steps), not only when the search is exhausted; the role-2/3 command 0086fc40 is not ported.
    /// </summary>
    public static void FinishHero(Engine.Adventure g, HeroState h, HeroRole role)
    {
        if (role is HeroRole.Collector or HeroRole.Supplier) return;
        if (!h.AiEnabled || h.Garrisoned || h.InTown < 0 || g.S.Town(h.InTown) is not { } t || t.Owner != h.Owner || t.AiGarrisonBlocked) return;
        Send(g, new GarrisonHeroCmd(t.Object, h.Id, true) { Player = h.Owner });
    }

    /// <summary>
    /// 009c4710 after the purchase (009c4aae..009c4bfe): in an own town with ability 0x2b (Walker's Hut, town vf 0x44)
    /// a Barbarian (hero vf 0x258 = 8) takes next = talisman level (vf 0x300) + 1; only when next &lt; the town maximum
    /// (vf 0x11c, strictly: the last level is never bought here) the quote (vf 0x118, next..next) must be 0, the purse
    /// (player vf 0xd8) and the AI supply (player vf 0x28 → CPlayerAIInformation vf 4 = 00a367b0, the set at +0x24
    /// stored by CSetPlayerAIDemandInfoCmd, our <c>AiSupply</c>, zero before the first AI day) must each cover the cost
    /// (009d0720); then pay (vf 0xe0), learn the quote's available spells (hero vf 0xe8) and set the level (vf 0x304).
    /// Sent as <see cref="UpgradeTalismanCmd"/> (00429730: same checks, pays, sets the level, then vf 0x384).
    /// UNVERIFIED-detail: the command's vf 0x384 re-grants every level's spells, the AI path only learns the new level's.
    /// </summary>
    public static void BuyTalismanLevel(Engine.Adventure g, HeroState h, TownState t)
    {
        if (!h.Alive || h.InTown != t.Object || t.Owner != h.Owner || g.Player(h.Owner) is not { } p) return;
        if (t.Type != "TOWN_STRONGHOLD" || t.Level(Engine.Adventure.WalkersHut) < 1 || h.Class != "HERO_CLASS_BARBARIAN") return;
        int next = h.TalismanLevel + 1;
        if (next >= g.TalismanMaxLevel) return;
        var q = g.TalismanQuoteFor(t, h, next, next);
        if (q.Status != TalismanStatus.Ok || !p.Purse.CanAfford(q.Cost)) return;
        var supply = p.AiSupply ?? new int[7];
        var need = new[] { q.Cost.Wood, q.Cost.Ore, q.Cost.Mercury, q.Cost.Crystal, q.Cost.Sulfur, q.Cost.Gem, q.Cost.Gold };
        for (int i = 0; i < 7; i++) if (supply[i] < need[i]) return;
        Send(g, new UpgradeTalismanCmd(t.Object, h.Id, next) { Player = h.Owner });
    }

    /// <summary>
    /// FUN_009c3f20 (009c4710 step 9, a hero in an own town): only when ConsiderGarrisoningHeroToImportantTown (row
    /// +0x38) is 0, the town not blocked for the AI (vf 0x114), the hero no collector / supplier, the owner has at
    /// least two heroes that are not a town's garrison hero (player vf 0xc8 = 004fc700), no enemy threatens the town
    /// with a path whose last value / 2666 &lt; 1 (CPlayerAIInformation vf 0x64(town, 1) = 00c28470) and the hero's
    /// estimate is not below the garrison hero's: the hero goes into the garrison (FUN_00435cc0 mode 3).
    /// A garrison hero already there comes out as the visitor (the command 00435cc0 / 004f58e0 swaps; GarrisonHeroCmd).
    /// GAP-details: the permission test FUN_00c21580 is left to the command; the threat path value is taken as our path
    /// cost.
    /// </summary>
    public void GarrisonHeroInTown(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState h, TownState t)
    {
        if (!h.Alive || diff.ConsiderGarrisoningHeroToImportantTown || t.AiGarrisonBlocked) return;
        if (_role.GetValueOrDefault(h.Id, HeroRole.Freelancer) is HeroRole.Supplier or HeroRole.Collector) return;
        if (g.Player(h.Owner) is not { Alive: true } p) return;
        int free = g.S.Heroes.Count(x => x.Alive && x.Owner == p.Index && !(x.InTown >= 0 && x.Garrisoned));
        if (free < 2) return;
        int near = Threats(p.Index).H.Values.Count(ht => ht.Find(t.Object) is { } r && r.Path.Count > 0 && r.Path[^1].Cost / 2666 < 1);
        if (near > 0) return;
        if (TownHeroes(g, t).Garrison is { } gh && gh != h && HeroEstimate(g, st, h) < HeroEstimate(g, st, gh)) return;
        Send(g, new GarrisonHeroCmd(t.Object, h.Id, true) { Player = h.Owner });
    }

    // ---------------------------------------------------------------- roles

    // Hero role (hero vf 0x2bc; exe 0 = our Freelancer) and partner (vf 0x2c0) kept from day to day, as the exe keeps
    // them on the hero. GAP-detail: not saved with the game (a loaded game starts with every hero a freelancer).
    readonly Dictionary<int, HeroRole> _role = new();
    readonly Dictionary<int, int> _partner = new();

    /// <summary>
    /// Role assignment (exe phase 12, FUN_00869ad0 over FUN_00868a40):
    ///   • every hero gets weight RolesSpread[current role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel) and the
    ///     list is sorted ascending (comparator 00866650 a &lt; b, merge sort FUN_0086f6a0: stable);
    ///   • k = min((heroes + 1) / 3 + 1, list size, heroes), at least 1, heroes are taken from the END of the list
    ///     (the heaviest first) as commanders (role 1);
    ///   • each commander, in that order, keeps its current partner when that hero still has role 2 / 3 and is in the
    ///     remaining list (FUN_00866810), else takes the remaining hero nearest to it (FUN_008668c0: squared distance
    ///     FUN_00a33600 dx² + dy² + (floors · map +0x6c)², strict &lt;, heroes without a map position skipped);
    ///     the partner becomes its collector (2) or supplier (3) and leaves the list;
    ///   • everybody left becomes a freelancer (role 0).
    /// The partner's role is collector or, when the supplier test FUN_00c5af30 holds, supplier (see SupplierTest).
    /// GAP-details: the floor factor (map +0x6c) is not
    /// traced (taken as 0); heroes fixed by SetHeroRoleMode (vf 0x2d4 ∈ {1, 2}, left out of the list and given role 0)
    /// are not separated because the script setting is not visible to the engine.
    /// </summary>
    public Dictionary<int, HeroRole> AssignRoles(Engine.Adventure g, List<HeroState> heroes, AdventureAiStats st, AdventureDifficulty diff)
    {
        var list = heroes.OrderBy(h => SpreadWeight(st, diff, _role.GetValueOrDefault(h.Id, HeroRole.Freelancer), h)).ToList();
        int k = Math.Max(1, Math.Min(Math.Min((heroes.Count + 1) / 3 + 1, list.Count), heroes.Count));
        var commanders = new List<HeroState>();
        for (int i = 0; i < k && list.Count > 0; i++) { commanders.Add(list[^1]); list.RemoveAt(list.Count - 1); }
        var roles = new Dictionary<int, HeroRole>();
        var partners = new Dictionary<int, int>();
        foreach (var c in commanders)
        {
            var partner = KeptPartner(c, list) ?? NearestHero(c, list);
            roles[c.Id] = HeroRole.Commander;
            if (partner == null) continue;
            partners[c.Id] = partner.Id;
            // 00869ad0: the partner is a supplier when the supplier test (FUN_00c5af30) holds with the roles of the
            // previous assignment (the new roles are only applied after the pass), else a collector.
            roles[partner.Id] = SupplierTest(g, st, partner, c, _role.GetValueOrDefault(partner.Id, HeroRole.Freelancer),
                _role.GetValueOrDefault(c.Id, HeroRole.Freelancer)) ? HeroRole.Supplier : HeroRole.Collector;
            partners[partner.Id] = c.Id;
            list.Remove(partner);
        }
        foreach (var h in list) roles[h.Id] = HeroRole.Freelancer;
        foreach (var h in heroes) { _role.Remove(h.Id); _partner.Remove(h.Id); }
        foreach (var (id, r) in roles) _role[id] = r;
        foreach (var (id, p) in partners) _partner[id] = p;
        return roles;
    }

    HeroState? KeptPartner(HeroState c, List<HeroState> list)
    {
        if (_role.GetValueOrDefault(c.Id, HeroRole.Freelancer) != HeroRole.Commander || !_partner.TryGetValue(c.Id, out int pid)) return null;
        var p = list.FirstOrDefault(x => x.Id == pid);
        return p != null && p.Owner == c.Owner && _role.GetValueOrDefault(p.Id) is HeroRole.Collector or HeroRole.Supplier ? p : null;
    }

    static HeroState? NearestHero(HeroState c, List<HeroState> list)
    {
        if (c.Garrisoned) return null;
        HeroState? best = null;
        long bestD = 0;
        foreach (var x in list)
        {
            if (x.Garrisoned) continue;
            long dx = x.Pos.X - c.Pos.X, dy = x.Pos.Y - c.Pos.Y, d = dx * dx + dy * dy;
            if (best == null || d < bestD) { best = x; bestD = d; }
        }
        return best;
    }

    /// <summary>Exe FUN_00868a40 × FUN_004e9a80: RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel).</summary>
    public static double SpreadWeight(AdventureAiStats st, AdventureDifficulty diff, HeroRole role, HeroState h) =>
        st.RoleSpread[(int)role - 1] * (1 + h.Level * diff.ArmyPowerCoefBonusPerHeroLevel);

    /// <summary>
    /// The next hero to act (exe FUN_00869770): among the player's heroes not finished this turn, the one with the
    /// lowest <see cref="PrimarySum"/> (strict &lt;: the earlier hero wins ties).
    /// </summary>
    public static HeroState? NextHero(Engine.Adventure g, int me, HashSet<int> done)
    {
        HeroState? h = null;
        int bestSum = 0;
        foreach (var x in g.S.Heroes)
        {
            // Heroes in a garrison have no map object (world vf 0x100 → null at 0x008698c2) and are not candidates.
            if (!x.Alive || x.Owner != me || done.Contains(x.Id) || x.Garrisoned) continue;
            // EnableHeroAI(hero, false): the AI leaves the hero to the map scripts. GAP-detail: the exe's gate here
            // is world vf 0x100(hero) → vf 0x44 (0x008698ba); a hero failing it is finished at once
            // (FUN_00868640(hero, 0, 1)). That this gate is the AI-enabled flag is not traced; the flag itself
            // (hero vf 0x2ac) is read by the garrison moves at 0x00868768 / 0x008699f8.
            if (!x.AiEnabled) { done.Add(x.Id); continue; }
            int sum = PrimarySum(g, x);
            if (h == null || sum < bestSum) { h = x; bestSum = sum; }
        }
        return h;
    }

    /// <summary>
    /// Attack + defence + spell power + knowledge as hero vf 0x10 / 0x14 / 0x18 / 0x1c return them (worn artifacts
    /// included; attack/defence at least 0, spell power/knowledge at least 1 — the same getters GetHeroStat uses).
    /// </summary>
    public static int PrimarySum(Engine.Adventure g, HeroState h)
    {
        int Worn(Func<H5.Core.Data.ArtifactDef, int> f) => h.Artifacts.Sum(a => g.Core.Artifacts.TryGetValue(a, out var d) ? f(d) : 0);
        return Math.Max(0, h.Attack + Worn(a => a.Attack)) + Math.Max(0, h.Defence + Worn(a => a.Defence))
             + Math.Max(1, h.SpellPower + Worn(a => a.SpellPower)) + Math.Max(1, h.Knowledge + Worn(a => a.Knowledge));
    }

    // ---------------------------------------------------------------- destinations

    readonly record struct Destination(int Object, Tile Tile, double Score, int Cost = 0);

    /// <summary>
    /// Old-target bonus (exe FUN_00a78940, 0x00a78a67): when the destination is the hero's remembered target the
    /// value becomes ftol(v · SearchOldTargetMultiplier) + 20 for v ≥ 0, ftol((float)v / multiplier) + 20 otherwise
    /// (the multiplier is the float at AI/Adventure +0x204); no random factor is applied then.
    /// </summary>
    public static long OldTargetValue(long v, float multiplier) =>
        (v >= 0 ? (long)(v * (double)multiplier) : (long)((float)v / (double)multiplier)) + 20;

    /// <summary>
    /// Random factor (exe FUN_00a78940, 0x00a78ab7): r = Range(1, SearchBestPositionRandomFactor) inclusive +
    /// 100 − SearchBestPositionRandomFactor, i.e. r ∈ [101 − R, 100]; v &gt; 0 → max(1, ftol(r · v · 0.01f)),
    /// otherwise ftol(v / r · 100).
    /// </summary>
    public static long RandomizedValue(long v, int roll, int randomFactor)
    {
        int r = roll + 100 - randomFactor;
        if (v > 0) return Math.Max(1L, (long)((double)r * v * (double)0.01f));
        return r == 0 ? 0 : (long)((double)v / r * 100.0);
    }

    /// <summary>
    /// Distance discount in the destination choice (exe FUN_00a7b630, 0x00a7b9b0): a destination whose state is
    /// positive, or whose state is 0 and value positive, and whose move cost exceeds SearchAverageTerrainCost T
    /// is worth v · T / cost (int64 division), at least 5.
    /// </summary>
    public static long DiscountedValue(int state, long v, int cost, int averageTerrainCost)
    {
        if (!(state > 0 || (state >= 0 && v > 0)) || averageTerrainCost >= cost) return v;
        return Math.Max(5L, v * averageTerrainCost / cost);
    }

    /// <summary>
    /// The choice keeps the best (state, discounted value) pair: a candidate replaces the best only when its state
    /// is higher, or equal with a strictly higher value (FUN_00a7b630 "discarded, %s &lt;= %s" / "new best").
    /// </summary>
    public static bool Better(int state, long value, int bestState, long bestValue) =>
        state > bestState || (state == bestState && value > bestValue);

    /// <summary>
    /// Search limits of the hero's search attempts (exe FUN_0086c010, "Wave spread: search limit = %d"): the first
    /// attempt searches up to 1500 with 6 attempts allowed, a commander / collector / supplier (roles 1..3) up to
    /// RolesSearchNearMaxCost (row +0x14) with 2 attempts; a remembered target raises it to at least its stored cost
    /// + 200 + the hero's move points; when the player is in danger (FUN_00866c30) it is RolesSearchFarMaxCost
    /// (row +0x18) with 1 attempt. Every further attempt doubles the limit, roles 1..3 jump to RolesSearchFarMaxCost.
    /// When all attempts find nothing the hero is finished.
    /// </summary>
    public static List<int> SearchLimits(HeroRole role, AdventureDifficulty diff, int? rememberedCost, int movePoints, bool playerInDanger)
    {
        bool roled = role is HeroRole.Commander or HeroRole.Collector or HeroRole.Supplier;
        int limit = roled ? diff.RolesSearchNearMaxCost : 1500, attempts = roled ? 2 : 6;
        if (rememberedCost is int c) limit = Math.Max(limit, c + 200 + movePoints);
        if (playerInDanger) { limit = diff.RolesSearchFarMaxCost; attempts = 1; }
        var list = new List<int> { limit };
        for (int a = 1; a < attempts; a++)
        {
            limit = roled ? diff.RolesSearchFarMaxCost : limit * 2;
            list.Add(limit);
        }
        return list;
    }

    /// <summary>
    /// "Player in danger" (exe FUN_00866c30): with n towns of which k have a threat (FUN_00c283d0) above
    /// ThreatsTownInDangerThreshold (row +0x70), true when n &lt; 1, k &gt; n − 1, or the player's heroes ≤ k + 1.
    /// </summary>
    public static bool PlayerInDanger(Engine.Adventure g, int me, int endangered = 0)
    {
        int towns = g.S.Towns.Count(t => t.Owner == me);
        if (towns < 1 || endangered > towns - 1) return true;
        return g.S.Heroes.Count(x => x.Alive && x.Owner == me) <= endangered + 1;
    }

    /// <summary>
    /// Tiles in reach of a stronger enemy hero: its path-cost flood up to reach = move points ·
    /// SearchDangerEnemyMobilityMultiplier + SearchDangerEnemyMobilityBonus (exe FUN_00872330). GAP-detail: which
    /// heroes count as dangerous (here: army power above ours) is reconstructed.
    /// </summary>
    static HashSet<Tile> DangerTiles(Engine.Adventure g, HeroState h, AdventureAiStats st, double myPower)
    {
        var team = g.S.Players[h.Owner].Team;
        var set = new HashSet<Tile>();
        foreach (var x in g.S.Heroes)
        {
            if (!x.Alive || x.Owner < 0 || x.Owner == h.Owner || g.S.Players[x.Owner].Team == team || x.Pos.Floor != h.Pos.Floor || x.Garrisoned) continue;
            if (g.ArmyPower(x.Army.Live(), x) <= myPower) continue;
            int reach = (int)Math.Round(x.MaxMovePoints * st.SearchDangerEnemyMobilityMultiplier + st.SearchDangerEnemyMobilityBonus);
            set.UnionWith(g.ReachableCosts(x, reach).Keys);
        }
        return set;
    }

    static ulong Mix(params ulong[] parts)
    {
        ulong h = 0x9E3779B97F4A7C15;
        foreach (var p in parts) { h ^= p + 0x9E3779B97F4A7C15 + (h << 6) + (h >> 2); h *= 0xBF58476D1CE4E5B9; }
        return h;
    }

    // ---------------------------------------------------------------- towns and heroes

    /// <summary>The count gates of FUN_0086b0a0 (see <see cref="HireHeroes"/>).</summary>
    public static bool HiringAllowed(Engine.Adventure g, PlayerState me, int heroes, int towns, AdventureDifficulty diff)
    {
        if (heroes >= g.HiringHeroLimit(me)) return false;
        if (heroes >= diff.HireHeroesBase + diff.HireHeroesPerTown * towns) return false;
        return heroes == 0 || ComputerHeroes(g) < diff.HireHeroesGlobalLimit;
    }

    /// <summary>Heroes of all computer players (exe FUN_00866b60: players 1..8 of type 1 that are not human).</summary>
    public static int ComputerHeroes(Engine.Adventure g)
    {
        var computers = g.S.Players.Where(p => p.Alive && !p.Human).Select(p => p.Index).ToHashSet();
        return g.S.Heroes.Count(h => h.Alive && h.Owner >= 0 && computers.Contains(h.Owner));
    }

    static void TakeGarrison(Engine.Adventure g, HeroState h, TownState t)
    {
        for (int from = 0; from < Army.Slots; from++)
        {
            if (t.Garrison[from] is not { Count: > 0 } s) continue;
            int to = Array.FindIndex(h.Army, x => x is { Count: > 0 } && x.Creature == s.Creature);
            if (to < 0) to = Array.FindIndex(h.Army, x => x is not { Count: > 0 });
            if (to < 0) break;
            Send(g, new TransferStackCmd(-1, t.Object, from, h.Id, -1, to, s.Count) { Player = h.Owner });
        }
    }

    /// <summary>
    /// Level-up choice (exe FUN_004e6d30 case 3, "level up" / "selected skill/perk %s (mastery %d, value %d)"): every
    /// offered skill or perk is valued with <see cref="SkillAiValue"/> (the mastery is only logged); the options
    /// whose value is the maximum (the running maximum starts at 0, a value ≥ it joins the list, a higher one first
    /// clears it) are collected in offer order and one of them is taken at random (FUN_008e7840 over [0, n − 1]).
    /// Our option order is skills then perks (the exe offer order is the engine's, not traced here); the random draw
    /// uses the AI's private generator.
    /// </summary>
    void LevelUps(Engine.Adventure g, HeroState h, AdventureAiStats st)
    {
        for (int guard = 0; guard < 16 && h.Alive && h.PendingLevelUps.Count > 0; guard++)
        {
            var offer = h.PendingLevelUps[0];
            var ids = offer.Skills.Select(x => x.SkillId).Concat(offer.Perks).ToList();
            var role = _role.GetValueOrDefault(h.Id, HeroRole.Freelancer);
            var best = BestLevelUpOptions(ids.Select(id => SkillAiValue(g, h.Town, role, id)).ToList());
            if (best.Count == 0) break;
            var rng = new Rng(Mix(g.S.Rng.S0, (ulong)g.S.Day, (ulong)h.Owner, (ulong)h.Id, (ulong)offer.NewLevel, 0x1E7E1));
            int pick = best[best.Count == 1 ? 0 : rng.Range(0, best.Count - 1)];
            if (!Send(g, new ChooseLevelUpCmd(h.Id, pick) { Player = h.Owner }).Ok) break;
        }
    }

    /// <summary>
    /// The indices FUN_004e6d30 keeps: max starts at 0; v ≥ max (or the list empty) adds the index, v &gt; max first
    /// clears the list and raises max.
    /// </summary>
    public static List<int> BestLevelUpOptions(IReadOnlyList<int> values)
    {
        var list = new List<int>();
        int max = 0;
        for (int i = 0; i < values.Count; i++)
        {
            int v = values[i];
            if (list.Count != 0 && v < max) continue;
            if (v > max) { list.Clear(); max = v; }
            list.Add(i);
        }
        return list;
    }

    /// <summary>
    /// Skill / perk value for a hero (exe FUN_00c5abd0): the skill's AIRacesValues entry for the hero's town type
    /// (hero vf 0x254 = +0x84, TOWN_HEAVEN 3 … TOWN_STRONGHOLD 10; other → −1), by role (hero vf 0x2bc): 1 Commander
    /// → CommanderValue, 2 / 3 → CollectorSupplierValue, 0 (our Freelancer) → FreelancerValue.
    /// </summary>
    public static int SkillAiValue(Engine.Adventure g, string town, HeroRole role, string skill)
    {
        int r = Array.IndexOf(TownTypes, town);
        if (r < 0 || !g.Core.Skills.TryGetValue(skill, out var sd)) return -1;
        int slot = role switch { HeroRole.Commander => 0, HeroRole.Collector or HeroRole.Supplier => 1, _ => 2 };
        return sd.AiRaceValues[3 * r + slot];
    }

    static readonly string[] TownTypes =
        { "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD" };

    void ResolvePending(Engine.Adventure g, ulong seed)
    {
        for (int guard = 0; guard < 8 && (g.S.Battle != null || g.S.Choice != null); guard++)
        {
            if (g.S.Battle != null) Send(g, QuickCombat.Resolve(g, seed + (ulong)guard, BattleAI));
            else if (g.S.Choice is { } c)
                Send(g, new AnswerChoiceCmd(Accept: c.Kind != "join_paid" || g.Current.Purse[Purse.Gold] >= c.Gold * 2) { Player = g.Current.Index });
        }
    }
}
