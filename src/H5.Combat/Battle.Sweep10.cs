using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 10 (docs/rules/audit/combat_sweep_10.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// The placement chooser FUN_00b3e1e0 over the free-anchor grid FUN_00b3fac0 builds for a footprint (FUN_00b3f120
    /// blocked cells, dilated for size &gt; 1 by FUN_00b3efb0): the requested anchor when it is free; otherwise every free
    /// anchor, scanned row by row (y outer, x inner), keeping those at the smallest squared distance (FUN_00b5f550) to
    /// the requested anchor; the tie is drawn with the given RNG (vf 0xc(0, n − 1)) or, without one, the first found.
    /// </summary>
    internal Cell? ExePlacement(Unit probe, Cell want, Rng? rng)
    {
        if (want.X >= 0 && want.Y >= 0 && want.X <= S.Width - probe.Size && want.Y <= S.Height - probe.Size && Fits(probe, want))
            return want;
        var best = new List<Cell>();
        int bestD = int.MaxValue;
        for (int y = 0; y <= S.Height - probe.Size; y++)
        for (int x = 0; x <= S.Width - probe.Size; x++)
        {
            var p = new Cell(x, y);
            if (!Fits(probe, p)) continue;
            int dx = x - want.X, dy = y - want.Y, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best.Clear(); }
            if (d == bestD) best.Add(p);
        }
        if (best.Count == 0) return null;
        return best[rng == null ? 0 : rng.Range(0, best.Count - 1)];
    }

    /// <summary>
    /// Where Pit Lords summon their Balors (exe FUN_00860660 → FUN_008605a0): on the corpse's own cell when the corpse is
    /// a large creature (vf 0x44 &gt; 1); for a small corpse, the 2-cell placement nearest to it (FUN_00b3fac0 size 2,
    /// FUN_00b3e1e0 without RNG). No placement → the ability is refused (before the charge is spent); a large corpse's
    /// cell is not checked there — FUN_00c56750 (FUN_00c557a0) then refuses an occupied one after the charge is spent.
    /// </summary>
    internal Cell? BalorPlacement(Unit corpse)
    {
        if (corpse.Size > 1) return corpse.Pos;
        var probe = new Unit { Id = -1, Size = Db.Creatures.TryGetValue("CREATURE_BALOR", out var bd) ? bd.CombatSize : 2 };
        return ExePlacement(probe, corpse.Pos, null);
    }
}
