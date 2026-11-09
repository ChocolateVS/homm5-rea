using H5.Core.Data;

namespace H5.Combat;

// Engine queries the original combat AI's position value needs (src/H5.Combat/AI/CombatValue.cs); no state is changed.
public sealed partial class Battle
{
    /// <summary>
    /// FUN_00bfa360(unit, 10, 0), the caster's spell potential (value slot [2]): over the caster's spell entries
    /// (vf 0x248, 0x14 bytes, id at +4) that it may cast now (vf 0x220: hero FUN_00bc0030 = not a rune (FUN_00a345b0), known
    /// and castable (hero vf 0x158), a charged wand (FUN_00a34490 → FUN_00ca62e0) or cost FUN_009781e0(0) ≤ mana), that have a
    /// target (FUN_00982be0), are not Explosion (0xa2) or Death Wail (0x13c), not countered (FUN_009785e0: the enemy hero's armed
    /// Counterspell, effect 0x41, with mana − 2·cost ≥ 0) and deal damage (FUN_00977c80(0) = FUN_00976db0(FUN_00976b90)):
    /// casts = the wand's charges (FUN_00a34470 / FUN_00a34490), else mana / cost (100 when the cost is 0); none below 1;
    /// value = min(casts, 10) · damage / 10 (integer division), the best one kept (0 when none).
    /// GAP-details: the creature vf 0x220 (FUN_00a6bb10) is read as "mana ≥ cost"; FUN_00982be0's target test is the
    /// engine's spell immunity check on an enemy stack (<see cref="ExeSpellImmune"/>); of FUN_00976db0 only the elemental
    /// artifacts are applied; spell 0x3c (SPELL_SNIPE_DEAD, FUN_00a34260) takes the caster vf 0x78 path (not ported, 0).
    /// </summary>
    internal int AiSpellPotential(Unit? creature, HeroState? hero)
    {
        int best = 0;
        int side = creature?.Side ?? hero!.Side;
        IEnumerable<(SpellDef Sp, Mastery M, double Power, int Mana, int Cost, int Charges)> entries;
        if (hero != null)
        {
            if (!hero.Present) return 0;
            entries = HeroSpells(hero).Select(id => Db.Spell(id)).Where(sp => sp != null && sp.School != "MAGIC_SCHOOL_RUNIC" && CanCastWarcry(hero, sp))
                .Select(sp => (sp!, HeroMastery(hero, sp!), HeroSpellPower(hero, sp!), hero.Mana, HeroManaCost(hero, sp!), WandFor(hero, sp!.Id)?.Charges ?? -1));
        }
        else
        {
            var u = creature!;
            entries = u.Spells.Select(k => (k, Db.Spell(k.Spell))).Where(p => p.Item2 != null)
                .Select(p => (p.Item2!, MoonWeek.SpellMastery(p.k.Mastery, p.Item2!.School, S.Week), (double)CreatureCasterPower(u, u.Count), u.Mana, CreatureManaCost(u, p.Item2!), -1));
        }
        var enemyHero = S.Heroes[1 - side];
        foreach (var (sp, m, power, mana, cost, charges) in entries)
        {
            if (charges < 0 && cost > mana) continue;                              // vf 0x220
            if (sp.Id is "SPELL_ABILITY_EXPLOSION" or "SPELL_ABILITY_DEATH_WAIL") continue;
            if (Counterable(sp) && enemyHero.Present && enemyHero.Links.ContainsKey(LinkCounterspell) && enemyHero.Mana - 2 * cost >= 0) continue;
            string id = BaseSpell(sp.Id);
            if (!SpellDamageSpells.Contains(id)) continue;                         // FUN_00976b90 gives 0
            var baseDef = Db.Spell(id) ?? sp;
            if (!S.Living.Any(t => !t.IsObject && Controller(t) != side && !ExeSpellImmune(t, sp, id, hero))) continue;   // FUN_00982be0
            int dmg = (int)ElementArtifactDamage(hero, sp.Element, SpellBaseDamage(baseDef, sp, m, power));
            if (dmg == 0) continue;
            int casts = charges >= 0 ? charges : cost > 0 ? mana / cost : 100;
            if (charges >= 0 || cost > 0) { if (casts < 1) continue; }
            best = Math.Max(best, Math.Min(casts, 10) * dmg / 10);
        }
        return best;
    }

