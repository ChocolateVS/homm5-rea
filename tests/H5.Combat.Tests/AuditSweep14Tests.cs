using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 14 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_14.md).</summary>
[Collection("game")]
public class AuditSweep14Tests(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    (Battle b, Unit a, Unit t) Make(string mine, int countA, string theirs, int countB, Cell posA, Cell posB, ulong seed = 7)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot(mine, countA) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot(theirs, countB) } },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        foreach (var u in s.Units.Where(u => u.IsWarMachine).ToList()) s.Units.Remove(u);
        var a = s.Units.First(u => u.Side == 0 && u.CreatureId == mine);
        var t = s.Units.First(u => u.Side == 1 && u.CreatureId == theirs);
        a.Pos = a.StartPos = posA;
        t.Pos = t.StartPos = posB;
        return (new Battle(f.Db, s), a, t);
    }

    // exe FUN_00979680 (from case 0x9b at 00987884): the landing anchor (the footprint's maximum corner) is drawn from
    // the cells within dx² + dy² ≤ r² of the target cell, r = the griffins' speed (vf 0x48) and up, where they stand
    // (vf 0xcc), their own take-off cells free (state 1, FUN_00a6af10). The engine took any free cell of the field.
    [Fact]
    public void BattleDiveLandsWithinItsSpeedOfTheTargetCell()
    {
        var (b, g, t) = Make("CREATURE_GRIFFIN", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(12, 5));
        int speed = b.SpeedOf(g);
        var cell = t.Pos;
        var seen = new HashSet<Cell>();
        for (int i = 0; i < 40; i++)
        {
            var land = b.BattleDiveLanding(g, cell);
            Assert.NotNull(land);
            var p = land!.Value;
            int ax = p.X + g.Size - 1 - cell.X, ay = p.Y + g.Size - 1 - cell.Y;
            Assert.True(ax * ax + ay * ay <= speed * speed, $"{p} lies beyond the speed {speed} of {cell}");
            Assert.True(b.Fits(g, p));
            seen.Add(p);
        }
        Assert.True(seen.Count > 1);   // a battle-RNG pick among the candidates

        // Wall the target cell in with obstacles out to the speed radius: the radius grows until a cell is found.
        var s = b.State;
        for (int x = 0; x < s.Width; x++)
            for (int y = 0; y < s.Height; y++)
            {
                int dx = x - cell.X, dy = y - cell.Y;
                if (dx * dx + dy * dy <= (speed + 1) * (speed + 1) && !t.Occupies(new Cell(x, y))) s.Obstacles.Add(new Cell(x, y));
            }
        var far = b.BattleDiveLanding(g, cell);
        Assert.NotNull(far);
        int fx = far!.Value.X + g.Size - 1 - cell.X, fy = far.Value.Y + g.Size - 1 - cell.Y;
        Assert.True(fx * fx + fy * fy > speed * speed);
    }

    // exe FUN_0097f610 case 0x14a: HasAbility(9) (a war machine: FUN_00a75160 "id == 9") with r < 0 and vf 0xf4, else
    // vf 0x7c (war machines only, FUN_00a765a0) with r < 0 and type +0x1c = 4. vf 0xf4 of a war machine
    // (FUN_00a75d20): false for the Ammo Cart (4) and the Catapult (2). The engine accepted every war machine.
    [Fact]
    public void SabotageTakesTheBallistaAndAmmoCartButNotTheCatapult()
    {
        var gremlin = f.Db.Creatures.Values.First(c => c.Abilities.Contains(Ab.Sabotage)).Id;
        var (b, a, _) = Make(gremlin, 500, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(12, 5));
        var s = b.State;
        var ballista = BattleSetup.AddWarMachine(f.Db, s, 1, "WAR_MACHINE_BALLISTA");
        var cart = BattleSetup.AddWarMachine(f.Db, s, 1, "WAR_MACHINE_AMMO_CART");
        var catapult = BattleSetup.AddWarMachine(f.Db, s, 1, "WAR_MACHINE_CATAPULT");
        foreach (var w in new[] { ballista, cart, catapult }) Assert.True(b.SabotageHealthAllows(a, w));
        Assert.True(b.SabotageTargetAllows(a, ballista));
        Assert.True(b.SabotageTargetAllows(a, cart));
        Assert.False(b.SabotageTargetAllows(a, catapult));
        var peasant = s.Units.First(u => u.CreatureId == "CREATURE_PEASANT");
        Assert.False(b.SabotageTargetAllows(a, peasant));   // not Mechanical
    }

    // exe FUN_0041a250: rune records only for a stack whose creature town is 9 (Fortress); FUN_00989190 refuses only a
    // rune it holds a record of — so the one-use limit (two with Strong Rune) holds on Fortress stacks only.
    [Fact]
    public void RuneRecordsExistOnlyForFortressStacks()
    {
        var (b, defender, _) = Make("CREATURE_DEFENDER", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var peasant = Add(b, 0, "CREATURE_PEASANT", 10, new Cell(1, 5));
        Assert.True(b.RuneHasRecords(defender.Id));
        Assert.False(b.RuneHasRecords(peasant.Id));
        Assert.False(b.RuneHasRecords(null));
    }
}
