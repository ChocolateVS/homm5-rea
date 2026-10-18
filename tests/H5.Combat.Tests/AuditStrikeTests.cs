namespace H5.Combat.Tests;

/// <summary>Strike loop rules confirmed against H5_Game.exe (FUN_00a65260 / FUN_00a64e90 / FUN_008a0f90).</summary>
[Collection("game")]
public class AuditStrikeTests(GameDataFixture f)
{
    static List<(int attacker, AttackKind kind)> Attacks(IEnumerable<BattleEvent> ev) =>
        ev.OfType<AttackStarted>().Select(a => (a.Attacker, a.Kind)).ToList();

    [Fact]
    public void DoubleAttackIsAnsweredAfterTheFirstBlow()
    {
        var b = f.Duel("CREATURE_MINOTAUR_KING", 30, "CREATURE_PEASANT", 400, posA: new Cell(5, 5), posB: new Cell(6, 5));
        b.Begin();
        var mino = b.State.Units[0]; var peasants = b.State.Units[1];
        Assert.True(mino.Has(Ab.DoubleAttack));
        b.State.Active = mino.Id;
        var r = b.Apply(new AttackCmd(mino.Id, peasants.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var seq = Attacks(r.Events);
        Assert.True(seq.Count >= 3, string.Join(" | ", r.Events.Select(e => e.ToString())));
        Assert.Equal(new[] { (mino.Id, AttackKind.Melee), (peasants.Id, AttackKind.Retaliation), (mino.Id, AttackKind.Melee) }, seq.Take(3).ToArray());
    }

    [Fact]
    public void RuneOfBerserkingAddsOneStrike()
    {
        var b = f.Duel("CREATURE_MINOTAUR_KING", 30, "CREATURE_PEASANT", 600, posA: new Cell(5, 5), posB: new Cell(6, 5));
        b.Begin();
        var mino = b.State.Units[0]; var peasants = b.State.Units[1];
        mino.Effects.Add(new Effect { Id = Fx.RuneBerserking, Positive = true, Dispellable = false });
        b.State.Active = mino.Id;
        var r = b.Apply(new AttackCmd(mino.Id, peasants.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, Attacks(r.Events).Count(a => a.attacker == mino.Id && a.kind == AttackKind.Melee));   // 2 + 1, not 2 × 2
    }
}
