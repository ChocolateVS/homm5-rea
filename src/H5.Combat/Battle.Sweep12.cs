using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 12 (docs/rules/audit/combat_sweep_12.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// The Magic Attack line test (exe FUN_00a5c420 → FUN_00b3e8c0 → FUN_00b3e720, called from FUN_00a62030 with width 0
    /// and the "unbounded" flag 1). Positions are the exe's (vf 0x40, the footprint's maximum corner), sizes vf 0x44.
    /// In doubled units D = (2(B − A) − sB + sA) per axis; a cell c of a unit gives P = 2(c − A) − (sA − 1). The unit is
    /// on the line when one of its cells (vf 0xc4) has P·D ≥ 0 and (Px·Dy − Py·Dx)² ≤ (width + 1)² · |D|² — no upper
    /// bound on P·D, so the line runs on past the target to the edge of the field.
    /// </summary>
    internal static bool OnMagicLine(Unit a, Unit t, Unit u, int width = 0)
    {
        var A = ExePos(a); var B = ExePos(t);
        int sa = a.Size, sb = t.Size;
        int dx = 2 * (B.X - A.X) - sb + sa, dy = 2 * (B.Y - A.Y) - sb + sa;
        long d2 = (long)dx * dx + (long)dy * dy;
        foreach (var c in u.Cells())
        {
            int px = 2 * (c.X - A.X) - (sa - 1), py = 2 * (c.Y - A.Y) - (sa - 1);
            long dot = (long)px * dx + (long)py * dy;
            if (dot < 0) continue;
            long cross = (long)px * dy - (long)py * dx;
            if (cross * cross <= (long)(width + 1) * (width + 1) * d2) return true;
        }
        return false;
    }

    /// <summary>
    /// The Rainbow Breath area (exe FUN_00b3ea30(ECX = out, EDX = attacker, target, radius), from FUN_00a62030 when the
    /// attacker has ability 0x8e and the strike is not an estimate): rings are FUN_00b3d010 around the attacker's exe
    /// position P. The first cell is a battle-RNG Random(n) pick among the ring-1 cells (ExeRing(P, size, 1)) that the
    /// target covers (vf 0xc0 = FUN_00a6d980); none → no area. d = that cell − P per axis, a negative component + size − 1.
    /// Then radius − 1 steps with k = 0 (sideways offset), m = 0 (outward steps) and T = {1, 2, 2}: on ring
    /// ExeRing(P, size + m, m + 1), i = the current cell's index (the ring length when absent), the candidates in order are
    /// ring[(i + n − 1) % n] (kind 0) when k ≤ 0 and k &gt; −T[m], ring[(i + 1) % n] (kind 1) when k ≥ 0 and k &lt; T[m],
    /// cell + d (kind 2) when m &lt; 2; Random(count) picks one (kind 0: k − 1, 1: k + 1, 2: m + 1) and it is appended.
    /// </summary>
    internal List<Cell> RainbowWalk(Unit a, Unit t, int radius)
    {
        var cells = new List<Cell>();
        var p = ExePos(a); int s = a.Size;
        var targetCells = t.Cells().ToHashSet();
        var first = ExeRing(p, s, 1).Where(targetCells.Contains).ToList();
        if (first.Count == 0) return cells;
        var cur = first[S.Rng.Next(first.Count)];
        cells.Add(cur);
        int ddx = cur.X - p.X, ddy = cur.Y - p.Y;
        if (ddx < 0) ddx += s - 1;
        if (ddy < 0) ddy += s - 1;
        int[] lim = { 1, 2, 2 };
        int k = 0, m = 0;
        for (int step = 1; step < radius; step++)
        {
            var ring = ExeRing(p, s + m, m + 1);
            int n = ring.Count, i = ring.IndexOf(cur);
            if (i < 0) i = n;
            var cand = new List<(Cell C, int Kind)>();
            if (k <= 0 && k > -lim[m]) cand.Add((ring[(i + n - 1) % n], 0));
            if (k >= 0 && k < lim[m]) cand.Add((ring[(i + 1) % n], 1));
            if (m < 2) cand.Add((new Cell(cur.X + ddx, cur.Y + ddy), 2));
            var (c, kind) = cand[S.Rng.Next(cand.Count)];
            if (kind == 0) k--; else if (kind == 1) k++; else m++;
            cur = c;
            cells.Add(cur);
        }
        return cells;
    }

    /// <summary>
    /// Rainbow Breath victims (exe FUN_00a622b1..: FUN_00b3d850(the target's army vf 0xc → vf 0x64, cells, attacker) →
    /// FUN_00b3d720 flag 0): for each walk cell in order, each unit of the target's army that is valid (FUN_00a6afd0) and
    /// covers the cell, once; every victim × 1.0. The radius is clamp((Range(1, 10) + luck) / 2 + 1, 1, 5).
    /// </summary>
    internal List<Unit> RainbowVictims(Unit a, Unit t, int radius)
    {
        var list = new List<Unit>();
        foreach (var c in RainbowWalk(a, t, radius))
            foreach (var u in S.Living)
                if (u.Side == t.Side && !u.IsObject && !list.Contains(u) && u.Cells().Contains(c)) list.Add(u);
        return list;
    }

    /// <summary>FUN_00979410: SPELL_ABILITY_RANDOM_CAST_DARK (0xae) lists Curse, Slow, Disrupting Ray, Plague, Weakness,
    /// Forgetfulness (0xb..0xf, 0x11); SPELL_ABILITY_RANDOM_CAST_DARK_LIGHT (0xaf) Bless, Haste, Stoneskin, Dispel,
    /// Bloodlust, Deflect Arrows (0x17..0x1a, 0x1c, 0x1d) and then the same six.</summary>
    internal static readonly string[] RandomCastDark =
        { "SPELL_CURSE", "SPELL_SLOW", "SPELL_DISRUPTING_RAY", "SPELL_PLAGUE", "SPELL_WEAKNESS", "SPELL_FORGETFULNESS" };
    internal static readonly string[] RandomCastLight =
        { "SPELL_BLESS", "SPELL_HASTE", "SPELL_STONESKIN", "SPELL_DISPEL", "SPELL_BLOODLUST", "SPELL_DEFLECT_ARROWS" };

    /// <summary>
    /// The Random Caster draw (exe 00987cb1 cases 0xae / 0xaf): FUN_00979410's list, of which FUN_009848a0 keeps the
    /// entries the application check FUN_00982010 accepts on the target (no UI message, in list order; the engine's
    /// ValidateSpellTarget with the cast's mastery), then the battle RNG Random(n) picks one.
    /// </summary>
    internal List<SpellDef> RandomCasterPool(Unit u, string ability, Unit t)
    {
        var ids = ability == Ab.RandomCasterBless ? RandomCastLight.Concat(RandomCastDark) : RandomCastDark;
        var pool = new List<SpellDef>();
        foreach (var id in ids)
            if (Db.Spell(id) is { } sp && ValidateSpellTarget(u.Id, Controller(u), sp, Mastery.Advanced, t.Id, null) == null)
                pool.Add(sp);
        return pool;
    }

    /// <summary>
    /// The per-stack rune record {id, uses, firsts, re-activations} (stack vf 0x2b8): a Fortress stack starts every rune
    /// of its hero's book with one use and one first activation (FUN_00989350 via FUN_0041a250); a Strong Rune hero
    /// (HasSkill 0x99) adds one use (FUN_00989210(id, 1, 0)). Each activation spends one (FUN_00989270, from the payments
    /// FUN_008a11c0 / FUN_00bbf610 via FUN_00a6a6d0 and FUN_00a6a970: uses − 1 and firsts − 1 while positive,
    /// re-activations + 1), and FUN_00989190 refuses the rune once uses &lt; 1 (COMBAT_RUNIC_SPELL_ALREADY_USED) — so a rune
    /// works once per stack, twice with Strong Rune (the second at the re-activation cost of FUN_00419ad0).
    /// </summary>
    internal static bool RuneUsedUp(HeroState h, SpellDef sp, int? target) =>
        h.AbilityUses.GetValueOrDefault($"{sp.Id}:{target}") >= (h.HasPerk(Sk.StrongRune) ? 2 : 1);

    internal static void SpendRuneUse(HeroState h, SpellDef sp, int? target)
    {
        var key = $"{sp.Id}:{target}";
        h.AbilityUses[key] = h.AbilityUses.GetValueOrDefault(key) + 1;
    }

    /// <summary>The spells FUN_00976b90 gives a damage (its switch on the base id: Magic Arrow … Armageddon 1..10, Plague,
    /// Unholy / Holy Word, Land Mine, Wasp Swarm, Explosion, Call Lightning, Firewall, Stone Spikes, Uber Chain Lightning,
    /// Boss Firewall, Deep Freeze, Divine Vengeance, Uber Meteor Shower, Call Storm); every other spell gives 0.</summary>
    internal static readonly HashSet<string> SpellDamageSpells = new()
    {
        "SPELL_MAGIC_ARROW", "SPELL_MAGIC_FIST", "SPELL_LIGHTNING_BOLT", "SPELL_ICE_BOLT", "SPELL_FIREBALL", "SPELL_FROST_RING",
        "SPELL_CHAIN_LIGHTNING", "SPELL_METEOR_SHOWER", "SPELL_IMPLOSION", "SPELL_ARMAGEDDON", "SPELL_PLAGUE", "SPELL_UNHOLY_WORD",
        "SPELL_HOLY_WORD", "SPELL_LAND_MINE", "SPELL_WASP_SWARM", "SPELL_ABILITY_EXPLOSION", "SPELL_ABILITY_CALL_LIGHTNING",
        "SPELL_FIREWALL", "SPELL_STONE_SPIKES", "SPELL_UBER_CHAIN_LIGHTNING", "SPELL_BOSS_FIREWALL", "SPELL_DEEP_FREEZE",
        "SPELL_DIVINE_VENGEANCE", "SPELL_UBER_METEOR_SHOWER", "SPELL_ABILITY_CALL_STORM",
    };

    /// <summary>
    /// Imbue Arrow's order (exe FUN_008cf270 008cf54c..008cf5bf, FUN_0097ea20 0097ea95..0097eab5): the strike damage goes
    /// into CCombat's deferred damage queue (vf 0x14 → vf 0), then FUN_0097ea20 casts the imbued spell; when the spell's
    /// FUN_00976b90 damage is 0 it flushes the queue (vf 0x14 → vf 8 = FUN_008d7f40) before the cast, otherwise the strike
    /// flushes it after the cast (008cf5ac..008cf5ba). The flush skips a dead target. True: the spell lands first.
    /// </summary>
    internal bool ImbueSpellFirst(HeroState h) =>
        h.HasPerk(Sk.ImbueArrow) && h.ImbuedSpell is { } id && SpellDamageSpells.Contains(BaseSpell(id));

    /// <summary>
    /// Sabotage's health test (exe FUN_0097f610 case 0x14a, 009804ed..009805e3): r = trunc(A − B · SabotageCoef) — A the
    /// target's and B the saboteurs' total health (vf 0x1dc = FUN_008a4190: (count − 1) · health + top health), the
    /// floats subtracted on the x87 stack, SabotageCoef (RPGStats +0x304, 10 stock, 1 in MMH55) as a float, FISTP under
    /// chop; the target qualifies when r &lt; 0 (with ABILITY_MECHANICAL or as a war machine, vf 0x84).
    /// </summary>
    internal bool SabotageHealthAllows(Unit saboteur, Unit target)
    {
        float k = (float)Stats.Get("combat/Creatures/Academy/SabotageCoef", 10);
        double x = (double)(float)target.TotalHealth - (double)(float)saboteur.TotalHealth * k;
        return Math.Truncate(x) < 0;
    }
}
