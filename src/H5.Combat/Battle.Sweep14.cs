namespace H5.Combat;

// Combat audit sweep 14 (docs/rules/audit/combat_sweep_14.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Battle Dive's landing (exe FUN_00979680, called by the finish case 0x9b of FUN_00984a00 at 00987884 with the
    /// target cell L0): the occupancy map FUN_00b3fac0(0, 1, 0, 0) holds the obstacles and every unit of both armies
    /// that is valid (FUN_00a6af10 — not in placement state 1, vf 0x4c, which the take-off's vf 0xe8(1) gives the
    /// griffins, so their own cells are free) and alive (FUN_00b3ce00). For r = the griffins' speed (vf 0x48 =
    /// FUN_008a1e50, <see cref="SpeedOf"/>), r + 1, … while r &lt; max(width, height)²: every anchor (x outer, y inner;
    /// the anchor is the footprint's maximum corner, <see cref="ExePos"/>) whose cell is free, with
    /// dx² + dy² ≤ r² to L0 (FUN_00b5f550, compared as floats) and where the griffins stand (vf 0xcc = FUN_00a6da30: the
    /// footprint of vf 0x44 cells free) is collected; the first r with candidates gives one by the battle RNG
    /// (combat vf 0xc → vf 0xc(0, n − 1)). None → null (the exe then only waits: vf 0x184(0.5) and the ATB setter).
    /// </summary>
    internal Cell? BattleDiveLanding(Unit u, Cell target)
    {
        int lim = Math.Max(S.Width, S.Height);
        lim *= lim;
        for (int r = SpeedOf(u); r < lim; r++)
        {
            var cand = new List<Cell>();
            for (int x = 0; x < S.Width; x++)
                for (int y = 0; y < S.Height; y++)
                {
                    int dx = x - target.X, dy = y - target.Y;
                    if ((float)(dx * dx) + (float)(dy * dy) > (float)(r * r)) continue;
                    var p = new Cell(x - (u.Size - 1), y - (u.Size - 1));
                    if (LandingFits(u, p)) cand.Add(p);
                }
            if (cand.Count > 0) return cand[S.Rng.Next(cand.Count)];
        }
        return null;
    }

    /// <summary>The occupancy map of FUN_00b3fac0 also marks the war machines (army units, FUN_00b3ce00), which the
    /// engine's walking test leaves out.</summary>
    bool LandingFits(Unit u, Cell p)
    {
        if (!Fits(u, p)) return false;
        foreach (var c in u.CellsAt(p))
            if (S.Units.Exists(w => w.Alive && w.IsWarMachine && w.Occupies(c))) return false;
        return true;
    }

    /// <summary>
    /// Sabotage's target test (exe FUN_0097f610 case 0x14a, 009804ed..009805e3) with r = <see cref="SabotageHealthAllows"/>:
    /// valid when HasAbility(ABILITY_MECHANICAL) (vf 0x28c(9); a war machine answers it with FUN_00a75160, "id == 9") or
    /// vf 0x84 ≠ 0, r &lt; 0 and vf 0xf4; else when vf 0x7c ≠ 0, r &lt; 0 and that object's record +0x1c is 4. Only a war
    /// machine (CCombatWarMachine, vtable 00e70594: +0x7c = FUN_00a765a0) has a vf 0x7c object; the creature, hero, town
    /// building and hive classes return 0 (FUN_00c98940), as does every vf 0x84 seen. A war machine's vf 0xf4
    /// (FUN_00a76e00 → FUN_00a75d20) reads the same +0x1c — the war machine type: Ammo Cart (4) false, Catapult (2)
    /// false (FUN_00b3b6b0, result 0), First Aid Tent (3) only when its field −0x1c is positive, else not dead. So the
    /// Ammo Cart passes by the second branch, the Ballista by the first, the Catapult never.
    /// </summary>
    internal bool SabotageTargetAllows(Unit saboteur, Unit t)
    {
        if (!SabotageHealthAllows(saboteur, t)) return false;
        if (!t.IsWarMachine) return t.Has(Ab.Mechanical) && t.Alive;
        return t.CreatureId switch
        {
            "WAR_MACHINE_AMMO_CART" => true,       // vf 0xf4 false; vf 0x7c record +0x1c = 4
            "WAR_MACHINE_CATAPULT" => false,       // vf 0xf4 false, type 2
            _ => t.Alive,                          // Ballista; First Aid Tent with its −0x1c field not modelled
        };
    }

    /// <summary>
    /// The per-stack rune records exist only for Fortress stacks: the set-up FUN_0041a250 adds them (vf 0x228 for every
    /// rune of the hero's book, Strong Rune's FUN_00989210(id, 1, 0)) only when the stack's creature town
    /// (FUN_004ba910 of vf 0x6c) is 9, and FUN_00989190 answers "used up" only for a rune whose record it finds — so a
    /// rune on any other stack is never refused as used (the engine counted one use, two with Strong Rune, on every
    /// stack). Not ported: the record {id, 0, 0, 0} that 00983790 adds after an ability roll (009843c5..009843f2).
    /// </summary>
    internal bool RuneHasRecords(int? target) => target is int t && S.Unit(t) is { } u && u.Town == Towns.Fortress;

    /// <summary>
    /// Magic Attack's unit kinds (exe FUN_00a62030 → FUN_00a5c420(…, every unit 1, war machines 1, …), 00a62075..00a62081):
    /// the attacker and units not on the field (FUN_00a6af50, state 1) are dropped, then only kind (unit vf 0) 1
    /// creatures (FUN_008b1e10), 9 hives (CCombatSpawn, FUN_008b1740) and — with the flag set — 3 war machines
    /// (FUN_00b59ea0) stay; heroes (2, FUN_007a5ed0) and town buildings (4..8, FUN_00bff800) never. The engine's
    /// obstacle units (siege buildings, spell objects) are left out.
    /// </summary>
    internal static bool MagicAttackKind(Unit u) => !u.IsObject;
}
