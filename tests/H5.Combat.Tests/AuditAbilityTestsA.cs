using System.Reflection;
using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Haven / Inferno / Necropolis / Sylvan creature abilities confirmed against H5_Game.exe
/// (see docs/rules/audit/combat_abilities_a.md).
/// </summary>
[Collection("game")]
public class AuditAbilityTestsA(GameDataFixture f)
{
    static bool BeginTurn(Battle b, int id) =>
        (bool)typeof(Battle).GetMethod("BeginTurn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, new object[] { id })!;

    [Fact]
    public void BattleFrenzyLastsTheWholeBattle()   // FUN_00a629b0 adds SPELL_EFFECT_BATTLE_FRENZY for 0x7ffffffe
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_BATTLE_GRIFFIN", 30, 3, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        var peasants = b.State.Units[0];
        var griffins = b.State.Units[1];
        b.State.Active = peasants.Id;
        var r = b.Apply(new AttackCmd(peasants.Id, griffins.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events.OfType<AttackStarted>(), e => e.Kind == AttackKind.Retaliation && e.Attacker == griffins.Id);
        double frenzy = Stats(b);
        Assert.Equal(frenzy, griffins.FrenzyMultiplier, 6);
        BeginTurn(b, griffins.Id);   // the griffins' own turn must not clear the frenzy
        Assert.Equal(frenzy, griffins.FrenzyMultiplier, 6);
    }

    static double Stats(Battle b) => b.Db.Stats.Get("combat/Creatures/Haven/BattleFrenzyBoostFactor", 1.5);

    [Fact]
    public void CleaveAddsAFullStrikeAfterTheFirstBlow()   // FUN_00a65260: HasAbility(CLEAVE) only when strike == 0
    {
        bool seen = false;
        for (ulong seed = 1; seed <= 20 && !seen; seed++)
        {
            var b = f.Duel("CREATURE_VINDICATOR", 40, "CREATURE_PEASANT", 300, seed, new Cell(5, 5), new Cell(6, 5));
            b.Begin();
            var u = b.State.Units[0];
            b.State.Active = u.Id;
            var r = b.Apply(new AttackCmd(u.Id, b.State.Units[1].Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            var order = r.Events.OfType<AttackStarted>().Select(e => e.Kind).ToList();
            if (!r.Events.OfType<AbilityTriggered>().Any(e => e.Ability == Ab.Cleave)) continue;
            seen = true;
            // First strike, the peasants' retaliation, then the cleave strike as a normal pattern strike.
            Assert.Equal(2, order.Count(k => k == AttackKind.Melee));
            int ret = order.IndexOf(AttackKind.Retaliation);
            Assert.True(ret > order.IndexOf(AttackKind.Melee) && ret < order.LastIndexOf(AttackKind.Melee));
        }
        Assert.True(seen);
    }

    Battle ChargeSetup(Cell start, out Unit ally)
    {
        // 2x2 vs 2x2 on the same rows so the direction is unambiguous (the 12-direction pattern rotation for large
        // creatures is a separate item).
        var b = f.Duel("CREATURE_CHAMPION", 3, "CREATURE_CAVALIER", 40, 1, start, new Cell(8, 4));
        ally = BattleSetup.AddCreature(b.Db, b.State, 0, "CREATURE_PEASANT", 30, new Cell(10, 4), summoned: false);
        b.Begin();
        b.State.Active = b.State.Units[0].Id;
        return b;
    }

    [Fact]
    public void ChampionChargeNeedsMoreThanTheMinimalDistanceAndHitsAllies()   // FUN_008aa990, ChampionCharge.xdb DamageAll
    {
        var b = ChargeSetup(new Cell(1, 4), out var ally);
        int before = ally.TotalHealth;
        var r = b.Apply(new AttackCmd(b.State.Units[0].Id, b.State.Units[1].Id, new Cell(6, 4)) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.True(ally.TotalHealth < before);   // 5 cells walked > 2: the own stack behind the target is hit at 0.5

        b = ChargeSetup(new Cell(4, 4), out ally);
        before = ally.TotalHealth;
        r = b.Apply(new AttackCmd(b.State.Units[0].Id, b.State.Units[1].Id, new Cell(6, 4)) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(before, ally.TotalHealth);   // exactly ChampionChargeMinimalDistance (2): no charge pattern
    }

    [Fact]
    public void AgilityIsClearedEachTurnAndSetByTheMove()   // FUN_008aaec0 (start = speed), FUN_008a6c40 (turn start), FUN_00a66050 (move)
    {
        var b = f.Duel("CREATURE_BLADE_SINGER", 10, "CREATURE_PEASANT", 10, 1, new Cell(1, 5), new Cell(11, 5));
        var u = b.State.Units[0];
        int per = (int)b.Db.Stats.Get("combat/Creatures/Sylvan/AgilityDefenceBoostPerCell", 2);
        int start = b.DefenceOf(u);   // before the battle's first turn
        b.Begin();
        BeginTurn(b, u.Id);
        int bare = b.DefenceOf(u);                                         // the turn start removes the bonus
        Assert.Equal(per * u.BaseSpeed, start - bare);                     // before its first turn: worth its speed
        b.State.Active = u.Id;
        Assert.True(b.Apply(new MoveCmd(u.Id, new Cell(4, 5)) { Side = 0 }).Ok);
        Assert.Equal(per * 3, b.DefenceOf(u) - bare);                      // 3 cells walked
        BeginTurn(b, u.Id);
        Assert.Equal(bare, b.DefenceOf(u));                                // a turn without moving keeps no bonus
    }

    (Battle b, ApplyResult r) Hit(string attacker, int count, string target, int tcount, Cell a, Cell tpos, params (int side, string id, Cell pos)[] extra)
    {
        var b = f.Duel(attacker, count, target, tcount, 1, a, tpos);
        foreach (var (side, id, pos) in extra) BattleSetup.AddCreature(b.Db, b.State, side, id, 50, pos, summoned: false);
        b.Begin();
        b.State.Active = b.State.Units[0].Id;
        var r = b.Apply(new AttackCmd(b.State.Units[0].Id, b.State.Units[1].Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        return (b, r);
    }

    static bool Damaged(Battle b, ApplyResult r, Cell c) =>
        r.Events.OfType<Damaged>().Any(d => b.State.Units.First(u => u.Id == d.Target).Pos == c);

    [Fact]
    public void ThreeHeadedHitsTheCellsBesideTheTargetNotBesideTheAttacker()   // Cerberius.xdb PatternAttack (8 rotations)
    {
        var (b, r) = Hit("CREATURE_CERBERI", 20, "CREATURE_PEASANT", 200, new Cell(5, 5), new Cell(6, 5),
            (1, "CREATURE_PEASANT", new Cell(6, 4)), (1, "CREATURE_PEASANT", new Cell(5, 4)));
        Assert.True(Damaged(b, r, new Cell(6, 4)));    // (1,-1): beside the target
        Assert.False(Damaged(b, r, new Cell(5, 4)));   // (0,-1): beside the attacker — not in the pattern
    }

    [Fact]
    public void FirebreatherHoundUsesItsOwnPattern()   // FirebreatherHound.xdb: far cells two steps out, DamageAll = false
    {
        var (b, r) = Hit("CREATURE_FIREBREATHER_HOUND", 20, "CREATURE_PEASANT", 200, new Cell(5, 5), new Cell(6, 5),
            (1, "CREATURE_PEASANT", new Cell(7, 3)), (0, "CREATURE_PEASANT", new Cell(7, 5)));
        Assert.True(Damaged(b, r, new Cell(7, 3)));    // 2 × (1,-1)
        Assert.False(Damaged(b, r, new Cell(7, 5)));   // own stack behind the target: enemies only
    }

    [Fact]
    public void AcidBreathHitsOnlyTheCellBehindTheTargetIncludingAllies()   // Gold_Dragon.xdb pattern, DamageAll = true
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var b = f.Duel("CREATURE_GOLD_DRAGON", 3, "CREATURE_CAVALIER", 30, seed, new Cell(2, 4), new Cell(4, 4));
            BattleSetup.AddCreature(b.Db, b.State, 0, "CREATURE_PEASANT", 50, new Cell(6, 5), summoned: false);
            BattleSetup.AddCreature(b.Db, b.State, 1, "CREATURE_PEASANT", 50, new Cell(7, 4), summoned: false);
            b.Begin();
            b.State.Active = b.State.Units[0].Id;
            var r = b.Apply(new AttackCmd(b.State.Units[0].Id, b.State.Units[1].Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            Assert.True(Damaged(b, r, new Cell(6, 5)));    // the own stack right behind the target
            Assert.False(Damaged(b, r, new Cell(7, 4)));   // two cells behind: no random radius for Acid Breath
        }
    }

    static bool ShotIsAnswered(Battle b, Unit shooter, Unit succubi)
    {
        b.State.Active = shooter.Id;
        var r = b.Apply(new ShootCmd(shooter.Id, succubi.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        return r.Events.OfType<AttackStarted>().Any(e => e.Kind == AttackKind.Retaliation && e.Attacker == succubi.Id);
    }

    [Fact]
    public void RangedRetaliationObeysTheOtherRetaliationRules()   // FUN_00a5fee0: shooter test, then Blind/sleep/... still apply
    {
        var b = f.Duel("CREATURE_ARCHER", 30, "CREATURE_SUCCUBUS", 20, 1, new Cell(1, 2), new Cell(10, 7));
        b.Begin();
        Assert.True(ShotIsAnswered(b, b.State.Units[0], b.State.Units[1]));

        b = f.Duel("CREATURE_ARCHER", 30, "CREATURE_SUCCUBUS", 20, 1, new Cell(1, 2), new Cell(10, 7));
        b.Begin();
        b.AddEffect(b.State.Units[1], new Effect { Id = "SPELL_BLIND", Remaining = 3, Mastery = Mastery.Expert });
        Assert.False(ShotIsAnswered(b, b.State.Units[0], b.State.Units[1]), "expert blind");   // COMBAT_NO_BLIND_RETALIATE

        b = f.Duel("CREATURE_ARCHER", 30, "CREATURE_SUCCUBUS", 20, 1, new Cell(1, 2), new Cell(10, 7));
        b.Begin();
        b.AddEffect(b.State.Units[1], new Effect { Id = Fx.Sleep, Remaining = 3 });
        Assert.False(ShotIsAnswered(b, b.State.Units[0], b.State.Units[1]), "sleep");   // a sleeping stack cannot retaliate
    }

    [Fact]
    public void DeathWailUsesTheDataCoefficientAndGapExponent()   // FUN_0085e610 damage, FUN_0097a530 case 0x13c area
    {
        var b = f.Duel("CREATURE_BANSHEE", 5, "CREATURE_PEASANT", 500, 1, new Cell(4, 4), new Cell(6, 4));   // banshees 2x2: gap 1
        var far = BattleSetup.AddCreature(b.Db, b.State, 1, "CREATURE_PEASANT", 500, new Cell(4, 7), summoned: false);   // gap 2
        var golems = BattleSetup.AddCreature(b.Db, b.State, 1, "CREATURE_IRON_GOLEM", 50, new Cell(2, 4), summoned: false);   // mechanical
        b.Begin();
        var w = b.State.Units[0];
        var near = b.State.Units[1];
        float coef = (float)b.Db.Stats.Get("combat/Creatures/Necropolis/DeathWailDamageCoef", 2);
        int expectNear = (int)((float)w.Count * (10 - b.MoraleOf(near)) * coef);
        int expectFar = (int)((int)((float)w.Count * (10 - b.MoraleOf(far)) * coef) / 2f);
        int hpNear = near.TotalHealth, hpFar = far.TotalHealth, hpGolems = golems.TotalHealth;
        b.State.Active = w.Id;
        var r = b.Apply(new UseAbilityCmd(w.Id, Ab.DeathWail) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(expectNear, hpNear - near.TotalHealth);
        Assert.Equal(expectFar, hpFar - far.TotalHealth);
        Assert.Equal(hpGolems, golems.TotalHealth);   // Mechanical is skipped
        Assert.Equal(2f, coef);                         // the 3.1 patch value (a2p1-data.pak)
    }

    Battle WithHeroes(string a, int countA, string b2, int countB, int enemyMana)
    {
        var b = f.Duel(a, countA, b2, countB, 1, new Cell(1, 2), new Cell(10, 7));
        b.State.Heroes[0].Present = true;
        b.State.Heroes[1].Present = true;
        b.State.Heroes[1].Mana = enemyMana;
        return b;
    }

    [Fact]
    public void ManaStealerTakesCeilOfStackTimesRateOverGrowth()   // FUN_0097bd60: ceil(N · 4 / growth 16)
    {
        var b = WithHeroes("CREATURE_IMP", 10, "CREATURE_PEASANT", 10, 100);
        b.Begin();
        Assert.Equal(97, b.State.Heroes[1].Mana);   // ceil(10·4/16) = 3 (the engine rounded 2.5 down to 2)
        Assert.Equal(3, b.State.Heroes[0].Mana);
    }

    [Fact]
    public void SyphonManaUsesTheHalvedRateNotHalfTheStack()   // FUN_0097bd60 spell 0x13f: k = trunc(4·0.5 + 0.5) = 2
    {
        var b = WithHeroes("CREATURE_QUASIT", 20, "CREATURE_PEASANT", 10, 100);
        b.Begin();
        var q = b.State.Units[0];
        b.State.Active = q.Id;
        var r = b.Apply(new UseAbilityCmd(q.Id, Ab.SyphonMana) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(97, b.State.Heroes[1].Mana);   // ceil(20·2/16) = 3 (the engine took 20·0.5 = 10)
    }
}
