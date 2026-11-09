using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.AI;

// Caravan purchases of the AI (exe phase 14 of the CPlayerAI state machine, FUN_0086a350; after phase 13, before 8).
public sealed partial class OriginalAdventureAI
{
    /// <summary>
    /// FUN_0086a350. For each own town (player vf 0xc4, list order) its caravan sources (00ce6110, <see
    /// cref="Engine.Adventure.CaravanSources"/>) are scanned; only map dwellings take part (source vf 4 flagged part
    /// and vf 0x18 dwelling part; a town source has no vf 0x18). A source is skipped when one of the player's heroes
    /// (player vf 0xc0) has its AI target record (hero vf 0x2ac) on an entrance tile of the source (00cea190: object
    /// position + vf 0xb8 offsets) with the record's cost (+0x10) below twice the hero's daily movement (vf 0xfc).
    /// Every stock entry of the dwelling (vf 0x40 → vf 0, 16-byte entries: count &gt; 0 and a creature, the entry's last)
    /// gives a candidate (town, source, creature, days) with score = ftol(max(Power, 1) · 100) (004e9b30) · count ·
    /// (1.0 when the creature's town (+0x98) is the town's race (vf 0xd4), else 0.5: 00e12e00 / 00e0f470) / max(days, 1).
    /// The candidates are sorted (0086f7a0) and a later candidate with the same source and creature dropped. The
    /// budget is the purse (vf 0xd8) minus the AI demand (vf 0x28 → CPlayerAIInformation vf 0 = +8, our
    /// <c>AiDemand</c>), clamped at 0 (009d0770); in order each candidate takes the dwelling's whole current stock (vf 0xc)
    /// or, when the budget does not cover it (009d0720), floor of the budget-to-unit-cost ratio (009d0500: the smallest
    /// ratio over the costed resources); a count &gt; 0 queues CPurchaseCreaturesToCaravan (00435920 → 00428f20) and
    /// takes the cost off the budget (009d0b90). Then the phase moves on to 8.
    /// UNVERIFIED-details: the sort is taken as descending by score and stable (the comparator of 00a3efe0 is not
    /// read); the hero record as our remembered target tile and cost; 009d0500's divisor as the unit cost.
    /// GAP-detail: the danger skip (vf 0x28 → vf 0x70(source position) = 00c28550, a tile-keyed table at
    /// CPlayerAIInformation whose writer is not traced; &gt; 1 skips the source) is not modelled.
    /// </summary>
    public void CaravanPurchases(Engine.Adventure g, int me)
    {
        if (g.Player(me) is not { } p) return;
        var cands = new List<(TownState Town, int Source, string Creature, int Days, double Score)>();
        foreach (var t in g.S.Towns.Where(t => t.Owner == me).ToList())
        {
            foreach (var src in g.CaravanSources(t))
            {
                if (src.IsTown || !g.S.Objects.TryGetValue(src.Object, out var os)) continue;
                var o = g.Map.Objects[src.Object];
                if (o.Kind != ObjectKind.Dwelling) continue;
                var entrances = o.ActiveTiles.ToList();
                bool heroComing = g.S.Heroes.Any(h => h.Alive && h.Owner == me
                    && _lastTarget.TryGetValue(h.Id, out var tile) && entrances.Contains(tile)
                    && _lastCost.TryGetValue(h.Id, out int cost) && cost < h.MaxMovePoints * 2);
                if (heroComing) continue;
                var list = o.Shared.DwellingCreatures;
                foreach (int row in g.DwellingRows(src.Object))
                {
                    if (row >= list.Count || row >= os.Available.Length || os.Available[row] <= 0) continue;
                    if (!g.Core.Creatures.TryGetValue(list[row], out var cd)) continue;
                    double factor = cd.Town == t.Type ? 1.0 : 0.5;
                    long value = (long)(Math.Max((double)cd.Power, 1.0) * 100.0);
                    double score = (double)(value * os.Available[row]) * factor / Math.Max(src.Days, 1);
                    cands.Add((t, src.Object, list[row], src.Days, score));
                }
            }
        }
        var order = cands.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Score).ThenBy(x => x.i).Select(x => x.c).ToList();
        for (int i = 0; i < order.Count; i++)
            for (int j = order.Count - 1; j > i; j--)
                if (order[j].Source == order[i].Source && order[j].Creature == order[i].Creature) order.RemoveAt(j);

        var budget = (int[])p.Purse.R.Clone();
        if (p.AiDemand is { } demand) for (int i = 0; i < 7; i++) budget[i] -= demand[i];
        for (int i = 0; i < 7; i++) budget[i] = Math.Max(budget[i], 0);
        foreach (var c in order)
        {
            if (!g.S.Objects.TryGetValue(c.Source, out var os) || !g.Core.Creatures.TryGetValue(c.Creature, out var cd)) continue;
            var list = g.Map.Objects[c.Source].Shared.DwellingCreatures;
            int row = g.DwellingRows(c.Source).FirstOrDefault(r => r < list.Count && list[r] == c.Creature, -1);
            if (row < 0 || row >= os.Available.Length) continue;
            int n = os.Available[row];
            var unit = new Purse(cd.Cost).R;
            if (!Covers(budget, unit, n)) n = (int)Math.Floor(Ratio(budget, unit));
            if (n <= 0) continue;
            Send(g, new BuyToCaravanCmd(c.Source, c.Town.Object, c.Creature, n) { Player = me });
            for (int i = 0; i < 7; i++) budget[i] -= unit[i] * n;
        }
    }

    static bool Covers(int[] budget, int[] unit, int n)
    {
        for (int i = 0; i < 7; i++) if (budget[i] < unit[i] * n) return false;
        return true;
    }

    /// <summary>009d0500: the smallest budget / cost (single precision) over the resources with a cost, negatives as 0.</summary>
    static float Ratio(int[] budget, int[] cost)
    {
        float r = float.MaxValue;
        for (int i = 0; i < 7; i++)
        {
            if (cost[i] == 0) continue;
            float x = (float)budget[i] / cost[i];
            if (x < 0f) x = 0f;
            if (x < r) r = x;
        }
        return r == float.MaxValue ? 0f : r;
    }
}
