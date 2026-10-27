using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 10 (docs/rules/audit/combat_sweep_10.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// The placement chooser FUN_00b3e1e0 over the free-anchor grid FUN_00b3fac0 builds for a footprint (FUN_00b3f120
    /// blocked cells, dilated for size &gt; 1 by FUN_00b3efb0): the requested anchor when it is free; otherwise every free
    /// anchor, scanned row by row (y outer, x inner), keeping those at the smallest squared distance (FUN_00b5f550) to
    /// the requested anchor; the tie is drawn with the given RNG (vf 0xc(0, n − 1)) or, without one, the first found.
    /// </summary>
    internal Cell? ExePlacement(Unit probe, Cell want, Rng? rng)
    {
        if (want.X >= 0 && want.Y >= 0 && want.X <= S.Width - probe.Size && want.Y <= S.Height - probe.Size && Fits(probe, want))
            return want;
        var best = new List<Cell>();
        int bestD = int.MaxValue;
        for (int y = 0; y <= S.Height - probe.Size; y++)
        for (int x = 0; x <= S.Width - probe.Size; x++)
        {
            var p = new Cell(x, y);
            if (!Fits(probe, p)) continue;
            int dx = x - want.X, dy = y - want.Y, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best.Clear(); }
            if (d == bestD) best.Add(p);
        }
        if (best.Count == 0) return null;
        return best[rng == null ? 0 : rng.Range(0, best.Count - 1)];
    }

    /// <summary>
    /// Where Pit Lords summon their Balors (exe FUN_00860660 → FUN_008605a0): on the corpse's own cell when the corpse is
    /// a large creature (vf 0x44 &gt; 1); for a small corpse, the 2-cell placement nearest to it (FUN_00b3fac0 size 2,
    /// FUN_00b3e1e0 without RNG). No placement → the ability is refused (before the charge is spent); a large corpse's
    /// cell is not checked there — FUN_00c56750 (FUN_00c557a0) then refuses an occupied one after the charge is spent.
    /// </summary>
    internal Cell? BalorPlacement(Unit corpse)
    {
        if (corpse.Size > 1) return corpse.Pos;
        var probe = new Unit { Id = -1, Size = Db.Creatures.TryGetValue("CREATURE_BALOR", out var bd) ? bd.CombatSize : 2 };
        return ExePlacement(probe, corpse.Pos, null);
    }

    /// <summary>
    /// The flee destination of Cowardice (mode 0) and Manoeuvre (mode 1) — exe FUN_00a5d790 → FUN_0089ffc0 (context:
    /// the attacker, the stack, the mode; the stack's enemy army list vf 0x10 → vf 0x6c; the walker cost map
    /// FUN_00b5ebd0 / FUN_00b5f0d0 from the stack's cell over the units-and-obstacles grid FUN_00b3f120) → FUN_008a0520 →
    /// FUN_008a0250: every cell of the box ±R (R = vf 0x48, the speed) around the stack, x outer / y inner, is scored by
    /// FUN_0089fd90 and the lowest score kept, ties drawn with the battle RNG (Random(n)).
    /// FUN_0089fd90(x, y): rejected (0xfffffff) for the stack's own cell or a path cost above 2R; score = 2R − cost + 1
    /// in mode 0 (the farthest cell), the cost in mode 1 (the nearest). For each enemy unit whose footprint touches the
    /// stack's footprint at (x, y) (FUN_00b3c940): mode 1 rejects the cell; mode 0 rejects it when that unit is the
    /// attacker and otherwise counts it — any count adds 2 · 2R. Then the side term: for the left army (vf 0xac) a cell
    /// with x ≥ the stack's x costs 2R more, for the right army a cell with x ≤ it.
    /// </summary>
    internal Cell? ExeFleeDestination(Unit t, Unit attacker, int mode)
    {
        int r = SpeedOf(t), r2 = 2 * r;
        const int Reject = 0xfffffff;
        var (dist, _) = MoveSearch(t, -1);
        var enemies = S.Living.Where(e => e.Side != t.Side && !e.IsObject && e.Id != t.Id).ToList();
        int Score(Cell c)
        {
            if (c == t.Pos || !dist.TryGetValue(c, out int cost) || cost > r2) return Reject;
            int score = mode == 1 ? cost : r2 - cost + 1;
            int near = 0;
            foreach (var e in enemies)
            {
                if (Distance(t, c, e, e.Pos) > 1) continue;
                if (mode == 1 || e.Id == attacker.Id) return Reject;
                near++;
            }
            if (near > 0 && mode != 2) score += 2 * r2;
            bool left = t.Side == 0;
            if (left ? c.X >= t.Pos.X : c.X <= t.Pos.X) score += r2;
            return score;
        }
        int x0 = Math.Max(0, t.Pos.X - r), x1 = Math.Min(S.Width - 1, t.Pos.X + r);
        int y0 = Math.Max(0, t.Pos.Y - r), y1 = Math.Min(S.Height - 1, t.Pos.Y + r);
        int best = 0xffffff;
        var ties = new List<Cell>();
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            var c = new Cell(x, y);
            int sc = Score(c);
            if (sc < best) { best = sc; ties.Clear(); ties.Add(c); }
            else if (sc == best) ties.Add(c);
        }
        if (ties.Count == 0) return null;
        return ties[ties.Count == 1 ? 0 : S.Rng.Next(ties.Count)];
    }

    /// <summary>
    /// The anchors around a footprint in the order of FUN_00b3d010(center, cs, ts): the right column (x = cx + ts, y from
    /// cy + 1 − cs to cy + ts − 1), the bottom row (y = cy + ts, x from cx + ts down to cx − cs), the left column
    /// (x = cx − cs, y from cy + ts − 1 up to cy + 1 − cs), the top row (y = cy − cs, x from cx − cs to cx + ts) — every
    /// anchor where a size-cs stack touches the size-ts one at (cx, cy).
    /// </summary>
    internal static List<Cell> ExeRing(Cell c, int cs, int ts)
    {
        var ring = new List<Cell>();
        for (int i = 1 - cs; i <= ts - 1; i++) ring.Add(new Cell(c.X + ts, c.Y + i));
        for (int i = ts; i >= -cs; i--) ring.Add(new Cell(c.X + i, c.Y + ts));
        for (int i = ts - 1; i >= 1 - cs; i--) ring.Add(new Cell(c.X - cs, c.Y + i));
        for (int i = -cs; i <= ts; i++) ring.Add(new Cell(c.X + i, c.Y - cs));
        return ring;
    }

    /// <summary>
    /// The walk before a touch (exe FUN_00978490 → pathfinder FUN_00c536f0 with a target): the goals are the free anchors
    /// of the ring around the target (FUN_00b3d010 with the caster's and the target's sizes, FUN_0045c100 free, added by
    /// FUN_00a77ee0); the search (FUN_00c52cc0 / FUN_00b5f0d0) runs from the caster's cell and FUN_00b5ee10 takes the
    /// first goal in ring order with the strictly smallest cost among those reached. Null when none is reachable.
    /// </summary>
    internal Cell? ExeWalkGoal(Unit u, Unit t)
    {
        var reach = Reachable(u);
        reach[u.Pos] = 0;
        Cell? best = null;
        int bestCost = 0xffffff;
        foreach (var c in ExeRing(t.Pos, u.Size, t.Size))
        {
            if (c.X < 0 || c.Y < 0 || c.X > S.Width - u.Size || c.Y > S.Height - u.Size) continue;
            if (c != u.Pos && !Fits(u, c)) continue;
            if (reach.TryGetValue(c, out int cost) && cost < bestCost) { bestCost = cost; best = c; }
        }
        return best;
    }

    /// <summary>
    /// Walks the caster next to its target (FUN_00978490): no move when the chosen goal is its own cell; otherwise it
    /// follows the path (FUN_00a66050) — an interrupted move (snare, moat) cancels the touch. False: no goal or cut short.
    /// </summary>
    internal bool ExeWalkToTarget(Unit u, Unit t)
    {
        if (ExeWalkGoal(u, t) is not { } goal) return false;
        if (goal != u.Pos) MoveUnit(u, goal);
        return u.Alive && u.Pos == goal;
    }

    static readonly float[] AsinTable = Enumerable.Range(0, 514).Select(i => (float)Math.Asin(Math.Min(1.0, i / 512.0))).ToArray();

    /// <summary>
    /// The exe's arcsine FUN_0046eb30: odd; for x ≥ 0 the table 00e125f0 (513 values asin(i / 512)) read at
    /// i = trunc(512x − 0.5) and interpolated with the fraction 512x − i (index clamped to 512, then masked with 0x1ff).
    /// </summary>
    internal static float ExeAsin(float x)
    {
        if (x < 0) return -ExeAsin(-x);
        float t = x * 512f;
        int i = (int)(t - 0.5f);
        float frac = t - i;
        if (i > 0x200) i = 0x200;
        i &= 0x1ff;
        float a = AsinTable[i];
        return (AsinTable[i + 1] - a) * frac + a;
    }

    /// <summary>
    /// Leap's landing (exe FUN_00979930, also its validity — FUN_0097f610 case 0x140): refused unless the move distance
    /// (FUN_00b3c840, 2·max + min) of the gap between the two footprints (FUN_00b3c690) exceeds 2 · Leap_JumpDistanceMin.
    /// The candidates are the ring anchors around the target (FUN_00b3f2f0 → FUN_00b3d010) where the leaper fits
    /// (vf 0xcc), within move distance 2 · Leap_JumpDistanceMax of the leaper, other than the target's and the leaper's
    /// own anchor; of these, the one with the smallest |wrap(angle)| between (target − cell) and (cell − leaper), both
    /// normalised (FUN_004b4500): FUN_00976ae0 = π/2 − asin(dot), 2π − that when the cross product is negative,
    /// FUN_008b7750 wraps into [−π, π) — the first in ring order on ties (strict &lt;, start value 100).
    /// </summary>
    internal Cell? ExeLeapLanding(Unit u, Unit t)
    {
        int min2 = 2 * (int)Stats.Get("combat/Creatures/Inferno/Leap_JumpDistanceMin", 3);
        int max2 = 2 * (int)Stats.Get("combat/Creatures/Inferno/Leap_JumpDistanceMax", 8);
        int gx = Gap(u.Pos.X, u.Pos.X + u.Size - 1, t.Pos.X, t.Pos.X + t.Size - 1);
        int gy = Gap(u.Pos.Y, u.Pos.Y + u.Size - 1, t.Pos.Y, t.Pos.Y + t.Size - 1);
        if (MoveDistance(new Cell(0, 0), new Cell(gx, gy)) <= min2) return null;
        Cell? best = null;
        float bestA = 100f;
        foreach (var c in ExeRing(t.Pos, u.Size, t.Size))
        {
            if (c.X < 0 || c.Y < 0 || c.X > S.Width - u.Size || c.Y > S.Height - u.Size || !Fits(u, c)) continue;
            if (MoveDistance(u.Pos, c) > max2 || c == t.Pos || c == u.Pos) continue;
            float ax = t.Pos.X - c.X, ay = t.Pos.Y - c.Y, bx = c.X - u.Pos.X, by = c.Y - u.Pos.Y;
            float la = 1f / (float)Math.Sqrt(ay * ay + ax * ax), lb = 1f / (float)Math.Sqrt(by * by + bx * bx);
            ax *= la; ay *= la; bx *= lb; by *= lb;
            float dot = ax * bx + by * ay;
            float ang = 1.5707964f - ExeAsin(dot);
            if (by * ax - bx * ay < 0f) ang = 6.2831855f - ang;
            double w = (ang + Math.PI) % (2 * Math.PI) + (ang < -Math.PI ? Math.PI : -Math.PI);
            float a = (float)Math.Abs(w);
            if (a < bestA) { bestA = a; best = c; }
        }
        return best;
    }

    /// <summary>
    /// A rune's resource cost (exe FUN_00419ad0 → the cast record +0xac): the spell's sSpellCost for a first activation
    /// on the stack (FUN_009d0a10), nothing when the Fine Rune roll succeeded (FUN_009d09c0); a Strong Rune
    /// re-activation costs round(resource · StrongRuneCostConstant) each (FUN_009d0d10, RPGStats +0x94c), or
    /// · StrongAndFineRuneCostConstant (+0x950) after a successful Fine Rune roll.
    /// </summary>
    internal int[] RuneCost(HeroState h, SpellDef sp, int? target, bool fineRune)
    {
        var r = sp.ResourceCost;
        if (r == null) return new int[7];
        var v = new[] { r.Wood, r.Ore, r.Mercury, r.Crystal, r.Sulfur, r.Gem, r.Gold };
        bool again = h.AbilityUses.ContainsKey($"{sp.Id}:{target}");
        if (!again) return fineRune ? new int[7] : v;
        float k = (float)Stats.Get(fineRune ? "combat/HeroSpecializations/Runemage/StrongAndFineRuneCostConstant" : "combat/HeroSpecializations/Runemage/StrongRuneCostConstant", fineRune ? 2 : 3);
        return v.Select(x => (int)Math.Round((float)x * k, MidpointRounding.ToEven)).ToArray();
    }

    static bool CanPay(int[] have, int[] cost)
    {
        for (int i = 0; i < 7; i++) if (have[i] < cost[i]) return false;
        return true;
    }

    /// <summary>
    /// Pays a rune at the cast: the Fine Rune roll (FUN_004199d0: hero HasSkill 0x9a, battle RNG float [0, 1) below
    /// FineRuneProbability, RPGStats +0x948), then the cost of <see cref="RuneCost"/> is debited from the owner's
    /// resources (kept in <see cref="HeroState.ResourcesSpent"/> for the adventure map).
    /// </summary>
    internal void PayRune(HeroState h, SpellDef sp, int? target)
    {
        if (h.Resources == null || sp.ResourceCost == null) return;
        bool fine = h.HasPerk(Sk.FineRune)
                    && (float)S.Rng.RangeD(0, 1) < (float)Stats.Get("combat/HeroSpecializations/Runemage/FineRuneProbability", 0.5);
        var cost = RuneCost(h, sp, target, fine);
        for (int i = 0; i < 7; i++) { h.Resources[i] -= cost[i]; h.ResourcesSpent[i] += cost[i]; }
    }
}
