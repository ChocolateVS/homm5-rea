using H5.Adventure.Engine;
using H5.Adventure.State;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// Hero-to-hero army exchange of the AI (exe FUN_00c5ad90 with the greedy merge FUN_00cde140 / FUN_00cdd180, the war
// machine hand-over FUN_00c5acb0 and the role update FUN_00c5ab30) and the supplier test FUN_00c5af30 built on it.
public sealed partial class OriginalAdventureAI
{
    /// <summary>
    /// Transfer rule DAT_00f7a6f0[role(a) · 4 + role(b)] (roles 0 free, 1 commander, 2 collector, 3 supplier):
    /// 1 a takes; 2 b takes; 3 b takes when attr(a) &lt; attr(b), else a; 4 a takes unless attr(a) &lt; attr(b) (then
    /// nothing); 5 nothing when attr(b) &lt; attr(a), else b takes; 6 a takes only when b's partner is a; 7 b takes only
    /// when a's partner is b; 0 nothing. attr = the hero attribute value (FUN_004e9f60).
    /// </summary>
    static readonly int[] TransferRule = { 3, 5, 2, 2, 4, 0, 6, 6, 1, 7, 0, 0, 1, 7, 0, 0 };

    static int RoleNumber(HeroRole r) => r switch { HeroRole.Commander => 1, HeroRole.Collector => 2, HeroRole.Supplier => 3, _ => 0 };

    HeroRole RoleOf(HeroState h) => _role.GetValueOrDefault(h.Id, HeroRole.Freelancer);

    /// <summary>A hero's remembered role (hero vf 0x2bc).</summary>
    public HeroRole Role(int hero) => _role.GetValueOrDefault(hero, HeroRole.Freelancer);

    /// <summary>Sets a hero's remembered role and partner (hero vf 0x2c4), as the role phase does.</summary>
    public void SetRole(int hero, HeroRole role, int partner = -1)
    {
        _role[hero] = role;
        if (partner >= 0) _partner[hero] = partner; else _partner.Remove(hero);
    }

    /// <summary>FUN_00c5ad90: who takes from whom between two own heroes (null = no exchange).</summary>
    public (HeroState Taker, HeroState Giver)? TransferSides(Engine.Adventure g, AdventureAiStats st, HeroState a, HeroState b)
    {
        if (a.Id == b.Id) return null;
        int rule = TransferRule[RoleNumber(RoleOf(a)) * 4 + RoleNumber(RoleOf(b))];
        long A() => HeroAttributeValue(g, st, a);
        long B() => HeroAttributeValue(g, st, b);
        return rule switch
        {
            1 => (a, b),
            2 => (b, a),
            3 => A() < B() ? (b, a) : (a, b),
            4 => A() < B() ? null : (a, b),
            5 => B() < A() ? null : (b, a),
            6 => _partner.GetValueOrDefault(b.Id, -1) == a.Id ? (a, b) : null,
            7 => _partner.GetValueOrDefault(a.Id, -1) == b.Id ? (b, a) : null,
            _ => null,
        };
    }

    /// <summary>
    /// FUN_00cde140 (hero to hero): first the offload FUN_00cdcfd0; then repeat — V0 = the taker's estimate
    /// (FUN_004eb050); every giver stack is tried (FUN_00cdd180, slot by FUN_00cdacd0 — see <see cref="PlanMove"/>) and
    /// undone; the stack with the largest gain V1 − V0 &gt; 0 (strict, the first on ties) is moved for good; stop when
    /// none gains; last the taker's arrangement FUN_00cddfd0. <paramref name="real"/>: through commands (else directly,
    /// on a simulation).
    /// </summary>
    void GreedyMerge(Engine.Adventure g, AdventureAiStats st, HeroState taker, HeroState giver, bool real)
    {
        Offload(g, st, taker, giver, real);
        for (int round = 0; round < 64; round++)
        {
            double v0 = HeroEstimate(g, st, taker);
            int best = -1;
            double bestD = 0.0;
            for (int i = 0; i < Army.Slots; i++)
            {
                if (PlanMove(g, st, taker, giver, i) is not { } m) continue;
                var saveT = Snapshot(taker.Army);
                var saveG = Snapshot(giver.Army);
                ApplyMove(taker, giver, i, m);
                double d = (double)HeroEstimate(g, st, taker) - v0;
                Restore(taker.Army, saveT);
                Restore(giver.Army, saveG);
                if (bestD < d) { bestD = d; best = i; }
            }
            if (best < 0 || PlanMove(g, st, taker, giver, best) is not { } bm) break;
            if (!real) ApplyMove(taker, giver, best, bm);
            else if (!(bm.Swap
                ? Send(g, new SwapStacksCmd(giver.Id, -1, best, taker.Id, -1, bm.To) { Player = taker.Owner })
                : Send(g, new TransferStackCmd(giver.Id, -1, best, taker.Id, -1, bm.To, bm.Count) { Player = taker.Owner })).Ok) break;
        }
        Arrange(g, taker, real);
    }

