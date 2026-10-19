using H5.Core.Data;

namespace H5.Combat;

// Further creature abilities (fan manual p.171-182): reactive movement, ally follow-ups and
// smaller activated abilities.
public sealed partial class Battle
{
    /// <summary>Cowardice / Maneuver: a struck stack runs off; Maneuver also shoots back at half damage (p.173, p.178).</summary>
    bool ReactiveRetreat(Unit attacker, Unit t)
    {
        if (!t.Alive || !attacker.Alive) return false;
        bool cowardice = HasAbilityNow(t, Ab.Cowardice), maneuver = HasAbilityNow(t, Ab.Maneure);
        if (!cowardice && !maneuver) return false;
        if (!CanMove(t)) return false;
        var options = Reachable(t).Keys.Where(c => Distance(t, c, attacker, attacker.Pos) > 1).ToList();
        if (options.Count == 0) return false;
        var dest = options.OrderBy(c => Octile(c, t.Pos)).ThenBy(c => c.X).ThenBy(c => c.Y).First();
        Emit(new AbilityTriggered(t.Id, cowardice ? Ab.Cowardice : Ab.Maneure, attacker.Id));
        MoveUnit(t, dest);
        // Running costs Cowardice stacks UseCowardiceRP blood rage (exe FUN_00a629b0: only when the run succeeded).
        if (cowardice) RageEvent(t, "UseCowardiceRP");
        if (maneuver && t.Alive && CanShoot(t))
        {
            if (!HasAmmoCart(t.Side)) t.Shots--;
            Strike(t, attacker, new Hit { Kind = AttackKind.Retaliation, Multiplier = 0.5 }, pattern: false, rangedRetaliation: true);
        }
        return true;
    }

    /// <summary>Lizard Bite: allied raiders next to the target join in for half damage (p.177).</summary>
    void LizardBites(Unit attacker, Unit t)
    {
        foreach (var r in S.LivingOf(attacker.Side).Where(r => r.Id != attacker.Id && HasAbilityNow(r, Ab.LizardBite) && AreAdjacent(r, t)).ToList())
        {
            if (!t.Alive) return;
            Emit(new AbilityTriggered(r.Id, Ab.LizardBite, t.Id));
            // ×0.5, and ×(1.2 + 0.01·level) more for the Riders specialisation (exe FUN_00a58de0, special 0xb5).
            var rh = HeroOf(r);
            double mult = 0.5 * (rh.Present && rh.Specialization == "HERO_SPEC_RIDERS" ? 1.2 + 0.01 * rh.Level : 1.0);
            Strike(r, t, new Hit { Kind = AttackKind.Ability, Multiplier = mult }, pattern: false);
        }
    }

    /// <summary>
    /// Wheeling Attack (exe FUN_00a5fa50 builds the victims, FUN_00a61c50 hits them as the rider passes): the path
    /// points from index 1 (the start cell is skipped) up to, but not including, the last
    /// RideByAttack_LastPointsToExclude points are scanned; every enemy next to one of them is a victim, attached to the
    /// point it is closest to (first such point wins ties) and hit in path order. The damage is the base roll through the
    /// attack/defence stage with a luck multiplier of exactly 1 (no luck roll), truncated, then ×RideByAttack_DamageCoef
    /// truncated — dealt directly: no retaliation and no on-hit effects.
    /// </summary>
    void RideByAttack(Unit u, List<Cell> path, Unit primary)
    {
        if (!HasAbilityNow(u, Ab.RideByAttack) || path.Count < 2) return;
        int exclude = (int)Stats.Get("combat/Creatures/Dungeon/RideByAttack_LastPointsToExclude", 1);
        double coef = Stats.Get("combat/Creatures/Dungeon/RideByAttack_DamageCoef", 0.25);
        var victims = new Dictionary<int, (int Index, int Dist)>();
        for (int i = 1; i < path.Count - exclude; i++)
        {
            foreach (var e in S.Living.Where(e => Controller(e) != Controller(u) && !e.IsObject && e.Id != primary.Id))
            {
                int d = Distance(u, path[i], e, e.Pos);
                if (d > 1) continue;
                if (!victims.TryGetValue(e.Id, out var v) || d < v.Dist) victims[e.Id] = (i, d);
            }
        }
        foreach (var id in victims.OrderBy(kv => kv.Value.Index).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList())
        {
            var e = S.Unit(id);
            if (e == null || !e.Alive) continue;
            // FUN_00a5c790 is called with a fixed luck multiplier of 1.0 (no luck or favoured-enemy roll); its integer
            // result is multiplied in single precision and truncated (FISTP under OR AH,0x0C at 00a61d42).
            int full = ComputeDamage(u, e, new Hit { Kind = AttackKind.Ability, Secondary = true }, ranged: false, luckMult: 1);
            int dmg = (int)(full * (float)coef);
            Emit(new AbilityTriggered(u.Id, Ab.RideByAttack, e.Id));
            if (dmg > 0) u.Frags += DealDamage(e, dmg, u.Id, AttackKind.Ability, Ab.RideByAttack) * e.Tier;
        }
    }


