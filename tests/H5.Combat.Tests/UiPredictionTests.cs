using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>The hover prediction's retaliation verdict follows the engine's rules (exe 00a5fee0).</summary>
[Collection("game")]
public class UiPredictionTests(GameDataFixture f)
{
    Battle Adjacent() => f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 30, posA: new Cell(4, 4), posB: new Cell(5, 4));

    [Fact]
    public void OnlyExpertBlindStopsRetaliationInPrediction()
    {
        foreach (var (mastery, expect) in new[] { (Mastery.Basic, true), (Mastery.Advanced, true), (Mastery.Expert, false) })
        {
            var b = Adjacent();
            var (a, t) = (b.State.Units[0], b.State.Units[1]);
            b.AddEffect(t, new Effect { Id = "SPELL_BLIND", Mastery = mastery, Remaining = 10 });
            var p = b.PredictAttack(a.Id, t.Id, null, ranged: false)!;
            Assert.Equal(expect, p.Retaliates);
            if (!expect) Assert.Equal("COMBAT_NO_BLIND_RETALIATE", p.RetaliateKey);
        }
    }

    [Fact]
    public void HypnotizedTargetNeverRetaliatesInPrediction()
    {
        var b = Adjacent();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        // exe 00a5fee0 @ 00a5ffbb: only a strike back at the hypnotized stack's controller is suppressed.
        b.AddEffect(t, new Effect { Id = "SPELL_HYPNOTIZE", SourceSide = a.Side, Remaining = 10 });
        Assert.Equal("COMBAT_NO_HYPNOTIZE_RETALIATE", b.PredictAttack(a.Id, t.Id, null, ranged: false)!.RetaliateKey);
    }

    [Fact]
    public void RuneOfBerserkingAddsOneStrikeInPrediction()
    {
        var b = Adjacent();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        b.AddEffect(a, new Effect { Id = H5.Combat.Fx.RuneBerserking, Remaining = 10 });
        Assert.Equal(2, b.PredictAttack(a.Id, t.Id, null, ranged: false)!.Strikes);
    }
}
