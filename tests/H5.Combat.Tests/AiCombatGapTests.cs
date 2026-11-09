using H5.Core.Data;
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
    [Fact]
    public void ManaLimitFollowsTheDamageRule()   // FUN_00a2f280 (00e22820 = 10.0, 00e0a7e4 = 1.0, x87 truncation, single precision)
    {
        Assert.Equal(0, OriginalBattleAI.ExeManaLimit(99, 100, 50));      // our damage below the enemy health: no limit
        Assert.Equal(0, OriginalBattleAI.ExeManaLimit(100, 100, 50));     // d = h: (1 − 1) · mana
        Assert.Equal(25, OriginalBattleAI.ExeManaLimit(200, 100, 50));    // keep (1 − h/d) of the mana
        Assert.Equal(45, OriginalBattleAI.ExeManaLimit(1000, 100, 50));   // d = 10h: still the formula (0.9f · 50 = 45 in single precision)
        Assert.Equal(33, OriginalBattleAI.ExeManaLimit(300, 100, 50));    // (1 − 1/3) · 50 = 33.3 truncated
        Assert.Equal(50, OriginalBattleAI.ExeManaLimit(1001, 100, 50));   // above 10h: the whole mana
    }

    [Fact]
    public void ManaLimitExcludesSpellsThatSpendBelowIt()   // FUN_008d1cb0: spell flag +0xb4 and record +0xa8 < think +0x1b0
    {
        var st = CombatAiStats.Load(f.Db);
        var v = new CombatValue { My = new SideValue { TroopsHealth = 100, Health = 100, Damage = 10 }, Enemy = new SideValue { TroopsHealth = 100, Health = 100, Damage = 10 } };
        OriginalBattleAI.Candidate C(bool spell, int mana, int type = 0) => new() { Command = new WaitCmd(0), IsSpell = spell, ManaAfter = mana, CompareType = type, Value = v };
        Assert.True(OriginalBattleAI.Excluded(C(true, 9), v, st.CompareCoefs, CombatMode.Normal, 10));
        Assert.False(OriginalBattleAI.Excluded(C(true, 10), v, st.CompareCoefs, CombatMode.Normal, 10));
        Assert.False(OriginalBattleAI.Excluded(C(false, 0), v, st.CompareCoefs, CombatMode.Normal, 10));   // not a spell
        Assert.True(OriginalBattleAI.Excluded(C(true, 9, 1), v, st.CompareCoefs, CombatMode.Normal, 10)); // type 1 kept by the start, then the limit
    }

    [Fact]
    public void AHumanHeroInAutoCombatSpendsNoManaByDefault()   // FUN_00a30460 → FUN_00a2f280; combat_ai_use_magic_for_human = 0
    {
        (Battle, HeroState) Setup()
        {
            var s = BattleSetup.Create(f.Db,
                new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_FOOTMAN", 10) } },
                new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot("CREATURE_IMP", 50) } },
                7, skipTactics: true, arena: "Grass_Big_01");
            var b = new Battle(f.Db, s);
            var h = s.Heroes[0];
            if (!h.Spells.Contains("SPELL_MAGIC_ARROW")) h.Spells.Add("SPELL_MAGIC_ARROW");
            h.Mana = 500;
            b.Begin();
            for (int i = 0; i < 50 && b.State.Active != Actor.Hero(0); i++)
            {
                var la = b.GetLegalActions();
                if (la == null) break;
                b.Apply(la.IsHero ? new WaitCmd(la.Actor) { Side = b.SideToAct!.Value } : new DefendCmd(la.Actor) { Side = b.SideToAct!.Value });
            }
            Assert.Equal(Actor.Hero(0), b.State.Active);
            return (b, h);
        }
        var (b1, _) = Setup();
        var human = new OriginalBattleAI { HumanSides = new[] { true, false } };
        Assert.IsNotType<CastSpellCmd>(human.Decide(b1, 0));
        Assert.Contains(human.LastCandidates, c => c.IsSpell);   // generated, then "Excluded by mana limit"
        // A computer player's hero has no limit (vf 0x18c → 0).
        var (b2, _) = Setup();
        var computer = new OriginalBattleAI { HumanSides = new[] { false, false } };
        computer.Decide(b2, 0);
        Assert.Contains(computer.LastCandidates, c => c.IsSpell && c.Value != null);
    }
}
