using H5.Core.Data;

namespace H5.Combat;

// Creature abilities added from the executable and the game's own ability texts:
// Treachery, Pack Hunter, Taunt, Eldritch Aura, Bond of Light, Power Feed, Howl, Set Snares,
// Defile Magic, Treant Union, Flamewave, Pack Dive, Battle Dive, Magic Magnetism.
// See docs/rules/EXE_FINDINGS.md for which parts are confirmed against H5_Game.exe.
public sealed partial class Battle
{
    const string FxDefiled = "FX_DEFILED", FxDiving = "FX_DIVING";

    IEnumerable<AbilityInfo> UnitAbilities3(Unit u)
    {
        bool Once(string id) => !u.AbilityUses.ContainsKey(id);
        var h = HeroOf(u);
        if (u.Has(Ab.PowerFeed) && h.Present && !(h.Links.TryGetValue(Ab.PowerFeed, out var linked) && linked == u.Id)
            && (!Stats.Bool("combat/Creatures/Sylvan/PowerFeedOnlyRanger") || h.Class == "HERO_CLASS_RANGER"))
            yield return new(Ab.PowerFeed, AbilityTargeting.None);
        if (u.Has(Ab.Howl) && Once(Ab.Howl)) yield return new(Ab.Howl, AbilityTargeting.None);
        if (u.Has(Ab.SetSnares)) yield return new(Ab.SetSnares, AbilityTargeting.Cell);
        if (u.Has(Ab.DefileMagic)) yield return new(Ab.DefileMagic, AbilityTargeting.Enemy);
        if (u.Has(Ab.TreeantUnion) && Once(Ab.TreeantUnion)) yield return new(Ab.TreeantUnion, AbilityTargeting.Friend);
        if (u.Has(Ab.PackDive) && Once(Ab.PackDive)) yield return new(Ab.PackDive, AbilityTargeting.Cell);
        if (u.Has(Ab.BattleDive)) yield return new(Ab.BattleDive, AbilityTargeting.Cell);
    }

