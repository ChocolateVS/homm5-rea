using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Battle-start stands of war machines and heroes (user reports 16 / 18), traced in H5_Game.exe.</summary>
[Collection("game")]
public class WarMachinePlacementTests(GameDataFixture f)
{
    BattleState Field()
    {
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 5, skipTactics: true, arena: "Grass_Big_01");
        s.Units.RemoveAll(u => u.IsWarMachine);
        foreach (var side in new[] { 0, 1 })
            foreach (var m in new[] { "WAR_MACHINE_BALLISTA", "WAR_MACHINE_FIRST_AID_TENT", "WAR_MACHINE_AMMO_CART" })
                BattleSetup.AddWarMachine(f.Db, s, side, m);
        return s;
    }

    static Unit Machine(BattleState s, int side, string id) => s.Units.Single(u => u.Side == side && u.CreatureId == id);

    // exe FUN_00c551f0: x = 1 (first army) / W − 2 of the bordered 16×12 field, y from table 00e8e010 (field type 4: Ballista 6,
    // Tent 4, Cart 2); size = WarMachines.xdb CombatSize (2, 2, 1 — record +0x8c, unit vf 0x44 = FUN_00a757f0), a 2×2
    // machine also covering x − 1 / x + 1 and y − 1. Exe cell (x, y) = ours (x − 2, y − 1).
    [Fact]
    public void WarMachinesStandInTheBorderColumnsBehindTheirArmy()
    {
        var s = Field();
        Assert.Equal((new Cell(-2, 4), 2), (Machine(s, 0, "WAR_MACHINE_BALLISTA").Pos, Machine(s, 0, "WAR_MACHINE_BALLISTA").Size));
        Assert.Equal((new Cell(-2, 2), 2), (Machine(s, 0, "WAR_MACHINE_FIRST_AID_TENT").Pos, Machine(s, 0, "WAR_MACHINE_FIRST_AID_TENT").Size));
        Assert.Equal((new Cell(-1, 1), 1), (Machine(s, 0, "WAR_MACHINE_AMMO_CART").Pos, Machine(s, 0, "WAR_MACHINE_AMMO_CART").Size));
        Assert.Equal(new Cell(12, 4), Machine(s, 1, "WAR_MACHINE_BALLISTA").Pos);
        Assert.Equal(new Cell(12, 2), Machine(s, 1, "WAR_MACHINE_FIRST_AID_TENT").Pos);
        Assert.Equal(new Cell(12, 1), Machine(s, 1, "WAR_MACHINE_AMMO_CART").Pos);
        // No two machines overlap.
        var cells = s.Units.Where(u => u.IsWarMachine).SelectMany(u => u.Cells()).ToList();
        Assert.Equal(cells.Count, cells.Distinct().Count());
    }

    // Siege field (type 3, bordered 18×16): the attacker's Catapult at table row 3 (y 8, size 2) → ours (−2, 6).
    [Fact]
    public void SiegeCatapultUsesTheSiegeFieldRow()
    {
        var s = new BattleState { Width = 14, Height = 14 };
        Assert.Equal(3, BattleSetup.ExeFieldType(s));
        Assert.Equal((-2, 6), BattleSetup.WarMachineCell(s, 0, "WAR_MACHINE_CATAPULT", 2));
        Assert.Equal((14, 9), BattleSetup.WarMachineCell(s, 1, "WAR_MACHINE_FIRST_AID_TENT", 2));
    }

    // exe FUN_00c56b90: the hero stands between exe cells (x, H − 2) and (x − 1, H − 3), x = 1 / W − 1 — the far-row border
    // square behind its own army, on opposite sides for the two heroes and clear of the machines.
    [Fact]
    public void HeroesStandAtTheFarBorderSquareOfTheirOwnSide()
    {
        var s = Field();
        Assert.Equal((new Cell(-1, 9), new Cell(-2, 8)), BattleSetup.HeroCells(s, 0));
        Assert.Equal((new Cell(13, 9), new Cell(12, 8)), BattleSetup.HeroCells(s, 1));
        foreach (var side in new[] { 0, 1 })
        {
            var (a, b) = BattleSetup.HeroCells(s, side);
            Assert.DoesNotContain(s.Units, u => u.IsWarMachine && (u.Occupies(a) || u.Occupies(b)));
        }
    }

    // Report 18: a war machine is an ordinary melee / shot target (the exe's world → cell, combat vf 0x70 = FUN_00af15a0, has no
    // bounds test, so the border cells are picked like any other).
    [Fact]
    public void AnEnemyWarMachineCanBeAttackedInMelee()
    {
        var s = Field();
        var b = new Battle(f.Db, s);
        b.Begin();
        var ballista = Machine(s, 1, "WAR_MACHINE_BALLISTA");
        var att = BattleSetup.AddCreature(f.Db, s, 0, "CREATURE_PEASANT", 50, new Cell(11, 4), summoned: false);
        s.Active = att.Id;
        var la = b.GetLegalActions()!;
        Assert.Contains(ballista.Id, la.Melee.Keys);
        int hp = ballista.TotalHealth;
        var r = b.Apply(new AttackCmd(att.Id, ballista.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.True(ballista.TotalHealth < hp);
    }
}
