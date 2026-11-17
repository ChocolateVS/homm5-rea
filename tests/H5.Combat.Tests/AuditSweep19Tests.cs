namespace H5.Combat.Tests;

/// <summary>Sweep 19 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_19.md).</summary>
[Collection("game")]
public class AuditSweep19Tests(GameDataFixture f)
{
    // exe FUN_00b406e0 / FUN_005ac0b0: n = |dx| + 1, m = |dy| + 1, N = max; accumulators from 0 move an axis when they
    // reach N; the start first, the end last.
    [Fact]
    public void SummonOtherLineWalksLikeTheExe()
    {
        Assert.Equal([new Cell(0, 0), new Cell(1, 0), new Cell(2, 1), new Cell(3, 1)], Battle.ExeLine(new Cell(0, 0), new Cell(3, 1)));
        Assert.Equal([new Cell(5, 5), new Cell(4, 4), new Cell(3, 3)], Battle.ExeLine(new Cell(5, 5), new Cell(3, 3)));
        Assert.Equal([new Cell(2, 2)], Battle.ExeLine(new Cell(2, 2), new Cell(2, 2)));
    }

    // exe FUN_00b3c690 (anchors at the maximum corner): d + s2 − 1 below, d + 1 − s1 above, the larger axis.
    // FUN_00a69d80 / FUN_00af14e0: 2 world units per cell, a bigger stack averaged with anchor − (size − 1).
    [Fact]
    public void SummonOtherDistanceAndArenaPoint()
    {
        Assert.Equal(1, Battle.ExeFootprintDistance(new Cell(4, 4), 1, new Cell(5, 4), 1));
        Assert.Equal(0, Battle.ExeFootprintDistance(new Cell(4, 4), 2, new Cell(3, 4), 1));   // inside the 2×2 caster's column
        Assert.Equal(2, Battle.ExeFootprintDistance(new Cell(4, 4), 2, new Cell(6, 3), 1));
        Assert.Equal((5f, 9f, 0f), Battle.ExeArenaPoint(new Cell(4, 4), 1));
        Assert.Equal((4f, 8f, 0f), Battle.ExeArenaPoint(new Cell(4, 4), 2));
    }

    // exe FUN_00979d50: the ring around the caster (FUN_00b3d010) walked towards the moved stack (FUN_00b5ff90); the
    // nearest by footprint gap, then the smallest angle to the moved stack. The engine took the nearest free cell.
    [Fact]
    public void SummonOtherBringsTheStackInLineNextToTheCaster()
    {
        var b = f.Duel("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, 1, new Cell(1, 3), new Cell(8, 3));
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        Assert.Equal(new Cell(2, 3), b.ExeSummonOtherDestination(u, t));
        // the cell in line blocked: its walk stops one further (gap 2), so the two diagonal neighbours (gap 1, 45°) tie
        // and the battle RNG draws one of them.
        b.State.Obstacles.Add(new Cell(2, 3));
        var d = b.ExeSummonOtherDestination(u, t);
        Assert.Contains(d!.Value, new[] { new Cell(2, 2), new Cell(2, 4) });
        // already next to the caster in that line: the walk from its own cell ends on it (first cell = B → rejected), so
        // a small stack is moved to a diagonal neighbour.
        b.State.Obstacles.Clear();
        t.Pos = new Cell(2, 3);
        Assert.Contains(b.ExeSummonOtherDestination(u, t)!.Value, new[] { new Cell(2, 2), new Cell(2, 4) });
    }
}