    /// <summary>Handles abilities from <see cref="UnitAbilities3"/>; sets handled=false for anything else.</summary>
    string? DoAbilities3(Unit u, UseAbilityCmd c, Unit? t, out bool handled)
    {
        handled = true;
        switch (c.Ability)
        {
            case Ab.PowerFeed:
            {
                // Hero Spell Power + the creature's own spell power, capped by the hero's Knowledge,
                // until the end of combat or the creature's death; not cumulative.
                var h = HeroOf(u);
                h.Links[Ab.PowerFeed] = u.Id;
                Emit(new AbilityTriggered(u.Id, c.Ability));
                return null;
            }
            case Ab.Howl:
            {
                // Once per battle: a friendly stack of equal power (same creature and size) appears next to
                // the wolves with a random ATB in [0, HowlMaxATBValueAtStart] (exe 00a5cbc0).
                u.AbilityUses[Ab.Howl] = 1;
                double atb = S.Rng.RangeD(0, Stats.Get("combat/Creatures/Neutral/HowlMaxATBValueAtStart", 0.5));
                Emit(new AbilityTriggered(u.Id, c.Ability));
                if (SummonStackNear(u, u.CreatureId, u.Count, c.Ability, atb) == null) return "no room to summon";
                return null;
            }
            case Ab.SetSnares:
            {
                var cell = c.Cell!.Value;
                if (S.Obstacles.Contains(cell) || S.At(cell) != null) return "cell must be empty";
                // exe 0098614d: n = max(1, N / max(1, weekly growth)) (integer division).
                int ng = Math.Max(1, u.Count / Math.Max(1, u.WeeklyGrowth));
                var f = new FieldObject
                {
                    Id = S.NextFieldId++, Kind = "SNARE", Side = Controller(u), Cells = { cell },
                    Power = ng, SourceUnit = u.Id, HiddenFromEnemy = true,
                };
                S.Fields.Add(f);
                Emit(new AbilityTriggered(u.Id, c.Ability));
                Emit(new FieldChanged(f.Id, f.Kind, f.Cells, false));
                return null;
            }
            case Ab.DefileMagic:
            {
                if (t!.Spells.Count == 0 && t.MaxMana <= 0) return "target is not a caster";
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                AddEffect(t, new Effect { Id = FxDefiled, SourceUnit = u.Id, SourceSide = Controller(u), Value = u.Count, Positive = false, Dispellable = false });
                return null;
            }
            case Ab.TreeantUnion:
            {
                // Dryads heal/resurrect a Treant stack, or are healed by an adjacent Treant stack.
                string baseTreant = "CREATURE_TREANT";
                bool targetIsTreant = t!.CreatureId == baseTreant || (Db.Creatures.TryGetValue(t.CreatureId, out var td) && td.BaseCreature == baseTreant);
                if (!targetIsTreant) return "target must be treants";
                u.AbilityUses[Ab.TreeantUnion] = 1;
                Emit(new AbilityTriggered(u.Id, c.Ability, t.Id));
                // exe 00986377: the treants get caster count × TreeanUnionCasterHits (+0x2cc), the dryads get
                // target count × TreeanUnionTargetHits (+0x2d0) — both counts taken before either heal.
                int toTreants = u.Count * (int)Stats.Get("combat/Creatures/Sylvan/TreeanUnionCasterHits", 4);
                int toDryads = t.Count * (int)Stats.Get("combat/Creatures/Sylvan/TreeanUnionTargetHits", 20);
                Heal(t, toTreants, resurrect: true, c.Ability);
                Heal(u, toDryads, resurrect: true, c.Ability);
                return null;
            }
            case Ab.PackDive:
            case Ab.BattleDive:
            {
                // Delayed air attack: the griffins leave the field and strike PackDive_DelayBeforAttack
                // turns later (exe spells 0x9a/0x15a, finishing strikes 0x9b ×2 / 0x15b ×PackDive_DamageMultiplier).
                var cell = c.Cell!.Value;
                if (c.Ability == Ab.PackDive) u.AbilityUses[Ab.PackDive] = 1;
                double delay = Stats.Get("combat/Creatures/Haven/PackDive_DelayBeforAttack", 1);
                var f = new FieldObject
                {
                    Id = S.NextFieldId++, Kind = c.Ability == Ab.PackDive ? "PACK_DIVE" : "BATTLE_DIVE",
                    Side = Controller(u), Cells = { cell }, SourceUnit = u.Id, Remaining = delay,
                    Damage = u.Pos.X, Power = u.Pos.Y,      // origin, for the dive direction
                };
                S.Fields.Add(f);
                AddEffect(u, new Effect { Id = FxDiving, Remaining = delay + 1e-6, Positive = true, Dispellable = false });
                AddEffect(u, new Effect { Id = H5.Combat.Fx.Invisible, Remaining = delay + 1e-6, Positive = true, Dispellable = false });
                Emit(new AbilityTriggered(u.Id, c.Ability));
                Emit(new FieldChanged(f.Id, f.Kind, f.Cells, false));
                return null;
            }
        }
        handled = false;
        return null;
    }

    // ------------------------------------------------------------------ passive hooks

    /// <summary>
    /// Treachery (exe FUN_008a0b60, vtable slot 0x68 of 00e4fe9c): once (flag +0x80), when count / reference count is
    /// below the threshold the goblins change sides. Threshold, single precision: (float)(Treachery × 0.01), minus
    /// level × GoblinKing_TreacheryBonusPerLevel when the stack carries SPELL_SPEC_GOBLIN_KING (0x13b, its +0x20 =
    /// the hero's level); the reference (+0xc) is the stack's initial count, at least 1; the ratio is compared in
    /// extended precision against the float threshold, so exactly 30 % (0.3 &lt; 0.300000012f) already defects.
    /// The traitors lose Hypnotize (0x14) and Seduce (0x148) (vf 0x270).
    /// </summary>
    void CheckTreachery(Unit t)
    {
        if (!t.Alive || !t.Has(Ab.Treachery) || t.AbilityUses.ContainsKey(Ab.Treachery)) return;
        float threshold = (float)((int)Stats.Get("combat/Creatures/Stronghold/Treachery", 30) * 0.01);
        var gk = HeroOf(t);
        if (gk.Present && gk.Specialization == "HERO_SPEC_GOBLIN_KING")
            threshold = (float)(threshold - gk.Level * (double)(float)Stats.Get("combat/HeroSpecializations/Barbarian/GoblinKing_TreacheryBonusPerLevel", 0.01));
        int reference = Math.Max(1, t.InitialCount);
        if (!((double)t.Count / reference < threshold)) return;
        t.AbilityUses[Ab.Treachery] = 1;
        t.Side = 1 - t.Side;
        if (t.HasEffect("SPELL_HYPNOTIZE")) RemoveEffect(t, "SPELL_HYPNOTIZE");
        if (t.HasEffect(H5.Combat.Fx.Seduced)) RemoveEffect(t, H5.Combat.Fx.Seduced);
        Emit(new AbilityTriggered(t.Id, Ab.Treachery));
    }

