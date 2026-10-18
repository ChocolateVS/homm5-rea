using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Creature abilities of Academy / Dungeon / Fortress / Stronghold / neutral creatures confirmed against H5_Game.exe
/// (see docs/rules/audit/combat_abilities_b.md).
/// </summary>
[Collection("game")]
public class AuditAbilityBTests(GameDataFixture f)
{
    /// <summary>Battle without heroes: one attacker against several defender stacks, all placed by hand.</summary>
    Battle Field(string attacker, int count, Cell at, params (string Id, int Count, Cell At)[] defenders)
    {
        var def = new ArmySetup();
        foreach (var d in defenders) def.Army.Add(new ArmySlot(d.Id, d.Count));
        var s = BattleSetup.Create(f.Db, new ArmySetup { Army = { new ArmySlot(attacker, count) } }, def, 1,
            obstacles: Array.Empty<Cell>(), skipTactics: true);
        s.Units[0].Pos = s.Units[0].StartPos = at;
        for (int i = 0; i < defenders.Length; i++) s.Units[1 + i].Pos = s.Units[1 + i].StartPos = defenders[i].At;
        return new Battle(f.Db, s);
    }

    [Fact]
    public void HarpoonPullsTowardsTheHarpoonerWithinFortyFiveDegrees()   // FUN_00a5d5f0 → FUN_00a5ceb0(1, π/4)
    {
        var b = f.Duel("CREATURE_HARPOONER", 20, "CREATURE_PEASANT", 100, 1, new Cell(1, 5), new Cell(8, 5));
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        Assert.Equal(new Cell(7, 5), b.HarpoonPullDestination(a, t));
        // Straight in and both 45° cells blocked: the 90° cells are outside π/4, so there is no pull.
        b.State.Obstacles.AddRange(new[] { new Cell(7, 5), new Cell(7, 4), new Cell(7, 6) });
        Assert.Null(b.HarpoonPullDestination(a, t));
    }

    [Fact]
    public void HarpoonNeverPullsCreaturesThatHoldTheirGround()   // FUN_00a5d5f0: ABILITY_HOLD_GROUND (0xa5) → no pull
    {
        var b = f.Duel("CREATURE_HARPOONER", 20, "CREATURE_STONE_DEFENDER", 30, 1, new Cell(1, 5), new Cell(8, 5));
        Assert.Null(b.HarpoonPullDestination(b.State.Units[0], b.State.Units[1]));
    }

    [Fact]
    public void BattleRageIsReappliedOnEveryDamageIncludingSpells()   // FUN_008a6e20 (creature take-damage virtual)
    {
        var b = f.Duel("CREATURE_PEASANT", 10, "CREATURE_BATTLE_RAGER", 40, 1, new Cell(1, 5), new Cell(8, 5));
        var rager = b.State.Units[1];
        b.AddEffect(rager, new Effect { Id = Fx.BattleRage, Value = 5, Remaining = 0.25, Positive = true, Dispellable = false });
        b.DealDamage(rager, 3, -1, AttackKind.Spell, "TEST");   // any damage, not only strikes
        Assert.Equal(Math.Round(f.Db.Stats.Get("combat/Creatures/Fortress/BattleRage_EffectDuration", 2)), rager.GetEffect(Fx.BattleRage)!.Remaining);
    }

