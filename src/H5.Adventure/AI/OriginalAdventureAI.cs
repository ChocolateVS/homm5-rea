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
        if (HireHeroes(g, towns, diff))
        {
            // After a hire the exe goes back to the role phase 12 (0x0086b39a), so the new hero gets a role too.
            roles = AssignRoles(g, g.S.Heroes.Where(h => h.Alive && h.Owner == me).ToList(), st, diff);
            MoveHeroes(g, battleSeedBase, me, diff, st, roles, claimed, done, ref actions);
        }
        if ((g.S.Day - 1) % diff.TownBuildPeriodInDays == 0)
            foreach (var t in towns) TryBuild(g, t, st);
        foreach (var t in towns) Recruit(g, t, st);
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
                var target = ChooseDestination(g, h, roles[h.Id], diff, st, claimed);
                if (target == null) break;
                var r = Send(g, new MoveHeroCmd(h.Id, target.Value.Tile) { Player = me });
                actions++;
                if (!r.Ok) break;
                claimed.Add(target.Value.Object);
                _lastTarget[h.Id] = target.Value.Tile;
                _lastCost[h.Id] = target.Value.Cost;
                ResolvePending(g, battleSeedBase + (ulong)actions);
                LevelUps(g, h, st);
                // BlockTownGarrisonForAI: no creature exchange with that garrison (exe reader 0x008687a0).
                if (h.InTown >= 0 && g.S.Town(h.InTown) is { } home && home.Owner == me && !home.AiGarrisonBlocked) TakeGarrison(g, h, home);
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
    /// GAP-details: the exe command swaps the visitor with a garrisoned hero; our GarrisonHeroCmd refuses when the
    /// garrison already holds a hero. Our turn ends a hero on our own conditions (no destination, a failed move, under
    /// 100 move points, 8 steps), not only when the search is exhausted; the role-2/3 command 0086fc40 is not ported.
    /// </summary>
    public static void FinishHero(Engine.Adventure g, HeroState h, HeroRole role)
    {
        if (role is HeroRole.Collector or HeroRole.Supplier) return;
        if (!h.AiEnabled || h.Garrisoned || h.InTown < 0 || g.S.Town(h.InTown) is not { } t || t.Owner != h.Owner || t.AiGarrisonBlocked) return;
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
    /// GAP-details: the collector / supplier test FUN_00c5af30 (army value FUN_004eb050 before / after a simulated
    /// transfer, ratio vs DAT_00e72bf8) is not ported — partners are collectors; the floor factor (map +0x6c) is not
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
            roles[partner.Id] = HeroRole.Collector;
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
    /// GAP-detail: town threats are not ported (k = 0), so this is "no towns or at most one hero".
    /// </summary>
    public static bool PlayerInDanger(Engine.Adventure g, int me)
    {
        int towns = g.S.Towns.Count(t => t.Owner == me), endangered = 0;
        if (towns < 1 || endangered > towns - 1) return true;
        return g.S.Heroes.Count(x => x.Alive && x.Owner == me) <= endangered + 1;
    }

    readonly Dictionary<int, int> _lastCost = new();

    Destination? ChooseDestination(Engine.Adventure g, HeroState h, HeroRole role, AdventureDifficulty diff, AdventureAiStats st, HashSet<int> claimed)
    {
        double myPower = g.ArmyPower(h.Army.Live(), h);
        int? remembered = _lastTarget.ContainsKey(h.Id) && _lastCost.TryGetValue(h.Id, out int lc) ? lc : null;
        var limits = SearchLimits(role, diff, remembered, h.MovePoints, PlayerInDanger(g, h.Owner));
        // The wave spread (exe FUN_00c08be0 from 0086c010): one flood of the hero's path costs up to the widest limit;
        // each attempt keeps the destinations within its own limit.
        var costs = g.ReachableCosts(h, limits.Max());
        var danger = diff.CheckHeroesDanger ? DangerTiles(g, h, st, myPower) : new HashSet<Tile>();
        foreach (int limit in limits)
        {
            var best = ChooseWithin(g, h, diff, st, claimed, myPower, limit, costs, danger);
            if (best != null && g.FindPath(h, best.Value.Tile) != null) return best;
        }
        return SeaTravel(g, h, myPower, diff, st, claimed);
    }

    Destination? ChooseWithin(Engine.Adventure g, HeroState h, AdventureDifficulty diff, AdventureAiStats st, HashSet<int> claimed, double myPower, int maxCost,
                              Dictionary<Tile, int> costs, HashSet<Tile> danger)
    {
        var rng = new Rng(Mix(g.S.Rng.S0, (ulong)g.S.Day, (ulong)h.Owner, (ulong)h.Id));
        Destination? best = null;
        int bestState = 0;
        long bestValue = 0;
        bool hasLast = _lastTarget.TryGetValue(h.Id, out var last);
        foreach (var o in g.Map.Objects)
        {
            if (o.Pos.Floor != h.Pos.Floor || o.Kind == ObjectKind.Static || claimed.Contains(o.Index)) continue;
            if (g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            double value = ObjectValue(g, h, o, os, myPower, diff, st);
            // SetAIHeroAttractor / SetAIPlayerAttractor (exe FUN_00bc5740): the hero's entry, else the player's;
            // -1 subtracts SearchNegativeAttractorValue, 1 adds SearchPositiveAttractorValue, 2 sets the result
            // state to 3, which outranks every destination of a lower state whatever its value (FUN_00a7b630).
            int attract = AttractorPriority(g, h, o);
            int state = attract == 2 ? 3 : 0;
            // "But the AI will not risk its heroes" (script reference, not traced in the exe): a hostile target
            // judged too strong stays out even with priority 1.
            if (attract == 1 && value <= 0 && (o.Kind is ObjectKind.Monster or ObjectKind.Hero ||
                                               (o.Kind == ObjectKind.Town && g.S.Town(o.Index) is { } at && at.Owner != h.Owner))) continue;
            if (attract == -1) value -= st.SearchNegativeAttractorValue;
            else if (attract == 1) value += st.SearchPositiveAttractorValue;
            if (value <= 0 && state == 0) continue;
            var tile = o.Kind is ObjectKind.Monster or ObjectKind.Hero ? o.Pos : o.ActiveTiles.FirstOrDefault(o.Pos);
            // Move cost = the flood's path cost to the destination tile (record +0x18); unreached tiles are not found.
            if (tile == h.Pos || !costs.TryGetValue(tile, out int cost) || cost > maxCost) continue;
            if (state == 0)
            {
                // Guards on the way: never walk into a stack we would lose to (reconstructed).
                if (o.Kind != ObjectKind.Monster && g.GuardingMonster(tile) is { } guard &&
                    g.S.Objects.TryGetValue(guard.Index, out var gs) && g.ArmyPower(gs.Guards, null) > myPower * (1 - diff.CowardiceVersusComputer)) continue;
                // Danger zones of stronger enemy heroes (FUN_00872330 reach flooded over their path costs).
                if (danger.Contains(tile)) continue;
            }
            // GAP-detail: whether state-3 destinations skip the danger checks is not traced (they do here).
            long v = (long)value;
            v = hasLast && last == tile
                ? OldTargetValue(v, (float)st.SearchOldTargetMultiplier)
                : RandomizedValue(v, rng.Range(1, Math.Max(1, st.SearchBestPositionRandomFactor)), st.SearchBestPositionRandomFactor);
            v = DiscountedValue(state, v, cost, st.SearchAverageTerrainCost);
            if (best == null || Better(state, v, bestState, bestValue))
            {
                best = new Destination(o.Index, tile, v, cost);
                bestState = state;
                bestValue = v;
            }
        }
        return best;
    }

    /// <summary>Attractor priority for the hero (own entry first, then its player's; FUN_00b4d5c0 / player vf 0x1e8).</summary>
    static int AttractorPriority(Engine.Adventure g, HeroState h, MapObject o)
    {
        if (o.Name.Length == 0) return 0;
        int p = AiAttractor.Get(h.AiAttractors, o.Name);
        if (p == 0 && h.Owner >= 0 && g.Player(h.Owner) is { } pl) p = AiAttractor.Get(pl.AiAttractors, o.Name);
        return p;
    }

    /// <summary>Value standing for a negative state (a refused fight): never chosen unless an attractor sets state 3.</summary>
    const double Refused = -1e15;

    /// <summary>No human player in the game has explored the tile (exe FUN_0098dbf0: player vf 0x19c(floor) fog byte).</summary>
    public static bool UnseenByHumans(Engine.Adventure g, Tile t)
    {
        var f = g.Map.Floor(t.Floor);
        if (f == null) return true;
        foreach (var p in g.S.Players)
            if (p.Alive && p.Human && t.Floor < p.Explored.Count && p.Explored[t.Floor][t.Y * f.Width + t.X]) return false;
        return true;
    }

    /// <summary>Gold-equivalent value of visiting an object, using the AI/Adventure ValueOf* tunables.</summary>
    static double ObjectValue(Engine.Adventure g, HeroState h, MapObject o, ObjState? os, double myPower, AdventureDifficulty diff, AdventureAiStats st)
    {
        switch (o.Kind)
        {
            case ObjectKind.Treasure:
                if (os == null) return 500;
                return os.ResourceKind == Purse.Gold ? os.Amount : os.ResourceKind >= 0 ? os.Amount * (os.ResourceKind <= Purse.Ore ? 250 : 500) : 1000;
            case ObjectKind.Artifact:
                var art = o.ArtifactId ?? o.RewardArtifact;
                return art != null && g.Core.Artifacts.TryGetValue(art, out var ad) ? st.ArtifactValue(ad.Type) : st.ValueOfMinorArtifact;
            case ObjectKind.Mine:
            case ObjectKind.AbandonedMine:
                // Daily income in gold equivalents (gold mine 1000, resource mines 2 units × 250/500) scaled by
                // IncomeMultiplier/10. Reconstructed: the exe's exact income valuation is not traced yet.
                if (os != null && os.Owner == h.Owner) return 0;
                bool goldMine = o.Tag.Contains("GOLD", StringComparison.OrdinalIgnoreCase) || o.SharedPath.Contains("Gold", StringComparison.OrdinalIgnoreCase);
                bool common = o.Tag.Contains("WOOD", StringComparison.OrdinalIgnoreCase) || o.Tag.Contains("ORE", StringComparison.OrdinalIgnoreCase)
                              || o.SharedPath.Contains("Sawmill", StringComparison.OrdinalIgnoreCase) || o.SharedPath.Contains("Ore", StringComparison.OrdinalIgnoreCase);
                return (goldMine ? 1000 : common ? 500 : 1000) * st.IncomeMultiplier / 10.0;
            case ObjectKind.Dwelling:
                return os == null || os.Owner != h.Owner ? st.IncomeMultiplier * 40 : 0;
            case ObjectKind.Shrine:
                return h.Spells.Count < 64 ? st.ValueOfSpellPerLevel * 2 : 0;
            case ObjectKind.Monster:
            {
                // Exe provider 004eee00 → FUN_00bc3af0 (see MonsterFightValue); a refused fight is state −1.
                if (os == null) return 0;
                return MonsterFightValue(g, st, diff, h, os.Guards, UnseenByHumans(g, o.Pos)) ?? Refused;
            }
            case ObjectKind.Town:
            {
                var t = g.S.Town(o.Index);
                if (t == null) return 0;
                if (t.Owner == h.Owner)
                    return h.InTown != t.Object && t.Garrison.StackCount() > 0 ? diff.ValueOfTown * 0.25 : 0;
                double garrison = g.ArmyPower(t.Garrison.Live(), null);
                bool human = t.Owner >= 0 && g.S.Players[t.Owner].Human;
                double aggr = human ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef;
                double coward = human ? diff.CowardiceVersusHuman : diff.CowardiceVersusComputer;
                if (garrison * (1 + coward) >= myPower * aggr) return 0;
                return diff.ValueOfTown;
            }
            case ObjectKind.Hero:
            {
                var other = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Pos == o.Pos && x.Owner != h.Owner);
                if (other == null) return 0;
                double enemy = g.ArmyPower(other.Army.Live(), other);
                bool human = g.S.Players[other.Owner].Human;
                double aggr = human ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef;
                double coward = human ? diff.CowardiceVersusHuman : diff.CowardiceVersusComputer;
                if (enemy * (1 + coward) >= myPower * aggr) return 0;
                return st.ValueOfHero + st.ValueOfHeroLevelPow2 * other.Level * other.Level;
            }
            default:
                return 0;
        }
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

    static void TryBuild(Engine.Adventure g, TownState t, AdventureAiStats st)
    {
        if (t.BuiltToday) return;
        // Income first (IncomeMultiplier weighs income buildings heavily), then dwellings low→high, defences, guild.
        var order = new List<string> { "TB_TOWN_HALL", "TB_MARKETPLACE" };
        for (int i = 1; i <= 7; i++) order.Add($"TB_DWELLING_{i}");
        order.AddRange(new[] { "TB_FORT", "TB_MAGIC_GUILD", "TB_TAVERN", "TB_BLACKSMITH" });
        foreach (var b in order.Concat(t.MaxLevel.Keys.Except(order).OrderBy(k => k, StringComparer.Ordinal)))
            if (Send(g, new BuildCmd(t.Object, b) { Player = t.Owner }).Ok) return;
    }

    /// <summary>Buys creatures high tier first, keeping TownCreaturesReserveCoef of the gold back.</summary>
    static void Recruit(Engine.Adventure g, TownState t, AdventureAiStats st)
    {
        int reserve = (int)(g.Current.Purse[Purse.Gold] * st.TownCreaturesReserveCoef);
        for (int tier = 7; tier >= 1; tier--)
        {
            int variant = g.DwellingCreature(t, tier, 1) != null ? 1 : 0;
            var cr = g.DwellingCreature(t, tier, variant);
            if (cr == null || !g.Core.Creatures.TryGetValue(cr, out var cd) || t.Available[tier - 1] <= 0) continue;
            int gold = Math.Max(1, cd.Cost.Gold);
            int n = Math.Min(t.Available[tier - 1], Math.Max(0, g.Current.Purse[Purse.Gold] - reserve) / gold);
            while (n > 0 && !Send(g, new RecruitCmd(t.Object, tier, n, variant) { Player = t.Owner }).Ok) n /= 2;
        }
    }

    /// <summary>
    /// Hero hiring (exe phase 7, FUN_0086b0a0), at most once per day (CPlayerAI +0x60, set after a hire at
    /// 0x0086b38f, cleared by SFLB_InitDay at 0x008674e6):
    ///   • heroes &lt; MaxHeroes − SetPlayerHeroesCountNotForHire (FUN_0098de60 − player vf 0xb0);
    ///   • when the player already has a hero, the heroes of all computer players together (FUN_00866b60:
    ///     players 1..8, computer, not human) must stay below HireHeroesGlobalLimit (row +0x44);
    ///   • heroes ≥ HireHeroesBase + HireHeroesPerTown · towns (row +0x3c / +0x40) passes the "over limit" flag to
    ///     FUN_0086aa50, which then only accepts heroes its AI-hiring settings object marks (vf 0x88) — GAP: that
    ///     test is not traced, so over the limit nobody is hired here;
    ///   • the two tavern offers (player vf 0x14c(0..1)) are tried in order; with heroes &gt; 0 an offer is skipped
    ///     when the hire cost without a town exceeds the gold (FUN_00b4ced0(player, null) vs resources +0x18);
    ///   • FUN_0086aa50 then keeps the best town: AI-hiring enabled for it (settings vf 0x54, EnableAIHeroHiring),
    ///     the hero passes the player's AI filter and the town's filter (FUN_00c069d0), the town is not in danger
    ///     (FUN_008672d0 — GAP: not modelled) and the hire check FUN_004f4570 passes. GAP: the score (race coef
    ///     FUN_008683f0 × hero value FUN_004eae60 × 1 or 0.1 by town race / (distance FUN_004ea050 + 1)) is not
    ///     ported; the first eligible town and the first offer win, as the exe's strict comparison does on ties.
    /// </summary>
    static bool HireHeroes(Engine.Adventure g, List<TownState> towns, AdventureDifficulty diff)
    {
        var me = g.Current;
        int heroes = g.S.Heroes.Count(h => h.Alive && h.Owner == me.Index);
        if (!HiringAllowed(g, me, heroes, towns.Count, diff)) return false;
        for (int slot = 0; slot < me.Tavern.Length && slot < 2; slot++)
        {
            if (me.Tavern[slot] is not { } id) continue;
            // 0x0086b2db: the offer gate passes no town (no TOWN_BONUS_HEROES discount here).
            if (heroes > 0 && g.HireCost(me) > me.Purse[Purse.Gold]) continue;
            if (!Engine.Adventure.FilterAccepts(g.Core, me.AiHireFilter, id)) continue;
            foreach (var t in towns.Where(t => t.Level("TB_TAVERN") > 0 && !me.AiHiringDisabledTowns.Contains(t.Object)))
            {
                int shown = Array.IndexOf(g.TownTavernOffers(me, t), id);
                if (shown >= 0 && Send(g, new HireHeroCmd(t.Object, shown) { Player = me.Index }).Ok) return true;
            }
        }
        return false;
    }

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
