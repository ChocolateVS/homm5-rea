using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>War machine behaviour confirmed against H5_Game.exe (see docs/rules/AUDIT_COMBAT.md).</summary>
[Collection("game")]
public class WarMachineAuditTests(GameDataFixture f)
{
    // Preset 0 (Ving) brings a Ballista.
    (Battle b, Unit ballista, Unit target) BallistaBattle(params string[] perks)
    {
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 5, skipTactics: true, arena: "Grass_Big_01");
        var h = s.Heroes[0];
        h.Perks.RemoveAll(p => p is Sk.Ballista or Sk.TripleBallista);
        h.Artifacts.Remove("ARTIFACT_RING_OF_MACHINE_AFFINITY");
        foreach (var p in perks)
            if (p.StartsWith("ARTIFACT_")) h.Artifacts.Add(p); else h.Perks.Add(p);
        var b = new Battle(f.Db, s);
        b.Begin();
        var ballista = s.Units.Single(u => u.Side == 0 && u.CreatureId == "WAR_MACHINE_BALLISTA");
        // A huge enemy stack so no shot kills it.
        var target = BattleSetup.AddCreature(f.Db, s, 1, "CREATURE_PEASANT", 5000, new Cell(9, 5), summoned: false);
        s.Active = ballista.Id;
        return (b, ballista, target);
    }

    static int ShotsAt(IEnumerable<BattleEvent> ev, int attacker, int target) =>
        ev.OfType<AttackStarted>().Count(a => a.Attacker == attacker && a.Target == target && a.Kind == AttackKind.Ranged);

    // exe FUN_00a75bf0 (vtable slot 0x1c0) + FUN_00a65260 strike loop: one action = all shots at one target.
    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { Sk.Ballista }, 2)]
    [InlineData(new[] { Sk.Ballista, Sk.TripleBallista }, 3)]
    [InlineData(new[] { Sk.Ballista, Sk.TripleBallista, "ARTIFACT_RING_OF_MACHINE_AFFINITY" }, 4)]
    public void BallistaShotsPerAction(string[] perks, int expected)
    {
        var (b, ballista, _) = BallistaBattle(perks);
        Assert.Equal(expected, b.WarMachineShotsPerAction(ballista));
    }

    [Fact]
    public void TripleBallistaFiresThreeShotsInOneActionThenEndsItsTurn()
    {
        var (b, ballista, target) = BallistaBattle(Sk.Ballista, Sk.TripleBallista);
        var r = b.Apply(new ShootCmd(ballista.Id, target.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, ShotsAt(r.Events, ballista.Id, target.Id));
        // The user's report: the player must NOT get the ballista again for more shots.
        Assert.NotEqual(ballista.Id, b.State.Active);
    }

    [Fact]
    public void BallistaStopsShootingWhenTheTargetDies()
    {
        var (b, ballista, _) = BallistaBattle(Sk.Ballista, Sk.TripleBallista);
        var weak = BattleSetup.AddCreature(f.Db, b.State, 1, "CREATURE_PEASANT", 1, new Cell(9, 2), summoned: false);
        b.State.Active = ballista.Id;
        var r = b.Apply(new ShootCmd(ballista.Id, weak.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, ShotsAt(r.Events, ballista.Id, weak.Id));   // loop breaks on a dead target, no retargeting
        Assert.Equal(0, r.Events.OfType<AttackStarted>().Count(a => a.Attacker == ballista.Id && a.Target != weak.Id));
    }

    // exe FUN_00977190: 10/20/50/100 by mastery, Empiric +5/level, ×2 with Ring of Machine Affinity.
    [Fact]
    public void RingOfMachineAffinityDoublesFirstAidAndAddsAmmoCartAttack()
    {
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 5, skipTactics: true, arena: "Grass_Big_01");
        var b = new Battle(f.Db, s);
        var h = s.Heroes[0];
        var archer = s.Units.First(u => u.Side == 0 && u.MaxShots > 0 && !u.IsWarMachine);
        BattleSetup.AddWarMachine(f.Db, s, 0, "WAR_MACHINE_AMMO_CART");
        h.Artifacts.Remove("ARTIFACT_RING_OF_MACHINE_AFFINITY");
        int plain = b.AttackOf(archer);
        h.Artifacts.Add("ARTIFACT_RING_OF_MACHINE_AFFINITY");
        Assert.Equal(plain + 4, b.AttackOf(archer));   // FUN_008a4800: +4 per cart with the ring
    }
}