    [Fact]
    public void AnyDamageEndsBlindAndSleepingStrike()   // FUN_008a6e20 removes SPELL_BLIND and SPELL_EFFECT_SLEEPING_STRIKE
    {
        var b = f.Duel("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 40, 1, new Cell(1, 5), new Cell(8, 5));
        var t = b.State.Units[1];
        b.AddEffect(t, new Effect { Id = "SPELL_BLIND", Remaining = 3 });
        b.AddEffect(t, new Effect { Id = Fx.Sleep, Remaining = 3 });
        b.DealDamage(t, 2, -1, AttackKind.Spell, "TEST");
        Assert.False(t.HasEffect("SPELL_BLIND"));
        Assert.False(t.HasEffect(Fx.Sleep));
    }

    /// <summary>Druid casting Lightning Bolt at the goblin defilers; returns the battle and the result.</summary>
    (Battle b, ApplyResult r) DruidCast(ulong seed, int? mana, bool defiled)
    {
        var def = new ArmySetup { Army = { new ArmySlot("CREATURE_GOBLIN_DEFILER", 100) } };
        var s = BattleSetup.Create(f.Db, new ArmySetup { Army = { new ArmySlot("CREATURE_DRUID", 20) } }, def, seed,
            obstacles: Array.Empty<Cell>(), skipTactics: true);
        s.Units[0].Pos = s.Units[0].StartPos = new Cell(1, 5);
        s.Units[1].Pos = s.Units[1].StartPos = new Cell(10, 5);
        var b = new Battle(f.Db, s);
        b.Begin();
        var (druid, gob) = (b.State.Units[0], b.State.Units[1]);
        if (mana is int m) druid.Mana = m;
        if (defiled) b.AddEffect(druid, new Effect { Id = "FX_DEFILED", SourceUnit = gob.Id, SourceSide = 1, Remaining = 10 });
        b.State.Active = druid.Id;
        return (b, b.Apply(new CastSpellCmd(druid.Id, "SPELL_LIGHTNING_BOLT", gob.Id) { Side = 0 }));
    }

    [Fact]
    public void DefileMagicExtraCostOnlyNeedsTheBaseCostAndClampsAtZero()   // FUN_008a11c0 (defile state 3)
    {
        var (b0, r0) = DruidCast(1, 1000, defiled: false);
        Assert.True(r0.Ok, r0.Error);
        int cost = 1000 - b0.State.Units[0].Mana;
        Assert.True(cost > 0);
        // With exactly cost + 1 mana, the "doubled cost" outcome still casts and leaves the druids at 0 mana.
        var hits = Enumerable.Range(1, 80).Select(s => DruidCast((ulong)s, cost + 1, defiled: true)).ToList();
        Assert.All(hits, x => Assert.True(x.b.State.Units[0].Mana >= 0));
        Assert.Contains(hits, x => x.b.State.Units[0].Mana == 0 && x.r.Events.OfType<SpellCast>().Any());
    }

    [Fact]
    public void PoisonAndVenomAreSeparateEffectsAndBothTick()   // FUN_00a61720 (0xb4) / FUN_00a61e60 (0x128), tick FUN_00bbb900
    {
        var b = f.Duel("CREATURE_ASSASSIN", 10, "CREATURE_PEASANT", 300, 1, new Cell(1, 5), new Cell(8, 5));
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        b.ApplyDamageOverTime(a, t, Fx.Poison, 1, 3, dispellable: true);
        b.ApplyDamageOverTime(a, t, Battle.FxVenom, 5, 3, dispellable: false);
        Assert.True(t.HasEffect(Fx.Poison));
        Assert.True(t.HasEffect(Battle.FxVenom));
        Assert.Equal(10, t.GetEffect(Fx.Poison)!.Value);
        Assert.Equal(50, t.GetEffect(Battle.FxVenom)!.Value);
    }

    [Fact]
    public void AStrongerPoisonIsNeverReplacedByAWeakerOne()   // FUN_00a61720: re-apply only if existing power <= new power
    {
        var b = f.Duel("CREATURE_ASSASSIN", 40, "CREATURE_PEASANT", 300, 1, new Cell(1, 5), new Cell(8, 5));
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        b.ApplyDamageOverTime(a, t, Fx.Poison, 1, 3, dispellable: true);   // power 40
        a.Count = 10;
        b.ApplyDamageOverTime(a, t, Fx.Poison, 1, 3, dispellable: true);   // weaker: ignored
        Assert.Equal(40, t.GetEffect(Fx.Poison)!.Value);
        a.Count = 60;
        b.ApplyDamageOverTime(a, t, Fx.Poison, 1, 3, dispellable: true);   // stronger: replaces
        Assert.Equal(60, t.GetEffect(Fx.Poison)!.Value);
    }

    [Fact]
    public void PhantomCopiesOfAPhoenixNeverRebirth()   // FUN_00a58bb0: no rebirth while under SPELL_PHANTOM (0x28)
    {
        var b = f.Duel("CREATURE_PEASANT", 10, "CREATURE_PHOENIX", 3, 1, new Cell(1, 5), new Cell(8, 5));
        var phoenix = b.State.Units[1];
        b.DealDamage(phoenix, phoenix.TotalHealth, -1, AttackKind.Spell, "TEST");
        Assert.True(phoenix.Alive);                      // the real stack rises again
        var b2 = f.Duel("CREATURE_PEASANT", 10, "CREATURE_PHOENIX", 3, 1, new Cell(1, 5), new Cell(8, 5));
        var copy = b2.State.Units[1];
        copy.IsClone = true;
        b2.DealDamage(copy, copy.TotalHealth, -1, AttackKind.Spell, "TEST");
        Assert.False(copy.Alive);
    }

    [Fact]
    public void StrikeAndReturnStaysWhenEntangled()   // FUN_00a65260 (00a65924): no return under SPELL_ABILITY_ENTANGLING_ROOTS
    {
        // Roots count only while their Treant is adjacent: the witch can leave (3,5) but is held once it reaches (5,5).
        Battle Setup(bool rooted)
        {
            var b = Field("CREATURE_BLOOD_WITCH", 3, new Cell(3, 5), ("CREATURE_PEASANT", 200, new Cell(6, 6)), ("CREATURE_TREANT", 30, new Cell(6, 4)));
            b.Begin();
            var w = b.State.Units[0];
            if (rooted) b.AddEffect(w, new Effect { Id = Fx.Entangled, SourceUnit = b.State.Units[2].Id, SourceSide = 1 });
            b.State.Active = w.Id;
            var r = b.Apply(new AttackCmd(w.Id, b.State.Units[1].Id, new Cell(5, 5)) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            return b;
        }
        Assert.Equal(new Cell(3, 5), Setup(rooted: false).State.Units[0].Pos);   // returns
        Assert.Equal(new Cell(5, 5), Setup(rooted: true).State.Units[0].Pos);    // held by the roots
    }

    [Fact]
    public void RideByAttackSkipsTheStartCellAndHitsWithoutRetaliation()   // FUN_00a5fa50 (from index 1), FUN_00a61c50
    {
        // Rider (2×2) starts at (2,0); bystander A is next to the start position only, bystander B is next to the route.
        var b = Field("CREATURE_BLACK_RIDER", 10, new Cell(2, 0),
            ("CREATURE_PEASANT", 50, new Cell(10, 0)),   // main target
            ("CREATURE_PEASANT", 50, new Cell(1, 0)),    // next to the start cell only
            ("CREATURE_PEASANT", 50, new Cell(5, 2)));   // next to the route (rider at (4,0) covers (4..5, 0..1))
        b.Begin();
        var u = b.State.Units[0];
        b.State.Active = u.Id;
        var (start, route) = (b.State.Units[2], b.State.Units[3]);
        var r = b.Apply(new AttackCmd(u.Id, b.State.Units[1].Id, new Cell(8, 0)) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var rideBy = r.Events.OfType<AbilityTriggered>().Where(e => e.Ability == Ab.RideByAttack).Select(e => e.Target).ToList();
        Assert.DoesNotContain(start.Id, rideBy);
        Assert.Contains(route.Id, rideBy);
        Assert.Equal(50 * start.MaxHealthCache, start.TotalHealth);
        Assert.True(route.TotalHealth < 50 * route.MaxHealthCache);
        // Dealt directly: no AttackStarted for the ride-by victims, so no luck and no retaliation from them.
        Assert.DoesNotContain(r.Events.OfType<AttackStarted>(), e => e.Target == route.Id || e.Attacker == route.Id);
    }
}
