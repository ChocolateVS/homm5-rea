namespace H5.Combat.Tests;

/// <summary>
/// Cursor → move destination / attack position as H5_Game.exe (00548270 → FUN_00576630 / FUN_00c53160 for moves,
/// FUN_00547e50 → 00b3d010 / 0053e570 for melee, 00540ef0 for the sword cursor). User reports 21 and 23.
/// </summary>
[Collection("game")]
public class CursorTargetingTests(GameDataFixture f)
{
    // FUN_00576630: anchors cursor + (dx, dy), dx outer, dy inner; the anchor is the high corner (low = anchor − 1).
    [Fact]
    public void SnapTriesAnchorsCursorPlusOffsetsInTheExeOrder()
    {
        var tried = new List<Cell>();
        Assert.Null(CursorTargeting.SnapMove(new Cell(5, 5), 2, c => { tried.Add(c); return false; }));
        Assert.Equal(new[] { new Cell(4, 4), new Cell(4, 5), new Cell(5, 4), new Cell(5, 5) }, tried);
        Assert.Equal(new Cell(5, 5), CursorTargeting.SnapMove(new Cell(5, 5), 1, _ => true));
        Assert.Null(CursorTargeting.SnapMove(new Cell(5, 5), 1, _ => false));
    }

    // A 2×2 stack at low (4, 4) (cells 4..5 × 4..5). The row above is y = 6: the cell over its high corner (5, 6) moves
    // it up by one (low (4, 5), overlapping its own cells); the other cell (4, 6) takes the first anchor (4, 6) → low
    // (3, 5), up and left by one. Hovering its own high corner (5, 5): anchor (5, 5) is where it stands (no path), so
    // (5, 6) → up by one.
    [StockFact]
    public void TwoByTwoStackMovesByOneFromTheRowAbove()
    {
        var b = f.Duel("CREATURE_GRIFFIN", 5, "CREATURE_PEASANT", 10, 1, new Cell(4, 4), new Cell(11, 0));
        var g = b.State.Units[0];
        Assert.Equal(2, g.Size);
        var moves = b.Reachable(g).Keys.ToHashSet();
        Assert.Equal(new Cell(4, 5), CursorTargeting.SnapMove(new Cell(5, 6), 2, moves.Contains));
        Assert.Equal(new Cell(3, 5), CursorTargeting.SnapMove(new Cell(4, 6), 2, moves.Contains));
        Assert.Equal(new Cell(4, 5), CursorTargeting.SnapMove(new Cell(5, 5), 2, moves.Contains));
        Assert.Equal(new Cell(3, 3), CursorTargeting.SnapMove(new Cell(4, 4), 2, moves.Contains));
    }

    // 00b3d010: counter-clockwise from +x, 4·(sA + sT) entries; 1×1 round 1×1 is the 8 neighbours from (x+1, y).
    [Fact]
    public void AttackRingOrder()
    {
        Assert.Equal(new[] { new Cell(6, 5), new Cell(6, 6), new Cell(5, 6), new Cell(4, 6), new Cell(4, 5), new Cell(4, 4), new Cell(5, 4), new Cell(6, 4) },
            CursorTargeting.AttackRing(new Cell(5, 5), 1, 1));
        // 1×1 attacker round a 2×2 target at low (4, 4): +x column (6, 4), (6, 5); +y row (6, 6) .. (3, 6); ...
        var r = CursorTargeting.AttackRing(new Cell(4, 4), 2, 1);
        Assert.Equal(12, r.Count);
        Assert.Equal(new[] { new Cell(6, 4), new Cell(6, 5), new Cell(6, 6), new Cell(5, 6), new Cell(4, 6), new Cell(3, 6) }, r.Take(6));
        // 2×2 attacker round a 1×1 target at (5, 5): first entry anchor (7, 5) → low (6, 4).
        var r2 = CursorTargeting.AttackRing(new Cell(5, 5), 1, 2);
        Assert.Equal(12, r2.Count);
        Assert.Equal(new Cell(6, 4), r2[0]);
        Assert.Equal(16, CursorTargeting.AttackRing(new Cell(4, 4), 2, 2).Count);
    }

    // 0053e570: half-step shift only when n is a multiple of 8; truncation; clamp to n − 1.
    [Fact]
    public void SectorOfAnAngle()
    {
        Assert.Equal(0, CursorTargeting.Sector(0, 8));
        Assert.Equal(0, CursorTargeting.Sector(22, 8));
        Assert.Equal(1, CursorTargeting.Sector(23, 8));
        Assert.Equal(0, CursorTargeting.Sector(-22, 8));
        Assert.Equal(7, CursorTargeting.Sector(-23, 8));
        Assert.Equal(2, CursorTargeting.Sector(90, 8));
        Assert.Equal(0, CursorTargeting.Sector(29, 12));
        Assert.Equal(11, CursorTargeting.Sector(-1, 12));
        Assert.Equal(1, CursorTargeting.Sector(30, 12));
        Assert.Equal(-45, CursorTargeting.HoverAngle(1, -1));
        Assert.Equal(89, CursorTargeting.HoverAngle(1e-6, 1)); // 89.99… truncated
    }

    // 00547e50: the sector's entry, else the nearest valid entry round the ring, the earliest on a tie.
    [Fact]
    public void AttackIndexFallsBackToTheNearestValidEntry()
    {
        Assert.Equal(2, CursorTargeting.PickAttackIndex(90, 8, _ => true));
        Assert.Equal(1, CursorTargeting.PickAttackIndex(90, 8, i => i is 1 or 3));
        Assert.Equal(7, CursorTargeting.PickAttackIndex(0, 8, i => i is 7 or 2));
        Assert.Null(CursorTargeting.PickAttackIndex(0, 8, _ => false));
        // Direction (+0x3c): index·360/n (+180/n when n/4 is odd); cursor sector after the camera yaw.
        Assert.Equal(90, CursorTargeting.AttackDirection(2, 8));
        Assert.Equal(15, CursorTargeting.AttackDirection(0, 12));
        Assert.Equal(2, CursorTargeting.AttackCursorSector(90, 0f));   // attacker above the target → CURSOR_ATTACK_DOWN
        Assert.Equal(6, CursorTargeting.AttackCursorSector(90, MathF.PI));
    }
}
