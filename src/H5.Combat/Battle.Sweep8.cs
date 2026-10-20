using H5.Core.Data;

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

    const string LinkConjurePhoenix = "SPELL_CONJURE_PHOENIX";

    /// <summary>
    /// Conjure Phoenix (exe 0097d7b0 case 0xeb → FUN_00862130). The summon info (FUN_00861f90 → ctor FUN_00862970) keeps the
    /// DefaultStats ConjuredPhoenix record (RPGStats +0x530), the cast power (vf 0x244), the caster level (vf 0x23c) and the
    /// divisor trunc(Eval of the spell's damage table at the mastery, power 0) = 4/3/2/1. The phoenix is then placed by
    /// FUN_00861340: no summon position (FUN_00860f20) → nothing; a phoenix still linked to the caster for 0xeb
    /// (FUN_00a6e310) takes its whole health through the damage routine (vf 0x1f4 = FUN_008a6e20 with vf 0x1dc) and is
    /// unlinked (vf 0x2a0); the new one is placed and linked (vf 0x298(0xeb, …)). With Elemental Balance on the enemy hero
    /// the same routine runs for that hero (00862130 @ 0086225e): a phoenix with the caster's stats for the enemy side,
    /// replacing one linked to the enemy hero. The engine refused a second phoenix and used ceil(power / divisor) with the
    /// stock numbers and the prototype's speed, initiative and abilities.
    /// </summary>
    void ConjurePhoenix(int side, HeroState? hero, string by, double power, int level, int divisor)
    {
        var rec = Db.ConjuredPhoenix;
        string proto = rec?.Element("PrototypeCreature")?.Value.Trim() is { Length: > 0 } pc ? pc : "CREATURE_PHOENIX";
        if (!Db.Creatures.ContainsKey(proto)) return;
        Unit? Place(int s, HeroState? linkTo)
        {
            var probe = new Unit { Size = Db.Creature(proto).CombatSize };
            Cell around = s == 0 ? new Cell(1, S.Height / 2) : new Cell(S.Width - 2, S.Height / 2);
            if (NearestFree(probe, around) is null) return null;          // FUN_00860f20 first: no place, no effect
            if (linkTo != null && linkTo.Links.TryGetValue(LinkConjurePhoenix, out var oldId))
            {
                if (S.Unit(oldId) is { Alive: true } old) DealDamage(old, old.TotalHealth, -1, AttackKind.Other, by);
                linkTo.Links.Remove(LinkConjurePhoenix);
            }
            var ph = SummonStack(s, proto, 1, by, atb: S.Rng.Next(6) * 0.05);
            if (ph == null) return null;
            ApplyUniqueCreatureStats(ph, rec, power, level, divisor);
            if (linkTo != null && ph.Alive) linkTo.Links[LinkConjurePhoenix] = ph.Id;
            return ph;
        }
        if (Place(side, hero) == null) return;
        var e = Enemy(side);
        if (e.Present && e.HasPerk("HERO_SKILL_ELEMENTAL_BALANCE") && Place(1 - side, e) is { } ph2)
            Emit(new AbilityTriggered(Actor.Hero(1 - side), "HERO_SKILL_ELEMENTAL_BALANCE", ph2.Id));
    }

    /// <summary>
    /// The SRPGCombatUniqueCreatureStats getters (exe FUN_004bcd60 health, FUN_004bce10 / FUN_004bcea0 min / max damage,
    /// FUN_004bcf30 / FUN_004bcfc0 offence / defence, FUN_004bd060 initiative, FUN_004bd0e0 speed, FUN_004bd1c0 abilities):
    /// value = Base + level·PerLevel / divisor + power·PerPower / divisor (health also + PerKnowledge × the army hero's
    /// vf 0x1c — not ported, 0 in stock and MMH55 data), clamped to [0, 10000] (health, damage), [0, 1000] (offence,
    /// defence), [1, 100] (initiative, speed) and truncated after + 0.0001 (initiative stays fractional); health is halved
    /// by the summon-halving flag (never set for a hero caster). The abilities are ONLY the record's: every Abilities item
    /// in order while its Level ≤ level / divisor (integer) — HasAbility FUN_008a34c0 uses this list instead of the
    /// creature's for a unit with a unique-summon record.
    /// </summary>
    internal static void ApplyUniqueCreatureStats(Unit u, System.Xml.Linq.XElement? rec, double power, int level, int divisor)
    {
        if (rec == null) return;
        double F(string n) => XmlHelpers.ParseDouble(rec.Element(n)?.Value, 0);
        float Raw(string n) =>
            (float)(F(n) + (float)level * (float)F(n + "_PerLevel") / (float)divisor + (float)power * (float)F(n + "_PerPower") / (float)divisor);
        int Trunc(float v, float lo, float hi) => (int)((double)Math.Clamp(v, lo, hi) + 0.0001);
        u.BaseHealth = Trunc(Raw("Health"), 0, 10000);
        u.BaseMinDamage = Trunc(Raw("DamageMin"), 0, 10000);
        u.BaseMaxDamage = Trunc(Raw("DamageMax"), 0, 10000);
        u.BaseAttack = Trunc(Raw("Offence"), 0, 1000);
        u.BaseDefence = Trunc(Raw("Defence"), 0, 1000);
        u.BaseInitiative = (float)((double)Math.Clamp(Raw("Initiative"), 1f, 100f) + 0.0001);
        u.BaseSpeed = Trunc(Raw("Speed"), 1, 100);
        var abilities = new List<string>();
        foreach (var item in rec.Element("Abilities")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            if (level / divisor < (int)XmlHelpers.ParseDouble(item.Element("Level")?.Value, 0)) break;
            abilities.AddRange((item.Element("Abilities")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                .Select(a => a.Value.Trim()).Where(a => a.Length > 0));
        }
        u.Abilities = abilities;
        u.MaxHealthCache = u.TopHealth = u.BaseHealth;
    }
}
