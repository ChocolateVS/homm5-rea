using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Creature abilities of Academy / Dungeon / Fortress / Stronghold / neutral creatures confirmed against H5_Game.exe
/// (see docs/rules/audit/combat_abilities_b.md).
/// </summary>
[Collection("game")]
public class AuditAbilityBTests(GameDataFixture f)
{
    /// <summary>Battle without heroes: one attacker against several defender stacks, all placed by hand.</summary>
    Battle Field(string attacker, int count, Cell at, params (string Id, int Count, Cell At)[] defenders)
    {
        var def = new ArmySetup();
        foreach (var d in defenders) def.Army.Add(new ArmySlot(d.Id, d.Count));
        var s = BattleSetup.Create(f.Db, new ArmySetup { Army = { new ArmySlot(attacker, count) } }, def, 1,
            obstacles: Array.Empty<Cell>(), skipTactics: true);
        s.Units[0].Pos = s.Units[0].StartPos = at;
        for (int i = 0; i < defenders.Length; i++) s.Units[1 + i].Pos = s.Units[1 + i].StartPos = defenders[i].At;
        return new Battle(f.Db, s);
    }

    [Fact]
    public void HarpoonPullsTowardsTheHarpoonerWithinFortyFiveDegrees()   // FUN_00a5d5f0 → FUN_00a5ceb0(1, π/4)
    {
        var b = f.Duel("CREATURE_HARPOONER", 20, "CREATURE_PEASANT", 100, 1, new Cell(1, 5), new Cell(8, 5));
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        Assert.Equal(new Cell(7, 5), b.HarpoonPullDestination(a, t));
        // Straight in and both 45° cells blocked: the 90° cells are outside π/4, so there is no pull.
        b.State.Obstacles.AddRange(new[] { new Cell(7, 5), new Cell(7, 4), new Cell(7, 6) });
        Assert.Null(b.HarpoonPullDestination(a, t));
    }

    [Fact]
    public void HarpoonNeverPullsCreaturesThatHoldTheirGround()   // FUN_00a5d5f0: ABILITY_HOLD_GROUND (0xa5) → no pull
    {
        var b = f.Duel("CREATURE_HARPOONER", 20, "CREATURE_STONE_DEFENDER", 30, 1, new Cell(1, 5), new Cell(8, 5));
        Assert.Null(b.HarpoonPullDestination(b.State.Units[0], b.State.Units[1]));
    }

    [Fact]
    public void RideByAttackSkipsTheStartCellAndHitsWithoutRetaliation()   // FUN_00a5fa50 (from index 1), FUN_00a61c50
    {
        // Rider (2×2) starts at (2,0); bystander A is next to the start position only, bystander B is next to the route.
        var b = Field("CREATURE_BLACK_RIDER", 10, new Cell(2, 0),
            ("CREATURE_PEASANT", 50, new Cell(10, 0)),   // main target
            ("CREATURE_PEASANT", 50, new Cell(1, 0)),    // next to the start cell only
            ("CREATURE_PEASANT", 50, new Cell(5, 2)));   // next to the route (rider at (4,0) covers (4..5, 0..1))
        b.Begin();
        var u = b.State.Units[0];
        b.State.Active = u.Id;
        var (start, route) = (b.State.Units[2], b.State.Units[3]);
        var r = b.Apply(new AttackCmd(u.Id, b.State.Units[1].Id, new Cell(8, 0)) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var rideBy = r.Events.OfType<AbilityTriggered>().Where(e => e.Ability == Ab.RideByAttack).Select(e => e.Target).ToList();
        Assert.DoesNotContain(start.Id, rideBy);
        Assert.Contains(route.Id, rideBy);
        Assert.Equal(50 * start.MaxHealthCache, start.TotalHealth);
        Assert.True(route.TotalHealth < 50 * route.MaxHealthCache);
        // Dealt directly: no AttackStarted for the ride-by victims, so no luck and no retaliation from them.
        Assert.DoesNotContain(r.Events.OfType<AttackStarted>(), e => e.Target == route.Id || e.Attacker == route.Id);
    }
}