    /// <summary>
    /// The combat AI's path maps (FUN_008d0720(map, own, enemy, flyerAsWalker) → FUN_00c52e20 → FUN_00b3fac0 / FUN_00b5ebd0,
    /// then FUN_00b5f0d0 from the actor's cell): the obstacle grid without stacks (FUN_00b3fac0 with its unit flag set), plus
    /// the stacks of the actor's own army (FUN_00b3cf00(actor vf 0xc)) when <paramref name="own"/> and of the other army
    /// (vf 0x10) when <paramref name="enemy"/>, minus the actor (FUN_00b3cf30); no budget. A flyer (vf 0x190) searches an
    /// empty board (FUN_00b3fac0's flyer flag) unless <paramref name="flyerAsWalker"/>. The search is the walker search of
    /// <see cref="MoveSearch"/> (steps 2 / 3, FIFO cost buckets, MoveDirs order, strictly cheaper parents).
    /// Whole-path map think +0xfc: (own false, enemy true, flyerAsWalker false); think +0xe4: (true, true, true).
    /// </summary>
    internal (Dictionary<Cell, int> Dist, Dictionary<Cell, Cell> Prev) AiPathMap(Unit u, bool own, bool enemy, bool flyerAsWalker)
    {
        bool flyer = u.Flying && !flyerAsWalker;
        bool Blocked(Cell c)
        {
            if (!S.InBounds(c)) return true;
            if (flyer) return false;
            if (S.Obstacles.Contains(c) || GateBlocks(c, u)) return true;
            foreach (var o in S.Units)
                if (o.Id != u.Id && o.Alive && !o.IsWarMachine && (o.Side == u.Side ? own : enemy) && o.Occupies(c)) return true;
            return false;
        }
        bool FitsHere(Cell p)
        {
            foreach (var c in u.CellsAt(p)) if (Blocked(c)) return false;
            return true;
        }
        var dist = new Dictionary<Cell, int> { [u.Pos] = 0 };
        var prev = new Dictionary<Cell, Cell>();
        var buckets = new SortedDictionary<int, Queue<Cell>> { [0] = new Queue<Cell>(new[] { u.Pos }) };
        while (buckets.Count > 0)
        {
            var first = buckets.First();
            var cur = first.Value.Dequeue();
            if (first.Value.Count == 0) buckets.Remove(first.Key);
            int cost = first.Key;
            if (cost > dist[cur]) continue;
            foreach (var d in MoveDirs)
            {
                var n = cur + d;
                int nc = cost + (d.X != 0 && d.Y != 0 ? MoveDiag : MoveOrth);
                if (dist.TryGetValue(n, out var old) && old <= nc) continue;
                if (!FitsHere(n)) continue;
                dist[n] = nc;
                prev[n] = cur;
                if (!buckets.TryGetValue(nc, out var q)) buckets[nc] = q = new Queue<Cell>();
                q.Enqueue(n);
            }
        }
        return (dist, prev);
    }

    /// <summary>
    /// FUN_00a5c5a0(shooter, cell, size) as the AI's danger score uses it: 1.0 for a shooter that is not a creature (vf 0 ≠ 1,
    /// a war machine); otherwise the engine's range / wall penalty (<see cref="RangePenaltyFactor"/>) against
    /// <paramref name="target"/> standing at <paramref name="cell"/>.
    /// </summary>
    internal double AiRangeFactorAt(Unit shooter, Unit target, Cell cell)
    {
        if (shooter.IsWarMachine) return 1.0;
        var keep = target.Pos;
        target.Pos = cell;
        try { return RangePenaltyFactor(shooter, target); }
        finally { target.Pos = keep; }
    }
}