    void OnHitExtras(Unit a, Unit t, bool melee, bool retaliation, int dmg)
    {
        if (!a.Alive || !t.Alive || dmg <= 0) return;
        // Mana Drain: drains mana from casters and resurrects as many Spectres (p.178).
        if (HasAbilityNow(a, Ab.ManaDrain) && t.Mana > 0)
        {
            int drained = Math.Min(a.Count, t.Mana);
            t.Mana -= drained;
            Emit(new ManaChanged(t.Id, t.Mana));
            Emit(new AbilityTriggered(a.Id, Ab.ManaDrain, t.Id));
            Heal(a, drained * a.MaxHealthCache, resurrect: true, Ab.ManaDrain);
        }
        // Deadly Strike is resolved in the strike loop (Strike → DeadlyStrikeKills, exe FUN_00a5bf60).
    }

    /// <summary>Phoenix Rebirth: once per combat a killed stack rises again with as many creatures (p.179).</summary>
    bool TryRebirth(Unit t)
    {
        // A Phantom Forces copy (SPELL_PHANTOM, 0x28) never rises again (exe FUN_00a58bb0).
        if (!t.Has(Ab.Rebirth) || t.IsClone || t.AbilityUses.ContainsKey(Ab.Rebirth)) return false;
        t.AbilityUses[Ab.Rebirth] = 1;
        Emit(new AbilityTriggered(t.Id, Ab.Rebirth));
        t.Count = 0;
        Heal(t, t.InitialCount * t.MaxHealthCache, resurrect: true, Ab.Rebirth);
        return t.Count > 0;
    }

    IEnumerable<AbilityInfo> MoreUnitAbilities(Unit u)
    {
        if (u.Has(Ab.LuckGambler) && u.AbilityUses.GetValueOrDefault(Ab.LuckGambler) < 3) yield return new(Ab.LuckGambler, AbilityTargeting.Any);
        if (u.Has(Ab.RandomCaster) && u.AbilityUses.GetValueOrDefault(Ab.RandomCaster) < 3) yield return new(Ab.RandomCaster, AbilityTargeting.Enemy);
        if (u.Has(Ab.RandomCasterBless) && u.AbilityUses.GetValueOrDefault(Ab.RandomCasterBless) < 3) yield return new(Ab.RandomCasterBless, AbilityTargeting.Any);
        if (u.Has(Ab.Incinerate)) yield return new(Ab.Incinerate, AbilityTargeting.Enemy, NeedsAdjacency: true);
        if (u.Has(Ab.AmmoSteal) && !u.AbilityUses.ContainsKey(Ab.AmmoSteal)) yield return new(Ab.AmmoSteal, AbilityTargeting.Enemy);
        if (u.Has(Ab.SyphonMana) && Enemy(Controller(u)).Mana > 0) yield return new(Ab.SyphonMana, AbilityTargeting.None);
        if (u.Has(Ab.Scavenger)) yield return new(Ab.Scavenger, AbilityTargeting.DeadFriend);
        if (u.Has(Ab.GoblinThrower) && Adjacent(u).Any(o => o.Side == u.Side && o.CreatureId.StartsWith("CREATURE_GOBLIN")))
            yield return new(Ab.GoblinThrower, AbilityTargeting.Enemy);
        foreach (var more in UnitAbilities3(u)) yield return more;
    }

