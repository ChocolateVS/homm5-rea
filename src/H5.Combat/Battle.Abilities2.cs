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
            Strike(r, t, new Hit { Kind = AttackKind.Ability, Multiplier = 0.5 }, pattern: false);
        }
    }

    /// <summary>Wheeling Attack: enemies next to the route take 25% damage without retaliating (p.182).</summary>
    void RideByAttack(Unit u, List<Cell> path, Unit primary)
    {
        if (!HasAbilityNow(u, Ab.RideByAttack) || path.Count < 2) return;
        int exclude = (int)Stats.Get("combat/Creatures/Dungeon/RideByAttack_LastPointsToExclude", 1);
        var route = path.Take(Math.Max(0, path.Count - exclude)).ToList();
        var hit = new HashSet<int> { primary.Id };
        foreach (var e in S.Living.Where(e => Controller(e) != Controller(u) && !e.IsObject).ToList())
        {
            if (hit.Contains(e.Id) || !route.Any(p => Distance(u, p, e, e.Pos) <= 1)) continue;
            hit.Add(e.Id);
            Strike(u, e, new Hit { Kind = AttackKind.Ability, Multiplier = Stats.Get("combat/Creatures/Dungeon/RideByAttack_DamageCoef", 0.25) }, pattern: false);
            if (!u.Alive) return;
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
        // Deadly Strike: 25% to kill half the target stack (p.315).
        if (melee && !retaliation && HasAbilityNow(a, Ab.DeadlyStrike) && t.Count > 1 && S.Rng.Chance(0.25))
        {
            Emit(new AbilityTriggered(a.Id, Ab.DeadlyStrike, t.Id));
            int half = t.Count / 2;
            DealDamage(t, (half - 1) * t.MaxHealthCache + t.TopHealth, a.Id, AttackKind.Ability, Ab.DeadlyStrike);
        }
    }

    /// <summary>Phoenix Rebirth: once per combat a killed stack rises again with as many creatures (p.179).</summary>
    bool TryRebirth(Unit t)
    {
        if (!t.Has(Ab.Rebirth) || t.AbilityUses.ContainsKey(Ab.Rebirth)) return false;
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
        if (u.Has(Ab.AmmoSteal) && !u.AbilityUses.ContainsKey(Ab.AmmoSteal)) yield return new(Ab.AmmoSteal, AbilityTargeting.None);
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
                if (cur != null && Math.Sign(cur.Value) != Math.Sign(v)) RemoveEffect(t, "FX_WHEEL_OF_FORTUNE");
                else AddEffect(t, new Effect { Id = "FX_WHEEL_OF_FORTUNE", Value = v, Positive = v > 0, SourceSide = u.Side });
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
                u.AbilityUses[Ab.AmmoSteal] = 1;
                int n = Math.Max(1, u.Count / Math.Max(1, u.WeeklyGrowth));       // p.181
                int range = SpeedOf(u);
                Emit(new AbilityTriggered(u.Id, c.Ability));
                foreach (var e in S.Living.Where(e => Controller(e) != Controller(u) && e.MaxShots > 0 && !e.IsWarMachine && Distance(u, e) <= range))
                    e.Shots = Math.Max(0, e.Shots - n);
                return null;
            }
            case Ab.SyphonMana:
            {
                var eh = Enemy(Controller(u));
                int drained = Math.Min(eh.Mana, (int)(u.Count * Stats.Get("combat/Creatures/Inferno/SyphonManaCoef", 0.5)));
                eh.Mana -= drained;
                Emit(new ManaChanged(Actor.Hero(eh.Side), eh.Mana));
                Emit(new AbilityTriggered(u.Id, c.Ability));
                var casters = S.LivingOf(u.Side).Where(o => o.MaxMana > 0).ToList();
                for (int i = 0; i < casters.Count && drained > 0; i++)
                {
                    int share = drained / (casters.Count - i);
                    casters[i].Mana += share; drained -= share;
                    Emit(new ManaChanged(casters[i].Id, casters[i].Mana));
                }
                return null;
            }
            case Ab.Scavenger:
            {
                if (t == null || t.Alive || Distance(u, t) > SpeedOf(u)) return "corpse out of reach";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                Heal(u, (int)(0.2 * t.InitialCount * t.BaseHealth), resurrect: true, c.Ability);   // p.180
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
