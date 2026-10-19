using H5.Core.Data;

namespace H5.Combat;

// Creature abilities, audit part D (docs/rules/audit/combat_abilities_d.md): the exe's data-driven PatternAttack
// (FUN_00a62030 / FUN_008aa990 / FUN_00a5b630), Acid Blood, Lightning Breath's split roll and the strike helpers
// (Lizard Bite, Pack Hunter: FUN_00a64120 → FUN_00a63e90).
public sealed partial class Battle
{
    /// <summary>
    /// A stack's cell as the exe stores it (vf 0x40): the footprint's maximum corner — a 2×2 stack covers pos−1 … pos
    /// (FUN_00b3d3f0 / FUN_00b3d4e0 test pos − size … pos; every 2×2 PatternAttack ring is centred on (−0.5, −0.5)).
    /// </summary>
    static Cell ExePos(Unit u) => new(u.Pos.X + u.Size - 1, u.Pos.Y + u.Size - 1);

    /// <summary>
    /// The PatternAttack a strike uses — creature vf 0xb8 = FUN_008aa990(ranged, cells walked, acid-blood flag):
    /// a creature whose definition has shots (def +0x4c &gt; 0) uses its pattern only for ranged strikes, any other
    /// only for melee strikes (the non-shooter test also needs vf 0x1e0, taken as true for its ranged strikes);
    /// Champion Charge with more cells walked than ChampionChargeMinimalDistance → DefaultStats ChampionChargePattern;
    /// the acid-blood call (00a68482) → DefaultStats AcidicHydraPattern; otherwise the creature's own PatternAttack.
    /// </summary>
    AttackPattern? PatternFor(Unit a, bool ranged, int walked, bool acidBlood)
    {
        if (!Db.Creatures.TryGetValue(a.CreatureId, out var def)) return null;
        bool defShooter = def.Shots > 0;
        if (defShooter != ranged) return null;
        if (HasAbilityNow(a, Ab.ChampionCharge) && Stats.Get("combat/Creatures/Haven/ChampionChargeMinimalDistance", 2) < walked)
            return Db.ChampionChargePattern;
        if (HasAbilityNow(a, Ab.AcidBlood) && acidBlood) return Db.AcidicHydraPattern;
        return def.Pattern;
    }

    /// <summary>
    /// FUN_00a5b630's angular distance between a rotation's AngleRotateTo and the attack direction θ:
    /// d = deg·0.017453292 − θ (single precision), |fmod(d + π, 2π) + (d &lt; −π ? π : −π)|.
    /// </summary>
    static float PatternAngleDiff(float angleDeg, float theta)
    {
        float d = angleDeg * 0.017453292f - theta;
        double adj = d < -Math.PI ? Math.PI : -Math.PI;
        return (float)Math.Abs((((double)d + Math.PI) % (2 * Math.PI)) + adj);
    }

    /// <summary>
    /// The exe's pattern victims (FUN_00a62030 generic branch): every rotation's cells are offset by the anchor (melee:
    /// the attacker's cell; ranged: the aimed cell); its victims are the stacks covering those cells, cell by cell
    /// (FUN_00b3d720), enemies only unless DamageAll (FUN_00b3d850 / FUN_00b3d960), never the attacker itself.
    /// FUN_00a5b630 picks the rotation that contains the main target with the MOST victims (ties: the smaller angle
    /// to the main target, first wins); failing that, the non-empty rotation closest in angle (ties: more victims).
    /// Each victim's factor is max(0, DamageToMainTargetCoefficient) for the main target, else
    /// max(0, DamageToOtherTargetsCoefficient). θ = atan2 from the anchor footprint's centre (attacker size) to the
    /// main target's centre (FUN_00a58b60). Returns an empty list when no rotation has victims.
    /// </summary>
    List<(Unit U, double F)> PatternVictims(Unit a, Unit main, Cell anchor, AttackPattern p)
    {
        var recs = new List<List<Unit>>();
        foreach (var (cells, _) in p.Rotations)
        {
            var victims = new List<Unit>();
            var sides = p.DamageAll ? new[] { 1, 0 } : new[] { 1 - Controller(a) };
            foreach (int side in sides)
                foreach (var (cx, cy) in cells)
                {
                    var c = new Cell(anchor.X + cx, anchor.Y + cy);
                    foreach (var u in S.Living)
                    {
                        if (Controller(u) != side || u.Id == a.Id || victims.Contains(u)) continue;
                        if (u.IsObject && u.Side == a.Side) continue;
                        var lo = u.Pos; int s = u.Size;
                        if (c.X >= lo.X && c.X < lo.X + s && c.Y >= lo.Y && c.Y < lo.Y + s) victims.Add(u);
                    }
                }
            recs.Add(victims);
        }
        double fromX = anchor.X - (a.Size - 1) / 2.0, fromY = anchor.Y - (a.Size - 1) / 2.0;
        var (tx, ty) = main.Center;
        float theta = (float)Math.Atan2(ty - fromY, tx - fromX);
        int pick = -1, bestN = 0; float best = 6.2831855f;
        for (int i = 0; i < recs.Count; i++)
        {
            int n = recs[i].Count; float diff = PatternAngleDiff(p.Rotations[i].Angle, theta);
            if (recs[i].Contains(main) && bestN <= n && (bestN != n || diff < best)) { pick = i; bestN = n; best = diff; }
        }
        if (pick < 0)
        {
            bestN = 0; best = 6.2831855f;
            for (int i = 0; i < recs.Count; i++)
            {
                int n = recs[i].Count; float diff = PatternAngleDiff(p.Rotations[i].Angle, theta);
                if (n > 0 && diff <= best && (diff != best || bestN < n)) { pick = i; bestN = n; best = diff; }
            }
        }
        if (pick < 0) return new();
        return recs[pick].Select(u => (u, (double)Math.Max(0f, u == main ? p.MainCoefficient : p.OtherCoefficient))).ToList();
    }

