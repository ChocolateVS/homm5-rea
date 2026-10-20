using H5.Combat.AI;
using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Combat AI decisions checked against H5_Game.exe (ledger: docs/rules/AUDIT_AI.md, "Combat AI").</summary>
[Collection("game")]
public class OriginalAiExeTests(GameDataFixture f)
{
    [Fact]
    public void ThinkLevelAndRowFollowTheExe()   // FUN_008d0840, FUN_004f66c0, FUN_00a2f0d0
    {
        // Script difficulty → engine difficulty (inverse of 00a33780): Easy 1, Normal 0, Hard 2, Heroic 3.
        Assert.Equal(new[] { 1, 0, 2, 3 }, new[] { 0, 1, 2, 3 }.Select(OriginalBattleAI.EngineDifficulty));
        // A computer side: engine 0/1 → 0, 2 → 4, 3 → 5, 4 → 6; a human side → 5.
        Assert.Equal(new[] { 0, 0, 4, 5, 6 }, new[] { 0, 1, 2, 3, 4 }.Select(d => OriginalBattleAI.ExeThinkLevel(false, d)));
        Assert.Equal(5, OriginalBattleAI.ExeThinkLevel(true, 1));
        // Level ≤ 3 → DifficultyNormal; ≥ 4 → DifficultyHeroic. The Easy and Hard rows are never read.
        var rows = new[] { 0, 1, 2, 3 }.Select(s => OriginalBattleAI.ExeRow(OriginalBattleAI.ExeThinkLevel(false, OriginalBattleAI.EngineDifficulty(s)))).ToArray();
        Assert.Equal(new[] { AiDifficulty.Normal, AiDifficulty.Normal, AiDifficulty.Heroic, AiDifficulty.Heroic }, rows);
        Assert.Equal(AiDifficulty.Heroic, OriginalBattleAI.ExeRow(OriginalBattleAI.ExeThinkLevel(true, 1)));
    }

