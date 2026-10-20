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
    public IBattleAI BattleAI { get; init; } = new OriginalBattleAI { MaxCandidates = 24 };

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
        var roles = AssignRoles(g, all);
        var claimed = new HashSet<int>();
        var done = new HashSet<int>();
        MoveHeroes(g, battleSeedBase, me, diff, st, roles, claimed, done, ref actions);
        if (HireHeroes(g, towns, diff))
            MoveHeroes(g, battleSeedBase, me, diff, st, roles, claimed, done, ref actions);
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
            var h = NextHero(g, me, done);
            if (h == null) break;
            done.Add(h.Id);
            if (!roles.ContainsKey(h.Id)) roles[h.Id] = HeroRole.Collector;
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
                ResolvePending(g, battleSeedBase + (ulong)actions);
                LevelUps(g, h, st);
                // BlockTownGarrisonForAI: no creature exchange with that garrison (exe reader 0x008687a0).
                if (h.InTown >= 0 && g.S.Town(h.InTown) is { } home && home.Owner == me && !home.AiGarrisonBlocked) TakeGarrison(g, h, home);
                if (h.MovePoints < 100) break;
            }
        }
    }

    // ---------------------------------------------------------------- roles

    /// <summary>
    /// Role per hero: the strongest army commands; when there are three or more heroes the weakest supplies
    /// troops from towns; the rest collect. (Role ids and spread multipliers are the exe's; the assignment
    /// rule itself is reconstructed.)
    /// </summary>
    static Dictionary<int, HeroRole> AssignRoles(Engine.Adventure g, List<HeroState> heroes)
    {
        var roles = new Dictionary<int, HeroRole>();
        var byPower = heroes.OrderByDescending(h => g.ArmyPower(h.Army.Live(), h)).ThenBy(h => h.Id).ToList();
        for (int i = 0; i < byPower.Count; i++)
            roles[byPower[i].Id] = i == 0 ? HeroRole.Commander
                : byPower.Count >= 3 && i == byPower.Count - 1 ? HeroRole.Supplier
                : HeroRole.Collector;
        return roles;
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
            if (!x.Alive || x.Owner != me || done.Contains(x.Id)) continue;
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

    readonly record struct Destination(int Object, Tile Tile, double Score);

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

    Destination? ChooseDestination(Engine.Adventure g, HeroState h, HeroRole role, AdventureDifficulty diff, AdventureAiStats st, HashSet<int> claimed)
    {
        double myPower = g.ArmyPower(h.Army.Live(), h);
        // GAP: the exe's search limit is the wave spread of FUN_0086c010 ("Wave spread: search limit = %d"),
        // starting at 1500 and widening through RolesSearchNearMaxCost / RolesSearchFarMaxCost; the role split
        // below is reconstructed.
        int maxCost = role == HeroRole.Commander ? diff.RolesSearchFarMaxCost : Math.Max(diff.RolesSearchNearMaxCost, h.MaxMovePoints * 3);
        var dangers = diff.CheckHeroesDanger ? EnemyDangers(g, h, st) : new List<(Tile Pos, int Reach, double Power)>();
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
            int d = Math.Max(Math.Abs(tile.X - h.Pos.X), Math.Abs(tile.Y - h.Pos.Y));
            if (d == 0) continue;
            // GAP: the exe's cost is the pathfinder's move cost to the destination; approximated here.
            int cost = d * st.SearchAverageTerrainCost;
            if (cost > maxCost && state == 0) continue;
            if (state == 0)
            {
                // Guards on the way: never walk into a stack we would lose to (reconstructed).
                if (o.Kind != ObjectKind.Monster && g.GuardingMonster(tile) is { } guard &&
                    g.S.Objects.TryGetValue(guard.Index, out var gs) && g.ArmyPower(gs.Guards, null) > myPower * (1 - diff.CowardiceVersusComputer)) continue;
                // Danger zones of stronger enemy heroes (FUN_00872330 reach; the flood fill is approximated).
                if (dangers.Any(z => z.Power > myPower && Math.Max(Math.Abs(z.Pos.X - tile.X), Math.Abs(z.Pos.Y - tile.Y)) * st.SearchAverageTerrainCost <= z.Reach)) continue;
            }
            // GAP-detail: whether state-3 destinations skip the danger checks is not traced (they do here).
            long v = (long)value;
            v = hasLast && last == tile
                ? OldTargetValue(v, (float)st.SearchOldTargetMultiplier)
                : RandomizedValue(v, rng.Range(1, Math.Max(1, st.SearchBestPositionRandomFactor)), st.SearchBestPositionRandomFactor);
            v = DiscountedValue(state, v, cost, st.SearchAverageTerrainCost);
            if (best == null || Better(state, v, bestState, bestValue))
            {
                best = new Destination(o.Index, tile, v);
                bestState = state;
                bestValue = v;
            }
        }
        if (best == null || g.FindPath(h, best.Value.Tile) == null) return SeaTravel(g, h, myPower, diff, st, claimed);
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
                if (os == null) return 0;
                double enemy = g.ArmyPower(os.Guards, null);
                if (enemy * (1 + diff.CowardiceVersusComputer) >= myPower * diff.AttackComputerAggressivityCoef) return 0;
                return enemy * 0.5;
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

    /// <summary>Enemy heroes with their danger reach (exe FUN_00872330: movePoints·mult + bonus) and army power.</summary>
    static List<(Tile Pos, int Reach, double Power)> EnemyDangers(Engine.Adventure g, HeroState h, AdventureAiStats st)
    {
        var team = g.S.Players[h.Owner].Team;
        return g.S.Heroes.Where(x => x.Alive && x.Owner >= 0 && x.Owner != h.Owner && g.S.Players[x.Owner].Team != team && x.Pos.Floor == h.Pos.Floor)
            .Select(x => (x.Pos, (int)Math.Round(x.MaxMovePoints * st.SearchDangerEnemyMobilityMultiplier + st.SearchDangerEnemyMobilityBonus),
                          g.ArmyPower(x.Army.Live(), x)))
            .ToList();
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
    ///     when the hire cost exceeds the gold (FUN_00b4ced0 vs resources +0x18);
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
            if (heroes > 0 && Engine.Adventure.HeroHireCost > me.Purse[Purse.Gold]) continue;
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

    /// <summary>Takes the level-up option with the highest ValueOfSkillPerMastery / ValueOfPerkFeat value.</summary>
    static void LevelUps(Engine.Adventure g, HeroState h, AdventureAiStats st)
    {
        for (int guard = 0; guard < 16 && h.Alive && h.PendingLevelUps.Count > 0; guard++)
        {
            var offer = h.PendingLevelUps[0];
            int n = offer.Skills.Count + offer.Perks.Count;
            int bestOpt = 0;
            double bestVal = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double v = i < offer.Skills.Count ? st.ValueOfSkillPerMastery * (int)offer.Skills[i].Mastery : st.ValueOfPerkFeat;
                if (v > bestVal) { bestVal = v; bestOpt = i; }
            }
            if (n == 0 || !Send(g, new ChooseLevelUpCmd(h.Id, bestOpt) { Player = h.Owner }).Ok) break;
        }
    }

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