    /// <summary>Handles abilities from <see cref="MoreUnitAbilities"/>; returns null if not one of them.</summary>
    string? DoMoreAbilities(Unit u, UseAbilityCmd c, Unit? t, out bool handled)
    {
        handled = true;
        switch (c.Ability)
        {
            case Ab.LuckGambler:
            {
                // Wheel of Fortune: random 1..3 Luck to a friend or from a foe; opposite effects dispel (p.182).
                u.AbilityUses[Ab.LuckGambler] = u.AbilityUses.GetValueOrDefault(Ab.LuckGambler) + 1;
                int v = S.Rng.Range(1, 3) * (Controller(t!) == Controller(u) ? 1 : -1);
                var cur = t!.GetEffect("FX_WHEEL_OF_FORTUNE");
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                // exe 00985705: Range(1, 3), negated for an enemy, always (re)applied with the new value — an opposite
                // effect is replaced, not cancelled.
                if (cur != null) RemoveEffect(t, "FX_WHEEL_OF_FORTUNE");
                AddEffect(t, new Effect { Id = "FX_WHEEL_OF_FORTUNE", Value = v, Positive = v > 0, SourceSide = u.Side });
                return null;
            }
            case Ab.RandomCaster:
            case Ab.RandomCasterBless:
            {
                // Random Curse / Random Blessing: a random level 1-3 Dark curse (or Light blessing on friends), Advanced mastery,
                // three times per combat (p.180, p.306).
                bool friend = Controller(t!) == Controller(u);
                if (friend && c.Ability == Ab.RandomCaster) return "djinns can only curse enemies";
                string school = friend ? "MAGIC_SCHOOL_LIGHT" : "MAGIC_SCHOOL_DARK";
                var pool = Db.Spells.Values.Where(sp => sp.School == school && sp.Level is >= 1 and <= 3 && !sp.Id.StartsWith("SPELL_MASS_")
                    && sp.Duration.Length > 0 && sp.Target == (friend ? "TARGET_FRIEND" : "TARGET_HOSTILE")).OrderBy(sp => sp.Id).ToList();
                if (pool.Count == 0) return "no spell available";
                u.AbilityUses[c.Ability] = u.AbilityUses.GetValueOrDefault(c.Ability) + 1;
                var spell = pool[S.Rng.Next(pool.Count)];
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                ApplyEffectSpell(u.Id, Controller(u), spell, Mastery.Advanced, CreatureSpellPower(u.Count, Math.Max(1, u.WeeklyGrowth)), t, u.Level, null);
                return null;
            }
            case Ab.Incinerate:
            {
                // Fly up and strike an adjacent enemy for +50% damage without retaliation (p.176).
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                Strike(u, t, new Hit { Kind = AttackKind.Melee, Multiplier = Stats.Get("combat/Creatures/Dungeon/IncinerateCoef", 1.5) }, pattern: true);
                return null;
            }
            case Ab.AmmoSteal:
            {
                // One aimed hostile target (spell Ammo_Steal.xdb IsAimed, exe 009865a0); the range check is the
                // engine's (speed) — the exe's AmmoSteal_DistanceDecrease / CalcDistanceIgnoreObstacles test is not traced.
                if (t!.MaxShots <= 0 || t.IsWarMachine || Distance(u, t) > SpeedOf(u)) return "target out of reach";
                u.AbilityUses[Ab.AmmoSteal] = 1;
                int n = AmmoStealAmount(u, t);
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                if (n > 0) t.Shots -= n;
                return null;
            }
            case Ab.SyphonMana:
            {
                var eh = Enemy(Controller(u));
                int drained = ManaDrainAmount(u, eh.Mana, syphon: true);   // exe FUN_0097bd60 (spell 0x13f)
                eh.Mana -= drained;
                Emit(new ManaChanged(Actor.Hero(eh.Side), eh.Mana));
                Emit(new AbilityTriggered(u.Id, c.Ability));
                var casters = S.LivingOf(u.Side).Where(o => o.MaxMana > 0).ToList();
                // Even split, the remainder going to the first stacks of the list (exe: amount / n + (i < amount % n)).
                int total = drained;
                for (int i = 0; i < casters.Count && total > 0; i++)
                {
                    int share = total / casters.Count + (i < total % casters.Count ? 1 : 0);
                    if (share <= 0) continue;
                    casters[i].Mana += share;
                    Emit(new ManaChanged(casters[i].Id, casters[i].Mana));
                }
                return null;
            }
            case Ab.Scavenger:
            {
                if (t == null || t.Alive || Distance(u, t) > SpeedOf(u)) return "corpse out of reach";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                Heal(u, ScavengerHeal(t), resurrect: true, c.Ability);
                t.Removed = true;
                return null;
            }
            case Ab.GoblinThrower:
            {
                var goblins = Adjacent(u).Where(o => o.Side == u.Side && o.CreatureId.StartsWith("CREATURE_GOBLIN")).OrderByDescending(o => o.TopHealth).First();
                Emit(new AbilityTriggered(u.Id, c.Ability, t!.Id));
                DealDamage(goblins, goblins.TopHealth, u.Id, AttackKind.Ability, c.Ability);
                Strike(u, t!, new Hit { Kind = AttackKind.Ranged }, pattern: false, rangedRetaliation: true);
                return null;
            }
        }
        return DoAbilities3(u, c, t, out handled);
    }
}