    /// <summary>A planned move of one giver stack: the taker slot, the count, and whether it displaces another creature.</summary>
    public readonly record struct MovePlan(int To, int Count, bool Swap);

    /// <summary>
    /// FUN_00cdd180 with the slot of FUN_00cdacd0 for one giver slot: the taker's slot of that creature (FUN_0045b0e0);
    /// else, while the taker has fewer than 6 stacks, its first free slot (FUN_0045a290); else FUN_00cdab70 (so the
    /// seventh slot is never filled this way). An empty or same-creature slot takes the whole stack, or all but one
    /// creature when the giver hero has at most one stack (0 → no move); a slot of another creature (FUN_00cdab70) takes
    /// the whole stack and the displaced stack goes into the giver's slot (giver army vf 0x10).
    /// </summary>
    public static MovePlan? PlanMove(Engine.Adventure g, AdventureAiStats st, HeroState taker, HeroState giver, int from)
    {
        if (giver.Army[from] is not { Count: > 0 } s) return null;
        int to = Array.FindIndex(taker.Army, x => x is { Count: > 0 } && x.Creature == s.Creature);
        if (to < 0 && taker.Army.StackCount() < 6) to = Array.FindIndex(taker.Army, x => x is not { Count: > 0 });
        if (to < 0) to = WeakestSlot(g, st, taker, IsShooter(g, s));
        if (to < 0) return null;
        if (taker.Army[to] is { Count: > 0 } t && t.Creature != s.Creature) return new MovePlan(to, s.Count, true);
        int n = giver.Army.StackCount() <= 1 ? s.Count - 1 : s.Count;
        return n > 0 ? new MovePlan(to, n, false) : null;
    }

    /// <summary>Creature record +0x4c &gt; 0 (FUN_004bb150): a shooter.</summary>
    static bool IsShooter(Engine.Adventure g, Stack s) => g.Core.Creatures.TryGetValue(s.Creature, out var c) && c.Shots > 0;

    /// <summary>
    /// FUN_00cdab70(army, incoming is a shooter): the occupied slot with the lowest stack value FUN_004e9b90 (with the
    /// taker hero; strict &lt;, the first on ties); an incoming shooter replaces only shooters when the army has 4 or
    /// more, an incoming non-shooter only non-shooters when the army has exactly one shooter. −1 when none.
    /// </summary>
    public static int WeakestSlot(Engine.Adventure g, AdventureAiStats st, HeroState taker, bool shooter)
    {
        int shooters = taker.Army.Count(x => x is { Count: > 0 } && IsShooter(g, x));
        bool onlyShooters = shooter && shooters >= 4, keepShooter = !shooter && shooters == 1;
        int best = -1;
        double bestV = 0.0;
        for (int i = 0; i < taker.Army.Length; i++)
        {
            if (taker.Army[i] is not { Count: > 0 } x) continue;
            if (onlyShooters && !IsShooter(g, x)) continue;
            if (keepShooter && IsShooter(g, x)) continue;
            double v = StackValue(g, st, x, taker);
            if (best == -1 || v < bestV) { best = i; bestV = v; }
        }
        return best;
    }

    static void ApplyMove(HeroState taker, HeroState giver, int from, MovePlan m)
    {
        var s = giver.Army[from]!;
        if (m.Swap) { (giver.Army[from], taker.Army[m.To]) = (taker.Army[m.To], s); return; }
        int n = m.Count;
        if (taker.Army[m.To] is { Count: > 0 } t) t.Count += n;
        else taker.Army[m.To] = new Stack(s.Creature, n) { Artifact = n == s.Count ? s.Artifact : null };
        s.Count -= n;
        if (s.Count <= 0) giver.Army[from] = null;
    }

