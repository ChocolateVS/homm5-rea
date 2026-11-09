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
    [Fact]
    public void GuardAndBlockMovesNeedUsToOutShoot()   // FUN_008d64e0: FUN_00bf9eb0 = own FUN_00bf9d30 > the enemy's
    {
        var st = CombatAiStats.Load(f.Db);
        var row = Enum.GetValues<AiDifficulty>().First(r => st.Difficulty[(int)r].UseBlockEnemyMoves && st.Difficulty[(int)r].UseDefendShootersMoves);
        IReadOnlyList<OriginalBattleAI.Candidate> Candidates(int ownArchers, int enemyArchers)
        {
            var s = BattleSetup.Create(f.Db,
                new ArmySetup { Army = { new ArmySlot("CREATURE_FOOTMAN", 10), new ArmySlot("CREATURE_ARCHER", ownArchers) } },
                new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", enemyArchers), new ArmySlot("CREATURE_PEASANT", 10) } },
                1, obstacles: Array.Empty<Cell>(), skipTactics: true);
            var b = new Battle(f.Db, s);
            var footman = s.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
            footman.Pos = new Cell(3, 5);
            s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_ARCHER").Pos = new Cell(1, 5);
            s.Units.First(u => u.Side == 1 && u.CreatureId == "CREATURE_ARCHER").Pos = new Cell(7, 5);
            s.Units.First(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT").Pos = new Cell(11, 1);
            b.Begin();
            for (int i = 0; i < 50 && b.State.Active != footman.Id; i++)
            {
                var la = b.GetLegalActions();
                if (la == null) break;
                b.Apply(la.IsHero ? new WaitCmd(la.Actor) { Side = b.SideToAct!.Value } : new DefendCmd(la.Actor) { Side = b.SideToAct!.Value });
            }
            Assert.Equal(footman.Id, b.State.Active);
            var ai = new OriginalBattleAI { RowOverride = row, HumanSides = new[] { false, false } };
            ai.Decide(b, 0);
            return ai.LastCandidates;
        }
        var strong = Candidates(60, 2);
        Assert.Contains(strong, c => c.Label.StartsWith("guard shooter"));
        Assert.Contains(strong, c => c.Label.StartsWith("block shooter"));
        var weak = Candidates(2, 60);
        Assert.DoesNotContain(weak, c => c.Label.StartsWith("guard shooter") || c.Label.StartsWith("block shooter"));
    }
    [Fact]
    public void ADefensiveStrategyMakesNoApproachMoves()   // FUN_00a2fd40 (settings +0x98 → think +0x1ac), FUN_008d5b90
    {
        (OriginalBattleAI Ai, bool Defensive) Run(int archers, int footmen, int enemyArchers)
        {
            var enemy = new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 30) } };
            if (enemyArchers > 0) enemy.Army.Add(new ArmySlot("CREATURE_ARCHER", enemyArchers));
            var s = BattleSetup.Create(f.Db,
                new ArmySetup { Army = { new ArmySlot("CREATURE_FOOTMAN", footmen), new ArmySlot("CREATURE_ARCHER", archers) } },
                enemy,
                1, obstacles: Array.Empty<Cell>(), skipTactics: true);
            var b = new Battle(f.Db, s);
            var footman = s.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
            footman.Pos = new Cell(1, 1);
            s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_ARCHER").Pos = new Cell(0, 5);
            s.Units.First(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT").Pos = new Cell(s.Width - 1, s.Height - 1);
            if (s.Units.FirstOrDefault(u => u.Side == 1 && u.CreatureId == "CREATURE_ARCHER") is { } ea) ea.Pos = new Cell(s.Width - 1, 0);
            // Our archer walled in: no guard-shooter cell can be reached, so only the approach generator could add records.
            s.Obstacles.AddRange(new[] { new Cell(0, 4), new Cell(1, 4), new Cell(1, 5), new Cell(1, 6), new Cell(0, 6) });
            b.Begin();
            for (int i = 0; i < 50 && b.State.Active != footman.Id; i++)
            {
                var la = b.GetLegalActions();
                if (la == null) break;
                b.Apply(la.IsHero ? new WaitCmd(la.Actor) { Side = b.SideToAct!.Value } : new DefendCmd(la.Actor) { Side = b.SideToAct!.Value });
            }
            Assert.Equal(footman.Id, b.State.Active);
            var ai = new OriginalBattleAI { HumanSides = new[] { false, false } };
            ai.Decide(b, 0);
            return (ai, ai.Defensive(b, 0, CombatValue.Of(b, 0)));
        }
        // Our archers carry the damage and the enemy has no shooting: "Strategy: defensive" → no approach records.
        var (def, isDef) = Run(60, 2, 0);
        Assert.True(isDef);
        Assert.Empty(def.LastCandidates);   // nothing generated → "Chose defend"
        // The enemy out-shoots us: attacking strategy (and no guard / block records), the footmen approach.
        var (att, isAtt) = Run(1, 60, 20);
        Assert.False(isAtt);
        Assert.Contains(att.LastCandidates, c => c.Label.StartsWith("approach"));
    }

    [Fact]
    public void BonusFactorFollowsTheExeTerms()   // FUN_00bfb4b0 (00e12f10 0.1, 00e12f18 0.05, 00e87cf4 1/6, 00e2f798 5, 00e87cf0 13)
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(2, 5), new Cell(9, 5));
        var u = b.State.Units[0];
        u.Atb = 0;
        float k0 = CombatValue.ExeBonus(b, u);
        // Without luck or morale: clamp(initiative, 2, 30) · 0.1 · (1 + 0.05 · (speed − 5)) · (0.95 + 0.1 · ATB).
        float expect = (float)((float)Math.Max(2f, Math.Min((float)b.InitiativeOf(u), 30f)) * 1.0 * 0.1f);
        expect = (float)(((b.SpeedOf(u) - 5) * (double)0.05f + 1.0) * expect);
        expect = (float)((0 * (double)0.1f + 1.0 - 0.05f) * expect);
        Assert.Equal(0, b.MoraleOf(u));
        Assert.Equal(0, b.LuckOf(u));
        Assert.Equal(expect, k0);
        u.Atb = 1;
        Assert.Equal((float)((0.1f + 1.0 - 0.05f) * (expect / (float)(1.0 - 0.05f))), CombatValue.ExeBonus(b, u), 5);
        u.Atb = 0;
        // Disabled time above 0.1 turns: × (1 − min(t, 5) / 6); 0.1 or less: unchanged.
        b.AddEffect(u, new Effect { Id = "SPELL_BLIND", Remaining = 2 });
        Assert.Equal((float)((1.0 - 2f * (double)(1f / 6f)) * k0), CombatValue.ExeBonus(b, u));
        u.GetEffect("SPELL_BLIND")!.Remaining = 9;
        Assert.Equal((float)((1.0 - 5f * (double)(1f / 6f)) * k0), CombatValue.ExeBonus(b, u));
        u.GetEffect("SPELL_BLIND")!.Remaining = 0.1;
        Assert.Equal(k0, CombatValue.ExeBonus(b, u));
    }

    [Fact]
    public void FlatExtrasAreZeroAndTheHeroPartStopsAtTurnThree()   // FUN_00bfba50 (unit vf 0x84 null), FUN_00bfbe50 00bfc00e..00bfc08b
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(2, 5), new Cell(9, 5));
        Assert.Equal(0, CombatValue.Of(b, 0).My.Extra);
        // n = trunc(time + 1): above 2 (from the third turn) the hero part is not computed.
        b.State.Time = 2.0;
        Assert.Equal(0, CombatValue.Of(b, 0).My.HeroPotential);
    }

    [Fact]
    public void CelestialShieldAndDeflectArrowsRaiseHealth()   // FUN_00bfba50 00bfbc97..00bfbcef (00e239bc 1.25), 00bfbd58..00bfbdb4
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_ARCHER", 20, 1, new Cell(2, 5), new Cell(9, 5));
        var u = b.State.Units[0];
        int hp = (int)u.TotalHealth;
        Assert.Equal(hp, (int)CombatValue.Of(b, 0).My.Health);
        b.AddEffect(u, new Effect { Id = "SPELL_CELESTIAL_SHIELD", Remaining = 3 });
        Assert.Equal((int)(hp * 1.25f), (int)CombatValue.Of(b, 0).My.Health);
        u.Effects.Clear();
        // Deflect Arrows: ((1 − s) + s / (1 − g)) · hp · hp, s = the enemy's shooter share (all archers: 1).
        b.AddEffect(u, new Effect { Id = "SPELL_DEFLECT_ARROWS", Remaining = 3, Value = 0.5 });
        Assert.Equal(1f, CombatValue.ShooterShare(b, 1));
        Assert.Equal(CombatValue.Fistp((1.0 - 1f + 1.0 / (1.0 - 0.5f) * 1f) * hp * hp), (int)CombatValue.Of(b, 0).My.Health);
    }

    [Fact]
    public void StrikeAndReturnCountsAsShooterAgainstAMeleeArmy()   // FUN_00a2f8f0 (enemy army) + FUN_00a2f130 in FUN_00bfba50
    {
        var witch = f.Db.Creatures.Values.First(c => c.Abilities.Contains(Ab.StrikeAndReturn) && c.Abilities.Contains(Ab.NoEnemyRetaliation));
        var b = f.Duel(witch.Id, 5, "CREATURE_PEASANT", 20, 1, new Cell(2, 5), new Cell(9, 5));
        Assert.True(CombatValue.NoRangedThreat(b, 1));
        var v = CombatValue.Of(b, 0).My;
        Assert.True(v.ShooterDamage > 0);
        Assert.Equal(0, v.WalkerDamage + v.FlyerDamage);
        // Against an army that shoots, the stack is a walker / flyer again.
        var b2 = f.Duel(witch.Id, 5, "CREATURE_ARCHER", 20, 1, new Cell(2, 5), new Cell(9, 5));
        Assert.False(CombatValue.NoRangedThreat(b2, 1));
        Assert.Equal(0, CombatValue.Of(b2, 0).My.ShooterDamage);
    }

    [Fact]
    public void HeroSpellPotentialIsTheBestDamageSpell()   // FUN_00bfa360(10, 0) for the army's hero (FUN_00bfbe50 00bfc182)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_FOOTMAN", 10) } },
            new ArmySetup { Army = new() { new ArmySlot("CREATURE_IMP", 50) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        var b = new Battle(f.Db, s);
        var h = s.Heroes[0];
        h.Spells.Clear();
        h.Wands.Clear();
        h.Spells.Add("SPELL_MAGIC_ARROW");
        var sp = f.Db.Spell("SPELL_MAGIC_ARROW")!;
        int cost = b.HeroManaCost(h, sp);
        Assert.True(cost > 0);
        h.Mana = cost - 1;   // no cast (mana / cost < 1)
        Assert.Equal(0, b.AiSpellPotential(null, h));
        int dmg = (int)b.SpellBaseDamage(sp, sp, b.HeroMastery(h, sp), b.HeroSpellPower(h, sp));
        h.Mana = cost * 3;   // three casts: 3 · dmg / 10
        Assert.Equal(3 * dmg / 10, b.AiSpellPotential(null, h));
        h.Mana = cost * 40;  // capped at 10 casts
        Assert.Equal(dmg, b.AiSpellPotential(null, h));
        Assert.Equal(dmg, (int)CombatValue.Of(b, 0).My.SpellPotential);
    }

    [Fact]
    public void ACasterArmyCountsAsRanged()   // FUN_00a2f710: shoots (vf 0x1e0) or FUN_00bfa4f0 = FUN_00bfa360(10, 0) ≥ 1
    {
        var caster = f.Db.Creatures.Values.First(c => c.Shots <= 0 && !c.Abilities.Contains(Ab.StrikeAndReturn)
            && c.KnownSpells.Any(k => Battle.SpellDamageSpells.Contains(k.Spell) && k.Spell != "SPELL_ABILITY_EXPLOSION"));
        var b = f.Duel(caster.Id, 20, "CREATURE_PEASANT", 20, 1, new Cell(2, 5), new Cell(9, 5));
        var u = b.State.Units[0];
        Assert.True(b.AiSpellPotential(u, null) >= 1);
        Assert.True(OriginalBattleAI.AllRanged(b, 0, false));
        u.Mana = 0;   // no cast left: a plain walker
        Assert.Equal(0, b.AiSpellPotential(u, null));
        Assert.False(OriginalBattleAI.AllRanged(b, 0, false));
    }

    [Fact]
    public void ApproachPlannerCutsAtOwnStacksAndCountsTurns()   // FUN_008d0720(0xfc, 0, 1, 0), FUN_00c536f0 modes 0 / 1 / 2
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot("CREATURE_FOOTMAN", 10), new ArmySlot("CREATURE_PEASANT", 10) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 10) } },
            1, obstacles: Array.Empty<Cell>(), skipTactics: true);
        var b = new Battle(f.Db, s);
        var u = s.Units.First(x => x.CreatureId == "CREATURE_FOOTMAN");
        var own = s.Units.First(x => x.Side == 0 && x.Id != u.Id);
        var enemy = s.Units.First(x => x.Side == 1);
        u.Pos = new Cell(0, 5); own.Pos = new Cell(3, 5); enemy.Pos = new Cell(s.Width - 1, 0);
        // The whole-path map ignores our own stacks: the straight line passes through (3, 5).
        var map = OriginalBattleAI.WholePath(b, u);
        Assert.Equal(6, map.Dist[new Cell(3, 5)]);
        Assert.Equal(20, map.Dist[new Cell(10, 5)]);
        int B = 2 * b.SpeedOf(u);
        Assert.Equal(8, B);
        // Mode 0 (this turn only) fails beyond B; mode 2 keeps the whole path (Turns (B + cost − 1) / B − 1 = 2).
        Assert.Null(OriginalBattleAI.PlanMove(b, u, new Cell(10, 5), 0, map));
        Assert.Equal(new OriginalBattleAI.MovePlan(new Cell(2, 5), 2), OriginalBattleAI.PlanMove(b, u, new Cell(10, 5), 2, map));
        // Mode 1: stop cells path[e..k] = (2,5) (3,5) (4,5) (the pieces of B from the end start at 2; k = 4 cells within B),
        // no danger there → the later of the ties (4, 5); then the cut before our own stack at (3, 5).
        Assert.Equal(new OriginalBattleAI.MovePlan(new Cell(2, 5), 2), OriginalBattleAI.PlanMove(b, u, new Cell(10, 5), 1, map));
        own.Pos = new Cell(0, 0);
        Assert.Equal(new OriginalBattleAI.MovePlan(new Cell(4, 5), 2), OriginalBattleAI.PlanMove(b, u, new Cell(10, 5), 1, map));
        // Within B: the whole path, no turns; the own cell fails.
        Assert.Equal(new OriginalBattleAI.MovePlan(new Cell(4, 5), 0), OriginalBattleAI.PlanMove(b, u, new Cell(4, 5), 0, map));
        Assert.Null(OriginalBattleAI.PlanMove(b, u, u.Pos, 1, map));
    }

    [Fact]
    public void ApproachStopsWhereTheEnemyCannotStrikeThisTurn()   // FUN_00c53640 → FUN_00c53310 (walker branch)
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 10, 1, new Cell(0, 5), new Cell(0, 0));
        var u = b.State.Units[0];
        var t = b.State.Units[1];
        int sp = b.SpeedOf(t);
        t.Pos = new Cell(5 + sp, 5);   // reaches (5, 5) next to (4, 5), not (4, 5)'s left neighbour cells around (3, 5)
        var cells = new List<Cell> { new(2, 5), new(3, 5), new(4, 5) };
        var (_, max) = b.DamageRangeOf(t);
        int d = (int)((float)t.Count * (float)max);
        Assert.Equal(new[] { 0, 0, d }, OriginalBattleAI.Danger(b, u, cells));
        Assert.Equal(new Cell(3, 5), OriginalBattleAI.StopCell(b, u, cells));   // lowest score, the later one on ties
    }

    [Fact]
    public void CombatLengthDistanceMovesTheLargeStackOnlyOnTheGreaterSide()   // FUN_00b3c870 (0x00b3c8ab..0x00b3c8fb)
    {
        var b = f.Duel("CREATURE_GRIFFIN", 1, "CREATURE_PEASANT", 1, 1, new Cell(0, 0), new Cell(0, 0));
        var big = b.State.Units[0];
        var small = b.State.Units[1];
        Assert.Equal(2, big.Size);
        Assert.Equal(7, OriginalBattleAI.ExeDistance(small, new Cell(0, 0), small, new Cell(3, 1)));   // min + 2·max
        Assert.Equal(8, OriginalBattleAI.ExeDistance(big, new Cell(5, 0), small, new Cell(0, 0)));     // a right of b: a.x − 1
        Assert.Equal(10, OriginalBattleAI.ExeDistance(big, new Cell(0, 0), small, new Cell(5, 0)));    // a left of b: unchanged
        Assert.Equal(8, OriginalBattleAI.ExeDistance(small, new Cell(0, 0), big, new Cell(5, 0)));     // b right of a: b.x − 1
        Assert.Equal(10, OriginalBattleAI.ExeDistance(small, new Cell(5, 0), big, new Cell(0, 0)));
    }

    [Fact]
    public void CombatLengthFightRoundsUseIntegerShares()   // FUN_00b393a0 0x00b3961d..0x00b396fb (x87 chop)
    {
        // Round 1: 50 / 100 = 0 → the enemy keeps its 30 damage; health 100 → 50 / 70. Round 2: 50 / 50 = 1 → enemy damage 0,
        // enemy health 0: two rounds, won.
        Assert.Equal((true, 2), OriginalBattleAI.FightRounds(50, 30, 100, 100));
        Assert.Equal((false, 1), OriginalBattleAI.FightRounds(10, 200, 100, 1000));
        Assert.Equal((true, 20), OriginalBattleAI.FightRounds(1, 1, 1000, 1000));   // at most 20 rounds
        Assert.Equal((false, 0), OriginalBattleAI.FightRounds(1, 1, 0, 1000));
    }

    [Fact]
    public void AShortCombatMakesNoGuardOrBlockMoves()   // FUN_00a30460 (settings +0xa3 = length < 3) → think +0x1b7, FUN_008d64e0
    {
        // 100 peasants next to 5: no moves, the first round kills (75 damage vs 16 health) → length 1, short.
        var b = f.Duel("CREATURE_PEASANT", 100, "CREATURE_PEASANT", 5, 1, new Cell(5, 5), new Cell(6, 5));
        Assert.Equal((true, 1), OriginalBattleAI.CombatLength(b, 0, CombatValue.Of(b, 0)));
        Assert.True(OriginalBattleAI.ShortCombat(b, 0, CombatValue.Of(b, 0)));
        // 20 against 20: 15 damage vs 63 health a side → five rounds, not short.
        var even = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(5, 5), new Cell(6, 5));
        var v = CombatValue.Of(even, 0);
        Assert.Equal((15, 63), (v.My.DamageMeasure(v.Enemy), OriginalBattleAI.HealthMeasure(v.My)));
        Assert.Equal((true, 5), OriginalBattleAI.CombatLength(even, 0, v));
        var far = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 20, 1, new Cell(0, 0), new Cell(11, 9));
        Assert.False(OriginalBattleAI.ShortCombat(far, 0, CombatValue.Of(far, 0)));
    }

    [Fact]
    public void TheMoatDamageOfEnemyStacksInTheMoatIsAddedToOurDamage()   // FUN_00bfbe50 → FUN_00bfa610 / FUN_0085c2c0 (type-5 objects)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 30), new ArmySlot("CREATURE_FOOTMAN", 30) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_FOOTMAN", 30) } },
            3, skipTactics: true, siege: new SiegeSetup { Town = "TOWN_HEAVEN", FortLevel = 3, TownLevel = 10, TownMaxLevel = 10 });
        var b = new Battle(f.Db, s);
        var sg = s.Siege!;
        Assert.True(sg.MoatDamage > 0);
        var archer = s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_ARCHER");
        var footman = s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_FOOTMAN");
        Assert.Equal(0, CombatValue.Of(b, 1).My.Additive);
        footman.Pos = sg.Moat.First(c => b.Fits(footman, c));
        Assert.Equal(sg.MoatDamage, CombatValue.Of(b, 1).My.Additive);           // the defender's half: attackers in its moat
        archer.Pos = sg.Moat.Last(c => c != footman.Pos && b.Fits(archer, c));
        Assert.Equal(sg.MoatDamage * (b.CanShoot(archer) ? 3 : 2), CombatValue.Of(b, 1).My.Additive);   // × 2 for a shooter
        Assert.Equal(0, CombatValue.Of(b, 0).My.Additive);                       // the moat never hurts the defenders
    }
}
