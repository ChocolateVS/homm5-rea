using H5.Combat.AI;

namespace H5.Combat.Tests;

/// <summary>
/// The combat screen's AI decisions (auto-combat and the computer side) worked out off the render thread
/// (<see cref="PendingDecision"/>): same commands as deciding in place, one per turn, in order, never for a stale position.
/// </summary>
[Collection("game")]
public class PendingDecisionTests(GameDataFixture f)
{
    Battle NewDuel()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 30, "CREATURE_GRIFFIN", 12, 7, new Cell(2, 5), new Cell(9, 5));
        b.Begin();
        b.State.AiControlled = new[] { false, true };
        return b;
    }

    [Fact]
    public void BackgroundDecisionsPlayTheSameBattleInTheSameOrder()
    {
        var inPlace = NewDuel();
        var copy = NewDuel();
        var ai0 = new OriginalBattleAI { HumanSides = new[] { true, false } };
        var ai1 = new OriginalBattleAI { HumanSides = new[] { true, false } };
        var bg0 = new OriginalBattleAI { HumanSides = new[] { true, false } };
        var bg1 = new OriginalBattleAI { HumanSides = new[] { true, false } };
        int commands = 0;
        while (inPlace.State.Phase != BattlePhase.Finished && commands < 200)
        {
            int side = inPlace.SideToAct!.Value;
            Assert.Equal(side, copy.SideToAct);
            var expected = (side == 0 ? ai0 : ai1).Decide(inPlace, side) with { Side = side };
            var before = copy.State.Serialize();
            var d = PendingDecision.Start(copy, side, side == 0 ? bg0 : bg1, background: true);
            Assert.True(SpinWait.SpinUntil(() => d.IsCompleted, TimeSpan.FromMinutes(1)));
            Assert.Equal(before, copy.State.Serialize());   // the live battle is not touched while the AI thinks
            var cmd = d.TakeIfCurrent(copy);
            Assert.Equal(expected, cmd);
            Assert.True(copy.Apply(cmd!).Ok);
            Assert.Null(d.TakeIfCurrent(copy));   // once applied, the decision is stale
            Assert.True(inPlace.Apply(expected).Ok);
            Assert.Equal(inPlace.State.Serialize(), copy.State.Serialize());
            commands++;
        }
        Assert.Equal(BattlePhase.Finished, inPlace.State.Phase);
    }

    [Fact]
    public void AStaleDecisionIsNotHandedOut()
    {
        var b = NewDuel();
        int side = b.SideToAct!.Value;
        var d = PendingDecision.Start(b, side, new OriginalBattleAI(), background: false);
        Assert.True(d.IsCompleted);
        Assert.NotNull(d.TakeIfCurrent(b));
        var la = b.GetLegalActions()!;
        Assert.True(b.Apply(new DefendCmd(la.Actor) { Side = side }).Ok);
        Assert.Null(d.TakeIfCurrent(b));
    }
}
