using H5.Combat.AI;
using H5.Core.Data;

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

    /// <summary>
    /// Plays the whole battle twice, deciding each turn in place (<see cref="IBattleAI.Decide"/>) and sliced (one AI
    /// step per frame, <see cref="DecisionMode.Sliced"/>): every command, the order and the final state are identical.
    /// Returns the steps the sliced decisions took (more steps than decisions: the work really is spread out).
    /// </summary>
    static (int Decisions, int Steps) SlicedMatchesInPlace(Battle inPlace, Battle sliced, Func<OriginalBattleAI> ai)
    {
        var direct = new[] { ai(), ai() };
        var stepped = new[] { ai(), ai() };
        int decisions = 0, steps = 0;
        while (inPlace.State.Phase != BattlePhase.Finished && decisions < 400)
        {
            int side = inPlace.State.Phase == BattlePhase.Tactics ? (inPlace.State.TacticsDone[0] ? 1 : 0) : inPlace.SideToAct!.Value;
            var expected = direct[side].Decide(inPlace, side) with { Side = side };
            var before = sliced.State.Serialize();
            var d = PendingDecision.Start(sliced, side, stepped[side], DecisionMode.Sliced);
            Assert.False(d.IsCompleted);
            while (!d.Advance(0)) steps++;
            steps++;
            Assert.Null(d.Error);
            Assert.Equal(before, sliced.State.Serialize());
            var cmd = d.TakeIfCurrent(sliced);
            Assert.Equal(expected, cmd);
            Assert.True(inPlace.Apply(expected).Ok);
            Assert.True(sliced.Apply(cmd!).Ok);
            Assert.Equal(inPlace.State.ComputeHash(), sliced.State.ComputeHash());
            decisions++;
        }
        Assert.Equal(BattlePhase.Finished, inPlace.State.Phase);
        Assert.Equal(inPlace.State.Serialize(), sliced.State.Serialize());
        return (decisions, steps);
    }

    [Fact]
    public void SlicedDecisionsPlayTheSameDuel()
    {
        var (n, steps) = SlicedMatchesInPlace(NewDuel(), NewDuel(), () => new OriginalBattleAI { HumanSides = new[] { true, false } });
        Assert.True(steps > n * 2, $"{steps} steps for {n} decisions");
    }

    [Fact]
    public void SlicedDecisionsPlayTheSameHeroDuel()
    {
        // Duel presets: heroes with spells and war machines (hero turns, spell candidates).
        Battle New()
        {
            var b = new Battle(f.Db, BattleSetup.CreateDuel(f.Db, 1, 2, 11, skipTactics: true));
            b.Begin();
            b.State.AiControlled = new[] { true, true };
            return b;
        }
        var (n, steps) = SlicedMatchesInPlace(New(), New(), () => new OriginalBattleAI { GameDifficulty = 3 });
        Assert.True(steps > n * 2, $"{steps} steps for {n} decisions");
    }

    [Fact]
    public void SlicedDecisionsPlayTheSameBattleWithTheFleeSimulation()
    {
        // A hopeless valuable hero on Heroic: the flee decision's rule 2 plays a round on a copy (nested decisions, also sliced).
        Battle New()
        {
            var s = BattleSetup.Create(f.Db,
                new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new List<ArmySlot> { new("CREATURE_PEASANT", 100) } },
                new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new List<ArmySlot> { new("CREATURE_ZOMBIE", 400) } },
                7, skipTactics: true, arena: "Grass_Big_01");
            s.AiControlled = new[] { true, true };
            s.AiRetreat[0] = s.AiRetreat[1] = new AiRetreatInfo { ArtifactValue = 20000, Experience = 60000, HireCost = 2500 };
            var b = new Battle(f.Db, s);
            b.Begin();
            return b;
        }
        var inPlace = New();
        SlicedMatchesInPlace(inPlace, New(), () => new OriginalBattleAI { GameDifficulty = 3 });
        Assert.Equal("fled", inPlace.State.EndReason);
    }
}
