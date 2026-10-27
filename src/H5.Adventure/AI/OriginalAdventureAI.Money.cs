using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.AI;

// The "AI money values" (exe FUN_00a325b0): the per-resource weights of the player's resource value (FUN_004ea050,
// CPlayerAIInformation vf 8) and the "average resource value" (vf 0xc). Computed at the AI's day start (phase 0,
// FUN_0086d750 → FUN_00868bb0) and stored by CSetPlayerAIDemandInfoCmd (00427e50 → 00c28440); kept in PlayerState so
// every peer and every simulation copy reads the same numbers. Players that never ran an AI turn keep the defaults.
public sealed partial class OriginalAdventureAI
{
    /// <summary>The weights and the average before the first AI turn (CPlayerAIInformation constructor: weights 1.0).</summary>
    static readonly double[] DefaultWeights = { 1, 1, 1, 1, 1, 1, 1 };
    const double DefaultAverage = (1.0 + 1.0 + 1.0 + 1.0 + 1.0 + 1.0) * (1.0 / 7.0);

    /// <summary>The player's resource weights (player vf 0x28 → CPlayerAIInformation vf 8(i)).</summary>
    static double[] ResourceWeights(Engine.Adventure g, int player) =>
        g.Player(player)?.AiMoneyValues is { Length: 7 } w ? w : DefaultWeights;

    /// <summary>The player's average resource value (CPlayerAIInformation vf 0xc).</summary>
    static double AverageResourceValue(Engine.Adventure g, int player) =>
        g.Player(player)?.AiMoneyAverage ?? DefaultAverage;

    /// <summary>Phase 0 (00868bb0): compute the money values and store them as the player's AI demand info.</summary>
    public static void SetMoneyValues(Engine.Adventure g, AdventureAiStats st, int player)
    {
        if (g.Player(player) is not { } p) return;
        var m = MoneyValues(g, st, player);
        p.AiMoneyValues = m.Values;
        p.AiMoneyAverage = m.Average;
    }

    /// <summary>
    /// FUN_00a325b0. v = 0 (7 doubles), demand = 0 (7 ints), then
    /// (1) creatures (FUN_00a31eb0), per own town: every dwelling offer with stock n (town creature source vf 0) adds
    ///     <see cref="AddCreature"/>(its creatures, n, 1.0); then every built dwelling's weekly growth (vf 0x10, the 8
    ///     growth parts summed, ids = the offer's) with factor (day % 7 + 1) · (1/7);
    /// (2) buildings (FUN_00a32230), per own town with creature potential P &gt; 0 (FUN_00a32130: Σ over the town's
    ///     dwelling list of the best creature's value × its WeeklyGrowth): T = the cost of every level of every building
    ///     of the town (FUN_00a32090); each building whose check against 1 000 000 of everything (town vf 0xac) gives
    ///     can-build (0), prerequisite (4) or town points (10): with c = the cost of its next level (00cd4180),
    ///     for each resource with c &gt; 0 and T &gt; 0: x = P / T; for 4 / 10 x = (Mx − need) / Mx · x · WeakMoneyForDeniedBuildings
    ///     (Mx = Σ maximum levels, town vf 0x7c; need = the next level's town points, vf 0x58) and
    ///     c = trunc(c · Weak + 0.5); v = max(v, x), the town's demand part = max over its buildings of c; demand += it;
    /// (3) supply = purse + daily income × SupplyDayIncomes; where demand &lt; supply: v ·= demand / supply (FUN_00a31c20);
    /// (4) average = (v0 + … + v5) · (1/7) (FUN_00a31bb0, gold left out); (5) v = max(1.0, v) (FUN_00a31d10).
    /// GAP-detail: the town's dwelling list behind P (town +0x154, read by creature source vf 4 = 00cd4860) is taken as
    /// every dwelling tier of the town type with its base and upgraded creatures; who fills it is not traced.
    /// </summary>
    public static (double[] Values, double Average, int[] Demand, int[] Supply) MoneyValues(Engine.Adventure g, AdventureAiStats st, int player)
    {
        var v = new double[7];
        var demand = new int[7];
        var p = g.Player(player);
        var towns = g.S.Towns.Where(t => t.Owner == player).ToList();

        // (1) FUN_00a31eb0
        double week = (double)g.S.DayOfWeek * (1.0 / 7.0);
        foreach (var t in towns)
        {
            for (int tier = 1; tier <= 7; tier++) AddCreature(g, v, demand, OfferIds(g, t, tier), t.Available[tier - 1], 1.0);
            for (int tier = 1; tier <= 7; tier++)
                if (t.Level($"TB_DWELLING_{tier}") > 0) AddCreature(g, v, demand, OfferIds(g, t, tier), g.WeeklyGrowthOf(t, tier), week);
        }

        // (2) FUN_00a32230
        var rich = new Purse { R = Enumerable.Repeat(1000000, 7).ToArray() };
        double weak = (double)(float)st.WeakMoneyForDeniedBuildings;
        foreach (var t in towns)
        {
            var maxc = new int[7];
            var total = new int[7];
            foreach (var key in t.MaxLevel.Keys)
                for (int l = 1; l <= g.Db.MaxLevel(t.Type, key); l++)
                    if (g.Db.Building(t.Type, key, l) is { } d) Add(total, new Purse(d.Cost).R);
            double pot = CreaturePotential(g, t);
            if (!(pot > 0.0)) continue;
            foreach (var key in t.MaxLevel.Keys)
            {
                var r = g.BuildCheck(t, key, rich);
                if (r is not (BuildDenial.CanBuild or BuildDenial.Prereq or BuildDenial.TownPoints)) continue;
                var cost = NextLevelCost(g, t, key);
                for (int i = 0; i < 7; i++)
                {
                    int c = cost[i];
                    if (c <= 0 || total[i] <= 0) continue;
                    double x = pot / total[i];
                    if (r != BuildDenial.CanBuild)
                    {
                        int need = PointsNeeded(g, t, key), mx = MaxTownPoints(g, t);
                        x = (double)(mx - need) / mx * x * weak;
                        c = (int)(c * weak + 0.5);
                    }
                    if (v[i] <= x) v[i] = x;
                    if (maxc[i] <= c) maxc[i] = c;
                }
            }
            Add(demand, maxc);
        }

        // (3) supply = income × SupplyDayIncomes + purse
        var supply = new int[7];
        if (p != null)
        {
            var inc = g.ExpectedIncome(p);
            for (int i = 0; i < 7; i++) supply[i] = unchecked(inc[i] * st.SupplyDayIncomes + p.Purse[i]);
        }
        for (int i = 0; i < 7; i++)
            if (demand[i] < supply[i]) v[i] = (double)demand[i] / supply[i] * v[i];
        double avg = (v[0] + v[1] + v[2] + v[3] + v[4] + v[5]) * (1.0 / 7.0);
        for (int i = 0; i < 7; i++) if (!(1.0 < v[i])) v[i] = 1.0;
        return (v, avg, demand, supply);
    }

