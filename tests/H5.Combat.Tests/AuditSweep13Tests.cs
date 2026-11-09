using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 13 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_13.md).</summary>
[Collection("game")]
public class AuditSweep13Tests(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    static ApplyResult Use(Battle b, Unit u, string ability, Unit? t = null, Cell? cell = null)
    {
        b.State.Active = u.Id;
        return b.Apply(new UseAbilityCmd(u.Id, ability) { Side = u.Side, TargetId = t?.Id, Cell = cell });
    }

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

    // exe FUN_00982010 (side test 009823e3..00982424, own-caster refusal 0098247f) → FUN_0097f610 case 0x9d → FUN_0097a230:
    // a friendly living stack whose top creature is wounded (vf 0x1fc(1) = FUN_008a40c0) is a valid target and is healed
    // by 100 × the angels' count (FUN_009789b0 case 0x9d); an unwounded full stack, the caster, an enemy and an Undead
    // ally are not; a dead ally whose cells are taken is refused rather than moved (FUN_00977da0 / FUN_00b3f330).
    [Fact]
    public void ResurrectAlliesHealsALivingWoundedAlly()
    {
        var (b, angel, enemy) = Make("CREATURE_ARCHANGEL", 2, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var wounded = Add(b, 0, "CREATURE_FOOTMAN", 10, new Cell(1, 5));
        var full = Add(b, 0, "CREATURE_FOOTMAN", 10, new Cell(1, 8));
        var skel = Add(b, 0, "CREATURE_SKELETON", 10, new Cell(3, 8));
        wounded.TopHealth = 1;
        Assert.Null(b.ResurrectAlliesRefusal(angel, wounded));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, full));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, angel));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, enemy));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, skel));

        b.Begin();
        int before = wounded.TotalHealth;
        var r = Use(b, angel, Ab.ResurrectAllies, wounded);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Math.Min(wounded.InitialCount * wounded.MaxHealthCache, before + 200), wounded.TotalHealth);
    }

    [Fact]
    public void ResurrectAlliesRefusesADeadStackWhosePlaceIsTaken()
    {
        var (b, angel, _) = Make("CREATURE_ARCHANGEL", 2, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var dead = Add(b, 0, "CREATURE_FOOTMAN", 10, new Cell(1, 5));
        dead.Count = 0; dead.TopHealth = 0;
        Assert.Null(b.ResurrectAlliesRefusal(angel, dead));
        var other = Add(b, 0, "CREATURE_PEASANT", 5, new Cell(1, 5));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, dead));
        other.Pos = new Cell(5, 5);
        Assert.Null(b.ResurrectAlliesRefusal(angel, dead));
    }
}
