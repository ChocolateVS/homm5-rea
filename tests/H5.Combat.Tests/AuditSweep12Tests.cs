using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 12 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_12.md).</summary>
[Collection("game")]
public class AuditSweep12Tests(GameDataFixture f)
{
    (Battle b, Unit a, Unit t) Make(string mine, int countA, string theirs, int countB, Cell posA, Cell posB, ulong seed = 7,
        Cell[]? mineExtra = null, Cell[]? theirsExtra = null)
    {
        mineExtra ??= Array.Empty<Cell>(); theirsExtra ??= Array.Empty<Cell>();
        var armyA = new List<ArmySlot> { new(mine, countA) }; armyA.AddRange(mineExtra.Select(_ => new ArmySlot("CREATURE_PEASANT", 5)));
        var armyB = new List<ArmySlot> { new(theirs, countB) }; armyB.AddRange(theirsExtra.Select(_ => new ArmySlot("CREATURE_PEASANT", 5)));
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = armyA },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = armyB },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        foreach (var u in s.Units.Where(u => u.IsWarMachine).ToList()) s.Units.Remove(u);
        var sideA = s.Units.Where(u => u.Side == 0).ToList(); var sideB = s.Units.Where(u => u.Side == 1).ToList();
        var a = sideA[0]; var t = sideB[0];
        a.Pos = a.StartPos = posA;
        t.Pos = t.StartPos = posB;
        for (int i = 0; i < mineExtra.Length; i++) sideA[i + 1].Pos = sideA[i + 1].StartPos = mineExtra[i];
        for (int i = 0; i < theirsExtra.Length; i++) sideB[i + 1].Pos = sideB[i + 1].StartPos = theirsExtra[i];
        return (new Battle(f.Db, s), a, t);
    }

    static Unit Dummy(int side, Cell pos, int size = 1) => new() { Id = 1000 + pos.X * 20 + pos.Y, Side = side, Pos = pos, Size = size, Count = 1 };

    // exe FUN_00b3e720 (width 0, unbounded flag 1 from FUN_00a5c420 / 00a62075): D = 2(B − A) − sB + sA on the exe max
    // corners, P = 2(c − A) − (sA − 1); a cell is on the line when P·D ≥ 0 and (P × D)² ≤ |D|². A 1×1 mage at (1, 5)
    // shooting (5, 5): (9, 5) past the target is hit, (7, 6) beside the line and (0, 5) behind the mage are not.
    [Fact]
    public void MagicAttackLineRunsPastTheTargetAndIsHalfACellWide()
    {
        var a = Dummy(0, new Cell(1, 5)); var t = Dummy(1, new Cell(5, 5));
        Assert.True(Battle.OnMagicLine(a, t, t));
        Assert.True(Battle.OnMagicLine(a, t, Dummy(1, new Cell(9, 5))));
        Assert.True(Battle.OnMagicLine(a, t, Dummy(0, new Cell(3, 5))));
        Assert.False(Battle.OnMagicLine(a, t, Dummy(1, new Cell(7, 6))));
        Assert.False(Battle.OnMagicLine(a, t, Dummy(1, new Cell(0, 5))));
        // Diagonal (1, 1) → (4, 4): D = (6, 6); (2, 3) gives P = (2, 4), cross = 2·6 − 4·6 = −12, 144 ≤ 72? no.
        var t2 = Dummy(1, new Cell(4, 4));
        Assert.True(Battle.OnMagicLine(Dummy(0, new Cell(1, 1)), t2, Dummy(1, new Cell(6, 6))));
        Assert.False(Battle.OnMagicLine(Dummy(0, new Cell(1, 1)), t2, Dummy(1, new Cell(2, 3))));
    }

    // exe FUN_00b3ea30: the walk has `radius` cells, starts on a target cell of ring 1 around the attacker (ExeRing of
    // FUN_00b3d010), never goes beyond ring 3 (m < 2 for an outward step) and the victims are the target's army only
    // (FUN_00b3d850 with the target's army list).
    [Fact]
    public void RainbowBreathIsARandomWalkOverTheTargetsArmy()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (b, a, t) = Make("CREATURE_RAINBOW_DRAGON", 2, "CREATURE_PEASANT", 30, new Cell(3, 4), new Cell(5, 4), seed,
                new[] { new Cell(6, 4), new Cell(6, 3) }, new[] { new Cell(7, 4) });
            Assert.Equal("CREATURE_RAINBOW_DRAGON", a.CreatureId);
            int radius = 5;
            var walk = b.RainbowWalk(a, t, radius);
            Assert.Equal(radius, walk.Count);
            Assert.Contains(walk[0], t.Cells());
            var p = new Cell(a.Pos.X + a.Size - 1, a.Pos.Y + a.Size - 1);
            foreach (var c in walk)
            {
                int ring = Math.Max(Math.Max(c.X - p.X, p.X - a.Size + 1 - c.X), Math.Max(c.Y - p.Y, p.Y - a.Size + 1 - c.Y));
                Assert.InRange(ring, 1, 3);
            }
            var victims = b.RainbowVictims(a, t, radius);
            Assert.Equal(t, victims[0]);
            Assert.All(victims, v => Assert.Equal(1, v.Side));
        }
    }

    // The first cell is the only ring-1 cell of a 1×1 target; radius 1 → the target alone.
    [Fact]
    public void RainbowBreathRadiusOneIsTheTarget()
    {
        var (b, a, t) = Make("CREATURE_RAINBOW_DRAGON", 2, "CREATURE_PEASANT", 30, new Cell(3, 4), new Cell(5, 4));
        Assert.Equal(new[] { new Cell(5, 4) }, b.RainbowWalk(a, t, 1));
        Assert.Equal(new[] { t }, b.RainbowVictims(a, t, 1));
    }
}
