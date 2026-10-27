using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// Creature purchase (exe FUN_00cde4c0 with FUN_00cdd630): one greedy routine for a hero in an own town (009c4710), a
// hero at an external dwelling (004e6d30 case 5) and a threatened own town without a garrison hero (phase 6,
// CAIPurchaseCreaturesInTown 00428b40, money ignored). TownCreaturesReserveCoef (+0x280) has no reader in the exe.
public sealed partial class OriginalAdventureAI
{
    /// <summary>
    /// Greedy purchase (FUN_00cde4c0): each round every offer with creatures is bought on a copy of the game (the whole
    /// affordable amount, FUN_00cdd630) and scored Δ = (V1 − V0) + M1 − M0, V = the buyer hero's estimate (FUN_004eb050)
    /// or, without a hero, the army value of the target, M = the resource value of the purse (0 when money is ignored);
    /// the best Δ &gt; 0 (strict, the lower offer on ties) is bought for real; repeat until nothing gains.
    /// GAP-details: the market exchange to afford a purchase (FUN_008e70d0) and the stack offload / final arrangement
    /// (FUN_00cdcfd0, FUN_00cddfd0) are not ported.
    /// </summary>
    public void Purchase(Engine.Adventure g, AdventureAiStats st, int buyer, HeroState? hero, bool ignoreMoney)
    {
        int me = g.Current.Index;
        for (int round = 0; round < 16; round++)
        {
            long v0 = PurchaseValue(g, st, buyer, hero);
            double m0 = ignoreMoney ? 0.0 : ResourceValue(g, me, g.Current.Purse);
            double best = 0.0;
            RecruitCmd? bestCmd = null;
            foreach (var cmd in PurchaseOffers(g, buyer, hero))
            {
                var sim = Simulation(g);
                if (!sim.Apply(cmd).Ok) continue;
                var sh = hero == null ? null : sim.S.Hero(hero.Id);
                double d = (double)PurchaseValue(sim, st, buyer, sh) - v0 + (ignoreMoney ? 0.0 : ResourceValue(sim, me, sim.Current.Purse)) - m0;
                if (d > best) { best = d; bestCmd = cmd; }
            }
            if (bestCmd == null || !Send(g, bestCmd).Ok) break;
        }
    }

    long PurchaseValue(Engine.Adventure g, AdventureAiStats st, int buyer, HeroState? hero)
    {
        if (hero != null) return HeroEstimate(g, st, hero);
        return g.S.Town(buyer) is { } t ? ArmyValue(g, st, t.Garrison, null) : 0;
    }

    /// <summary>
    /// The purchase of one offer (FUN_00cdd630): the whole available count, cut to what the purse affords; a dwelling
    /// with one creature buys it, with two the upgrade; with three (base and two upgrades) FUN_00cdbcc0 picks
    /// the upgrade the army has fewer than half as many of the other (c1 &gt; 0, c2 ≤ c1 / 2 → 1; c2 &gt; 0, c1 ≤ c2 / 2 → 2).
    /// GAP-detail: FUN_00cdbcc0's per-creature rules (and the 0x29 / 0x41 base rule) are not decoded; the first
    /// upgrade is taken otherwise.
    /// </summary>
    IEnumerable<RecruitCmd> PurchaseOffers(Engine.Adventure g, int buyer, HeroState? hero)
    {
        var p = g.Current;
        var army = hero?.Army ?? g.S.Town(buyer)?.Garrison ?? Army.Empty();
        if (g.S.Town(buyer) is { } t)
        {
            for (int tier = 1; tier <= 7; tier++)
            {
                int n = t.Available[tier - 1];
                if (n <= 0) continue;
                var ids = new List<string>();
                for (int v = 0; v <= 2; v++) if (g.DwellingCreature(t, tier, v) is { } c && !ids.Contains(c)) ids.Add(c);
                int idx = ids.Count switch { 0 => -1, 1 => 0, 2 => 1, _ => UpgradeChoice(army, ids[1], ids[2]) };
                if (idx < 0) continue;
                int variant = idx;
                if (Count(g, p, ids[idx], n) is int k and > 0) yield return new RecruitCmd(t.Object, tier, k, variant) { Player = p.Index };
            }
        }
        else if (g.S.Objects.TryGetValue(buyer, out var os) && g.Map.Objects[buyer] is { Kind: ObjectKind.Dwelling } o)
        {
            for (int i = 0; i < o.Shared.DwellingCreatures.Count && i < os.Available.Length; i++)
                if (os.Available[i] > 0 && Count(g, p, o.Shared.DwellingCreatures[i], os.Available[i]) is int k and > 0)
                    yield return new RecruitCmd(buyer, i + 1, k) { Player = p.Index };
        }
    }

    static int UpgradeChoice(Stack?[] army, string up1, string up2)
    {
        int c1 = army.Live().Where(s => s.Creature == up1).Sum(s => s.Count), c2 = army.Live().Where(s => s.Creature == up2).Sum(s => s.Count);
        if (c1 > 0 && c2 <= c1 / 2) return 1;
        if (c2 > 0 && c1 <= c2 / 2) return 2;
        return 1;
    }

    static int? Count(Engine.Adventure g, PlayerState p, string creature, int available)
    {
        if (!g.Core.Creatures.TryGetValue(creature, out var c)) return null;
        float a = Affordable(p.Purse, new Purse(c.Cost));
        int k = a >= int.MaxValue ? available : (int)MathF.Floor(a);
        return Math.Min(available, k);
    }

    /// <summary>A private copy of the game to try a command on (see <see cref="StateCopy"/>): no scripts, no observers.</summary>
    static Engine.Adventure Simulation(Engine.Adventure g) => g.Simulation(StateCopy.Of(g.S));
}
