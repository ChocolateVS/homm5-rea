namespace H5.Combat;

// Combat audit sweep 19 (docs/rules/audit/combat_sweep_19.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Summon Other's destination (exe FUN_00979d50, ECX = the caster, EAX = the moved stack). The exe's cell of a stack
    /// (vf 0x40) is its footprint's maximum corner (FUN_00a744f0 lists the cells (x − i, y − j)), so here
    /// anchor = Pos + (size − 1). The candidates: every ring anchor around the caster (FUN_00b3d010(caster anchor,
    /// caster size, moved size), <see cref="ExeRing"/>) against every cell of the moved stack (vf 0xc4 = FUN_00a6d870 →
    /// FUN_00a744f0: i outer, j inner), each pair walked by FUN_00b5ff90 (<see cref="ExeSummonOtherWalk"/>). An accepted
    /// cell is scored by FUN_00b3c6f0(caster anchor, caster size, cell, 1) (the footprint gap FUN_00b3c690, max of the
    /// axes; smaller wins) and on an equal score by the angle between caster → cell and caster → moved stack
    /// (<see cref="ExeSummonOtherAngle"/>): a strictly smaller angle starts a new list, one within 0.001 (DAT_00e21c48)
    /// of the best joins it (start values 0xffffff and 2π). No collected cell, or the moved stack's own anchor among
    /// them → null (it stays); else the battle RNG's vf 0x10(count) picks one (0097a1a2..0097a1cc).
    /// </summary>
    internal Cell? ExeSummonOtherDestination(Unit caster, Unit moved)
    {
        int cs = caster.Size, ms = moved.Size;
        var cA = new Cell(caster.Pos.X + cs - 1, caster.Pos.Y + cs - 1);
        var mA = new Cell(moved.Pos.X + ms - 1, moved.Pos.Y + ms - 1);
        var mCells = new List<Cell>();
        if (ms == 1) mCells.Add(mA);
        else
            for (int i = 0; i < ms; i++)
            for (int j = 0; j < ms; j++)
                mCells.Add(new Cell(mA.X - i, mA.Y - j));
        var casterW = ExeArenaPoint(cA, cs);
        var movedW = ExeArenaPoint(mA, ms);
        var list = new List<Cell>();
        int best = 0xffffff;
        float bestA = 6.2831855f;   // 0x40c90fdb
        foreach (var ring in ExeRing(cA, cs, ms))
        foreach (var b in mCells)
        {
            var a = ring;
            if (!ExeSummonOtherWalk(ref a, b, moved)) continue;
            int d = ExeFootprintDistance(cA, cs, a, 1);
            float ang = ExeSummonOtherAngle(casterW, movedW, ExeArenaPoint(a, ms));
            if (d < best || d == best && ang < bestA)
            {
                bestA = ang; best = d;
                list.Clear(); list.Add(a);
            }
            else if (d == best && Math.Abs(ang - bestA) < 0.001) list.Add(a);
        }
        if (list.Count == 0 || list.Contains(mA)) return null;
        var pick = list[S.Rng.Next(list.Count)];
        return new Cell(pick.X - (ms - 1), pick.Y - (ms - 1));
    }

    /// <summary>
    /// FUN_00b5ff90(A in/out, B, grid): the cells of the line from A to B (FUN_00b406e0 / FUN_005ac0b0: n = |dx| + 1,
    /// m = |dy| + 1, N = max(n, m), sx / sy the signs (FUN_005ac080), accumulators from 0 each step adding n (m) and
    /// moving x (y) by sx (sy) when the sum reaches N, minus N; A first, B last), kept by FUN_00b5f7b0(…, 0) when in
    /// bounds with the blocked bit clear — the moved stack's anchor grid FUN_00b3f120 (obstacles and the other units,
    /// the moved stack itself left out, dilated for its size): an anchor where it fits. None, or the first is B → false;
    /// else A := the first one.
    /// </summary>
    internal bool ExeSummonOtherWalk(ref Cell a, Cell b, Unit moved)
    {
        foreach (var p in ExeLine(a, b))
        {
            if (!Fits(moved, new Cell(p.X - (moved.Size - 1), p.Y - (moved.Size - 1)))) continue;
            if (p == b) return false;
            a = p;
            return true;
        }
        return false;
    }

    /// <summary>The cells FUN_00b406e0 lists from <paramref name="a"/> to <paramref name="b"/> (both included).</summary>
    internal static List<Cell> ExeLine(Cell a, Cell b)
    {
        int n = Math.Abs(b.X - a.X) + 1, m = Math.Abs(b.Y - a.Y) + 1, N = Math.Max(n, m);
        int sx = Math.Sign(b.X - a.X), sy = Math.Sign(b.Y - a.Y);
        int x = a.X, y = a.Y, ax = 0, ay = 0;
        var line = new List<Cell> { a };
        while (x != b.X || y != b.Y)
        {
            ax += n;
            if (N <= ax) { x += sx; ax -= N; }
            ay += m;
            if (N <= ay) { y += sy; ay -= N; }
            line.Add(new Cell(x, y));
        }
        return line;
    }

    /// <summary>
    /// FUN_00b3c6f0 → FUN_00b3c690: per axis d = c1 − c2, plus s2 − 1 when negative, plus 1 − s1 when positive
    /// (anchors at the footprints' maximum corners); the larger |d|.
    /// </summary>
    internal static int ExeFootprintDistance(Cell c1, int s1, Cell c2, int s2)
    {
        int Axis(int d) => d < 0 ? d + s2 - 1 : d > 0 ? d + 1 - s1 : d;
        return Math.Max(Math.Abs(Axis(c1.X - c2.X)), Math.Abs(Axis(c1.Y - c2.Y)));
    }

    /// <summary>
    /// A stack's point on the arena (FUN_00a69d80 with the CCombat object a unit's vf 8 returns): the combat's vf 0x6c
    /// (FUN_00af14e0) maps an anchor to (o.x + 2(x − 1.5), o.y − 2H + 2(y + 0.5), terrain height there — the virtual
    /// base's vf 0x38); for a stack bigger than 1 it is averaged (× 0.5, DAT_00e0ab70) with the point of
    /// anchor − (size − 1). The origin and H cancel in the differences the angle takes; the engine keeps no terrain, so
    /// the height is 0 (a flat arena).
    /// </summary>
    internal static (float X, float Y, float Z) ExeArenaPoint(Cell anchor, int size)
    {
        float x = 2f * (anchor.X - 1.5f), y = 2f * (anchor.Y + 0.5f), z = 0f;
        if (size - 1 > 0)
        {
            x = (x + 2f * (anchor.X - (size - 1) - 1.5f)) * 0.5f;
            y = (2f * (anchor.Y - (size - 1) + 0.5f) + y) * 0.5f;
        }
        return (x, y, z);
    }

    /// <summary>
    /// The tie-break angle of FUN_00979d50 (00979e0e..00979e8d, 00979f3e..0097a04b): v = moved − caster and
    /// w = cell − caster, each scaled by 1 / sqrt(y² + z² + x²) (FUN_0046ed30); a = π/2 − asin(w.y·v.y + w.z·v.z + w.x·v.x)
    /// (FUN_0046eb30, <see cref="ExeAsin"/>), wrapped by fmod(a + π, 2π) ∓ π, absolute value.
    /// </summary>
    internal static float ExeSummonOtherAngle((float X, float Y, float Z) caster, (float X, float Y, float Z) moved, (float X, float Y, float Z) cell)
    {
        float vx = moved.X - caster.X, vy = moved.Y - caster.Y, vz = moved.Z - caster.Z;
        float lv = 1f / (float)Math.Sqrt(vy * vy + vz * vz + vx * vx);
        vx *= lv; vy *= lv; vz *= lv;
        float wx = cell.X - caster.X, wy = cell.Y - caster.Y, wz = cell.Z - caster.Z;
        float lw = 1f / (float)Math.Sqrt(wy * wy + wz * wz + wx * wx);
        wx *= lw; wy *= lw; wz *= lw;
        float dot = wy * vy + wz * vz + wx * vx;
        float ang = 1.5707964f - ExeAsin(dot);
        double w = (ang + Math.PI) % (2 * Math.PI) + (ang < -Math.PI ? Math.PI : -Math.PI);
        return (float)Math.Abs(w);
    }
}
