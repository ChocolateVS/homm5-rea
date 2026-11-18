using H5.Core.Data;

namespace H5.Combat;

// Inferno Gating (SPELL_ABILITY_GATING, exe spell 0x9f) — docs/rules/audit/combat_gating.md: traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Who may gate (exe FUN_00c54f50 at unit set-up, FUN_00860ce0 at the cast test): a stack of an army with a hero whose
    /// creature is of town 8 (Inferno, FUN_004ba910) and of tier (record +0x8c, FUN_004ba900) ≤ 2 × the hero's Gating
    /// mastery (vf 0x174(0xe)) gets the spell; it is castable while its gating charge (creature interface +0x24 = record
    /// +0x7c SpellPoints2, 1 for every Inferno creature) is left. Summoned stacks are set up by the same FUN_00c56750 →
    /// FUN_00c565e0 → FUN_00c561a0 and so qualify; a gated stack's charge is set to 0 (00861b15 vf 0x38(0)).
    /// </summary>
    bool CanGate(Unit u)
    {
        var h = HeroOf(u);
        if (!h.Present || u.Kind != UnitKind.Creature || u.Town != Towns.Inferno || u.AbilityUses.ContainsKey(Ab.Gating)) return false;
        return u.Tier <= 2 * (int)h.SkillLevel(Sk.Gating);
    }

    /// <summary>
    /// The gating cell test (exe FUN_00860ce0 from the cast test FUN_00982010 → FUN_00981650, also the UI's
    /// FUN_00548270 and the cast itself 00984a8d). The cell is the cursor cell: the exe anchor, the footprint's maximum
    /// corner (rewrite low corner = cell − (size − 1)). The caster's footprint there must be free (vf 0xcc = FUN_00a6da30)
    /// in the occupancy map FUN_00b3fac0(0, 1, 0, caster) — obstacles and every unit on the field but the caster itself,
    /// so its own cells are allowed — with FUN_00860c30 adding the footprints (vf 0xc8) of every live unit of both
    /// armies (FUN_00af4550) that still carries 0x9f, i.e. the stacks gating in and not yet arrived.
    /// </summary>
    internal string? GatingCellRefusal(Unit u, Cell cell)
    {
        if (!CanGate(u)) return "cannot gate";
        var low = new Cell(cell.X - (u.Size - 1), cell.Y - (u.Size - 1));
        if (low.X < 0 || low.Y < 0 || low.X > S.Width - u.Size || low.Y > S.Height - u.Size) return "needs a free cell";
        if (!LandingFits(u, low)) return "needs a free cell";
        foreach (var g in S.Units)
            if (g.Id != u.Id && g.Alive && g.HasEffect(GatedEffect) && u.CellsAt(low).Any(g.Occupies)) return "needs a free cell";
        return null;
    }

    /// <summary>True when the active stack may gate to <paramref name="cell"/> (the combat screen's cursor test).</summary>
    public bool CanGateAt(int unitId, Cell cell) => S.Unit(unitId) is { Alive: true } u && GatingCellRefusal(u, cell) == null;

    /// <summary>
    /// The cast (exe FUN_00861840, case 0x9f of FUN_00984a00 @ 0098798a with the command's cell): the charge is spent
    /// (vf 0x38(c − 1)); n = <see cref="GatingCount"/> (FUN_0085fe60); the caster's ATB is set (vf 0x18c,
    /// <see cref="GatingCallerAtb"/>). n ≤ 0 places nothing. Otherwise FUN_00c56750(army, the caster's creature, n,
    /// cell, …, state 1) creates the stack at the chosen cell (FUN_00c557a0 only checks the field bounds) off the field
    /// (placement state 1, so it occupies nothing), with the gating effect 0x9f for good (vf 0x260(0x9f, 0x7ffffffe)) and
    /// the gating mark (GATING_CIRCLE1x1 / 2x2, FUN_008a2ff0) at its cell; its gating charge is 0 (vf 0x38(0)) and its
    /// ATB 0 — 0x3f7fffef with Urgash's Call — through vf 0x184 / FUN_00a2d6a0. It arrives at its first turn start
    /// (<see cref="GatedArrival"/>). The ctor's battle-RNG ATB draw (FUN_00a73e70, [0, 0.25)) is made and overwritten.
    /// Returns the caster's ATB.
    /// </summary>
    double CastGating(Unit u, Cell cell)
    {
        var h = HeroOf(u);
        u.AbilityUses[Ab.Gating] = 1;
        int n = GatingCount(u, h);
        Emit(new AbilityTriggered(u.Id, Ab.Gating));
        double reset = GatingCallerAtb(h);
        if (n <= 0) return reset;
        var low = new Cell(cell.X - (u.Size - 1), cell.Y - (u.Size - 1));
        var g = BattleSetup.AddCreature(Db, S, u.Side, u.CreatureId, n, low, summoned: true);
        g.Atb = (float)S.Rng.RangeD(0f, 0.25f);
        g.OffField = true;
        g.HasMovedFromStart = true;
        g.AbilityUses[Ab.Gating] = 1;
        Emit(new UnitSummoned(g.Id, g.CreatureId, n, g.Side, g.Pos, Ab.Gating));
        AddEffect(g, new Effect { Id = GatedEffect, Positive = true, Dispellable = false });
        SetAtb(g.Id, GatedStartAtb(h));
        seqDirty = true;
        return reset;
    }

    /// <summary>
    /// FUN_00bbb390, a gated stack's first turn start: the gating mark goes (vf 0x164 → vf 0x54); FUN_00c558b0 (flag 0)
    /// takes Invisibility (0x13d) from the units standing on its footprint and looks for its placement — its own anchor
    /// when free, else the nearest free one (FUN_00b3fac0 / FUN_00b3e1e0, ties by the battle RNG; <see cref="ExePlacement"/>).
    /// Found: 0x9f ends (vf 0x270), the stack is put there (vf 0x14c) with the gating visual (vf 0x12c(…, 0x9f)) and its
    /// ATB becomes clamp(GatedStartATB, RPGStats +0x5c8, 0, 1) (vf 0x184). None: the "gating failed" log entry (vf 0x100)
    /// and ATB 0.5 (vf 0x18c); it stays off the field with 0x9f and tries again at its next turn start. Either way it does
    /// not act (FUN_00bbcb40 acts only at ATB ≥ 1).
    /// </summary>
    void GatedArrival(Unit u)
    {
        foreach (var o in S.Units.Where(o => o.Id != u.Id && o.Alive && !o.OffField && u.Cells().Any(o.Occupies)).ToList())
            RemoveEffect(o, H5.Combat.Fx.Invisible);
        var probe = new Unit { Id = -1, Size = u.Size };
        if (ExePlacement(probe, u.Pos, S.Rng) is not { } at)
        {
            Emit(new AbilityTriggered(u.Id, GatingFailed));
            SetAtb(u.Id, 0.5);
            return;
        }
        u.OffField = false;
        if (at != u.Pos) Teleport(u, at);
        RemoveEffect(u, GatedEffect);
        Emit(new AbilityTriggered(u.Id, GatedEffect));
        SetAtb(u.Id, Math.Clamp((float)Stats.Get("combat/HeroSkills/DemonLord/Gating/GatedStartATB", 0), 0f, 1f));
    }

    /// <summary>The ability event of a failed arrival (the spell's second CombatLogTexts entry, GatingFailed).</summary>
    public const string GatingFailed = "GATING_FAILED";

    /// <summary>
    /// The combat AI's gating cells (exe FUN_008d28e0, from the spell generator FUN_008d6670 for spell 0x9f): the units
    /// of both armies (FUN_00af55e0: the attacker's army list, then the defender's) unless the think's +0x1bb flag is set
    /// or the strategy is defensive (think +0x1ac) and the caster's creature has no shots (record +0x4c, FUN_004bb150) —
    /// then the enemy army's (combat vf 0xe0 → vf 0x6c). For each, the ring anchors around it for the caster's size
    /// (FUN_00b3f2f0 → FUN_00b3d010) where the caster's footprint is free in the plain occupancy map (vf 0xcc over
    /// FUN_00b3f120(0, …), the caster's own cells blocked); the list loses its duplicates (FUN_008d1010, which moves the
    /// last entry into a duplicate's place); each that passes the cast test (FUN_00982010 → <see cref="GatingCellRefusal"/>)
    /// is a candidate. GAP-detail: the think flag +0x1bb is not modelled (taken as 0). Cells are exe anchors.
    /// </summary>
    internal List<Cell> GatingAiCells(Unit u, bool defensive)
    {
        bool shooter = Db.Creatures.TryGetValue(u.CreatureId, out var d) && d.Shots > 0;
        IEnumerable<Unit> units = !defensive || shooter
            ? S.Units.Where(o => o.Side == 0).Concat(S.Units.Where(o => o.Side == 1))
            : S.Units.Where(o => o.Side != u.Side);
        var probe = new Unit { Id = -1, Size = u.Size };
        var list = new List<Cell>();
        foreach (var t in units)
        {
            if (!t.Alive || t.Kind != UnitKind.Creature) continue;
            foreach (var c in ExeRing(t.Pos, u.Size, t.Size))
                if (c.X >= 0 && c.Y >= 0 && c.X <= S.Width - u.Size && c.Y <= S.Height - u.Size && LandingFits(probe, c))
                    list.Add(new Cell(c.X + u.Size - 1, c.Y + u.Size - 1));
        }
        ExeDedupe(list);
        return list.Where(c => GatingCellRefusal(u, c) == null).ToList();
    }

    /// <summary>FUN_008d1010: for each i, every later equal entry j is overwritten by the last entry and the list shrinks;
    /// j then moves on (the moved entry is not compared with i again).</summary>
    internal static void ExeDedupe(List<Cell> l)
    {
        for (int i = 0; i < l.Count; i++)
            for (int j = i + 1; j < l.Count; j++)
                if (l[i] == l[j]) { l[j] = l[^1]; l.RemoveAt(l.Count - 1); }
    }
}
