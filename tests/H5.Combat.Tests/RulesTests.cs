using H5.Core.Data;

namespace H5.Combat.Tests;

[Collection("game")]
public class RulesTests(GameDataFixture f)
{
    /// <summary>Fan manual p.302 worked example: 10 Griffins attack 30 Horned Demons, then the demons retaliate.</summary>
    [StockFact]
    public void GriffinsVersusHornedDemons()
    {
        var b = f.Duel("CREATURE_GRIFFIN", 10, "CREATURE_DEMON", 30, posA: new Cell(4, 4), posB: new Cell(6, 4));
        var griffins = b.State.Units[0];
        var demons = b.State.Units[1];
        // Damage = 10 * 7.8 * [1 + 0.05*(7-3)] = 93.6 → 93
        int dmg = b.ComputeDamage(griffins, demons, new Battle.Hit { Kind = AttackKind.Melee, FixedRoll = 7.8 }, ranged: false);
        Assert.Equal(93, dmg);
        b.DealDamage(demons, dmg, griffins.Id, AttackKind.Melee, null);
        Assert.Equal(23, demons.Count);
        Assert.Equal(11, demons.TopHealth);
        // Retaliation: 23 * 1.6 / [1 + 0.05*(5-1)] = 30.66 → 30, killing exactly one Griffin.
        int ret = b.ComputeDamage(demons, griffins, new Battle.Hit { Kind = AttackKind.Retaliation, FixedRoll = 1.6 }, ranged: false);
        Assert.Equal(30, ret);
        b.DealDamage(griffins, ret, demons.Id, AttackKind.Retaliation, null);
        Assert.Equal(9, griffins.Count);
    }

