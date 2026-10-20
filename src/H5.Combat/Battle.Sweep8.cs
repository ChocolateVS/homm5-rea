namespace H5.Combat;

// Combat audit sweep 8 (docs/rules/audit/combat_sweep_8.md): mechanics re-checked against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>The base roll and luck of the strike that triggers Storm Strike (exe 00a68348: the storm call reuses the
    /// strike's roll local and luck multiplier).</summary>
    (double Roll, double Luck, AttackKind Kind) stormStrikeHit;

    /// <summary>
    /// Storm Strike / Storm Bolt lightning (exe FUN_00a608d0; the strike loop FUN_00a673c0 calls it after the blow with the
    /// target's base roll, Storm Bolt FUN_00a60b60 with a fresh roll and luck 1.0). Nothing happens unless the attacker has
    /// ABILITY_STORMSTRIKE (vf 0x28c(0x5b), also for the bolt) and the target is still a living creature (FUN_00a5a440).
    /// Victims (FUN_00a5f620): the target first, then a chain through the enemy stacks connected to it by adjacency;
    /// EVERY victim, the target included, takes the attacker's damage FUN_00a5c790 computed against that victim from the
    /// same base roll and luck, without the strike's special multiplier.
    /// </summary>
    void StormLightning(Unit a, Unit t, double roll, double luck, AttackKind kind)
    {
        if (!HasAbilityNow(a, Ab.StormStrike) || !t.Alive) return;
        var victims = StormStrikeVictims(a, t);
        Emit(new AbilityTriggered(a.Id, Ab.StormStrike, t.Id));
        foreach (var v in victims)
            DealDamage(v, ComputeDamage(a, v, new Hit { Kind = kind, TotalRoll = roll, Secondary = true }, ranged: false, luck),
                a.Id, AttackKind.Ability, Ab.StormStrike);
    }

    /// <summary>
    /// The lightning's victims (exe FUN_00a5f620 / FUN_00a5f3b0). Candidates (FUN_00b3e640 with filter FUN_00a5a440): the
    /// living enemy stacks connected to the target through a chain of adjacent ones (flood fill in army order), the target
    /// removed. From the current victim (the target first) the next one is among the remaining candidates adjacent to it
    /// (FUN_00b3e4e0): none — the chain ends; one — it; several — the one whose direction from the current victim
    /// deviates least (|wrap(angle − base)|, ties within 0.001 drawn with the battle RNG Random(n)) from the direction to
    /// the centroid of all remaining candidates (FUN_00a5d810); a current victim at that centroid (distance &lt; 0.001)
    /// draws uniformly among the adjacent ones. No length limit.
    /// </summary>
    internal List<Unit> StormStrikeVictims(Unit a, Unit t)
    {
        bool Cand(Unit o) => o.Alive && !o.IsObject && Controller(o) != Controller(a);
        var army = S.Units.Where(Cand).ToList();
        var cand = new List<Unit>();
        foreach (var o in army) if (o != t && AreAdjacent(o, t)) cand.Add(o);
        for (int grown = -1; grown != cand.Count;)
        {
            grown = cand.Count;
            for (int i = 0; i < grown; i++)
                foreach (var o in army)
                    if (o != cand[i] && !cand.Contains(o) && AreAdjacent(o, cand[i])) cand.Add(o);
        }
        cand.Remove(t);
        var victims = new List<Unit> { t };
        var cur = t;
        while (true)
        {
            var adj = cand.Where(o => o != cur && AreAdjacent(o, cur)).ToList();
            if (adj.Count == 0) break;
            Unit next;
            if (adj.Count == 1) next = adj[0];
            else
            {
                float n = 0, gx = 0, gy = 0;
                foreach (var o in cand) { n += 1f; gx += (float)o.Center.X; gy += (float)o.Center.Y; }
                float inv = 1f / n;
                gx *= inv; gy *= inv;
                float cx = (float)cur.Center.X, cy = (float)cur.Center.Y;
                float dx = gx - cx, dy = gy - cy;
                if (MathF.Sqrt(dx * dx + dy * dy) < 0.001f) next = adj[S.Rng.Next(adj.Count)];
                else
                {
                    float baseAngle = MathF.Atan2(dy, dx), best = 6.2831855f;
                    var picks = new List<Unit>();
                    foreach (var o in adj)
                    {
                        float d = MathF.Abs(WrapAngle(MathF.Atan2((float)o.Center.Y - cy, (float)o.Center.X - cx) - baseAngle));
                        if (d < best) { picks.Clear(); picks.Add(o); best = d; }
                        else if (MathF.Abs(d - best) < 0.001f) picks.Add(o);
                    }
                    next = picks[S.Rng.Next(picks.Count)];
                }
            }
            victims.Add(next);
            cand.Remove(next);
            cur = next;
        }
        return victims;
    }
}