    static void Add(int[] acc, int[] x) { for (int i = 0; i < 7; i++) acc[i] = unchecked(acc[i] + x[i]); }

    /// <summary>
    /// FUN_00a31a10: with n &gt; 0, the creature of the list with the highest value (FUN_00a31960: ftol(max(Power, 1) ·
    /// 100), strict &gt;, first on ties) and its cost c: per resource with c &gt; 0, v = max(v, value · factor / c) and
    /// demand += c · n.
    /// </summary>
    static void AddCreature(Engine.Adventure g, double[] v, int[] demand, IReadOnlyList<string> ids, int n, double factor)
    {
        if (n <= 0 || BestCreature(g, ids) is not { } best) return;
        var cost = new Purse(best.Def.Cost).R;
        for (int i = 0; i < 7; i++)
        {
            if (cost[i] <= 0) continue;
            double x = best.Value * factor / cost[i];
            if (!(x < v[i])) v[i] = x;
            demand[i] = unchecked(demand[i] + cost[i] * n);
        }
    }

    static (H5.Core.Data.CreatureDef Def, double Value)? BestCreature(Engine.Adventure g, IReadOnlyList<string> ids)
    {
        (H5.Core.Data.CreatureDef, double)? best = null;
        foreach (var id in ids)
        {
            if (!g.Core.Creatures.TryGetValue(id, out var c)) continue;
            double val = (long)(Math.Max((double)c.Power, 1.0) * 100.0);
            if (best == null || best.Value.Item2 < val) best = (c, val);
        }
        return best;
    }

    /// <summary>The creatures a town dwelling offers (built variants), as the recruit offers list them.</summary>
    static List<string> OfferIds(Engine.Adventure g, TownState t, int tier)
    {
        var ids = new List<string>();
        for (int v = 0; v <= 2; v++) if (g.DwellingCreature(t, tier, v) is { } c && !ids.Contains(c)) ids.Add(c);
        return ids;
    }

    /// <summary>FUN_00a32130: Σ over the town's dwellings of the best creature's value × its WeeklyGrowth (creature +0xa8).</summary>
    static double CreaturePotential(Engine.Adventure g, TownState t)
    {
        double pot = 0.0;
        for (int tier = 1; tier <= 7; tier++)
        {
            var ids = new List<string>();
            string key = $"TB_DWELLING_{tier}";
            if (g.Db.Building(t.Type, key, 1)?.Creature is { } c0) ids.Add(c0);
            if (g.Db.Building(t.Type, key, 2) is { } up)
            {
                if (up.Creature is { } c1 && !ids.Contains(c1)) ids.Add(c1);
                if (up.Creature2 is { } c2 && !ids.Contains(c2)) ids.Add(c2);
            }
            if (BestCreature(g, ids) is { } b) pot = (double)b.Def.WeeklyGrowth * b.Value + pot;
        }
        return pot;
    }

    /// <summary>00cd4180(rec + 0x10, 0): the cost of level min(current, max − 1) + 1.</summary>
    static int[] NextLevelCost(Engine.Adventure g, TownState t, string key)
    {
        int max = g.Db.MaxLevel(t.Type, key);
        int idx = Math.Max(0, Math.Min(t.Level(key), max - 1));
        return g.Db.Building(t.Type, key, idx + 1) is { } d ? new Purse(d.Cost).R : new int[7];
    }

    /// <summary>Town vf 0x58 (00cd14c0): the town points the next level needs; town points + 1 without a record.</summary>
    static int PointsNeeded(Engine.Adventure g, TownState t, string key) =>
        g.Db.Building(t.Type, key, t.Level(key) + 1)?.DevLevelNeeded ?? t.DevLevel + 1;

    /// <summary>Town vf 0x7c (00cd2170): Σ of the maximum levels of the town's buildings.</summary>
    static int MaxTownPoints(Engine.Adventure g, TownState t) => t.MaxLevel.Keys.Sum(k => g.Db.MaxLevel(t.Type, k));
}