    /// <summary>
    /// Taunt (exe 00a65d50): each other enemy stack with TAUNT adjacent to the attack position may draw
    /// the melee attack to itself (trigger chance with Taunt_ProcFactor); the last success wins.
    /// </summary>
    Unit TauntRedirect(Unit attacker, Unit target, Cell from)
    {
        var result = target;
        foreach (var o in S.Living.Where(o => o.Id != target.Id && Controller(o) == Controller(target) && o.Has(Ab.Taunt)
                     && AdjacentAt(attacker, from, o)).ToList())
        {
            if (ProcRoll(o, attacker, Stats.Get("combat/Creatures/Stronghold/Taunt_ProcFactor", 1.2), soldiersLuckDoubles: true, secondRoll: false))
            {
                Emit(new AbilityTriggered(o.Id, Ab.Taunt, attacker.Id));
                result = o;
            }
        }
        return result;
    }

    /// <summary>
    /// Eldritch Aura (exe FUN_008a4b70 flag 0x1000): set on a stack when an adjacent stack of EITHER side (the enemy and
    /// the ally neighbour lists, not the stack itself) has the aura. A MAGIC_ATTACK shooter cannot shoot a flagged
    /// stack at all (FUN_00b3b330 returns 0) and its magic line skips flagged stacks (FUN_00a62030, vf 0x21c(0x1000)).
    /// </summary>
    bool EldritchFlag(Unit t) => S.Living.Any(o => o.Id != t.Id && HasAbilityNow(o, Ab.EldritchAura) && AreAdjacent(o, t));

    bool EldritchProtected(Unit attacker, Unit target, bool ranged) =>
        ranged && HasAbilityNow(attacker, Ab.MagicAttack) && EldritchFlag(target);

    /// <summary>Power Feed bonus to the hero's spell power (≤ Knowledge) while the feeding stack lives.</summary>
    double PowerFeedBonus(HeroState h)
    {
        if (!h.Links.TryGetValue(Ab.PowerFeed, out var id) || S.Unit(id) is not { Alive: true } u) return 0;
        return Math.Min(CreatureSpellPower(u.Count, Math.Max(1, u.WeeklyGrowth)), h.Knowledge);
    }

    bool _bondCopying;

    /// <summary>
    /// Bond of Light: a Light Magic effect spell landing on any stack is also applied to every Bond of Light
    /// creature, at Expert mastery (BondOfLight_MinDuplicatedEffectMastery), whoever cast it.
    /// </summary>
    void BondOfLightCopy(Unit original, Effect e)
    {
        if (_bondCopying || e.School != "MAGIC_SCHOOL_LIGHT" || e.SpellLevel <= 0 || Db.Spell(e.Id) is not { } sp) return;
        var m = (Mastery)Math.Max((int)Mastery.Expert, (int)Stats.Get("combat/Creatures/Sylvan/BondOfLight_MinDuplicatedEffectMastery", 3));
        _bondCopying = true;
        try
        {
            foreach (var b in S.Living.Where(b => b.Id != original.Id && b.Has(Ab.BondOfLight)).ToList())
                ApplyEffectSpell(e.SourceUnit, e.SourceSide >= 0 ? e.SourceSide : Controller(b), sp, m, e.Power, b, e.CasterLevel, null);
        }
        finally { _bondCopying = false; }
    }

    /// <summary>
    /// Defile Magic (fan manual p.324): resolve on the defiled caster's next spell. Returns the outcome:
    /// 1 fail, 2 half spell power, 3 double cost, 4 backfire (goblins' ATB reset; non-area hostile spell hits them).
    /// </summary>
    int DefileOutcome(Unit caster, H5.Core.Data.SpellDef sp)
    {
        var e = caster.GetEffect(FxDefiled);
        if (e == null) return 0;
        RemoveEffect(caster, FxDefiled);
        var goblins = S.Unit(e.SourceUnit);
        if (goblins is not { Alive: true }) return 0;
        int ng = (int)Math.Ceiling(goblins.Count / 25.0);
        int nt = (int)Math.Ceiling(caster.Tier + (caster.Upgraded ? 1 : 0) - 1 + caster.Count / (double)Math.Max(1, caster.WeeklyGrowth));
        int outcome = RollShifted(new[] { 10, 40, 40, 10 }, ng - nt) + 1;
        Emit(new AbilityTriggered(goblins.Id, Ab.DefileMagic, caster.Id));
        if (outcome == 4) SetAtb(goblins.Id, 0);
        return outcome;
    }

