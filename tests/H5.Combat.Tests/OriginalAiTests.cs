using H5.Combat.AI;

namespace H5.Combat.Tests;

[Collection("game")]
public class OriginalAiTests(GameDataFixture f)
{
    [Fact]
    public void AiTunablesComeFromDefaultStats()
    {
        var st = CombatAiStats.Load(f.Db);
        Assert.Equal(0.2, st.KamikazeModeThreshold, 6);       // RPGStats+0xb84
        Assert.Equal(1.8, st.PowerSavingThreshold, 6);        // RPGStats+0xb88
        Assert.Equal(35000, st.MaxRuneMoneyEstimate);         // RPGStats+0xb8c
        Assert.Equal(500, st.PowerSaving.MyDamage);
        Assert.Equal(200, st.PowerSaving.MyTroopsHealth);
        Assert.Equal(400, st.Normal.EnemyTroopsHealth);
        Assert.Equal(3, st.Difficulty[(int)AiDifficulty.Normal].MaxSpellLevelToCast);
        Assert.Equal(5, st.Difficulty[(int)AiDifficulty.Heroic].MaxSpellLevelToCast);
        Assert.False(st.Difficulty[(int)AiDifficulty.Hard].ConsiderOnlyShortestPathToEnemy);
    }

    [Fact]
    public void MeasuresFollowExeFormulas()
    {
        // FUN_00bf9a50: TroopsHealth · max(0.1, 1/factor(1, TroopsDefence)); factor(1, 11) = 1/(1+0.05·10) = 2/3.
        var v = new SideValue { TroopsHealth = 300, TroopsDefence = 11, Health = 400 };
        Assert.Equal(450, v.TroopsMeasure());
        Assert.Equal(150, v.WarMachinesMeasure());
        // FUN_00bfc4a0 damage part: round(factor(AttackLevel, 1) · Damage · Bonus).
        var d = new SideValue { Damage = 100, AttackLevel = 11, Bonus = 1.2 };
        Assert.Equal(180, d.DamageMeasure(new SideValue()));
    }

    [Fact]
    public void WipingOutEnemyTroopsBeatsEverythingOutsideKamikaze()
    {
        var st = CombatAiStats.Load(f.Db);
        var wiped = new CombatValue { My = new SideValue { TroopsHealth = 10, Damage = 1 }, Enemy = new SideValue() };
        var strong = new CombatValue { My = new SideValue { TroopsHealth = 1000, Damage = 500 }, Enemy = new SideValue { TroopsHealth = 1, Damage = 1 } };
        Assert.True(CombatValue.IsBetter(wiped, strong, st.Normal, CombatMode.Normal));
        Assert.False(CombatValue.IsBetter(strong, wiped, st.Normal, CombatMode.Normal));
    }

    [Fact]
    public void ComparatorPrefersFewerTurnsThenPriorityThenNonSpellThenCheaperMove()
    {
        var st = CombatAiStats.Load(f.Db);
        var v = new CombatValue { My = new SideValue { TroopsHealth = 10 }, Enemy = new SideValue { TroopsHealth = 10 } };
        OriginalBattleAI.Candidate C(int turns, int prio, bool spell, int move) =>
            new() { Command = new WaitCmd(0), Turns = turns, Priority = prio, IsSpell = spell, MovePrice = move, Value = v };
        Assert.True(OriginalBattleAI.CandidateBetter(C(3, 0, false, 0), C(1, 0, false, 0), st.Normal, CombatMode.Normal));
        Assert.True(OriginalBattleAI.CandidateBetter(C(-1, 2, false, 0), C(-1, 1, false, 0), st.Normal, CombatMode.Normal));
        Assert.True(OriginalBattleAI.CandidateBetter(C(-1, 0, true, 0), C(-1, 0, false, 0), st.Normal, CombatMode.Normal));
        Assert.True(OriginalBattleAI.CandidateBetter(C(-1, 0, false, 300), C(-1, 0, false, 100), st.Normal, CombatMode.Normal));
        Assert.False(OriginalBattleAI.CandidateBetter(C(-1, 0, false, 100), C(-1, 0, false, 300), st.Normal, CombatMode.Normal));
    }

    [Fact]
    public void DecisionsAreDeterministicAndBattlesFinish()
    {
        string Play()
        {
            var s = BattleSetup.CreateDuel(f.Db, 0, 3, 11);
            var b = new Battle(f.Db, s);
            BattleRunner.RunToEnd(b, new OriginalBattleAI(), new OriginalBattleAI());
            Assert.Equal(BattlePhase.Finished, s.Phase);
            return s.ComputeHash();
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void RegistrySelectsAis()
    {
        Assert.IsType<OriginalBattleAI>(BattleAiRegistry.Create());
        Assert.IsType<GreedyBattleAI>(BattleAiRegistry.Create("greedy"));
        Assert.Equal(AiDifficulty.Hard, ((OriginalBattleAI)BattleAiRegistry.Create("original", AiDifficulty.Hard)).Difficulty);
    }
}
