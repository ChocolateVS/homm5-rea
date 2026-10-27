using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.AI;

// The AI's market exchange to afford a cost (exe FUN_008e70d0, "AI exchanges N X for M Y to fit cost"), used by the
// town build (FUN_009c5650, no money) and every creature purchase (FUN_00cdd630). With no marketplace it first builds
// marketplaces (FUN_008e6d60, "AI builded marketplace in town"). The exe applies the result as seven set-resource
// commands (FUN_005d5440, executed at once by FUN_00b0f550); here the purse is set the same way.
public sealed partial class OriginalAdventureAI
{
    /// <summary>
    /// FUN_008e70d0: have = the purse; already affordable (FUN_009d0500 ≥ 1) → true. Without marketplaces: count the
    /// marketplaces the purse can pay for (<see cref="BuildMarketplaces"/>, nothing built); none → false; else their
    /// cost is taken off <c>have</c>. Then for each resource r in order with a deficit d = cost − have: among the other
    /// resources s with a surplus x = have − cost &gt; 0 and a rate q = MarketRates(s, markets)[r] &gt; 0, give
    /// (d − 1) / q + 1 (gold) or q · d (else) — at most x (non-gold rounded down to whole lots, none → skip, and r is
    /// tried again afterwards) — for give · q (gold) or give / q; the source with the highest
    /// weight(s) · max(0, supply − demand)[s] · 0.5 (the AI money values of the day; strict &gt;, the first on ties)
    /// is taken; no source → false. Marketplaces counted before are then built for real (the count must agree, else
    /// false) and the purse is set to <c>have</c>.
    /// GAP-details: the rates are halved when the world's vf 0xb0 is 0x34 (FUN_00bcdf70), not identified, not applied;
    /// the exe's two resource values of each trial (004ea050 ×4) are computed and unused, as here.
    /// </summary>
    public static bool MarketTrade(Engine.Adventure g, int player, int[] cost)
    {
        if (g.Player(player) is not { } p) return false;
        var have = (int[])p.Purse.R.Clone();
        if (!(Affordable(new Purse { R = have }, new Purse { R = cost }) < 1.0f)) return true;
        int markets = g.MarketplaceCount(p), built = 0;
        if (markets <= 0)
        {
            var acc = new int[7];
            built = BuildMarketplaces(g, p, acc, build: false);
            if (built <= 0) return false;
            markets += built;
            for (int i = 0; i < 7; i++) have[i] = unchecked(have[i] - acc[i]);
        }
        var surplus = new int[7];
        for (int i = 0; i < 7; i++) surplus[i] = Math.Max(0, unchecked((p.AiSupply?[i] ?? 0) - (p.AiDemand?[i] ?? 0)));
        var w = ResourceWeights(g, player);
        for (int to = 0, guard = 0; to < 7 && guard < 256; to++, guard++)
        {
            int d = cost[to] - have[to];
            if (d <= 0) continue;
            int best = -1, bestGive = 0, bestGet = 0;
            bool bestPartial = false;
            double bestScore = 0.0;
            for (int src = 0; src < 7; src++)
            {
                if (src == to) continue;
                int x = have[src] - cost[src];
                if (x <= 0) continue;
                int q = Engine.Adventure.MarketRates(src, markets)[to];
                if (q <= 0) continue;
                bool partial = false;
                int give = to == Purse.Gold ? (d - 1) / q + 1 : unchecked(q * d);
                if (give > x)
                {
                    if (to != Purse.Gold) x = x / q * q;
                    if (x < 1) continue;
                    partial = true;
                    give = x;
                }
                int get = to == Purse.Gold ? unchecked(give * q) : give / q;
                double score = w[src] * surplus[src] * 0.5;
                if (best == -1 || score > bestScore)
                {
                    bestScore = score; best = src; bestGive = give; bestGet = get; bestPartial = partial;
                }
            }
            if (best == -1) return false;
            have[to] += bestGet;
            have[best] -= bestGive;
            if (bestPartial) to--;
        }
        if (built > 0 && BuildMarketplaces(g, p, new int[7], build: true) != built) return false;
        SetPurse(g, p, have);
        return true;
    }

    /// <summary>
    /// FUN_008e6d60: none when supply − demand of wood is negative and there is no wood income. Otherwise, over the
    /// player's towns without a marketplace whose marketplace can be built now (town vf 0xac with the purse = 0): the
    /// running cost <paramref name="acc"/> plus this one must still be affordable, else stop; count it, and with
    /// <paramref name="build"/> build it (town vf 0x6c). Returns the count.
    /// GAP-detail: whether vf 0x6c(…, 0, 1) charges the cost is not traced; the engine's build charges it, so a second
    /// marketplace is checked against the reduced purse as the exe's real pass would if it does.
    /// </summary>
    static int BuildMarketplaces(Engine.Adventure g, PlayerState p, int[] acc, bool build)
    {
        int wood = unchecked((p.AiSupply?[Purse.Wood] ?? 0) - (p.AiDemand?[Purse.Wood] ?? 0));
        if (wood < 0 && g.ExpectedIncome(p)[Purse.Wood] < 1) return 0;
        Array.Clear(acc);
        int n = 0;
        foreach (var t in g.S.Towns.Where(t => t.Owner == p.Index).ToList())
        {
            if (t.Level("TB_MARKETPLACE") >= 1) continue;
            if (g.BuildCheck(t, "TB_MARKETPLACE", p.Purse) != BuildDenial.CanBuild) continue;
            var total = new int[7];
            var c = NextLevelCost(g, t, "TB_MARKETPLACE");
            for (int i = 0; i < 7; i++) total[i] = unchecked(acc[i] + c[i]);
            if (Affordable(p.Purse, new Purse { R = total }) < 1.0f) return n;
            Array.Copy(total, acc, 7);
            n++;
            if (build) Send(g, new BuildCmd(t.Object, "TB_MARKETPLACE") { Player = p.Index });
        }
        return n;
    }

    /// <summary>The seven set-resource commands (FUN_005d5440): the purse becomes <paramref name="have"/>.</summary>
    static void SetPurse(Engine.Adventure g, PlayerState p, int[] have)
    {
        var delta = new int[7];
        bool any = false;
        for (int i = 0; i < 7; i++) { delta[i] = have[i] - p.Purse[i]; any |= delta[i] != 0; }
        if (!any) return;
        for (int i = 0; i < 7; i++) g.ScriptGive(p.Index, i, delta[i]);
    }
}