    [Fact]
    public void DecisionsUseTheRowOfTheGameDifficultyAndSide()   // FUN_008d0840 → FUN_00a2f0d0
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(2, 5), new Cell(9, 5));
        b.Begin();
        b.State.AiControlled = new[] { true, true };
        int side = b.SideToAct ?? 0;
        var easy = new OriginalBattleAI { GameDifficulty = 0 };
        easy.Decide(b, side);
        Assert.Equal(AiDifficulty.Normal, easy.LastRow);
        var hard = new OriginalBattleAI { GameDifficulty = 2 };
        hard.Decide(b, side);
        Assert.Equal(AiDifficulty.Heroic, hard.LastRow);
        // The same AI playing for a human (auto combat) uses level 5.
        var auto = new OriginalBattleAI { GameDifficulty = 0, HumanSides = new[] { true, true } };
        auto.Decide(b, side);
        Assert.Equal(5, auto.LastLevel);
        Assert.Equal(AiDifficulty.Heroic, auto.LastRow);
    }

    [StockFact]
    public void TheCompareAlwaysReadsPowerSavingWeights()   // FUN_00bfae20 0x00bfaf5d..0x00bfaf8a: switch falls through to +0x80
    {
        var st = CombatAiStats.Load(f.Db);
        Assert.Same(st.PowerSaving, st.CompareCoefs);
        Assert.Equal(500, st.CompareCoefs.MyDamage);
    }

    [Fact]
    public void SpellCompareTypesComeFromTheExeTable()   // FUN_008d02b0
    {
        Assert.Equal(0, OriginalBattleAI.SpellCompareType("SPELL_FIREBALL"));
        Assert.Equal(0, OriginalBattleAI.SpellCompareType("SPELL_HASTE"));
        Assert.Equal(1, OriginalBattleAI.SpellCompareType("SPELL_ABILITY_EXPLOSION"));
        Assert.Equal(1, OriginalBattleAI.SpellCompareType("GATING"));
        Assert.Equal(1, OriginalBattleAI.SpellCompareType("SPELL_WARCRY_WORD_OF_THE_CHIEF"));
        Assert.Equal(2, OriginalBattleAI.SpellCompareType("SPELL_ABILITY_BERSERKER_RAGE"));
    }

    [Fact]
    public void ExclusionDropsOnlyWhatTheStartBeats()   // FUN_008d1cb0: type 1 vs start value, type 2 also own HP
    {
        var st = CombatAiStats.Load(f.Db);
        var start = new CombatValue { My = new SideValue { TroopsHealth = 100, Health = 100, Damage = 10 }, Enemy = new SideValue { TroopsHealth = 100, Health = 100, Damage = 10 } };
        var same = new CombatValue { My = new SideValue { TroopsHealth = 100, Health = 100, Damage = 10 }, Enemy = new SideValue { TroopsHealth = 100, Health = 100, Damage = 10 } };
        var worse = new CombatValue { My = new SideValue { TroopsHealth = 50, Health = 50, Damage = 5 }, Enemy = new SideValue { TroopsHealth = 100, Health = 100, Damage = 10 } };
        OriginalBattleAI.Candidate C(int type, CombatValue v) => new() { Command = new WaitCmd(0), CompareType = type, Value = v };
        Assert.False(OriginalBattleAI.Excluded(C(1, same), start, st.CompareCoefs, CombatMode.Normal));   // a tie is kept
        Assert.True(OriginalBattleAI.Excluded(C(1, worse), start, st.CompareCoefs, CombatMode.Normal));
        Assert.False(OriginalBattleAI.Excluded(C(0, worse), start, st.CompareCoefs, CombatMode.Normal));  // type 0: never
        var lessHp = new CombatValue { My = new SideValue { TroopsHealth = 100, Health = 99, Damage = 10 }, Enemy = new SideValue { TroopsHealth = 90, Health = 90, Damage = 10 } };
        Assert.False(OriginalBattleAI.Excluded(C(1, lessHp), start, st.CompareCoefs, CombatMode.Normal));
        Assert.True(OriginalBattleAI.Excluded(C(2, lessHp), start, st.CompareCoefs, CombatMode.Normal));   // +0x74 < +0x98
    }

    [Fact]
    public void RingCellsFollowTheExeOrder()   // FUN_00b3d010
    {
        var b = f.Duel("CREATURE_PEASANT", 1, "CREATURE_PEASANT", 1, 1, new Cell(2, 2), new Cell(5, 5));
        var t = b.State.Units[1];
        Assert.Equal(new[] { new Cell(6, 5), new Cell(6, 6), new Cell(5, 6), new Cell(4, 6), new Cell(4, 5), new Cell(4, 4), new Cell(5, 4), new Cell(6, 4) },
            OriginalBattleAI.Ring(t, 1));
        // A size-2 attacker: anchors one further out on the low sides, 12 cells.
        var big = OriginalBattleAI.Ring(t, 2);
        Assert.Equal(12, big.Count);
        Assert.Equal(new Cell(6, 4), big[0]);
        Assert.Equal(new Cell(3, 5), big[6]);
        Assert.Equal(new Cell(3, 3), big[^4]);
    }

    [Fact]
    public void AreaCentresKeepOnlyUndominatedCells()   // FUN_008d12a0 + FUN_008d6ce0
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 10) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 10), new ArmySlot("CREATURE_PEASANT", 10), new ArmySlot("CREATURE_PEASANT", 10) } },
            1, obstacles: Array.Empty<Cell>(), skipTactics: true);
        var own = s.Units.First(u => u.Side == 0 && !u.IsWarMachine);
        var foes = s.Units.Where(u => u.Side == 1 && !u.IsWarMachine).ToList();
        own.Pos = own.StartPos = new Cell(4, 5);
        foes[0].Pos = foes[0].StartPos = new Cell(5, 5);
        foes[1].Pos = foes[1].StartPos = new Cell(6, 5);
        foes[2].Pos = foes[2].StartPos = new Cell(9, 5);
        var b = new Battle(f.Db, s);
        var heroic = CombatAiStats.Load(f.Db).Difficulty[(int)AiDifficulty.Heroic] with { CenterAreaSpellsOnCreatureOnly = false };
        var enemies = s.Living.Where(u => u.Side == 1).ToList();
        var cells = OriginalBattleAI.SpellCells(b, "SPELL_FIREBALL", 0, enemies, heroic).Select(c => c.Item1).ToList();
        // {A, B} without our stack first reached at (6,4) (it removes (5,4), which also hit us); {C} first at (8,4).
        Assert.Equal(new[] { new Cell(6, 4), new Cell(8, 4) }, cells);
        // With CenterAreaSpellsOnCreatureOnly the single-target generator aims at the enemy stacks.
        var normal = heroic with { CenterAreaSpellsOnCreatureOnly = true };
        Assert.Equal(3, OriginalBattleAI.SpellCells(b, "SPELL_FIREBALL", 0, enemies, normal).Count);
    }

    [Fact]
    public void ShooterGeneratesOnlyShotsAndSpells()   // FUN_008d6850 → FUN_008d3950
    {
        var b = f.Duel("CREATURE_ARCHER", 20, "CREATURE_PEASANT", 20, 1, new Cell(1, 5), new Cell(9, 5));
        b.Begin();
        b.State.Active = b.State.Units[0].Id;
        var ai = new OriginalBattleAI();
        ai.Decide(b, 0);
        Assert.NotEmpty(ai.LastCandidates);
        Assert.All(ai.LastCandidates, c => Assert.True(c.Command is ShootCmd || c.IsSpell, c.Label));
    }

    [Fact]
    public void ApproachOnlyWithoutAttacksAndWithTurns()   // FUN_008d68c0: FUN_008d5b90 when FUN_008d64e0 added nothing
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(0, 5), new Cell(11, 5));
        b.Begin();
        b.State.Active = b.State.Units[0].Id;
        var ai = new OriginalBattleAI();
        ai.Decide(b, 0);
        Assert.NotEmpty(ai.LastCandidates);
        Assert.All(ai.LastCandidates, c => { Assert.IsType<MoveCmd>(c.Command); Assert.True(c.Turns >= 1); });
        // Next to the enemy: attacks only (Turns 0), no approach moves.
        var b2 = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(5, 5), new Cell(6, 5));
        b2.Begin();
        b2.State.Active = b2.State.Units[0].Id;
        ai.Decide(b2, 0);
        Assert.Contains(ai.LastCandidates, c => c.Command is AttackCmd);
        Assert.DoesNotContain(ai.LastCandidates, c => c.Label.StartsWith("approach"));
        Assert.All(ai.LastCandidates.Where(c => c.Command is AttackCmd), c => Assert.Equal(0, c.Turns));
    }

    [Fact]
    public void ConsiderOnlyShortestPathKeepsOneStrikeCellPerTarget()   // FUN_008d3c40 0x008d3fa9..0x008d3fba
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(4, 5), new Cell(6, 5));
        b.Begin();
        b.State.Active = b.State.Units[0].Id;
        b.State.AiControlled = new[] { true, true };
        var normal = new OriginalBattleAI { GameDifficulty = 1 };   // Normal row: ConsiderOnlyShortestPathToEnemy
        normal.Decide(b, 0);
        Assert.Single(normal.LastCandidates, c => c.Command is AttackCmd);
        var heroic = new OriginalBattleAI { GameDifficulty = 3 };   // Heroic row: every strike cell
        heroic.Decide(b, 0);
        Assert.True(heroic.LastCandidates.Count(c => c.Command is AttackCmd) > 1);
    }
}