    /// <summary>
    /// Set Snares outcome (fan manual p.324): 0 stop, 1 two tiles less, 2 one tile less, 3 misfire (setter's ATB → 0).
    /// </summary>
    int SnareOutcome(Unit victim, FieldObject snare)
    {
        int ng = (int)snare.Power;
        int nt = (int)Math.Ceiling(victim.Tier + (victim.Upgraded ? 1 : 0) - 1 + victim.Count / (double)Math.Max(1, victim.WeeklyGrowth));
        return RollShifted(new[] { 25, 25, 25, 25 }, ng - nt);
    }

    /// <summary>
    /// Snares along a walking path (flyers only trigger them on landing): the first enemy snare touched
    /// fires, is removed, and stops the walk, shortens it by 2 or 1 tiles, or misfires (setter's ATB → 0).
    /// </summary>
    List<Cell> ApplySnares(Unit u, List<Cell> path)
    {
        int first = -1;
        FieldObject? snare = null;
        for (int i = u.Flying ? path.Count - 1 : 1; i < path.Count && snare == null; i++)
        {
            var cells = u.CellsAt(path[i]).ToList();
            snare = S.Fields.Find(f => f.Kind == "SNARE" && f.Side != Controller(u) && f.Cells.Any(cells.Contains));
            if (snare != null) first = i;
        }
        if (snare == null) return path;
        S.Fields.Remove(snare);
        Emit(new FieldChanged(snare.Id, snare.Kind, snare.Cells, true));
        int outcome = SnareOutcome(u, snare);
        Emit(new AbilityTriggered(snare.SourceUnit, Ab.SetSnares, u.Id));
        int last = path.Count - 1;
        int end = outcome switch
        {
            0 => first,
            1 => Math.Max(0, last - 2),
            2 => Math.Max(0, last - 1),
            _ => last,
        };
        if (outcome == 3 && S.Unit(snare.SourceUnit) is { Alive: true } setter) SetAtb(setter.Id, 0);
        // The creature cannot end on an occupied cell; step back until it fits.
        while (end > 0 && !Fits(u, path[end])) end--;
        return path.Take(end + 1).ToList();
    }

    /// <summary>
    /// Four-outcome roll where each point of advantage moves 5% from the last outcome towards the first
    /// (never below 5%), and each point of disadvantage the other way (p.324).
    /// </summary>
    int RollShifted(int[] baseChances, int advantage)
    {
        var p = baseChances.ToArray();
        for (int step = 0; step < Math.Abs(advantage); step++)
        {
            bool goblins = advantage > 0;
            int to = goblins ? 0 : p.Length - 1;
            // take 5% from the furthest outcome that still has more than 5%
            for (int k = 0; k < p.Length; k++)
            {
                int from = goblins ? p.Length - 1 - k : k;
                if (from == to) break;
                if (p[from] > 5) { p[from] -= 5; p[to] += 5; break; }
            }
        }
        int roll = S.Rng.Next(100);
        for (int i = 0, acc = 0; i < p.Length; i++) { acc += p[i]; if (roll < acc) return i; }
        return p.Length - 1;
    }

