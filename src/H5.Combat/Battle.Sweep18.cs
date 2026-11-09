using H5.Core.Data;
namespace H5.Combat;

// Combat audit sweep 18 (docs/rules/audit/combat_sweep_18.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    internal const string AvatarOfDeathSpell = "SPELL_ABILITY_AVATAR_OF_DEATH";
    const string AvatarOfDeathMark = "SUMMON_SPELL:" + AvatarOfDeathSpell;
    Cell? _avatarOfDeathPos;

    /// <summary>
    /// The knowledge term of a unique summon's health (the summon info's +0x2c): the caster's hero object (vf 0x74 for
    /// Conjure Phoenix 00861fee, vf 0x78 for Avatar of Death 0086234f — both 00bc2b20 on the combat hero vtable 00e8499c,
    /// then vf 0 = 00819270) asked for vf 0x1c (Knowledge, 00b4cd80: at least 1), or 1 without one.
    /// </summary>
    static int UniqueSummonKnowledge(HeroState? h) => h == null ? 1 : Math.Max(1, h.Knowledge);

    /// <summary>
    /// The summon position FUN_00860f20(out, combat; attacker flag (FUN_00af1c60: the caster's army is combat vf 0x84(1)),
    /// creature size (record +0xdc)): the free-anchor grid FUN_00b3fac0(0, size, 0, 0) (obstacles and every living unit,
    /// dilated for size &gt; 1), then the field columns from the caster's own edge — exe x = 2 … W − 3 for the attacker,
    /// W − 3 … 2 for the defender (W = the field width vf 0x44, two border columns each side) — and in each the rows
    /// y = 1 … H − 2 top down; the first column with a free anchor gives one of them by the battle RNG
    /// (combat vf 0xc → vf 0xc(0, n − 1)); with a null out pointer (the cast test 00862470) nothing is drawn. The exe
    /// anchor is the footprint's maximum corner on the bordered field: rewrite cell = (x − 2, y − 1) − (size − 1).
    /// </summary>
    internal Cell? ExeSummonPosition(int side, int size, Rng? rng)
    {
        var probe = new Unit { Id = -1, Size = size };
        for (int i = 0; i < S.Width; i++)
        {
            int ax = side == 0 ? i : S.Width - 1 - i;
            var col = new List<Cell>();
            for (int ay = 0; ay < S.Height; ay++)
            {
                var p = new Cell(ax - (size - 1), ay - (size - 1));
                if (p.X >= 0 && p.Y >= 0 && LandingFits(probe, p)) col.Add(p);
            }
            if (col.Count > 0) return rng == null ? col[0] : col[rng.Range(0, col.Count - 1)];
        }
        return null;
    }

    /// <summary>
    /// The summoned stack of FUN_00c56820 (FUN_00c557a0 cell test, ctor FUN_008abad0): its base ctor FUN_00a73e70 gives
    /// every unit the battle RNG's float vf 8(0, 0.25f) as its ATB (the turn object FUN_00a2e380), drawn after the
    /// position; the turn counter moves (FUN_00a2db60). The engine gave summons 0.05 · Random(6).
    /// </summary>
    Unit ExeSummonAt(int side, string creature, int count, string by, Cell pos)
    {
        var u = BattleSetup.AddCreature(Db, S, side, creature, count, pos, summoned: true);
        u.Atb = (float)S.Rng.RangeD(0f, 0.25f);
        u.HasMovedFromStart = true;
        Emit(new UnitSummoned(u.Id, creature, count, side, u.Pos, by));
        seqDirty = true;
        return u;
    }

    int AvatarOfDeathSize() =>
        Db.AvatarOfDeath?.Element("PrototypeCreature")?.Value.Trim() is { Length: > 0 } pc && Db.Creatures.TryGetValue(pc, out var d) ? d.CombatSize : 0;

    /// <summary>
    /// The cast test of SPELL_ABILITY_AVATAR_OF_DEATH (0097c320 → FUN_00862470): refused while the caster still has a
    /// unit linked for the spell (vf 0x30 = FUN_00a6eee0 over the hero's (spell, unit) pairs; the avatar's death drops the
    /// pair — 008a6e20 → the dead unit's vf 0x2b0 = FUN_00a730b0 → the partner's vf 0x29c), when vf 0x220 (FUN_00bc0030,
    /// the mana test the cast path makes anyway) fails, or when there is no summon position (FUN_00860f20 without out).
    /// </summary>
    internal string? AvatarOfDeathRefusal(HeroState h)
    {
        if (h.Links.ContainsKey(AvatarOfDeathSpell)) return "avatar of death already summoned";
        int size = AvatarOfDeathSize();
        if (size == 0 || ExeSummonPosition(h.Side, size, null) == null) return "no place for the avatar";
        return null;
    }

    /// <summary>
    /// The cast's own position draw (FUN_00862570 @ 0086262e), before the hero's cast vf 0x224 = FUN_00bbf610 spends the
    /// mana (Erratic Mana's rolls follow it).
    /// </summary>
    internal void AvatarOfDeathPrePick(HeroState h) => _avatarOfDeathPos = ExeSummonPosition(h.Side, AvatarOfDeathSize(), S.Rng);

    /// <summary>
    /// Avatar of Death (FUN_00984a00 case 0xc8 → FUN_00862570). FUN_00862320 builds the summon info (ctor FUN_00862970)
    /// with the DefaultStats Necromancer AvatarOfDeath_Creature record (RPGStats +0x880 → 008628f0), the cast power
    /// (vf 0x244), the caster level (vf 0x23c = FUN_00bbfb90: hero level), the knowledge term (<see cref="UniqueSummonKnowledge"/>),
    /// divisor 1 (the phoenix's is from its damage table) and +0x34 = the spell 0xc8. The position comes first
    /// (<see cref="AvatarOfDeathPrePick"/>; none → nothing), then the cast (vf 0x130 animation, vf 0x224), the creature
    /// FUN_00c56820 (count 1; the base ctor FUN_00a73e70 gives it the battle RNG's float in [0, 0.25) as its ATB, like
    /// every unit; the turn counter moves, FUN_00a2db60) and the link vf 0x298(0xc8, avatar). Its stats and abilities are
    /// the record's at level / 1 (<see cref="ApplyUniqueCreatureStats"/>, HasAbility FUN_008a34c0); it stays until killed.
    /// The engine had no cast (the spell was not offered).
    /// </summary>
    void AvatarOfDeath(int side, HeroState? hero, double power, int level)
    {
        var pos = _avatarOfDeathPos;
        _avatarOfDeathPos = null;
        var rec = Db.AvatarOfDeath;
        if (hero == null || pos is not Cell p || rec?.Element("PrototypeCreature")?.Value.Trim() is not { Length: > 0 } proto
            || !Db.Creatures.ContainsKey(proto)) return;
        var u = ExeSummonAt(side, proto, 1, AvatarOfDeathSpell, p);
        ApplyUniqueCreatureStats(u, rec, power, level, 1, UniqueSummonKnowledge(hero));
        u.AbilityUses[AvatarOfDeathMark] = 1;
        if (u.Alive) hero.Links[AvatarOfDeathSpell] = u.Id;
    }

    /// <summary>
    /// The death branch of the damage routine FUN_008a6e20 (008a748b..008a74f2): a unit whose summon info's spell
    /// (FUN_004bb080, +0x34) is 0xc8 makes its army's hero (vf 0xc → vf 0xc) set its mana to 0 (vf 0x22c(0) =
    /// FUN_00bbfac0, the field the casts spend) — "If this creature dies, the Hero will lose all his Mana points".
    /// </summary>
    void AvatarOfDeathDied(Unit t)
    {
        if (!t.AbilityUses.ContainsKey(AvatarOfDeathMark)) return;
        var h = S.Heroes[t.Side];
        if (!h.Present) return;
        h.Mana = 0;
        Emit(new ManaChanged(Actor.Hero(h.Side), 0));
    }
    /// <summary>Action-counted effects whose count the turn start takes (vf 0x268 = FUN_00a72c80 at 00bbc923 and
    /// 00bbb9df), not the turn end.</summary>
    static bool TurnStartCounted(string id) => id is H5.Combat.Fx.Dash or H5.Combat.Fx.Poison or FxVenom;

    /// <summary>
    /// vf 0x268(id, now) = FUN_00a72c80: every effect of that id whose duration is not a marker (0x7fffffff / 0x7ffffffd;
    /// an action-counted effect has 0x7ffffffe, 004e4d30) loses one count (+0x20, FUN_004e3dd0) and, below 1, expires at
    /// once (FUN_00a72650).
    /// </summary>
    void CountDown(Unit u, string id)
    {
        if (u.GetEffect(id) is not { } e || e.Actions < 0) return;
        if (--e.Actions < 1) RemoveEffect(u, id);
    }

    /// <summary>
    /// FUN_00bbc8e0, the turn start tail: run by the turn start FUN_00bbcb40 after the unit's vf 0xd8 and by the bad
    /// morale FUN_00a6cd80 (→ 00bbcaa0). In order: SPELL_ABILITY_DASH (0xb0) counts down when present (vf 0x28 → vf 0x268;
    /// the cast 00987e8a gives Dash_Duration, RPGStats +0x2f4); Battle Frenzy (0x112), Berserker Rage (0xf5) and
    /// Preparation (0x11e) end (vf 0x270); the Defile Magic release; the Battle Dive finish FUN_00bbc510 (kept in the
    /// turn start, <see cref="BattleDiveTurnStart"/>); FUN_00bbb900 unless the unit is in placement state 1 (vf 0x4c, the
    /// diving griffins); FUN_00bbb390 when it carries SPELL_ABILITY_GATING (0x9f). True when a gated stack arrived (its
    /// ATB is then below 1, so it does not act: FUN_00bbcb40 acts only while ATB ≥ 1).
    /// </summary>
    internal bool ExeTurnStartTail(Unit u)
    {
        CountDown(u, H5.Combat.Fx.Dash);
        Sweep7TurnStart(u);
        RemoveEffect(u, PreparationEffect);
        if (!u.HasEffect(FxDiving)) DamageOverTimeTick(u);
        if (u.Alive && u.HasEffect(GatedEffect)) { GatedArrival(u); return true; }
        return false;
    }

    /// <summary>
    /// FUN_00bbb900: first Searing Aura (FUN_00bbb650: the enemy aura stacks next to the unit, fire through FUN_0097d600,
    /// "SEARING_AURA_HIT"), then the unit's effect list (vf 0x24c) in order — SPELL_PLAGUE (0xe), poison (0xb4), Set Afire
    /// (0x10e) and venom (0x128): damage = the effect's +0x28; poison and venom first count down (vf 0x268, expiring at
    /// once below 1, the damage still dealt); Set Afire on a Mark of Fire carrier (0xf6) × clamp(MarkOfFire_FireDamage-
    /// Multiplier, RPGStats +0x374, 0, 1000), chop (00bbbab1..00bbbaf6); each hit first gives the stack ReceiveDDSpellRP
    /// (vf 0x7c with DemonRage +0x3c, 00bbb9ea) and is dealt by the stack damage path FUN_00a616e0 (no fire immunity);
    /// the walk stops when the stack is dead (vf 0x5c). The engine dealt poison, venom, Plague, Set Afire (with fire
    /// immunity) and the aura in a fixed order with no rage, and also to diving griffins.
    /// </summary>
    void DamageOverTimeTick(Unit u)
    {
        foreach (var o in Adjacent(u).Where(o => Controller(o) != Controller(u) && HasAbilityNow(o, Ab.SearingAura)).ToList())
            if (u.Alive) DealElementalDamage(u, (int)Stats.Get("combat/Creatures/Inferno/SearingAuraDamageHits", 10) * o.Count, Elements.Fire, o.Id, Ab.SearingAura);
        foreach (var e in u.Effects.ToList())
        {
            if (!u.Alive) break;
            if (e.Id is not ("SPELL_PLAGUE" or H5.Combat.Fx.Poison or H5.Combat.Fx.Ignite or FxVenom)) continue;
            int dmg = (int)e.Value;
            if (e.Id is H5.Combat.Fx.Poison or FxVenom) CountDown(u, e.Id);
            else if (e.Id == H5.Combat.Fx.Ignite && u.HasEffect(H5.Combat.Fx.MarkOfFire))
                dmg = (int)(Math.Clamp((float)Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2), 0f, 1000f) * (double)dmg);
            RageEvent(u, "ReceiveDDSpellRP");
            string by = e.Id switch { H5.Combat.Fx.Poison => "POISON", FxVenom => "VENOM", H5.Combat.Fx.Ignite => "IGNITE", _ => "SPELL_PLAGUE" };
            DealDamage(u, dmg, -1, AttackKind.Other, by);
        }
    }

    /// <summary>
    /// FUN_00bbb390, a gated stack's first turn: when it can be placed (FUN_00c558b0: its anchor or the nearest free one,
    /// FUN_00b3fac0 / FUN_00b3e1e0 — the stack stands on its own cell here, so always) the gating effect 0x9f ends
    /// (vf 0x270), the arrival plays and its ATB becomes clamp(GatedStartATB, RPGStats +0x5c8, 0, 1) without the ready
    /// hooks (vf 0x184 / FUN_00a2d6a0); otherwise its ATB is set to 0.5 (vf 0x18c). Either way it does not act. The
    /// engine kept the effect for the whole combat (gated initiative and no retaliation for ever) and let it act.
    /// </summary>
    void GatedArrival(Unit u)
    {
        RemoveEffect(u, GatedEffect);
        Emit(new AbilityTriggered(u.Id, GatedEffect));
        SetAtb(u.Id, Math.Clamp((float)Stats.Get("combat/HeroSkills/DemonLord/Gating/GatedStartATB", 0), 0f, 1f));
    }
    const string DefileLink = "DEFILE_LINK:";

    /// <summary>
    /// FUN_00c50920: the defilers of a caster — the units with Defile Magic (0x6d) whose SPELL_ABILITY_DEFILE_MAGIC
    /// (0x146) links (vf 0x30, written by the cast 00984a00 case 0x146 → vf 0x298) hold it. Every defiler that defiled
    /// the stack since its own last turn start counts, not only the last one (the effect itself is one: a second cast
    /// refreshes it, 00a73ab0).
    /// </summary>
    internal List<Unit> DefilersOf(Unit caster)
    {
        int last = caster.GetEffect(FxDefiled)?.SourceUnit ?? -1;   // the last cast's defiler is linked too
        return S.Units.Where(d => d.Alive && (d.Id == last || d.AbilityUses.ContainsKey(DefileLink + caster.Id)) && HasAbilityNow(d, Ab.DefileMagic)).ToList();
    }

    /// <summary>
    /// The defiler's turn start (FUN_00bbc8e0, vf 0x28c(0x6d)): each stack in its 0x146 links loses the effect
    /// (vf 0x270(0x146, …, 1) — the shared effect, whoever else defiled it) and its links are cleared (vf 0x2a4(0x146)).
    /// </summary>
    void ReleaseDefiled(Unit u)
    {
        foreach (var v in S.Units.Where(v => v.GetEffect(FxDefiled)?.SourceUnit == u.Id).ToList()) RemoveEffect(v, FxDefiled);
        foreach (var key in u.AbilityUses.Keys.Where(k => k.StartsWith(DefileLink, StringComparison.Ordinal)).ToList())
        {
            if (S.Unit(int.Parse(key[DefileLink.Length..])) is { } v) RemoveEffect(v, FxDefiled);
            u.AbilityUses.Remove(key);
        }
    }

    /// <summary>
    /// The creature caster's first N(t) term of the Defile Magic roll (00c50b38..00c50b74): with the caster's vf 0x6c
    /// creature interface (vtable 00e4fe9c), [record +0x100 BaseCreature ≠ 0 (FUN_004babd0)] + its vf 0x1c (FUN_008a2110:
    /// 8 for a unique summon (+0x88 summon info), else the record's +0x8c CreatureTier, FUN_004ba900), then NEG / SBB / NEG:
    /// 1 when either is non-zero, else 0 — so 1 for every creature with a tier.
    /// </summary>
    internal int DefileCasterTerm(Unit caster)
    {
        bool hasBase = Db.Creatures.TryGetValue(caster.CreatureId, out var d) && d.BaseCreature is { Length: > 0 } b && b != "CREATURE_UNKNOWN";
        int tier = caster.AbilityUses.ContainsKey(AvatarOfDeathMark) ? 8 : d?.Tier ?? caster.Tier;
        return hasBase || tier != 0 ? 1 : 0;
    }
}
