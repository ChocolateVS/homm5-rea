using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 8 of the combat audit: GAP rows and core mechanics confirmed against H5_Game.exe
/// (docs/rules/audit/combat_sweep_8.md).</summary>
[Collection("game")]
public class AuditSweep8Tests(GameDataFixture f)
{
    Battle Make((string id, int count)[] mine, (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        return new Battle(f.Db, s);
    }

    (Battle b, Unit thane, Unit t, Unit x, Unit y, Unit z) StormLayout()
    {
        var b = Make(new[] { ("CREATURE_THUNDER_THANE", 10) },
            new[] { ("CREATURE_PEASANT", 500), ("CREATURE_PEASANT", 500), ("CREATURE_PEASANT", 500), ("CREATURE_PEASANT", 500) });
        var thane = b.State.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_THUNDER_THANE");
        var p = b.State.Units.Where(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT").ToList();
        foreach (var u in b.State.Units.Where(u => u.IsWarMachine).ToList()) b.State.Units.Remove(u);
        thane.Pos = new Cell(6, 5);
        p[0].Pos = new Cell(5, 6); p[1].Pos = new Cell(6, 6); p[2].Pos = new Cell(7, 6); p[3].Pos = new Cell(8, 6);
        return (b, thane, p[1], p[0], p[2], p[3]);
    }

    // exe FUN_00a5f620 / FUN_00a5f3b0: the lightning goes from the target to the adjacent candidate whose direction is
    // closest to the centroid of all remaining connected enemy stacks (FUN_00a5d810), and only ever on to stacks
    // adjacent to the current victim. From T the centroid of {X, Y, Z} lies towards Y, so Y then Z are struck and X —
    // adjacent to the target but never to a later victim — is not. The engine chained to the lowest-id neighbour.
    [Fact]
    public void StormStrikeChainFollowsTheCentroidDirection()
    {
        var (b, thane, t, x, y, z) = StormLayout();
        Assert.Equal(new[] { t, y, z }, b.StormStrikeVictims(thane, t));
    }

    // exe FUN_00a608d0 (called by the strike loop FUN_00a673c0 @ 00a6836c with the strike's base roll and luck): every
    // victim, the target included, takes FUN_00a5c790 — the attacker's damage against that victim from the same roll. So
    // the target is hit by the blow and again by the lightning. The engine skipped the target and re-used one number.
    [Fact]
    public void StormStrikeHitsTheTargetAgainWithTheStrikesRoll()
    {
        var (b, thane, t, x, y, z) = StormLayout();
        int t0 = t.TotalHealth, x0 = x.TotalHealth, y0 = y.TotalHealth, z0 = z.TotalHealth;
        b.Begin();
        b.State.Active = thane.Id;
        var r = b.Apply(new AttackCmd(thane.Id, t.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        int ty = y0 - y.TotalHealth;
        Assert.True(ty > 0);
        Assert.Equal(x0, x.TotalHealth);
        Assert.Equal(ty, z0 - z.TotalHealth);
        Assert.True(t0 - t.TotalHealth >= 2 * ty, $"{t0 - t.TotalHealth} vs {ty}");
    }

    // exe Firewall field (CArenaFirewall, interface vtable 00e61818, field kind vf 4 = FUN_00a83aa0 → 2): the damage
    // vf 0x24 = FUN_0098a920 is only dealt by the cast FUN_0085b790 and by FUN_0085b530, whose callers FUN_0085bfe0 /
    // FUN_0085c0c0 run for cells newly covered by a moving or placed unit (FUN_00a66050, FUN_00a5a480); the turn tick
    // vf 0x30 = FUN_0098a950 only counts the duration. A stack that stays in the fire is not hit again; the engine hit it
    // at the end of each of its turns.
    [Fact]
    public void FirewallDoesNotHitAStackStandingInIt()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 200) });
        b.Begin();
        var pe = b.State.Units.First(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT");
        b.ResolveSpell(Actor.Hero(0), 0, f.Db.Spell("SPELL_FIREWALL")!, Mastery.Expert, 10, null, pe.Pos, fromHero: true);
        Assert.Contains(b.State.Fields, x => x.Kind == "FIREWALL" && x.Cells.Any(pe.Occupies));
        int hp = pe.TotalHealth;
        Assert.True(hp < 200 * pe.MaxHealthCache);
        b.State.Active = pe.Id;
        Assert.True(b.Apply(new DefendCmd(pe.Id) { Side = 1 }).Ok);
        Assert.Equal(hp, pe.TotalHealth);
    }
}
