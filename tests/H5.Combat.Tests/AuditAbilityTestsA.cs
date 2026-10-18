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
}
