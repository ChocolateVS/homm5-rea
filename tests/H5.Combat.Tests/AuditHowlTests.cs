using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>ABILITY_HOWL traced against H5_Game.exe (docs/rules/audit/combat_sweep_19b.md).</summary>
[Collection("game")]
public class AuditHowlTests(GameDataFixture f)
{
    static ApplyResult Attack(Battle b, Unit u, Unit t)
    {
        b.State.Active = u.Id;
        var r = b.Apply(new AttackCmd(u.Id, t.Id) { Side = u.Side });
        Assert.True(r.Ok, r.Error);
        return r;
    }

    // exe FUN_00a5cbc0 from the attack routine FUN_00a65260 @ 00a65653: on the wolves' attack, one charge (Wolf
    // SpellPoints1 = 1), a stack of the same creature with the wolves' count (vbase vf 0x1c(t) = 008a42c0, the count
    // history 008acdd0) at the free cell nearest to the target (FUN_00c558b0 → FUN_00b3e1e0), no charge of its own,
    // ATB = battle RNG [0, HowlMaxATBValueAtStart] (FUN_00a2d6a0). Not an activated ability.
    [Fact]
    public void HowlSummonsTheWolvesCountNextToTheTargetOnTheAttack()
    {
        var b = f.Duel("CREATURE_WOLF", 10, "CREATURE_PEASANT", 200, 1, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        var wolves = b.State.Units[0]; var peasants = b.State.Units[1];
        Assert.DoesNotContain(b.UnitAbilities(wolves), a => a.Id == Ab.Howl);
        var r = Attack(b, wolves, peasants);
        var s = Assert.Single(r.Events.OfType<UnitSummoned>(), e => e.By == Ab.Howl);
        Assert.Equal(("CREATURE_WOLF", 10, wolves.Side), (s.CreatureId, s.Count, s.Side));
        var w = b.State.Unit(s.UnitId)!;
        int dx = w.Pos.X - peasants.Pos.X, dy = w.Pos.Y - peasants.Pos.Y;
        Assert.Equal(1, dx * dx + dy * dy);
        Assert.InRange(r.Events.OfType<AtbChanged>().First(e => e.Actor == w.Id).Atb, 0, f.Db.Stats.Get("combat/Creatures/Neutral/HowlMaxATBValueAtStart", 0.5));

        // the charge is spent; the new stack has none
        r = Attack(b, wolves, peasants);
        Assert.DoesNotContain(r.Events.OfType<UnitSummoned>(), e => e.By == Ab.Howl);
        r = Attack(b, w, peasants);
        Assert.DoesNotContain(r.Events.OfType<UnitSummoned>(), e => e.By == Ab.Howl);
    }
}
