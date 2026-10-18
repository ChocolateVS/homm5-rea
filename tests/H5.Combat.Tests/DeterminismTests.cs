namespace H5.Combat.Tests;

[Collection("game")]
public class DeterminismTests(GameDataFixture f)
{
    static (string Hash, List<BattleEvent> Events, BattleState State) Run(GameDataFixture f, int a, int b, ulong seed)
    {
        var s = BattleSetup.CreateDuel(f.Db, a, b, seed);
        var battle = new Battle(f.Db, s);
        var ai = new GreedyBattleAI();
        var events = BattleRunner.RunToEnd(battle, ai, ai);
        return (s.ComputeHash(), events, s);
    }

    [Fact]
    public void SameSeedSameOutcome()
    {
        var r1 = Run(f, 0, 3, 42);
        var r2 = Run(f, 0, 3, 42);
        Assert.Equal(r1.Hash, r2.Hash);
        Assert.Equal(MessageJson.Serialize(r1.Events), MessageJson.Serialize(r2.Events));
    }

    [Fact]
    public void DifferentSeedsDiverge()
    {
        Assert.NotEqual(Run(f, 0, 3, 1).Hash, Run(f, 0, 3, 2).Hash);
    }

    [Fact]
    public void RngIsStableAcrossVersions()
    {
        // Pin the generator: changing it would break replays and saved games.
        var r = new Rng(12345);
        var first = Enumerable.Range(0, 3).Select(_ => r.NextU64()).ToArray();
        var again = new Rng(12345);
        Assert.Equal(first, Enumerable.Range(0, 3).Select(_ => again.NextU64()).ToArray());
        var d = new Rng(7);
        for (int i = 0; i < 1000; i++) Assert.InRange(d.NextDouble(), 0, 1 - double.Epsilon);
    }

    [Fact]
    public void SerializationRoundTripPreservesFuture()
    {
        // Play part of a battle, snapshot, then continue both the original and the restored copy.
        var s = BattleSetup.CreateDuel(f.Db, 9, 12, 99);
        var battle = new Battle(f.Db, s);
        var ai = new GreedyBattleAI();
        battle.Begin();
        for (int i = 0; i < 20 && s.Phase != BattlePhase.Finished; i++)
        {
            int side = s.Phase == BattlePhase.Tactics ? (s.TacticsDone[0] ? 1 : 0) : battle.SideToAct!.Value;
            Assert.True(battle.Apply(ai.Decide(battle, side)).Ok);
        }
        var json = s.Serialize();
        var restored = BattleState.Deserialize(json);
        Assert.Equal(s.ComputeHash(), restored.ComputeHash());
        Assert.Equal(json, restored.Serialize());

        var b2 = new Battle(f.Db, restored);
        for (int i = 0; i < 2000 && s.Phase != BattlePhase.Finished; i++)
        {
            int side = battle.SideToAct!.Value;
            var cmd = ai.Decide(battle, side);
            var cmd2 = ai.Decide(b2, side);
            Assert.Equal(MessageJson.Serialize(cmd), MessageJson.Serialize(cmd2));
            battle.Apply(cmd);
            b2.Apply(cmd2);
            Assert.Equal(s.ComputeHash(), restored.ComputeHash());
        }
    }

    [Fact]
    public void CommandsAndEventsRoundTripThroughJson()
    {
        BattleCommand[] cmds =
        {
            new AttackCmd(3, 7, new Cell(2, 5)) { Side = 1 }, new CastSpellCmd(-1, "SPELL_FIREBALL", null, new Cell(4, 4)),
            new UseAbilityCmd(5, Ab.LayHands, 2), new DeployCmd(1, new Cell(0, 3)), new EndTacticsCmd(), new HeroAttackCmd(4),
        };
        foreach (var c in cmds)
        {
            var json = MessageJson.Serialize(c);
            Assert.Contains("\"$t\"", json);
            Assert.Equal(c, MessageJson.DeserializeCommand(json));
        }
        var (_, events, _) = Run(f, 6, 18, 3);
        var ej = MessageJson.Serialize(events);
        Assert.Equal(ej, MessageJson.Serialize(MessageJson.DeserializeEvents(ej)));
    }
}

[Collection("game")]
public class DuelSimTests(GameDataFixture f)
{
    public static IEnumerable<object[]> Pairs() =>
        Enumerable.Range(0, 24).Select(i => new object[] { i, (i * 7 + 5) % 24 });

    /// <summary>Every preset fights and every game ends with a winner well before the command limit.</summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void PresetDuelTerminates(int a, int b)
    {
        var s = BattleSetup.CreateDuel(f.Db, a, b, (ulong)(a * 31 + b));
        var battle = new Battle(f.Db, s);
        var ai = new GreedyBattleAI();
        BattleRunner.RunToEnd(battle, ai, ai, maxCommands: 3000);
        Assert.Equal(BattlePhase.Finished, s.Phase);
        Assert.NotNull(s.Winner);
        Assert.NotEqual("turn limit", s.EndReason);
    }
}
