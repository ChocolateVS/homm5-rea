using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// The destination search of one hero (exe 0086c010 wave spread → 00a7af80 per destination (free scan 00a78400,
// evaluator 00a78b60 with the value pool 00bc5740 / the visit simulation 00bc5480) → 00a7b630 choice with the
// strategic propagation 00a79700 and the value 00a78940).
public sealed partial class OriginalAdventureAI
{
    /// <summary>A destination record (exe 0x48-byte element): cell, state, value, cost, critical, strategic parts.</summary>
    sealed class Candidate
    {
        public MapObject O = null!;
        public Tile Tile;
        public int Cost;
        public int State;
        public long Value;
        public bool Critical;
        public int StratState;
        public long StratValue;
        /// <summary>Threat answered by going there (FUN_00c28c30 reserves it for this hero).</summary>
        public ThreatRecord? Threat;
    }

    readonly Dictionary<int, int> _lastCost = new();
    Dictionary<int, (int State, long Value)>? _before;
    /// <summary>The (state, value) of the hero's last choice (00a7b630 output +0x44), kept with the remembered target.</summary>
    readonly Dictionary<int, (int State, long Value)> _lastBest = new();

    /// <summary>"merge" of two states (00a79700 / 00a78940): a negative one dominates, else the larger.</summary>
    public static int MergeState(int a, int b) { int m = Math.Min(a, b); return m < 0 ? m : Math.Max(a, b); }

    /// <summary>FUN_005ff750 "bad": state &lt; 0, or state 0 with a negative value.</summary>
    static bool Bad(int state, long value) => state < 0 || (state == 0 && value < 0);

    static bool Positive(int state, long value) => state > 0 || (state == 0 && value > 0);

    Destination? ChooseDestination(Engine.Adventure g, HeroState h, Dictionary<int, HeroRole> roles, AdventureDifficulty diff, AdventureAiStats st, HashSet<int> claimed)
    {
        var role = roles.GetValueOrDefault(h.Id, HeroRole.Freelancer);
        double myPower = g.ArmyPower(h.Army.Live(), h);
        int? remembered = _lastTarget.ContainsKey(h.Id) && _lastCost.TryGetValue(h.Id, out int lc) ? lc : null;
        var limits = SearchLimits(role, diff, remembered, h.MovePoints, PlayerInDanger(g, h.Owner, EndangeredTowns(g, diff, h.Owner)));
        // The wave spread (exe FUN_00c08be0 from 0086c010): one flood of the hero's path costs up to the widest limit;
        // each attempt keeps the destinations within its own limit.
        var costs = g.ReachableCosts(h, limits.Max());
        var danger = diff.CheckHeroesDanger ? DangerTiles(g, h, st, myPower) : new HashSet<Tile>();
        var cache = new Dictionary<int, (int, long)>();
        _before = null;
        foreach (int limit in limits)
        {
            var best = ChooseWithin(g, h, role, roles, diff, st, claimed, myPower, limit, costs, danger, cache);
            if (best != null && g.FindPath(h, best.Value.Tile) != null) return best;
        }
        return SeaTravel(g, h, myPower, diff, st, claimed);
    }

