using H5.Combat.AI;

namespace H5.Combat.Tests;

/// <summary>Combat AI rows closed against H5_Game.exe (ledger: docs/rules/AUDIT_AI.md, "Combat AI").</summary>
[Collection("game")]
public class AiCombatGapTests(GameDataFixture f)
{
    static readonly int[] Rich = { 100, 100, 100, 100, 100, 100, 100000 };

    [Fact]
    public void RunePurchaseFollowsTheExeGate()   // FUN_008d0960 (from FUN_008d30e0)
    {
        var cost = new[] { 0, 0, 0, 0, 2, 0, 0 };   // e.g. 2 sulfur
        // Every paid resource above 49 in the purse: bought whatever its value.
        Assert.True(OriginalBattleAI.BuysRune(100, 100, cost, Rich, Rich, new[] { 0, 0, 0, 0, 1e9, 0, 0.0 }, 35000));
        // Purse at 49 or less: the value test. 0.0 money values (no player / simulation default) → always under the cap.
        var poor = new[] { 100, 100, 100, 100, 49, 100, 100000 };
        Assert.True(OriginalBattleAI.BuysRune(100, 100, cost, poor, poor, null, 35000));
        // value = 2 · 1000 = 2000; r = 1 → scale 7.5 − 5.5 = 2 → 4000 < 4001 buys, < 4000 does not.
        var m = new[] { 0, 0, 0, 0, 1000.0, 0, 0 };
        Assert.True(OriginalBattleAI.BuysRune(100, 100, cost, poor, poor, m, 4001));
        Assert.False(OriginalBattleAI.BuysRune(100, 100, cost, poor, poor, m, 4000));
        // r < 0.8 → 0.5 (1000); 2 ≤ r < 4 → 6r − 4 (r = 3: 14 → 28000); r ≥ 4 → 20 (40000).
        Assert.True(OriginalBattleAI.BuysRune(70, 100, cost, poor, poor, m, 1001));
        Assert.False(OriginalBattleAI.BuysRune(70, 100, cost, poor, poor, m, 1000));
        Assert.False(OriginalBattleAI.BuysRune(300, 100, cost, poor, poor, m, 28000));
        Assert.True(OriginalBattleAI.BuysRune(300, 100, cost, poor, poor, m, 28001));
        Assert.False(OriginalBattleAI.BuysRune(500, 100, cost, poor, poor, m, 40000));
        // Wood / ore paid: at least 15 must stay in the in-combat resources, even when rich.
        var wood = new[] { 5, 0, 0, 0, 0, 0, 0 };
        Assert.False(OriginalBattleAI.BuysRune(100, 100, wood, Rich, new[] { 19, 100, 0, 0, 0, 0, 0 }, null, 35000));
        Assert.True(OriginalBattleAI.BuysRune(100, 100, wood, Rich, new[] { 20, 100, 0, 0, 0, 0, 0 }, null, 35000));
    }

    [Fact]
    public void AnAiArmyEndsItsTacticsAtOnce()   // 004d9520 → army vf 0x44 = 004d95b0 (0085a190, then vf 0xd0 = 004d9400)
    {
        // The AI never places stacks itself in the tactics phase: CCombatAI (vtable 00e91d38) has no placement method and
        // CEndTactic (0054e920) is built only by the combat screen (00541fa0). An AI-owned army is auto-placed by
        // 0085a190 when its tactics phase starts and ends it at once.
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(2, 5), new Cell(9, 5));
        b.State.Phase = BattlePhase.Tactics;
        Assert.IsType<EndTacticsCmd>(new OriginalBattleAI().Decide(b, 0));
        Assert.IsType<EndTacticsCmd>(new OriginalBattleAI().Decide(b, 1));
    }
}