    /// <summary>
    /// The strike's target list when the exe's generic pattern branch applies, else null (the engine's other target
    /// rules run). FUN_00a62030 order: a ranged MAGIC_ATTACK, LIGHTNING_BREATH, RAINBOW_BREATH and a melee FLAMEWAVE
    /// have their own branches; CRUSHING_BLOW uses the pattern with its own victim rule (FUN_00b3d910, engine branch).
    /// </summary>
    List<(Unit U, double F)>? ExePatternTargets(Unit a, Unit t, Hit hit, bool ranged)
    {
        if (ranged && HasAbilityNow(a, Ab.MagicAttack)) return null;
        if (HasAbilityNow(a, Ab.LightningBreath) || HasAbilityNow(a, Ab.RainbowBreath)) return null;
        if (!ranged && HasAbilityNow(a, Ab.Flamewave)) return null;
        if (HasAbilityNow(a, Ab.CrushingBlow)) return null;
        var p = PatternFor(a, ranged, hit.CellsWalked, acidBlood: false);
        if (p == null) return null;
        var list = PatternVictims(a, t, ranged ? ExePos(t) : ExePos(a), p);
        // An empty list falls back to the main target alone (00a67581).
        if (list.Count == 0) list.Add((t, 1));
        return list;
    }

    /// <summary>
    /// Acid Blood (exe 00a673c0, 00a683ff–00a6859d): for every struck target with ACID_BLOOD — any strike that is not
    /// ranged and not the Pack Dive finish (0x15b), retaliations included — the hydra runs its pattern from its own
    /// cell with the striker as main target and DefaultStats AcidicHydraPattern (4-cell lines, DamageAll); every victim
    /// takes (D + E) / 4, truncated toward zero, where D is the hydra's own damage against it (base roll FUN_00a597a0,
    /// attack/defence FUN_00a5c790, melee, the strike's retaliation flag) and E its Elemental Chains extra
    /// (FUN_00977e60; not implemented for physical strikes in the engine, 0 here).
    /// </summary>
    void AcidBloodSplash(Unit hydra, Unit striker, Hit hit)
    {
        var p = PatternFor(hydra, ranged: false, walked: 0, acidBlood: true);
        if (p == null) return;
        var victims = PatternVictims(hydra, striker, ExePos(hydra), p);
        if (victims.Count == 0) return;
        Emit(new AbilityTriggered(hydra.Id, Ab.AcidBlood, striker.Id));
        var kind = hit.Kind == AttackKind.Retaliation ? AttackKind.Retaliation : AttackKind.Melee;
        var amounts = victims.Select(v => (v.U, Dmg: ComputeDamage(hydra, v.U, new Hit { Kind = kind }, false) / 4)).ToList();
        foreach (var (v, d) in amounts)
            if (v.Alive && d > 0) DealDamage(v, d, hydra.Id, AttackKind.Ability, Ab.AcidBlood);
    }

    /// <summary>FUN_004e4a20: Blind (0x13), Frozen (0xc9), Sabotage (0x14a), Sleeping Strike (0x150) or Seduce (0x148).</summary>
    bool DisabledForHelperStrike(Unit u) => u.HasEffect("SPELL_BLIND") || u.HasEffect(H5.Combat.Fx.Frozen)
        || u.HasEffect(H5.Combat.Fx.Sabotaged) || u.HasEffect(H5.Combat.Fx.Sleep) || u.HasEffect(H5.Combat.Fx.Seduced);

