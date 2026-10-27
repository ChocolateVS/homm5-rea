using H5.Combat.AI;
using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>The combat AI's flee decision, H5_Game.exe FUN_00c972d0 (ledger: docs/rules/AUDIT_AI.md, "AI flee / surrender decision").</summary>
[Collection("game")]
public class AiRetreatTests(GameDataFixture f)
{
    BattleState Setup(int mine, int theirs, string enemy = "CREATURE_BLACK_DRAGON")
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new List<ArmySlot> { new("CREATURE_PEASANT", mine) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new List<ArmySlot> { new(enemy, theirs) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        s.AiControlled = new[] { true, true };
        return s;
    }

    static AiRetreatInfo Valuable => new() { ArtifactValue = 20000, Experience = 60000, HireCost = 2500 };

    [Fact]
    public void Threshold_follows_the_artifact_steps_difficulty_experience_and_attacker()   // 0x00c97599..0x00c97663
    {
        Assert.Equal(0.16f - 0.45f, OriginalBattleAI.RetreatThreshold(0, 0, 0, false), 6);
        Assert.Equal(0.2f - 0.45f, OriginalBattleAI.RetreatThreshold(1, 0, 1, false), 6);
        Assert.Equal(0.21f - 0.4f - 0.06f, OriginalBattleAI.RetreatThreshold(6000, 0, 2, true), 6);
        // 0.22 − 0.35 + min(0.3, 60000 · 5e-6) = 0.17 → capped at 0.16; the attacker then sits at 0.11.
        Assert.Equal(0.16f, OriginalBattleAI.RetreatThreshold(20000, 60000, 3, false), 6);
        Assert.Equal(0.22f - 0.35f + 0.3f - 0.06f, OriginalBattleAI.RetreatThreshold(20000, 60000, 3, true), 5);
        Assert.Equal(0.16f, OriginalBattleAI.RetreatThreshold(20000, 0, 4, false), 6);   // engine 4: no offset, capped
    }

    [Fact]
    public void A_hopeless_valuable_hero_retreats_on_hard_but_not_on_normal()
    {
        var s = Setup(60, 10);
        s.AiRetreat[0] = Valuable;
        var b = new Battle(f.Db, s); b.Begin();
        string hash = s.ComputeHash();
        var hard = new OriginalBattleAI { GameDifficulty = 2 };
        Assert.NotNull(hard.RetreatDecision(b, 0));
        Assert.Equal(hash, s.ComputeHash());   // the simulation runs on a copy
        // engine difficulty 0 / 1 (script Normal / Easy): a computer army never decides (FUN_004f6670) …
        Assert.Null(new OriginalBattleAI { GameDifficulty = 1 }.RetreatDecision(b, 0));
        Assert.Null(new OriginalBattleAI { GameDifficulty = 0 }.RetreatDecision(b, 0));
        // … a human army (auto combat) does (army vf 0x34 = owner vf 0x190).
        Assert.NotNull(new OriginalBattleAI { GameDifficulty = 1, HumanSides = new[] { true, false } }.RetreatDecision(b, 0));
        // the winning side keeps fighting
        s.AiRetreat[1] = Valuable;
        Assert.Null(hard.RetreatDecision(b, 1));
    }

    [Fact]
    public void Cheap_heroes_denied_heroes_and_sides_without_data_fight_on()
    {
        var s = Setup(60, 10);
        var b = new Battle(f.Db, s); b.Begin();
        var ai = new OriginalBattleAI { GameDifficulty = 3 };
        Assert.Null(ai.RetreatDecision(b, 0));   // no adventure data (duel)
        s.AiRetreat[0] = new AiRetreatInfo { ArtifactValue = 999, Experience = 1999 };
        Assert.Null(ai.RetreatDecision(b, 0));   // FUN_00c96ea0 < 1000 and experience < 2000
        s.AiRetreat[0] = new AiRetreatInfo { ArtifactValue = 1000 };
        Assert.NotNull(ai.RetreatDecision(b, 0));
        s.RetreatDenied[0] = true;               // DenyAIHeroFlee → CanRetreat reason 7
        Assert.Null(ai.RetreatDecision(b, 0));
    }

    [Fact]
    public void Ratio_test_needs_the_ransom_and_hire_gold()
    {
        // Two peasant stacks against slow zombies: when our hero acts with a peasant stack next in the queue, FUN_00c96e10
        // gives 0 and rule 2 is skipped; the ratio test decides.
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new List<ArmySlot> { new("CREATURE_PEASANT", 100), new("CREATURE_PEASANT", 100) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new List<ArmySlot> { new("CREATURE_ZOMBIE", 600) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        s.AiControlled = new[] { true, true };
        s.AiRetreat[0] = Valuable;
        var b = new Battle(f.Db, s); b.Begin();
        var fight = new OriginalBattleAI { GameDifficulty = 3, Retreats = false };
        var ai = new OriginalBattleAI { GameDifficulty = 3 };
        for (int i = 0; i < 20 && s.Phase == BattlePhase.Combat; i++)
        {
            if (b.SideToAct == 0 && OriginalBattleAI.NextOwnTurnLimit(b, 0) == null && ai.RetreatDecision(b, 0) == "ratio")
            {
                var v = CombatValue.Of(b, 0);
                Assert.True(v.MyPower / (v.EnemyPower * 1.1f + (double)v.MyPower) < OriginalBattleAI.RetreatThreshold(20000, 60000, 3, true));
                s.Heroes[0].Resources = new[] { 0, 0, 0, 0, 0, 0, (int)b.SurrenderCost(0) + 2499 };
                Assert.Null(ai.RetreatDecision(b, 0));
                s.Heroes[0].Resources![6] += 1;
                Assert.Equal("ratio", ai.RetreatDecision(b, 0));
                return;
            }
            b.Apply(fight.Decide(b, b.SideToAct!.Value));
        }
        Assert.Fail("no ratio decision reached");
    }

    [Fact]
    public void Decide_retreats_without_ransom()
    {
        var s = Setup(100, 400, "CREATURE_ZOMBIE");   // slower than peasants: side 0 gets its turns
        s.AiRetreat[0] = Valuable;
        s.AiRetreat[1] = Valuable;
        var b = new Battle(f.Db, s); b.Begin();
        var ai = new OriginalBattleAI { GameDifficulty = 3 };
        for (int i = 0; i < 50 && s.Phase == BattlePhase.Combat; i++)
        {
            int side = b.SideToAct!.Value;
            var cmd = ai.Decide(b, side);
            if (cmd is SurrenderCmd sc)
            {
                Assert.Equal(0, side);
                Assert.False(sc.PayRansom);
                Assert.True(b.Apply(cmd).Ok);
                Assert.Equal("fled", s.EndReason);
                return;
            }
            Assert.True(b.Apply(cmd).Ok);
        }
        Assert.Fail("the hopeless side never retreated");
    }
}