    /// <summary>
    /// FUN_00cdcfd0 (before the merge): only while the giver has fewer than 7 stacks and the taker more than one; V =
    /// the taker's estimate, once; each taker stack in slot order is taken out and the estimate recomputed (only its
    /// low 32 bits are loaded, FILD dword at 0x00cdd102): when V' − V &gt; 0 the stack goes to the giver (army add
    /// vf 0xc(stack, −1), 004ef7e4..004ef881: the giver's slot of that creature, else its first free one) and the scan
    /// goes on while the two counts still allow it (FUN_00cd9c90); else it goes back into its slot.
    /// </summary>
    void Offload(Engine.Adventure g, AdventureAiStats st, HeroState taker, HeroState giver, bool real)
    {
        static bool Allowed(HeroState t, HeroState gv) => gv.Army.StackCount() < 7 && t.Army.StackCount() > 1;
        if (!Allowed(taker, giver)) return;
        long v = HeroEstimate(g, st, taker);
        for (int i = 0; i < Army.Slots; i++)
        {
            if (taker.Army[i] is not { Count: > 0 } s) continue;
            if (!Allowed(taker, giver)) return;
            taker.Army[i] = null;
            double d = (double)unchecked((int)HeroEstimate(g, st, taker)) - (double)v;
            taker.Army[i] = s;
            if (!(d > 0.0)) continue;
            int to = Array.FindIndex(giver.Army, x => x is { Count: > 0 } && x.Creature == s.Creature);
            if (to < 0) to = Array.FindIndex(giver.Army, x => x is not { Count: > 0 });
            if (to < 0) return;
            if (!real)
            {
                if (giver.Army[to] is { Count: > 0 } x) x.Count += s.Count; else giver.Army[to] = s;
                taker.Army[i] = null;
            }
            else if (!Send(g, new TransferStackCmd(taker.Id, -1, i, giver.Id, -1, to, s.Count) { Player = taker.Owner }).Ok) return;
            if (!Allowed(taker, giver)) return;
        }
    }

    /// <summary>
    /// FUN_00cddfd0 (the taker's army at the end): every slot is emptied; the shooters (FUN_004bb150), in slot order,
    /// go to slots 0, 2, 4, 6, 1, 3, 5; the other stacks, in slot order, into the first free slots. Returns, per slot,
    /// the old slot index that ends there (−1 empty).
    /// </summary>
    public static int[] Arrangement(Engine.Adventure g, Stack?[] army)
    {
        var target = Enumerable.Repeat(-1, army.Length).ToArray();
        var left = new bool[army.Length];
        int k = 0;
        for (int i = 0; i < army.Length; i++)
        {
            if (army[i] is not { Count: > 0 } s) continue;
            if (!IsShooter(g, s)) { left[i] = true; continue; }
            target[k] = i;
            k += 2;
            if (k > 6) k = 1;
        }
        int j = 0;
        for (int i = 0; i < army.Length; i++)
        {
            if (!left[i]) continue;
            while (target[j] != -1) j++;
            target[j] = i;
        }
        return target;
    }

    /// <summary>Applies <see cref="Arrangement"/> by swaps inside the army (SwapStacksCmd when real).</summary>
    void Arrange(Engine.Adventure g, HeroState h, bool real)
    {
        var target = Arrangement(g, h.Army);
        var cur = Enumerable.Range(0, h.Army.Length).ToArray();
        for (int p = 0; p < target.Length; p++)
        {
            if (target[p] < 0) continue;
            int q = Array.IndexOf(cur, target[p]);
            if (q == p) continue;
            (cur[p], cur[q]) = (cur[q], cur[p]);
            if (!real) (h.Army[p], h.Army[q]) = (h.Army[q], h.Army[p]);
            else if (!Send(g, new SwapStacksCmd(h.Id, -1, p, h.Id, -1, q) { Player = h.Owner }).Ok) return;
        }
    }

    static Stack?[] Snapshot(Stack?[] a) =>
        a.Select(s => s == null ? null : new Stack(s.Creature, s.Count) { Artifact = s.Artifact }).ToArray();