    /// <summary>
    /// Flamewave: on a melee attack, chance (FlameWave_ProcFactor) of a fire wave: a cone FlameWave_WaveLength
    /// rows long from the target cell, widening by one cell per side each row; row n takes D/n (physical).
    /// </summary>
    void Flamewave(Unit a, Unit t)
    {
        if (!HasAbilityNow(a, Ab.Flamewave) || !a.Alive || !ProcRoll(a, t, Stats.Get("combat/Creatures/Fortress/FlameWave_ProcFactor", 2), soldiersLuckDoubles: true, secondRoll: false))
            return;
        Emit(new AbilityTriggered(a.Id, Ab.Flamewave, t.Id));
        int dx = Math.Sign(t.Pos.X - a.Pos.X), dy = Math.Sign(t.Pos.Y - a.Pos.Y);
        if (dx == 0 && dy == 0) dx = a.Side == 0 ? 1 : -1;
        int length = (int)Stats.Get("combat/Creatures/Fortress/FlameWave_WaveLength", 5);
        var hit = new HashSet<int> { t.Id };
        for (int row = 1; row < length; row++)
        {
            var centre = new Cell(t.Pos.X + dx * row, t.Pos.Y + dy * row);
            for (int w = -row; w <= row; w++)
            {
                var c = dx != 0 && dy == 0 ? new Cell(centre.X, centre.Y + w)
                      : dx == 0 ? new Cell(centre.X + w, centre.Y)
                      : new Cell(centre.X + (w > 0 ? 0 : w), centre.Y + (w > 0 ? -w : 0));
                if (S.At(c) is not { } v || !hit.Add(v.Id) || Controller(v) == Controller(a) || v.IsObject) continue;
                int dmg = ComputeDamage(a, v, new Hit { Kind = AttackKind.Melee, Multiplier = 1.0 / (row + 1), Secondary = true }, false);
                DealDamage(v, dmg, a.Id, AttackKind.Ability, Ab.Flamewave);
            }
        }
    }

    /// <summary>Resolves an expired dive field: the griffins strike the cell's occupants (and, for Pack Dive, the line beyond).</summary>
    void ResolveDive(FieldObject f)
    {
        var u = S.Unit(f.SourceUnit);
        if (u is not { Alive: true }) return;
        RemoveEffect(u, FxDiving);
        RemoveEffect(u, H5.Combat.Fx.Invisible);
        var target = f.Cells[0];
        if (f.Kind == "BATTLE_DIVE")
        {
            if (S.At(target) is { } v && v.Id != u.Id)
                Strike(u, v, new Hit { Kind = AttackKind.Melee, Multiplier = 2 }, pattern: false);   // ×2 on anyone, friend or foe
            // Return to a random free cell.
            var free = new List<Cell>();
            for (int x = 0; x < S.Width; x++) for (int y = 0; y < S.Height; y++) { var c = new Cell(x, y); if (Fits(u, c)) free.Add(c); }
            if (free.Count > 0 && u.Alive) Teleport(u, free[S.Rng.Next(free.Count)]);
        }
        else
        {
            // Area of the finishing strike (exe FUN_0097a530 case 0x15a/0x15b): every cell of rows y and y+1 of the
            // target cell across the whole field width, enemy stacks only (FUN_00b3d850).
            double mult = Stats.Get("combat/Creatures/Haven/PackDive_DamageMultiplier", 0.75);
            var seen = new HashSet<int>();
            for (int x = 0; x < S.Width; x++)
                for (int y = target.Y; y < target.Y + 2; y++)
                    if (S.At(new Cell(x, y)) is { } v && v.Id != u.Id && Controller(v) != Controller(u) && !v.IsObject && seen.Add(v.Id) && u.Alive)
                        Strike(u, v, new Hit { Kind = AttackKind.Melee, Multiplier = mult }, pattern: false);
        }
    }

    /// <summary>
    /// Magic Magnetism: a golem caught in a Destruction area spell with other stacks absorbs
    /// MagicMagnetism_DamageRedirectPercentPerWeeklyGrowth% per weekly growth of their damage; half heals it.
    /// Returns the damage left for <paramref name="victim"/>.
    /// </summary>
    double MagnetismRedirect(Unit victim, double dmg, IReadOnlyCollection<Unit> areaVictims, H5.Core.Data.SpellDef sp, int side)
    {
        if (sp.School != "MAGIC_SCHOOL_DESTRUCTIVE" || sp.Id == "SPELL_ARMAGEDDON") return dmg;
        var golem = areaVictims.FirstOrDefault(g => g.Id != victim.Id && g.Alive && g.Has(Ab.Magnetism));
        if (golem == null) return dmg;
        double pct = Math.Min(100, Stats.Get("combat/Creatures/Academy/MagicMagnetism_DamageRedirectPercentPerWeeklyGrowth", 5)
                                   * golem.Count / (double)Math.Max(1, golem.WeeklyGrowth));
        double absorbed = dmg * pct / 100;
        if (absorbed <= 0) return dmg;
        Emit(new AbilityTriggered(golem.Id, Ab.Magnetism, victim.Id));
        DealSpellDamage(golem, sp, absorbed, side, ignoreResist: false);
        if (golem.Alive) Heal(golem, (int)(absorbed / 2), resurrect: false, Ab.Magnetism);
        return dmg - absorbed;
    }
}