    [StockFact]
    public void AttackDefenceMultiplierTable()
    {
        // p.301 table: D-A = 10 → x0.667; A-D = 20 → x2.0
        var b = f.Duel("CREATURE_PEASANT", 100, "CREATURE_PEASANT", 100);
        var a = b.State.Units[0]; var t = b.State.Units[1];
        a.BaseAttack = 1; t.BaseDefence = 11;
        int low = b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 1 }, false);
        Assert.Equal(66, low);    // 100 / 1.5 = 66.67
        a.BaseAttack = 31; t.BaseDefence = 11;
        int high = b.ComputeDamage(a, t, new Battle.Hit { FixedRoll = 1 }, false);
        Assert.Equal(200, high);
    }

    [Fact]
    public void ShooterPenalties()
    {
        var b = f.Duel("CREATURE_ARCHER", 10, "CREATURE_PEASANT", 100, posA: new Cell(0, 0), posB: new Cell(11, 9));
        var archer = b.State.Units[0]; var peasants = b.State.Units[1];
        archer.BaseAttack = peasants.BaseDefence = 0;
        int far = b.ComputeDamage(archer, peasants, new Battle.Hit { FixedRoll = 2 }, ranged: true);
        peasants.Pos = new Cell(3, 0);
        int near = b.ComputeDamage(archer, peasants, new Battle.Hit { FixedRoll = 2 }, ranged: true);
        int melee = b.ComputeDamage(archer, peasants, new Battle.Hit { Kind = AttackKind.Melee, FixedRoll = 2 }, ranged: false);
        Assert.Equal(20, near);
        Assert.Equal(10, far);    // beyond half the battlefield: 50%
        Assert.Equal(10, melee);  // shooter in melee: 50%
    }

    [Theory]
    [InlineData(1, 1, 5)]
    [InlineData(2, 1, 9)]
    [InlineData(10, 4, 10)]
    [InlineData(100, 5, 26)]
    [InlineData(1000, 15, 37)]
    [InlineData(20, 2, 20)]
    public void CreatureSpellPowerTable(int size, int growth, int expected) =>
        Assert.Equal(expected, Battle.CreatureSpellPower(size, growth));   // fan manual p.305-306

    [StockFact]
    public void HeroKillsTable()
    {
        var b = f.Duel("CREATURE_PEASANT", 1, "CREATURE_PEASANT", 1);
        Assert.Equal(2.0, b.HeroKills(1, 1), 3);
        Assert.Equal(2.333, b.HeroKills(2, 1), 3);
        Assert.Equal(0.52, b.HeroKills(10, 7), 3);
        Assert.Equal(12.0, b.HeroKills(31, 1), 3);
        Assert.Equal(1.033, b.HeroKills(21, 7), 3);   // p.302: a hero kills one tier-7 creature from level 21
    }

    [StockFact]
    public void StartingAtbAndTurnOrder()
    {
        // p.310: an Initiative-16 stack starting at ATB 0 still acts before an Initiative-8 stack starting at 0.25.
        var b = f.Duel("CREATURE_NIGHTMARE", 10, "CREATURE_BALOR", 5);
        b.Begin();
        var fast = b.State.Units[0]; var slow = b.State.Units[1];
        Assert.True(fast.BaseInitiative >= 2 * slow.BaseInitiative);
        Assert.Equal(fast.Id, b.State.Active);
        Assert.InRange(slow.Atb, 0, 1.0);
    }

    [Fact]
    public void WaitResetsAtbToHalfAndDefendRaisesDefence()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_FOOTMAN", 10);
        b.Begin();
        var active = b.State.Unit(b.State.Active!.Value)!;
        int side = active.Side;
        var r = b.Apply(new WaitCmd(active.Id) { Side = side });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events, e => e is AtbChanged a && a.Actor == active.Id && Math.Abs(a.Atb - 0.5) < 1e-9);

        var next = b.State.Unit(b.State.Active!.Value)!;
        int defBefore = b.DefenceOf(next);
        Assert.True(b.Apply(new DefendCmd(next.Id) { Side = next.Side }).Ok);
        Assert.Equal((int)Math.Floor(next.BaseDefence * 1.3), b.DefenceOf(next));
        Assert.True(b.DefenceOf(next) > defBefore);
    }

    [Fact]
    public void RetaliationOncePerTurnUnlessUnlimited()
    {
        var b = f.Duel("CREATURE_PEASANT", 50, "CREATURE_GRIFFIN", 5, posA: new Cell(4, 4), posB: new Cell(5, 4));
        var griffins = b.State.Units[1];
        Assert.Contains(Ab.UnlimitedRetaliation, griffins.Abilities);
        b.Begin();
        Assert.Equal(int.MaxValue, griffins.RetaliationsLeft);
        var b2 = f.Duel("CREATURE_PEASANT", 50, "CREATURE_FOOTMAN", 5);
        Assert.Equal(1, b2.State.Units[1].RetaliationsLeft);
    }

    [Fact]
    public void IllegalCommandsAreRejectedWithoutSideEffects()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_ARCHER", 10);
        b.Begin();
        var hash = b.State.ComputeHash();
        int active = b.State.Active!.Value;
        var other = b.State.Units.First(u => u.Id != active);
        Assert.False(b.Apply(new WaitCmd(other.Id) { Side = other.Side }).Ok);                       // not its turn
        var me = b.State.Unit(active)!;
        Assert.False(b.Apply(new WaitCmd(me.Id) { Side = 1 - me.Side }).Ok);                         // not your unit
        Assert.False(b.Apply(new MoveCmd(me.Id, new Cell(-5, 3)) { Side = me.Side }).Ok);            // out of range
        Assert.False(b.Apply(new CastSpellCmd(me.Id, "SPELL_FIREBALL", null, new Cell(5, 5)) { Side = me.Side }).Ok);
        Assert.Equal(hash, b.State.ComputeHash());
    }

    [StockFact]
    public void MovementCostsUseOctileBudget()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_FOOTMAN", 10, posA: new Cell(2, 2), posB: new Cell(11, 9));
        var u = b.State.Units[0];   // speed 4 → budget 8 (exe: 2 × speed, steps 2 / 3)
        var reach = b.Reachable(u);
        Assert.True(reach.ContainsKey(new Cell(6, 2)));        // 4 orthogonal = 8
        Assert.False(reach.ContainsKey(new Cell(7, 2)));
        Assert.True(reach.ContainsKey(new Cell(4, 4)));        // 2 diagonal = 6
        Assert.False(reach.ContainsKey(new Cell(5, 5)));       // 3 diagonal = 9
        Assert.True(reach.ContainsKey(new Cell(5, 4)));        // 2 diag + 1 orth = 8
    }

    [Fact]
    public void LargeCreaturesOccupyFourCells()
    {
        var b = f.Duel("CREATURE_GRIFFIN", 5, "CREATURE_PEASANT", 10, posA: new Cell(3, 3), posB: new Cell(5, 3));
        var g = b.State.Units[0];
        Assert.Equal(2, g.Size);
        Assert.Equal(4, g.Cells().Count());
        Assert.True(Battle.AreAdjacent(g, b.State.Units[1]));
        Assert.Same(g, b.State.At(new Cell(4, 4)));
    }

    [Fact]
    public void LuckAndMoraleFromSkillsAndFactions()
    {
        var p = f.Db.DuelPresets[0];   // Haven knight, mixed army
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 5, skipTactics: true);
        var b = new Battle(f.Db, s);
        var hero = s.Heroes[0];
        Assert.True(hero.Luck >= (int)hero.SkillLevel(Sk.Luck));
        foreach (var u in s.LivingOf(0).Where(u => u.Kind == UnitKind.Creature))
            Assert.InRange(b.MoraleOf(u), -10, 15);
        // Undead have neutral morale.
        var undead = s.LivingOf(1).FirstOrDefault(u => u.Has(Ab.Undead));
        if (undead != null) Assert.Equal(0, b.MoraleOf(undead));
    }

    [Fact]
    public void LightningBoltDamageMatchesManual()
    {
        var s = BattleSetup.CreateDuel(f.Db, 15, 0, 7, skipTactics: true);
        var b = new Battle(f.Db, s);
        var sp = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        Assert.Equal(220, sp.DamageAt(Mastery.Expert).Eval(10));
        Assert.Equal(SpellTargetingExpectation, b.TargetingOf(sp));
    }

    const Battle.SpellTargeting SpellTargetingExpectation = Battle.SpellTargeting.Unit;
}
