using System.Reflection;
using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Activated creature abilities checked against the cast switch FUN_00984a00 of H5_Game.exe
/// (see docs/rules/audit/combat_abilities_c.md).
/// </summary>
[Collection("game")]
public class AuditAbilityTestsC(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    static ApplyResult Use(Battle b, Unit u, string ability, Unit? t = null, Cell? cell = null)
    {
        b.State.Active = u.Id;
        return b.Apply(new UseAbilityCmd(u.Id, ability) { Side = u.Side, TargetId = t?.Id, Cell = cell });
    }

    [Fact]
    public void ExplosionHasASingleCharge()   // exe 00987602: charges (SpellPoints1 = 1) decremented by the cast
    {
        var b = f.Duel("CREATURE_HORNED_DEMON", 10, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        var u = b.State.Units[0];
        var r = Use(b, u, Ab.Explosion);
        Assert.True(r.Ok, r.Error);
        Assert.DoesNotContain(b.UnitAbilities(u), a => a.Id == Ab.Explosion);
    }

    [Fact]
    public void HarmTouchDealsOneFullCreatureAndStripsEveryTimedBuff()   // exe 0098747f, FUN_00a61530, FUN_004e4120
    {
        var b = f.Duel("CREATURE_WRAITH", 5, "CREATURE_FOOTMAN", 20, 1, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        t.TopHealth = 3;   // a wounded top creature: the touch still takes one creature's FULL health
        t.Effects.Add(new Effect { Id = "SPELL_BLOODLUST", Positive = true, Remaining = 3, School = "MAGIC_SCHOOL_DARK" });
        t.Effects.Add(new Effect { Id = Fx.Invisible, Positive = true, Remaining = 3, Dispellable = false });
        t.Effects.Add(new Effect { Id = Fx.Dash, Positive = true, Actions = 3, Dispellable = false });
        t.Effects.Add(new Effect { Id = "SPELL_PERMANENT_TEST", Positive = true });
        int before = t.TotalHealth;
        var r = Use(b, u, Ab.HarmTouch, t);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(before - t.MaxHealthCache, t.TotalHealth);
        Assert.False(t.HasEffect("SPELL_BLOODLUST"));        // not Light magic, still removed
        Assert.False(t.HasEffect(Fx.Invisible));             // the "cannot be dispelled" flag does not protect
        Assert.True(t.HasEffect(Fx.Dash));                   // FUN_004e3ac0 set
        Assert.True(t.HasEffect("SPELL_PERMANENT_TEST"));    // permanent duration
    }

    [Fact]
    public void ManaFeedStopsAtTheHerosMaximum()   // exe 00987b0e: min(creature mana, hero max − hero mana)
    {
        var b = f.Duel("CREATURE_DRUID_ELDER", 10, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(9, 5));
        var h = b.State.Heroes[0];
        h.Present = true; h.Mana = 5; h.MaxMana = 12;
        b.Begin();
        var u = b.State.Units[0];
        u.Mana = 20;
        var r = Use(b, u, Ab.ManaFeed);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(12, h.Mana);
        Assert.Equal(13, u.Mana);
    }

    [StockFact]
    public void TreantUnionUsesCasterHitsForTheTreantsAndTargetHitsForTheDryads()   // exe 00986377 (+0x2cc / +0x2d0)
    {
        var b = f.Duel("CREATURE_DRYAD", 30, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(10, 5));
        var treants = Add(b, 0, "CREATURE_TREANT", 6, new Cell(2, 1));
        b.Begin();
        var u = b.State.Units[0];
        u.Count = 4; treants.Count = 4;   // room to heal / resurrect on both
        int casterHits = (int)b.Db.Stats.Get("combat/Creatures/Sylvan/TreeanUnionCasterHits", 4);
        int targetHits = (int)b.Db.Stats.Get("combat/Creatures/Sylvan/TreeanUnionTargetHits", 20);
        int tBefore = treants.TotalHealth, uBefore = u.TotalHealth;
        var r = Use(b, u, Ab.TreeantUnion, treants);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(tBefore + 4 * casterHits, treants.TotalHealth);
        Assert.Equal(uBefore + 4 * targetHits, u.TotalHealth);
    }

    [Fact]
    public void AmmoStealTakesFromOneAimedTarget()   // exe 009865a0: n = N / growth (+1 by remainder chance), one target
    {
        var b = f.Duel("CREATURE_POLTERGEIST", 1, "CREATURE_ARCHER", 10, 1, new Cell(5, 5), new Cell(7, 5));
        var other = Add(b, 1, "CREATURE_ARCHER", 10, new Cell(7, 3));
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        u.Count = 3 * Math.Max(1, u.WeeklyGrowth);   // no remainder: exactly three shots
        int shots = t.Shots, otherShots = other.Shots;
        var r = Use(b, u, Ab.AmmoSteal, t);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(shots - 3, t.Shots);
        Assert.Equal(otherShots, other.Shots);
    }

    [StockFact]
    public void LeapRetaliationAddsTheBonusToOne()   // exe FUN_00a58de0 special 0x140: ×(1 + Base + PerCell·cells)
    {
        int Retaliation(bool leap)
        {
            var b = f.Duel("CREATURE_HORNED_LEAPER", 1, "CREATURE_PEASANT", 300, 1, leap ? new Cell(1, 5) : new Cell(7, 5), new Cell(8, 5));
            b.Begin();
            var u = b.State.Units[0]; var t = b.State.Units[1];
            b.State.Active = u.Id;
            var r = leap ? b.Apply(new UseAbilityCmd(u.Id, Ab.Leap) { Side = 0, TargetId = t.Id })
                         : b.Apply(new AttackCmd(u.Id, t.Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            return r.Events.OfType<Damaged>().Where(d => d.Target == u.Id && d.Source == t.Id).Sum(d => d.Damage);
        }
        int normal = Retaliation(false), leaped = Retaliation(true);
        double k = 1 + f.Db.Stats.Get("combat/Creatures/Inferno/Leap_RetaliateDamageBonusBase", 2);
        Assert.True(normal > 0);
        // Peasants deal a fixed 1–1; one grunt kills at most a couple of the 300, so the ratio is the multiplier.
        Assert.InRange(leaped / (double)normal, k * 0.98, k * 1.01);
    }

    [Fact]
    public void SeduceDurationFollowsTheExeFormula()   // exe FUN_009774a0: clamp(log2(ratio) + 1.5, 0, 4), truncated to 0.001
    {
        Assert.Equal(1.5, Battle.SeduceDuration(10, 5, 10, 5), 6);
        Assert.Equal(2.5, Battle.SeduceDuration(20, 5, 10, 5), 6);
        Assert.Equal(4.0, Battle.SeduceDuration(80, 5, 10, 5), 6);
        Assert.Equal(0.0, Battle.SeduceDuration(10, 5, 40, 5), 6);
        Assert.Equal(0.084, Battle.SeduceDuration(3, 1, 8, 1), 6);   // log2(0.375) + 1.5 = 0.08496…, truncated
    }

    [Fact]
    public void RageOfTheForestReleasesTheRootedStacks()   // exe 00985a42: effect 0xac removed from the treants' victims
    {
        var b = f.Duel("CREATURE_ANGER_TREANT", 5, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        t.Effects.Add(new Effect { Id = Fx.Entangled, SourceUnit = u.Id, SourceSide = u.Side });
        var r = Use(b, u, Ab.RageOfForest);
        Assert.True(r.Ok, r.Error);
        Assert.False(t.HasEffect(Fx.Entangled));
    }

    [Fact]
    public void BalorSummoningNeedsADemonicCorpse()   // exe 00860660: HasAbility(ABILITY_DEMONIC) on the corpse
    {
        var b = f.Duel("CREATURE_ARCHDEVIL", 3, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(10, 5));
        var peasants = Add(b, 0, "CREATURE_PEASANT", 200, new Cell(2, 2));
        var imps = Add(b, 0, "CREATURE_IMP", 200, new Cell(2, 7));
        b.Begin();
        peasants.Count = 0; imps.Count = 0;
        var u = b.State.Units[0];
        var r = Use(b, u, Ab.BalorSummoning, peasants);
        Assert.False(r.Ok);
        r = Use(b, u, Ab.BalorSummoning, imps);
        Assert.True(r.Ok, r.Error);
        var balor = b.State.Units.Single(x => x.CreatureId == "CREATURE_BALOR");
        Assert.Equal(Math.Min(6, 200 * imps.BaseHealth / balor.BaseHealth), balor.Count);
    }

    [Fact]
    public void PackDiveHitsTheTwoRowBandAcrossTheField()   // exe FUN_0097a530 case 0x15b: rows y, y+1, every column
    {
        var b = f.Duel("CREATURE_BATTLE_GRIFFIN", 10, "CREATURE_PEASANT", 30, 1, new Cell(1, 1), new Cell(10, 5));
        var farInBand = Add(b, 1, "CREATURE_PEASANT", 30, new Cell(3, 6));
        var outOfBand = Add(b, 1, "CREATURE_PEASANT", 30, new Cell(10, 8));
        b.Begin();
        var u = b.State.Units[0];
        var field = new FieldObject { Id = 99, Kind = "PACK_DIVE", Side = 0, Cells = { new Cell(6, 5) }, SourceUnit = u.Id, Damage = u.Pos.X, Power = u.Pos.Y };
        int a = b.State.Units[1].TotalHealth, c = farInBand.TotalHealth, o = outOfBand.TotalHealth;
        typeof(Battle).GetMethod("ResolveDive", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, new object[] { field });
        Assert.True(b.State.Units[1].TotalHealth < a);
        Assert.True(farInBand.TotalHealth < c);
        Assert.Equal(o, outOfBand.TotalHealth);
    }

    [Fact]
    public void ScatterShotRollsLuckOnceForAllTargets()   // exe 00987788 → FUN_00a65d50: one attack, one luck roll
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var b = f.Duel("CREATURE_ARCHER", 20, "CREATURE_PEASANT", 30, seed, new Cell(1, 5), new Cell(8, 5));
            Add(b, 1, "CREATURE_PEASANT", 30, new Cell(9, 5));
            Add(b, 1, "CREATURE_PEASANT", 30, new Cell(8, 6));
            b.State.Heroes[0].Present = true; b.State.Heroes[0].Luck = 5;
            b.Begin();
            var u = b.State.Units[0];
            var r = Use(b, u, Ab.ScatterShot, cell: new Cell(8, 5));
            Assert.True(r.Ok, r.Error);
            Assert.True(r.Events.OfType<LuckProc>().Count(e => e.UnitId == u.Id) <= 1);
        }
    }

    // ------------------------------------------------------------------ part B (Academy, Dungeon, Fortress, Stronghold)

    [Fact]
    public void DashHasNoCharge()   // exe 00987e8a: no charge spent; the Rakshasas have SpellPoints1 = 0
    {
        var b = f.Duel("CREATURE_RAKSHASA_RUKH", 5, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(10, 5));
        b.Begin();
        var u = b.State.Units[0];
        var r = Use(b, u, Ab.Dash);
        Assert.True(r.Ok, r.Error);
        Assert.Contains(b.UnitAbilities(u), a => a.Id == Ab.Dash);
    }

    [Fact]
    public void CallStormHasASingleCharge()   // exe 00988279: charge −1 (Storm Lord SpellPoints1 = 1)
    {
        var b = f.Duel("CREATURE_STORM_LORD", 5, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(10, 5));
        b.Begin();
        var u = b.State.Units[0];
        var r = Use(b, u, Ab.CallStorm, cell: new Cell(10, 5));
        Assert.True(r.Ok, r.Error);
        Assert.DoesNotContain(b.UnitAbilities(u), a => a.Id == Ab.CallStorm);
    }

    [Fact]
    public void CallLightningGoesThroughMagicProtection()   // exe 00987ecb: FUN_00976b90 then FUN_0097d600
    {
        var b = f.Duel("CREATURE_TITAN", 4, "CREATURE_IRON_GOLEM", 50, 1, new Cell(2, 5), new Cell(10, 5));
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        int before = t.TotalHealth;
        var r = Use(b, u, Ab.CallLightning, t);
        Assert.True(r.Ok, r.Error);
        int dealt = before - t.TotalHealth;
        Assert.True(dealt > 0 && dealt < 30 * u.Count, $"dealt {dealt}");   // the golems' magic proof applies
    }

    [Fact]
    public void ScavengerUsesTheCreaturesCurrentHealthAndTheCoefficient()   // exe 0098624f: trunc(hp·count·ScavengerCoef)
    {
        var b = f.Duel("CREATURE_WYVERN_PAOKAI", 3, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(10, 5));
        var corpse = Add(b, 0, "CREATURE_PEASANT", 30, new Cell(6, 5));
        b.Begin();
        var u = b.State.Units[0];
        u.TopHealth = 1;
        corpse.Count = 0; corpse.MaxHealthCache = corpse.BaseHealth + 2;   // e.g. a hero health bonus
        int before = u.TotalHealth;
        var r = Use(b, u, Ab.Scavenger, corpse);
        Assert.True(r.Ok, r.Error);
        double coef = b.Db.Stats.Get("combat/Creatures/Stronghold/ScavengerCoef", 0.2);
        Assert.Equal(before + (int)((float)(corpse.MaxHealthCache * 30) * (float)coef), u.TotalHealth);
    }

    [Fact]
    public void SetSnaresPowerIsStackOverGrowthAtLeastOne()   // exe 0098614d: max(1, N / growth)
    {
        var b = f.Duel("CREATURE_GOBLIN_TRAPPER", 30, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(10, 5));
        b.Begin();
        var u = b.State.Units[0];
        var r = Use(b, u, Ab.SetSnares, cell: new Cell(6, 5));
        Assert.True(r.Ok, r.Error);
        var snare = b.State.Fields.Single(x => x.Kind == "SNARE");
        Assert.Equal(Math.Max(1, 30 / Math.Max(1, u.WeeklyGrowth)), (int)snare.Power);
    }

    [Fact]
    public void LuckGamblerReplacesAnOppositeEffect()   // exe 00985705: the new value is always applied
    {
        var b = f.Duel("CREATURE_DJINN_VIZIER", 5, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(10, 5));
        b.Begin();
        var u = b.State.Units[0]; var t = b.State.Units[1];
        t.Effects.Add(new Effect { Id = "FX_WHEEL_OF_FORTUNE", Value = 2, Positive = true });
        var r = Use(b, u, Ab.LuckGambler, t);
        Assert.True(r.Ok, r.Error);
        var e = t.GetEffect("FX_WHEEL_OF_FORTUNE");
        Assert.NotNull(e);
        Assert.InRange(e!.Value, -3, -1);
    }

    [Fact]
    public void OrderOfTheChiefCanPushTheAtbPastOne()   // exe 009867fb: min(ATB + bonus·0.01, 1.25)
    {
        var b = f.Duel("CREATURE_ORCCHIEF_CHIEFTAIN", 100, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(10, 5));
        var ally = Add(b, 0, "CREATURE_GOBLIN", 100, new Cell(2, 7));
        b.Begin();
        var u = b.State.Units[0];
        ally.Atb = 0.9;
        int before = ally.TotalHealth;
        var r = Use(b, u, Ab.OrderOfChief, ally);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1.25, ally.Atb, 6);
        Assert.Equal(before - 100, ally.TotalHealth);   // trunc(N · OrderOfTheChiefDamageCoef 1)
    }

    [Fact]
    public void SacrificeGoblinUsesAFullGoblinsHealth()   // exe 009869e0 / 00986b2e: trunc(vf 0x1a8 · ManaRestorePerHP)
    {
        var b = f.Duel("CREATURE_SHAMAN_WITCH", 10, "CREATURE_PEASANT", 50, 1, new Cell(2, 5), new Cell(10, 5));
        var gob = Add(b, 0, "CREATURE_GOBLIN", 20, new Cell(3, 5));
        b.Begin();
        var u = b.State.Units[0];
        gob.TopHealth = 1;   // a wounded top goblin still gives a full goblin's worth
        int mana = u.Mana;
        var r = Use(b, u, Ab.SacrificeGoblin, gob);
        Assert.True(r.Ok, r.Error);
        double k = b.Db.Stats.Get("combat/Creatures/Stronghold/SacrificeGoblin_ManaRestorePerHP", 2);
        Assert.Equal(mana + (int)((float)gob.MaxHealthCache * (float)k), u.Mana);
    }
}