    /// <summary>
    /// FUN_00a64120, run after every melee strike (FUN_00a64830, inside the strike loop before the retaliation) and after
    /// every melee retaliation strike (FUN_00a646b0): when the struck target is still alive, Lizard Bite (not after a
    /// retaliation) and — if the striker has PACK_HUNTER, retaliations included — Pack Hunter (FUN_00a63e90).
    /// </summary>
    void StrikeHelpers(Unit striker, Unit target, bool retaliation)
    {
        if (!target.Alive) return;
        if (!retaliation) HelperStrikes(Ab.LizardBite, striker, target);
        if (target.Alive && HasAbilityNow(striker, Ab.PackHunter)) HelperStrikes(Ab.PackHunter, striker, target);
    }

    /// <summary>
    /// FUN_00a63e90(ability): every stack of the striker's side with the ability, not on the target's side, adjacent to
    /// the target (FUN_00b3d3f0), neither the striker nor the target and not disabled (FUN_004e4a20) makes one full
    /// strike (FUN_00a673c0 with its no-luck flag, so luck ×1; pattern and on-hit effects as usual; no retaliation).
    /// Lizard Bite strikes carry special SPELL_LIZARD_BITE_HIT (0xb5: ×0.5, ×(1.2 + 0.01·level) with the Riders
    /// specialisation in FUN_00a58de0); Pack Hunter strikes have no special (full damage). Any stack with the ability
    /// helps — not only stacks of the striker's creature type.
    /// </summary>
    void HelperStrikes(string ability, Unit striker, Unit target)
    {
        foreach (var u in S.LivingOf(striker.Side).ToList())
        {
            if (!target.Alive) return;
            if (!u.Alive || !HasAbilityNow(u, ability) || u.Side == target.Side || u.Id == striker.Id || u.Id == target.Id
                || !AreAdjacent(u, target) || DisabledForHelperStrike(u)) continue;
            Emit(new AbilityTriggered(u.Id, ability, target.Id));
            double mult = 1;
            if (ability == Ab.LizardBite)
            {
                var rh = HeroOf(u);
                mult = 0.5 * (rh.Present && rh.Specialization == "HERO_SPEC_RIDERS" ? 1.2 + 0.01 * rh.Level : 1.0);
            }
            Strike(u, target, new Hit { Kind = AttackKind.Melee, Multiplier = mult, NoLuck = true }, pattern: true);
        }
    }

    /// <summary>
    /// Chain Shot order (exe FUN_00a5dbb0 with filter LAB_00a59ba0): the main target first, then up to 3 more, each the
    /// candidate nearest (vf 0x118; the engine's stack distance) to the LAST chained stack among the living stacks of the
    /// other side not yet chained, objects (vf 0x7c) and creatures of TOWN_INFERNO (creature +0x98 == 8, FUN_004ba910)
    /// excluded; ties are broken by the battle RNG (two Range(0, n−1) draws, the second one picks).
    /// </summary>
    List<Unit> ChainShotTargets(Unit a, Unit t)
    {
        var chain = new List<Unit> { t };
        for (int k = 0; k < 3; k++)
        {
            var last = chain[^1];
            var cand = S.Living.Where(o => Controller(o) != Controller(a) && !o.IsObject && o.Town != Towns.Inferno && !chain.Contains(o)).ToList();
            if (cand.Count == 0) break;
            int best = cand.Min(o => Distance(o, last));
            var tied = cand.Where(o => Distance(o, last) == best).ToList();
            S.Rng.Range(0, tied.Count - 1);
            chain.Add(tied[S.Rng.Range(0, tied.Count - 1)]);
        }
        return chain;
    }

    /// <summary>
    /// Chain Shot damage (exe FUN_00a64ad0): the base roll is made once (FUN_00a597a0 against the main target); starting
    /// from twice the roll, each chained target in turn multiplies the running value by its own range penalty
    /// (FUN_00a5c5a0, so the penalties compound down the chain) and by 0.5 (DAT_00e0ab70), single precision; that value
    /// enters the target's damage stages. Returned as the roll each target is struck with (its own range penalty is
    /// applied again inside ComputeDamage, so it is divided out here).
    /// </summary>
    List<double> ChainShotRolls(Unit a, Unit t, Hit hit, List<Unit> chain)
    {
        var rolls = new List<double>();
        float running = (float)(2 * BaseDamageRoll(a, t, hit, ranged: true));
        foreach (var u in chain)
        {
            double pen = RangePenaltyFactor(a, u);
            running = (float)(running * pen);
            running = (float)(running * 0.5);
            rolls.Add(running / pen);
        }
        return rolls;
    }

    /// <summary>The ranged distance / wall penalty factor of ComputeDamage (exe FUN_00a5c5a0).</summary>
    double RangePenaltyFactor(Unit a, Unit t)
    {
        double m = 1;
        if (!a.IsWarMachine && !HasAbilityNow(a, Ab.NoRangePenalty) && !HasAbilityNow(a, Ab.RangePenalty))
        {
            int half = S.Width / 2;
            if (NearestCellDistanceSq(a, t) > half * half) m *= 0.5;
        }
        if (ShotCrossesWalls(a, t)) m *= 0.5;
        return m;
    }
}
