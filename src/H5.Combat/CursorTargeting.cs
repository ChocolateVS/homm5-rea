namespace H5.Combat;

/// <summary>
/// How H5_Game.exe turns the cursor over the combat field into a move destination or an attack position
/// (hover handler 00549500 → 00544230 hover state → 00548270 action).
/// The exe places a stack of size n by its high corner (the anchor; footprint anchor − (0..n−1), 005762f0);
/// these helpers take and return our low (top-left) cells, the conversion being low = anchor − (n − 1).
/// </summary>
public static class CursorTargeting
{
    /// <summary>
    /// Move destination for a cursor cell (00548270 move branch → FUN_00576630 with the stack: anchors cursor + (dx, dy),
    /// dx outer, dy inner, both 0..n−1, the first one the stack has a path to (FUN_00c53160) wins). With none, the exe
    /// keeps the cursor cell as the anchor, which is no valid move either (the move builder 00c540c0 refuses it); a
    /// size-1 stack takes the cursor cell itself.
    /// </summary>
    public static Cell? SnapMove(Cell cursor, int size, Func<Cell, bool> canMoveTo)
    {
        if (size <= 1) return canMoveTo(cursor) ? cursor : null;
        for (int dx = 0; dx < size; dx++)
        for (int dy = 0; dy < size; dy++)
        {
            var low = new Cell(cursor.X + dx - (size - 1), cursor.Y + dy - (size - 1));
            if (canMoveTo(low)) return low;
        }
        return null;
    }

    /// <summary>
    /// The attack positions round a target in the exe's order (FUN_00b3f2f0 → FUN_00b3d010, target size sT, attacker size
    /// sA, anchors relative to the target's anchor P): the +x column bottom to top (y = 1−sT .. sA−1), the +y row right to
    /// left (x = sA .. −sT), the −x column top to bottom, the −y row left to right; 4·(sA + sT) entries, counter-clockwise
    /// from +x. Returned as the attacker's low cells.
    /// </summary>
    public static List<Cell> AttackRing(Cell targetLow, int targetSize, int attackerSize)
    {
        int sT = targetSize, sA = attackerSize;
        int px = targetLow.X + sT - 1, py = targetLow.Y + sT - 1;
        var ring = new List<Cell>(4 * (sA + sT));
        void Add(int ax, int ay) => ring.Add(new Cell(ax - (sA - 1), ay - (sA - 1)));
        for (int j = 1 - sT; j <= sA - 1; j++) Add(px + sA, py + j);
        for (int i = sA; i >= -sT; i--) Add(px + i, py + sA);
        for (int j = sA - 1; j >= 1 - sT; j--) Add(px - sT, py + j);
        for (int i = -sT; i <= sA; i++) Add(px + i, py - sT);
        return ring;
    }

    /// <summary>
    /// The hover state's angle (00544230): atan2 of the cursor's world point minus the hovered stack's centre, times
    /// 57.295776 (00e1c570), stored with truncation (FISTP under control word | 0xc00). Grid units; the exe's world
    /// x / y run along the grid's x / y.
    /// </summary>
    public static int HoverAngle(double dx, double dy) => (int)((float)Math.Atan2(dy, dx) * 57.295776f);

    /// <summary>
    /// FUN_0053e570(angle, n): sector of an angle among n. Step 360/n (float); when n is a multiple of 8 the angle is
    /// shifted by half a step (00e0ab70 = 0.5); fmod by 360.0, +360 when negative, divided by the step and truncated,
    /// clamped to 0..n−1.
    /// </summary>
    public static int Sector(int angle, int n)
    {
        float step = 360f / n;
        float offset = n % 8 == 0 ? step * 0.5f : 0f;
        double v = (angle + (double)offset) % 360.0;   // _CIfmod: sign of the dividend
        if (v < 0) v += 360f;
        int idx = (int)(v / step);
        if (idx > n - 1) idx = n - 1;
        return idx < 0 ? 0 : idx;
    }

    /// <summary>
    /// The attack position chosen for a hover angle (FUN_00547e50): the ring entry of the angle's sector when the attack
    /// from it is valid (00c540c0), else the valid entry nearest to that sector round the ring (min(|k mod n|, n − |k mod n|)),
    /// the earliest entry on a tie. Returns the ring index, or null when no entry is valid.
    /// </summary>
    public static int? PickAttackIndex(int angle, int n, Func<int, bool> valid)
    {
        if (n <= 0) return null;
        int idx = Sector(angle, n);
        if (valid(idx)) return idx;
        int? chosen = null;
        int best = n;
        int k = idx;
        for (int i = 0; i < n; i++, k--)
        {
            int d = Math.Abs(k % n);
            if (n - d <= d) d = n - d;
            if (d < best && valid(i)) { chosen = i; best = d; }
        }
        return chosen;
    }

    /// <summary>The attack direction stored with the action (00547e50, action +0x3c): index·360/n, plus 180/n when n/4 is odd.</summary>
    public static int AttackDirection(int index, int n) => index * 360 / n + ((n / 4) % 2 != 0 ? 180 / n : 0);

    /// <summary>
    /// The sword cursor for an attack direction (00540ef0, action kind 3): FUN_0053e570(trunc(direction − camera yaw ·
    /// 57.295776), 8) → CURSOR_ATTACK_LEFT, _LEFTDOWN, _DOWN, _RIGHTDOWN, _RIGHT, _RIGHTUP, _UP, _LEFTUP
    /// (ids 0x12, 0x17, 0x15, 0x19, 0x13, 0x18, 0x14, 0x16; names 00516900). Returns that sector.
    /// </summary>
    public static int AttackCursorSector(int direction, float cameraYaw) =>
        Sector((int)(direction - (double)(cameraYaw * 57.295776f)), 8);
}