    Destination? ChooseWithin(Engine.Adventure g, HeroState h, HeroRole role, Dictionary<int, HeroRole> roles, AdventureDifficulty diff, AdventureAiStats st,
                              HashSet<int> claimed, double myPower, int maxCost, Dictionary<Tile, int> costs, HashSet<Tile> danger,
                              Dictionary<int, (int, long)> cache)
    {
        var rng = new Rng(Mix(g.S.Rng.S0, (ulong)g.S.Day, (ulong)h.Owner, (ulong)h.Id));
        bool hasLast = _lastTarget.TryGetValue(h.Id, out var last);
        bool targetOk = hasLast && costs.ContainsKey(last);
        var dests = new List<Candidate>();
        foreach (var o in SearchObjects(g, h))
        {
            if (o.Pos.Floor != h.Pos.Floor || o.Kind is ObjectKind.Static or ObjectKind.Stand or ObjectKind.Unknown || claimed.Contains(o.Index)) continue;
            if (g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            var tile = o.Kind is ObjectKind.Monster or ObjectKind.Hero ? o.Pos : o.ActiveTiles.FirstOrDefault(o.Pos);
            // Move cost = the flood's path cost to the destination tile (record +0x18); unreached tiles are not found.
            if (tile == h.Pos || !costs.TryGetValue(tile, out int cost) || cost > maxCost) continue;
            if (!FreeScan(g, h, role, o, tile, cost, targetOk)) continue;
            if (!cache.TryGetValue(o.Index, out var sv)) cache[o.Index] = sv = ValueOf(g, h, o, os, tile, cost, diff, st);
            var d = new Candidate { O = o, Tile = tile, Cost = cost, State = sv.Item1, Value = sv.Item2 };
            // "But the AI will not risk its heroes" (script reference, not traced in the exe): a hostile target
            // judged too strong stays out even with priority 1.
            if (AttractorPriority(g, h, o) == 1 && d.Value <= 0 && (o.Kind is ObjectKind.Monster or ObjectKind.Hero ||
                                                     (o.Kind == ObjectKind.Town && g.S.Town(o.Index) is { } at && at.Owner != h.Owner))) continue;
            if (d.State == 0)
            {
                // Guards on the way: never walk into a stack we would lose to (reconstructed).
                if (o.Kind != ObjectKind.Monster && g.GuardingMonster(tile) is { } guard &&
                    g.S.Objects.TryGetValue(guard.Index, out var gs) && g.ArmyPower(gs.Guards, null) > myPower * (1 - diff.CowardiceVersusComputer)) continue;
                // Danger zones of stronger enemy heroes (FUN_00872330 reach flooded over their path costs).
                if (danger.Contains(tile)) continue;
            }
            Protect(g, st, diff, h, roles, d, costs);
            dests.Add(d);
        }
        StrategicPropagation(g, h, st, diff, dests);

        // The choice (FUN_00a7b630).
        int T = st.SearchAverageTerrainCost;
        Candidate? chosen = null;
        int bestState = 0;
        long bestValue = 0;
        if (targetOk && _lastBest.TryGetValue(h.Id, out var prev))
        {
            int c = costs[last];
            bestState = prev.State;
            bestValue = c > T ? (long)T * prev.Value * 3 / (2L * c) : prev.Value;
            var o = g.Map.Objects.FirstOrDefault(x => x.Kind is ObjectKind.Monster or ObjectKind.Hero ? x.Pos == last : x.ActiveTiles.FirstOrDefault(x.Pos) == last);
            if (o != null) chosen = new Candidate { O = o, Tile = last, Cost = c, State = prev.State, Value = bestValue };
        }
        foreach (var d in dests)
        {
            if (Positive(bestState, bestValue) && d.State == 0 && d.Value == 0) continue;   // "discarded neutral for positive previous"
            int s = MergeState(d.State, d.StratState);
            long v = d.Value + d.StratValue;
            v = hasLast && last == d.Tile
                ? OldTargetValue(v, (float)st.SearchOldTargetMultiplier)
                : RandomizedValue(v, rng.Range(1, Math.Max(1, st.SearchBestPositionRandomFactor)), st.SearchBestPositionRandomFactor);
            v = DiscountedValue(s, v, d.Cost, T);
            if (d.Critical || Better(s, v, bestState, bestValue))
            {
                chosen = d;
                bestState = s;
                bestValue = v;
                if (d.Critical) break;
            }
        }
        if (chosen == null || !(Positive(bestState, bestValue) || chosen.Critical)) return null;
        _lastBest[h.Id] = (bestState, bestValue);
        if (chosen.Threat is { } tr) tr.Defender = h.Id;
        return new Destination(chosen.O.Index, chosen.Tile, bestValue, chosen.Cost);
    }

    /// <summary>
    /// The objects a search considers: the map objects, with the heroes taken from the game (a hero's map object, if any,
    /// only records its starting place): every other hero standing on the map, as a stand-in object at its tile
    /// (index −1000 − hero id).
    /// </summary>
    static IEnumerable<MapObject> SearchObjects(Engine.Adventure g, HeroState h)
    {
        foreach (var o in g.Map.Objects) if (o.Kind != ObjectKind.Hero) yield return o;
        foreach (var x in g.S.Heroes)
            if (x.Alive && x.Id != h.Id && !x.Garrisoned && !(x.InTown >= 0 && x.Owner == h.Owner))
                yield return new MapObject { Index = -1000 - x.Id, Kind = ObjectKind.Hero, Pos = x.Pos, Name = x.DefId };
    }

    /// <summary>
    /// Collector / supplier free scan (exe FUN_00a78400): allied foreign towns are never targets. A commander takes
    /// anything it reaches today and, farther, heroes, towns, ownable objects, artifacts, prisons, tents, monsters and
    /// visitable objects; a collector (supplier) without a valid remembered target takes anything on its floor within
    /// Chebyshev RolesCollectorFreeScanMaxMoves (RolesSupplierFreeScanMaxMoves) tiles (strict), farther a collector only
    /// own / allied towns, dwellings and heroes, a supplier only its partner, heroes it reaches today or the partner's
    /// town; a freelancer everything.
    /// UNVERIFIED-detail: the visitable test (vf 0x28 → vf 0x24) is taken as true; "partner's town" (vf 0x2c8) as the town
    /// the partner stands in.
    /// </summary>
    bool FreeScan(Engine.Adventure g, HeroState h, HeroRole role, MapObject o, Tile tile, int cost, bool targetOk)
    {
        if (o.Kind == ObjectKind.Town && g.S.Town(o.Index) is { } t && t.Owner != h.Owner && t.Owner >= 0 && SameTeam(g, t.Owner, h.Owner)) return false;
        bool ownable = o.Kind is ObjectKind.Mine or ObjectKind.AbandonedMine or ObjectKind.Dwelling or ObjectKind.DwarvenWarren or ObjectKind.Garrison or ObjectKind.Town;
        int OwnerOf() => o.Kind == ObjectKind.Town ? g.S.Town(o.Index)?.Owner ?? -1 : g.S.Objects.TryGetValue(o.Index, out var x) ? x.Owner : -1;
        switch (role)
        {
            case HeroRole.Commander:
                return true;   // reachable today, or one of the listed kinds — every kind we search qualifies
            case HeroRole.Collector:
            case HeroRole.Supplier:
            {
                int lim = role == HeroRole.Collector ? _stats!.RolesCollectorFreeScanMaxMoves : _stats!.RolesSupplierFreeScanMaxMoves;
                if (tile.Floor == h.Pos.Floor && !targetOk && Math.Max(Math.Abs(tile.X - h.Pos.X), Math.Abs(tile.Y - h.Pos.Y)) < lim) return true;
                if (role == HeroRole.Collector)
                {
                    if (ownable)
                    {
                        if (o.Kind is not (ObjectKind.Town or ObjectKind.Dwelling)) return false;
                        int ow = OwnerOf();
                        return ow >= 0 && SameTeam(g, ow, h.Owner);
                    }
                    if (o.Kind == ObjectKind.Hero)
                        return g.S.Heroes.FirstOrDefault(x => x.Alive && x.Pos == o.Pos) is { } oh && SameTeam(g, oh.Owner, h.Owner);
                    return false;
                }
                int partner = _partner.GetValueOrDefault(h.Id, -1);
                if (o.Kind == ObjectKind.Hero)
                    return g.S.Heroes.FirstOrDefault(x => x.Alive && x.Pos == o.Pos) is { } oh && (oh.Id == partner || cost <= h.MovePoints);
                if (o.Kind == ObjectKind.Town)
                    return g.S.Hero(partner) is { } p && p.InTown == o.Index;
                return false;
            }
            default:
                return true;
        }
    }

    /// <summary>
    /// Value of visiting an object (exe value pool 00bc5740): the object's provider gives it directly
    /// (<see cref="DirectValue"/>) or the visit is simulated (<see cref="SimulatedVisit"/>); a good result reachable
    /// today becomes state 1 for an enemy hero, or an enemy town without heroes and garrison; then the script attractors
    /// (−1 / +1 add ∓Search{Negative,Positive}AttractorValue, 2 sets state 3).
    /// </summary>
    (int State, long Value) ValueOf(Engine.Adventure g, HeroState h, MapObject o, ObjState? os, Tile tile, int cost, AdventureDifficulty diff, AdventureAiStats st)
    {
        var (s, v) = DirectValue(g, h, o, os, diff, st) ?? SimulatedVisit(g, h, o, tile, cost, diff, st);
        if (!Bad(s, v) && cost <= h.MovePoints)
        {
            if (o.Kind == ObjectKind.Hero && g.S.Heroes.FirstOrDefault(x => x.Alive && x.Pos == o.Pos) is { } oh && !SameTeam(g, oh.Owner, h.Owner)) s = 1;
            if (o.Kind == ObjectKind.Town && g.S.Town(o.Index) is { } t && !SameTeam(g, t.Owner, h.Owner) &&
                TownHeroes(g, t) is { Garrison: null, Visitor: null } && t.Garrison.StackCount() == 0) s = 1;
        }
        switch (AttractorPriority(g, h, o))
        {
            case -1: v -= st.SearchNegativeAttractorValue; break;
            case 1: v += st.SearchPositiveAttractorValue; break;
            case 2: s = 3; break;
        }
        return (s, v);
    }

    /// <summary>
    /// Providers that answer directly (provider vf 4 = 1): artifacts (FUN_004ec5d0: artifact value, a scroll by its
    /// spell), neutral monsters (004eee00, <see cref="MonsterFightValue"/>, a refused fight is state −1), shrines
    /// (00a808f0: the spell's value when the hero is no Barbarian and does not know it), mines (00b088a0 / 008b5680 /
    /// 00ce30f0: not allied — guarded → the fight, else production × IncomeMultiplier), keymaster tents (00bc93f0: a
    /// missing key → ValueOfBorderGuardKey), garrisons (00ca4710: the fight), shipyards / signs (0). Null → simulate.
    /// GAP-details: the per-kind direct values of CAdvMapBuilding (00b01af0) and CAdvMapTreasure (00a832b0) are not
    /// decoded, so those are simulated; enemy heroes keep the reconstructed value (their provider 00bc3ee0 is not
    /// decoded); fights against guards use <see cref="AttackerWins"/> for mines / garrisons.
    /// </summary>
    (int, long)? DirectValue(Engine.Adventure g, HeroState h, MapObject o, ObjState? os, AdventureDifficulty diff, AdventureAiStats st)
    {
        switch (o.Kind)
        {
            case ObjectKind.Artifact:
                if (os?.SpellId is { } scroll) return (0, SpellValue(g, st, scroll));
                var art = os?.ArtifactId ?? o.ArtifactId ?? o.RewardArtifact;
                return (0, art != null ? ArtifactItemValue(g, st, art) : 0);
            case ObjectKind.Monster:
                if (os == null) return (0, 0);
                return MonsterFightValue(g, st, diff, h, os.Guards, UnseenByHumans(g, o.Pos)) is long mv ? (0, mv) : (-1, 0);
            case ObjectKind.Shrine:
                if (os?.SpellId is not { } sp || h.Spells.Contains(sp) || h.Class == "HERO_CLASS_BARBARIAN") return (0, 0);
                return (0, SpellValue(g, st, sp));
            case ObjectKind.Mine:
            case ObjectKind.AbandonedMine:
            case ObjectKind.DwarvenWarren:
            {
                if (os != null && os.Owner >= 0 && SameTeam(g, os.Owner, h.Owner)) return (0, 0);
                var guards = os == null ? new List<Stack>() : os.Guards.Concat(os.Army.Live()).ToList();
                if (guards.Count > 0 && !AttackerWins(g, st, diff, h.Army.Live(), h, guards, null)) return (-1, 0);
                var (kind, amount) = o.Kind == ObjectKind.DwarvenWarren ? (Purse.Mercury, 1) : Engine.Adventure.MineYield(o.Shared.Type);
                var prod = new Purse();
                if (kind >= 0) prod[kind] = amount;
                return (0, ResourceValue(g, h.Owner, prod) * st.IncomeMultiplier);
            }
            case ObjectKind.Tent:
                if (o.Shared.Type != "BUILDING_KEYMASTER_TENT") return (0, 0);
                return g.Player(h.Owner) is { } p && !p.Keys.Contains(o.Shared.Color) ? (0, st.ValueOfBorderGuardKey) : (0, 0);
            case ObjectKind.Garrison:
                if (os == null || (os.Owner >= 0 && SameTeam(g, os.Owner, h.Owner)) || !os.Army.Live().Any()) return (0, 0);
                return AttackerWins(g, st, diff, h.Army.Live(), h, os.Army.Live(), null) ? (0, 0) : (-1, 0);
            case ObjectKind.Shipyard:
            case ObjectKind.Sign:
                return (0, 0);
            case ObjectKind.Hero:
            {
                var other = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Pos == o.Pos && x.Owner != h.Owner);
                if (other == null || SameTeam(g, other.Owner, h.Owner)) return (0, 0);
                double enemy = g.ArmyPower(other.Army.Live(), other), mine = g.ArmyPower(h.Army.Live(), h);
                bool human = g.Player(other.Owner)?.Human == true;
                double aggr = human ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef;
                double coward = human ? diff.CowardiceVersusHuman : diff.CowardiceVersusComputer;
                if (enemy * (1 + coward) >= mine * aggr) return (-1, 0);
                return (0, st.ValueOfHero + (long)st.ValueOfHeroLevelPow2 * other.Level * other.Level);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Simulated visit (exe FUN_00bc5480): on a private copy of the game (the exe uses simulation mode and rolls the
    /// world back), with the opponent Q = the object's owner unless allied: before = <see cref="PositionValue"/>; the
    /// hero's move points become current − cost (the day's maximum when the cost is larger); the hero visits the
    /// object; after = the position value again, state at most −1 when the hero died; result = (after state,
    /// after − before).
    /// GAP-details: a battle the visit starts is decided by <see cref="AttackerWins"/> (won: the defenders are gone and
    /// the hero keeps its army; lost: the hero is gone) instead of the exe's simulation-mode battle; a choice is
    /// answered as the AI's turn answers it.
    /// </summary>
    (int, long) SimulatedVisit(Engine.Adventure g, HeroState h, MapObject o, Tile tile, int cost, AdventureDifficulty diff, AdventureAiStats st)
    {
        int q = -1;
        if (o.Kind == ObjectKind.Town) q = g.S.Town(o.Index)?.Owner ?? -1;
        else if (g.S.Objects.TryGetValue(o.Index, out var os0)) q = os0.Owner;
        if (q >= 0 && SameTeam(g, q, h.Owner)) q = -1;
        // The "before" position does not depend on the object: computed once per opponent and search.
        _before ??= new();
        if (!_before.TryGetValue(q, out var before)) _before[q] = before = PositionValue(g, _stats!, diff, h.Owner, q);
        var sim = Simulation(g);
        var sh = sim.S.Hero(h.Id);
        if (sh == null) return (0, 0);
        sh.MovePoints = cost > sh.MovePoints ? sh.MaxMovePoints : sh.MovePoints - cost;
        sh.Pos = tile;
        sh.InTown = -1;
        sh.Garrisoned = false;
        sim.Apply(new MoveHeroCmd(h.Id, tile) { Player = h.Owner });
        for (int guard = 0; guard < 4 && (sim.S.Battle != null || sim.S.Choice != null); guard++)
        {
            if (sim.S.Battle is { } pb)
            {
                var att = sim.S.Hero(pb.AttackerHero);
                var dh = pb.DefenderHero >= 0 ? sim.S.Hero(pb.DefenderHero) : null;
                bool win = att != null && AttackerWins(sim, st, diff, att.Army.Live(), att, pb.DefenderArmy, dh);
                var survivors = win && att != null ? att.Army.ToSlots() : new List<H5.Core.Data.ArmySlot>();
                if (!sim.Apply(new ResolveBattleCmd(win ? 0 : 1, survivors, win ? new List<H5.Core.Data.ArmySlot>() : pb.DefenderArmy.Select(x => new H5.Core.Data.ArmySlot(x.Creature, x.Count)).ToList())
                    { Player = sim.Current.Index }).Ok) break;
            }
            else if (sim.S.Choice is { } c)
                if (!sim.Apply(new AnswerChoiceCmd(Accept: c.Kind != "join_paid" || sim.Current.Purse[Purse.Gold] >= c.Gold * 2) { Player = sim.Current.Index }).Ok) break;
        }
        var after = PositionValue(sim, _stats!, diff, h.Owner, q);
        int s = after.State;
        if (sim.S.Hero(h.Id) == null && s >= -1) s = -1;
        return (s, after.Value - before.Value);
    }

    /// <summary>Attractor priority for the hero (own entry first, then its player's; FUN_00b4d5c0 / player vf 0x1e8).</summary>
    static int AttractorPriority(Engine.Adventure g, HeroState h, MapObject o)
    {
        if (o.Name.Length == 0) return 0;
        int p = AiAttractor.Get(h.AiAttractors, o.Name);
        if (p == 0 && h.Owner >= 0 && g.Player(h.Owner) is { } pl) p = AiAttractor.Get(pl.AiAttractors, o.Name);
        return p;
    }

    /// <summary>No human player in the game has explored the tile (exe FUN_0098dbf0: player vf 0x19c(floor) fog byte).</summary>
    public static bool UnseenByHumans(Engine.Adventure g, Tile t)
    {
        var f = g.Map.Floor(t.Floor);
        if (f == null) return true;
        foreach (var p in g.S.Players)
            if (p.Alive && p.Human && t.Floor < p.Explored.Count && p.Explored[t.Floor][t.Y * f.Width + t.X]) return false;
        return true;
    }

    /// <summary>
    /// Town protection and interception in the evaluator (exe FUN_00a78b60 with the threat table):
    /// A — an own town under threat: among the threatening heroes not answered by another AI hero, that our hero would not
    /// beat in the field, that are not there first unless the town is important (<see cref="ImportantTown"/>), and —
    /// for a town that is not important — that would not win against our hero inside the town, the one with the largest
    /// total threat; then critical = important when that enemy is ≤ 1 day away, state 2 (≤ 1 day) or 0, value +=
    /// ThreatsTownProtectDistanceInDays · threat / (days² + 1).
    /// B — an enemy hero threatening towns that we would beat: its record of largest threat + town total; the
    /// destination moves to the first cell of its path we reach no later than it does (or the last), state 2, value =
    /// hero estimate + threat + value.
    /// GAP-details: the inside-town test is the field estimate with our hero's army; the guarded-path test of
    /// 00a78b60 is our reconstructed guard check.
    /// </summary>
    void Protect(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState h, Dictionary<int, HeroRole> roles, Candidate d, Dictionary<Tile, int> costs)
    {
        var table = Threats(h.Owner);
        if (d.O.Kind == ObjectKind.Town && g.S.Town(d.O.Index) is { } town && town.Owner == h.Owner && table.T.TryGetValue(town.Object, out var eT))
        {
            bool imp = ImportantTown(g, h.MovePoints, h.Owner, town, d.Cost, h.MaxMovePoints, roles);
            double best = 0, bestThr = 0;
            int bestDays = 0;
            ThreatRecord? bestR = null;
            foreach (int hid in eT.Heroes)
            {
                if (!table.H.TryGetValue(hid, out var eH) || eH.Find(town.Object) is not { } r || !eH.Hero.Alive) continue;
                if (r.Defender >= 0 && r.Defender != h.Id) continue;
                if (AttackerWins(g, st, diff, h.Army.Live(), h, eH.Hero.Army.Live(), eH.Hero)) continue;
                int eDays = DaysTo(r.Cost, eH.Hero.MovePoints, eH.Hero.MaxMovePoints);
                int ourDays = DaysTo(d.Cost, h.MovePoints, h.MaxMovePoints);
                if (eDays < ourDays && !imp) continue;
                if (!imp && AttackerWins(g, st, diff, eH.Hero.Army.Live(), eH.Hero, h.Army.Live(), h)) continue;
                if (eH.SumThreat > best) { best = eH.SumThreat; bestThr = r.Threat; bestR = r; bestDays = eDays; }
            }
            if (bestR != null)
            {
                bool crit = bestDays <= 1;
                if (crit) d.Critical = imp;
                d.State = crit ? 2 : 0;
                d.Value = (long)(diff.ThreatsTownProtectDistanceInDays * bestThr / (bestDays * bestDays + 1) + (double)d.Value);
                d.Threat = bestR;
            }
        }
        if (d.O.Kind != ObjectKind.Hero || g.S.Heroes.FirstOrDefault(x => x.Alive && x.Pos == d.O.Pos) is not { } eh || SameTeam(g, eh.Owner, h.Owner)) return;
        if (!table.H.TryGetValue(eh.Id, out var eHe)) return;
        double bv = 0;
        ThreatRecord? br = null;
        bool flag = false;
        foreach (var r in eHe.Records)
        {
            if (r.Defender >= 0 && r.Defender != h.Id) continue;
            if (!table.T.TryGetValue(r.Town, out var t2)) continue;
            if (!AttackerWins(g, st, diff, h.Army.Live(), h, eh.Army.Live(), eh)) continue;
            double v = r.Threat + t2.Total;
            if (v > bv && g.S.Town(r.Town) is { } tw)
            {
                bv = v; br = r;
                flag = ImportantTown(g, h.MovePoints, h.Owner, tw, d.Cost, h.MaxMovePoints, roles);
            }
        }
        if (br == null) return;
        for (int j = 0; j < br.Path.Count; j++)
        {
            var (cell, ecost) = br.Path[j];
            if (!costs.TryGetValue(cell, out int oc)) continue;
            int eDays = DaysTo(ecost, eh.MovePoints, eh.MaxMovePoints), oDays = DaysTo(oc, h.MovePoints, h.MaxMovePoints);
            if ((oDays == 0 && j == 0) || oDays < eDays || j == br.Path.Count - 1)
            {
                d.Tile = cell; d.Cost = oc; d.Critical = flag; d.State = 2;
                d.Value = (long)((double)HeroEstimate(g, st, eh) + br.Threat + (double)d.Value);
                d.Threat = br;
                return;
            }
        }
    }

    /// <summary>
    /// Strategic propagation (exe FUN_00a79700): from every destination a flood up to T · SearchStrategicDistance;
    /// an ownable object not ours there gets the capture bonus value · (T · StrategicCaptureDistance / n + 1); every
    /// other destination the flood reaches inherits merge(state) and the value, damped by 3T / (3T + cost − limit)
    /// beyond the limit.
    /// GAP-details: the capture bonus needs ownable vf 0x38 (n, unidentified) and vf 0x3c: not applied; the flood is
    /// the engine's path cost from the destination with the searching hero's movement; beyond-limit costs do not occur
    /// with our flood (it stops at the limit).
    /// </summary>
    void StrategicPropagation(Engine.Adventure g, HeroState h, AdventureAiStats st, AdventureDifficulty diff, List<Candidate> dests)
    {
        int T = st.SearchAverageTerrainCost, maxCost = unchecked(T * st.SearchStrategicDistance);
        if (dests.Count < 2 || maxCost <= 0) return;
        var byTile = new Dictionary<Tile, List<Candidate>>();
        foreach (var d in dests) { if (!byTile.TryGetValue(d.Tile, out var l)) byTile[d.Tile] = l = new(); l.Add(d); }
        var probe = (HeroState)StateCopy.Copy(h)!;
        var snapshot = dests.Select(d => (d.State, d.Value)).ToList();
        for (int i = 0; i < dests.Count; i++)
        {
            var d = dests[i];
            probe.Pos = d.Tile;
            var (s, v) = snapshot[i];
            foreach (var (cell, c) in g.ReachableCosts(probe, maxCost))
            {
                if (!byTile.TryGetValue(cell, out var hits)) continue;
                long vv = (uint)c > (uint)maxCost ? (long)(3.0 * T / (3.0 * T + (uint)c - maxCost) * v) : v;
                foreach (var e in hits)
                {
                    if (ReferenceEquals(e, d)) continue;
                    e.StratState = MergeState(e.StratState, s);
                    e.StratValue += vv;
                }
            }
        }
    }
}

public sealed partial class OriginalAdventureAI
{
    /// <summary>Test hook: the simulated visit of an object at no move cost.</summary>
    public (int State, long Value) SimulatedVisitForTest(Engine.Adventure g, HeroState h, MapObject o)
    {
        _stats ??= AdventureAiStats.Load(g.Core);
        _before = null;
        return SimulatedVisit(g, h, o, o.ActiveTiles.FirstOrDefault(o.Pos), 0, _stats.Difficulty[Math.Clamp(g.S.Difficulty, 0, 3)], _stats);
    }
}
