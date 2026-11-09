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
    [Fact]
    public void ContactMatrixFloodsAroundObstacles()   // FUN_004b23b0 (004b1b50 / 004b1ff0 / 004b1e20 / 004b18f0)
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(2, 5), new Cell(9, 5));
        var s = b.State;
        var m = Contact.Build(b, 0);
        Assert.True(m[1, 0] && m[0, 1]);   // the same open region
        Assert.Equal(1.0, CombatValue.ContactFactor(b, s.Units[0], m));
        // A wall across the whole field: no region reaches both stacks; the walker's damage counts 0 (FUN_00bfa280).
        for (int y = 0; y < s.Height; y++) s.Obstacles.Add(new Cell(5, y));
        m = Contact.Build(b, 0);
        Assert.False(m[1, 0] || m[0, 1]);
        Assert.Equal(0.0, CombatValue.ContactFactor(b, s.Units[0], m));
        Assert.Equal(0, CombatValue.Of(b, 0).My.Damage);
        // Regions join diagonally (8 directions): a one-cell gap on the diagonal is enough.
        s.Obstacles.Remove(new Cell(5, 0));
        Assert.True(Contact.Build(b, 0)[1, 0]);
        // Next to each other always counts (FUN_004b18f0), whatever the flood says.
        s.Obstacles.Add(new Cell(5, 0));
        s.Units[1].Pos = new Cell(3, 5);
        Assert.True(Contact.Build(b, 0)[1, 0]);
    }

    [Fact]
    public void AnUnreachableShooterCountsDouble()   // FUN_00bfba50 → FUN_00bfb950 (matrix [shooter][enemy] via 004b1660)
    {
        var st = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 20), new ArmySlot("CREATURE_PEASANT", 10) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 30) } },
            1, obstacles: Array.Empty<Cell>(), skipTactics: true);
        var b = new Battle(f.Db, st);
        var archer = st.Units.First(u => u.CreatureId == "CREATURE_ARCHER");
        var own = st.Units.First(u => u.Side == 0 && u != archer);
        var enemy = st.Units.First(u => u.Side == 1);
        archer.Pos = new Cell(1, 5); own.Pos = new Cell(4, 2); enemy.Pos = new Cell(10, 5);
        var m = Contact.Build(b, 0);
        Assert.Equal(1.0, CombatValue.ShooterThreat(b, archer, m));   // the enemy peasants can walk up to it
        int plain = CombatValue.ExpectedDamage(b, archer, m);
        // Walled in: the enemy still reaches our other stack (its damage counts) but not the archer → f = 0.
        for (int x = 0; x <= 2; x++) for (int y = 4; y <= 6; y++) if (x != 1 || y != 5) st.Obstacles.Add(new Cell(x, y));
        m = Contact.Build(b, 0);
        Assert.Equal(0.0, CombatValue.ShooterThreat(b, archer, m));
        Assert.Equal(2 * plain + CombatValue.ExpectedDamage(b, own, m), CombatValue.Of(b, 0).My.Damage);   // trunc((1 − 0 + 1) · dmg)
        // An enemy that reaches none of our stacks has no damage (FUN_00bfa280) and drops out of the share: f = 1.
        own.Pos = new Cell(0, 0);
        st.Obstacles.AddRange(new[] { new Cell(1, 0), new Cell(0, 1), new Cell(1, 1) });
        m = Contact.Build(b, 0);
        Assert.Equal(0, CombatValue.ExpectedDamage(b, enemy, m));
        Assert.Equal(1.0, CombatValue.ShooterThreat(b, archer, m));
    }

    [Fact]
    public void MeasuresTruncateUnderChop()   // FUN_00bf9cc0 / 00bfc4a0 / 00bf9b80: FISTP with RC = 11
    {
        var v = new SideValue { ShooterDamage = 10, ShooterAttack = 1, Bonus = 1.19f };
        Assert.Equal(11, v.ShooterPower(new SideValue { TroopsDefence = 1.9 }));   // factor(1, trunc(1.9) = 1) = 1 → 11.9 → 11
        var p = new SideValue { Damage = 10, Bonus = 1, AttackLevel = 1, Health = 9, Defence = 1 };
        Assert.Equal(19, p.Power(new SideValue { AttackLevel = 1.99f, TroopsDefence = 1 }));   // 10 + 9 · 1/factor(1, 1)
        // The hero share counts when THIS side out-shoots the other (CMP own, other; JLE skip).
        var me = new SideValue { Damage = 0, HeroPotential = 100, ShooterDamage = 50, ShooterAttack = 1, Bonus = 1 };
        var them = new SideValue { WalkerDamage = 30, ShooterDamage = 10, ShooterAttack = 1, Bonus = 1 };
        Assert.Equal(75, me.DamageMeasure(them));   // trunc(30 / 40 · 100)
        Assert.Equal(0, them.DamageMeasure(me));
    }

    [Fact]
    public void IncorporealHealthCountsMore()   // FUN_00bfba50 0x00bfbc07..0x00bfbc7b (ability 0x28, effect 0x100; doubles 00e87cf8 / 00e72bf8)
    {
        var b = f.Duel("CREATURE_GHOST", 10, "CREATURE_PEASANT", 30, 1, new Cell(1, 5), new Cell(10, 5));
        var ghost = b.State.Units[0];
        Assert.True(ghost.Has(Ab.Incorporeal));
        Assert.Equal((int)(long)((int)ghost.TotalHealth * 1.4), (int)CombatValue.Of(b, 0).My.Health);
    }
}