    static void Restore(Stack?[] a, Stack?[] from)
    {
        for (int i = 0; i < a.Length; i++) a[i] = from[i];
    }

    /// <summary>FUN_00c5acb0: each war machine the taker lacks and the giver has changes hands.</summary>
    static void HandOverMachines(HeroState taker, HeroState giver)
    {
        if (!taker.Ballista && giver.Ballista) { giver.Ballista = false; taker.Ballista = true; }
        if (!taker.AmmoCart && giver.AmmoCart) { giver.AmmoCart = false; taker.AmmoCart = true; }
        if (!taker.FirstAidTent && giver.FirstAidTent) { giver.FirstAidTent = false; taker.FirstAidTent = true; }
    }

    /// <summary>
    /// Supplier test FUN_00c5af30(sup, com): only a collector / supplier with a commander; on a copy, sup becomes com's
    /// supplier (vf 0x2c4(3, com)), the transfer runs (rule 6: the commander takes) and the result is com's estimate
    /// after / before &gt; 1.1 (DAT_00e72bf8, strict; NaN false).
    /// </summary>
    public bool SupplierTest(Engine.Adventure g, AdventureAiStats st, HeroState sup, HeroState com, HeroRole supRole, HeroRole comRole)
    {
        if (!sup.Alive || !com.Alive || supRole is not (HeroRole.Collector or HeroRole.Supplier) || comRole != HeroRole.Commander) return false;
        var sim = Simulation(g);
        if (sim.S.Hero(sup.Id) is not { } s || sim.S.Hero(com.Id) is not { } c) return false;
        bool hadS = _role.TryGetValue(sup.Id, out var oldS), hadC = _role.TryGetValue(com.Id, out var oldC);
        bool hadP = _partner.TryGetValue(sup.Id, out int oldP);
        _role[sup.Id] = HeroRole.Supplier;
        _role[com.Id] = HeroRole.Commander;
        _partner[sup.Id] = com.Id;
        try
        {
            double before = HeroEstimate(sim, st, c);
            if (TransferSides(sim, st, c, s) is { } ts)
            {
                GreedyMerge(sim, st, ts.Taker, ts.Giver, real: false);
                HandOverMachines(ts.Taker, ts.Giver);
            }
            double after = HeroEstimate(sim, st, c);
            return after / before > 1.1;
        }
        finally
        {
            if (hadS) _role[sup.Id] = oldS; else _role.Remove(sup.Id);
            if (hadC) _role[com.Id] = oldC; else _role.Remove(com.Id);
            if (hadP) _partner[sup.Id] = oldP; else _partner.Remove(sup.Id);
        }
    }

    /// <summary>
    /// Two own heroes meet (AI visit of a friendly hero, 004e6d30 → FUN_00c5ad90): the transfer for real through
    /// <see cref="TransferStackCmd"/>, the war machines, then the role update FUN_00c5ab30 for both (giver, then taker).
    /// </summary>
    public void HeroesMeet(Engine.Adventure g, AdventureAiStats st, HeroState a, HeroState b)
    {
        if (TransferSides(g, st, a, b) is not { } ts) return;
        GreedyMerge(g, st, ts.Taker, ts.Giver, real: true);
        HandOverMachines(ts.Taker, ts.Giver);
        UpdateSupplierRole(g, st, ts.Giver, ts.Taker);
        UpdateSupplierRole(g, st, ts.Taker, ts.Giver);
    }

    /// <summary>
    /// FUN_00c5ab30(hero, other): a collector / supplier that met someone else than its partner becomes a supplier
    /// when the supplier test holds (collector) or a collector when it fails (supplier); one that met its partner
    /// becomes a collector again.
    /// </summary>
    void UpdateSupplierRole(Engine.Adventure g, AdventureAiStats st, HeroState h, HeroState other)
    {
        var r = RoleOf(h);
        if (r is not (HeroRole.Collector or HeroRole.Supplier) || !_partner.TryGetValue(h.Id, out int pid)) return;
        if (other.Id != pid)
        {
            bool test = g.S.Hero(pid) is { } p && SupplierTest(g, st, h, p, r, RoleOf(p));
            if (r == HeroRole.Collector && test) { _role[h.Id] = HeroRole.Supplier; return; }
            if (r != HeroRole.Supplier || test) return;
        }
        _role[h.Id] = HeroRole.Collector;
    }
}
