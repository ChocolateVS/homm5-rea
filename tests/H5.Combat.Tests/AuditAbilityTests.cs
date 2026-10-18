using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// On-hit creature abilities confirmed against H5_Game.exe's hit-reaction function FUN_00a629b0 and its helpers
/// (flag at +0x14 = ranged strike, +0x21 = retaliation strike; see docs/rules/audit/combat_abilities.md).
/// </summary>
[Collection("game")]
public class AuditAbilityTests(GameDataFixture f)
{
    /// <summary>Runs one attack per seed and returns the battles after the attack.</summary>
    IEnumerable<(Battle b, ApplyResult r)> Attacks(string a, int countA, string t, int countB, Cell posA, Cell posB, int seeds = 40,
        Func<Battle, BattleCommand>? cmd = null)
    {
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var b = f.Duel(a, countA, t, countB, seed, posA, posB);
            b.Begin();
            var u = b.State.Units[0];
            b.State.Active = u.Id;
            var r = b.Apply(cmd?.Invoke(b) ?? new AttackCmd(u.Id, b.State.Units[1].Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            yield return (b, r);
        }
    }

    static readonly Cell L = new(5, 5), R = new(6, 5);

    [Fact]
    public void WoundNeverAffectsElementals()   // FUN_00a629b0: target !MECHANICAL && !ELEMENTAL before Wound_ProcFactor
    {
        foreach (var (b, _) in Attacks("CREATURE_AXE_FIGHTER", 30, "CREATURE_WATER_ELEMENTAL", 8, L, R))
            Assert.False(b.State.Units[1].HasEffect(Fx.Wound));
        Assert.Contains(Attacks("CREATURE_AXE_FIGHTER", 30, "CREATURE_PEASANT", 300, L, R), x => x.b.State.Units[1].HasEffect(Fx.Wound));
    }

    [Fact]
    public void MarkOfFireOnlyOnRangedStrikes()   // FUN_00a629b0: requires the ranged flag (+0x14)
    {
        foreach (var (b, _) in Attacks("CREATURE_FLAME_MAGE", 30, "CREATURE_PEASANT", 300, L, R))
            Assert.False(b.State.Units[1].HasEffect(Fx.MarkOfFire));
        Assert.Contains(Attacks("CREATURE_FLAME_MAGE", 30, "CREATURE_PEASANT", 300, new Cell(1, 5), new Cell(10, 5),
            cmd: b => new ShootCmd(b.State.Units[0].Id, b.State.Units[1].Id) { Side = 0 }), x => x.b.State.Units[1].HasEffect(Fx.MarkOfFire));
    }

    [Fact]
    public void SleepingStrikeImmunityIsBlindMechanicalElementalNotUndead()   // FUN_00a5e4f0
    {
        foreach (var (b, _) in Attacks("CREATURE_NOSFERATU", 20, "CREATURE_STONE_GARGOYLE", 50, L, R))
            Assert.False(b.State.Units[1].HasEffect(Fx.Sleep));
        Assert.Contains(Attacks("CREATURE_NOSFERATU", 20, "CREATURE_SKELETON", 60, L, R), x => x.b.State.Units[1].HasEffect(Fx.Sleep));
    }

    [Fact]
    public void WhipStrikeCastsSlowWeaknessOrBerserk()   // FUN_00a5e2c0: Rng(100) <40 Slow, <75 Weakness, else Berserk
    {
        var applied = Attacks("CREATURE_MATRIARCH", 10, "CREATURE_PEASANT", 300, L, R, seeds: 80)
            .SelectMany(x => x.b.State.Units[1].Effects.Select(e => e.Id)).ToHashSet();
        Assert.DoesNotContain("SPELL_CURSE", applied);
        Assert.Contains("SPELL_WEAKNESS", applied);
    }

    [Fact]
    public void CrushingBlowCanStunTheTarget()   // FUN_00a5af70: CrushingBlow_ProcFactor, then like Bash (ATB 0, no retaliation)
    {
        Assert.Contains(Attacks("CREATURE_CYCLOP_UNTAMED", 10, "CREATURE_PEASANT", 400, L, R),
            x => x.r.Events.OfType<AbilityTriggered>().Any(e => e.Ability == Ab.CrushingBlow)
                 && !x.r.Events.OfType<AttackStarted>().Any(e => e.Kind == AttackKind.Retaliation));
    }

    [Fact]
    public void ForceArrowCanPushAndStun()   // FUN_00a5d420: PiercingArrow_ProcFactor on a shot; not HOLD_GROUND targets
    {
        Assert.Contains(Attacks("CREATURE_SHARP_SHOOTER", 30, "CREATURE_PEASANT", 400, new Cell(1, 5), new Cell(8, 5),
            cmd: b => new ShootCmd(b.State.Units[0].Id, b.State.Units[1].Id) { Side = 0 }),
            x => x.r.Events.OfType<AbilityTriggered>().Any(e => e.Ability == Ab.PiercingArrow) && x.b.State.Units[1].Pos == new Cell(9, 5));
    }

    [Fact]
    public void ForceArrowPushPicksClosestFreeCellWithinSixtyDegrees()   // FUN_00a5ceb0 with π/3 (called from FUN_00a5d420)
    {
        var b = f.Duel("CREATURE_SHARP_SHOOTER", 30, "CREATURE_PEASANT", 400, 1, new Cell(1, 5), new Cell(8, 5));
        b.State.Obstacles.Clear();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        Assert.Equal(new Cell(9, 5), b.PushDestination(a, t, Battle.ForceArrowPushTolerance));
        // Straight back blocked: the two 45° cells tie and the exe's ring order (FUN_00b3d010) lists (+1,+1) first.
        b.State.Obstacles.Add(new Cell(9, 5));
        Assert.Equal(new Cell(9, 6), b.PushDestination(a, t, Battle.ForceArrowPushTolerance));
        // Everything within ±60° of "away" blocked: no push, so the ability does not trigger.
        b.State.Obstacles.Add(new Cell(9, 6));
        b.State.Obstacles.Add(new Cell(9, 4));
        Assert.Null(b.PushDestination(a, t, Battle.ForceArrowPushTolerance));
        // Paw Strike accepts any direction (2π): the closest remaining free cell is straight up/down (90°).
        Assert.Equal(new Cell(8, 6), b.PushDestination(a, t, Battle.PawStrikePushTolerance));
    }

    [Fact]
    public void CowardiceRunsInsideTheFirstStrike()   // FUN_00a629b0 → FUN_00a5d790(0): runs instead of retaliating
    {
        var b = f.Duel("CREATURE_MINOTAUR_KING", 1, "CREATURE_GOBLIN", 200, 1, L, R);
        b.State.Obstacles.Clear();
        b.Begin();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        b.State.Active = a.Id;
        var r = b.Apply(new AttackCmd(a.Id, t.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events.OfType<AbilityTriggered>(), e => e.Ability == Ab.Cowardice);
        // The goblins ran during the first blow, so the double attack's second strike never landed and nobody retaliated.
        Assert.Single(r.Events.OfType<AttackStarted>(), e => e.Attacker == a.Id);
        Assert.DoesNotContain(r.Events.OfType<AttackStarted>(), e => e.Kind == AttackKind.Retaliation);
    }

    [Fact]
    public void CowardiceNeedsAPendingRetaliation()   // FUN_00a629b0: only while *param_2 (target would retaliate)
    {
        var b = f.Duel("CREATURE_MINOTAUR_KING", 1, "CREATURE_GOBLIN", 200, 1, L, R);
        b.State.Obstacles.Clear();
        b.Begin();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        t.RetaliationsLeft = 0;
        b.State.Active = a.Id;
        var r = b.Apply(new AttackCmd(a.Id, t.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.DoesNotContain(r.Events.OfType<AbilityTriggered>(), e => e.Ability == Ab.Cowardice);
        Assert.Equal(R, t.Pos);
    }

    [Fact]
    public void BearRoarHalvesTheFailureOfSmallVictims()   // FUN_00a5e800: p = trunc(100 − (100 − p)/SmallCreaturesChanceMult)
    {
        var b = f.Duel("CREATURE_BLACKBEAR_RIDER", 10, "CREATURE_PEASANT", 100, 1, L, R);
        var (a, small) = (b.State.Units[0], b.State.Units[1]);
        int p = b.ProcPercent(a, small, 0.9, soldiersLuckDoubles: true);
        Assert.Equal((int)(100f - (100 - p) / 2f), b.BearRoarPercent(a, small));
        var big = f.Duel("CREATURE_BLACKBEAR_RIDER", 10, "CREATURE_GOLD_DRAGON", 2, 1, L, R);
        var (a2, dragon) = (big.State.Units[0], big.State.Units[1]);
        Assert.Equal(big.ProcPercent(a2, dragon, 0.9, soldiersLuckDoubles: true), big.BearRoarPercent(a2, dragon));
    }

    [Fact]
    public void PurgerNeverRemovesPrayer()   // FUN_004e4000: SPELL_PRAYER (0x36), DEATH_SCREAM (0x3b), SEDUCE_CASTED excluded
    {
        bool removedPrayer = false, removedBless = false;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var b = f.Duel("CREATURE_ZEALOT", 20, "CREATURE_PEASANT", 400, seed, L, R);
            b.Begin();
            var (a, t) = (b.State.Units[0], b.State.Units[1]);
            t.Effects.Add(new Effect { Id = Fx.Benediction, Value = 2, Remaining = 5, Positive = true });
            t.Effects.Add(new Effect { Id = "SPELL_BLESS", Remaining = 5, Positive = true });
            b.State.Active = a.Id;
            Assert.True(b.Apply(new AttackCmd(a.Id, t.Id) { Side = 0 }).Ok);
            removedPrayer |= !t.HasEffect(Fx.Benediction);
            removedBless |= !t.HasEffect("SPELL_BLESS");
        }
        Assert.False(removedPrayer);
        Assert.True(removedBless);
    }

    [Fact]
    public void PushOfEntangledTargetSucceedsWithoutMoving()   // FUN_00a5ceb0: SPELL_ABILITY_ENTANGLING_ROOTS → current position
    {
        var b = f.Duel("CREATURE_SHARP_SHOOTER", 30, "CREATURE_TREANT", 5, 1, new Cell(1, 5), new Cell(7, 5));
        var root = b.State.Units[1];
        var victim = b.State.Units[0];
        b.State.Obstacles.Clear();
        // Entangle the shooter (adjacent to the treant) and push it away from the treant.
        victim.Pos = new Cell(6, 5);
        victim.Effects.Add(new Effect { Id = Fx.Entangled, SourceUnit = root.Id, SourceSide = root.Side });
        Assert.Equal(victim.Pos, b.PushDestination(root, victim, Battle.ForceArrowPushTolerance));
    }

    [Theory]
    [InlineData(Mastery.Basic, true)]
    [InlineData(Mastery.Advanced, true)]
    [InlineData(Mastery.Expert, false)]
    public void OnlyExpertBlindStopsRetaliation(Mastery m, bool retaliates)   // FUN_00a5fee0: blind level > 2
    {
        var b = f.Duel("CREATURE_PEASANT", 20, "CREATURE_PEASANT", 60, 1, L, R);
        b.Begin();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        t.Effects.Add(new Effect { Id = "SPELL_BLIND", Mastery = m, Remaining = 3 });
        b.State.Active = a.Id;
        var r = b.Apply(new AttackCmd(a.Id, t.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(retaliates, r.Events.OfType<AttackStarted>().Any(e => e.Attacker == t.Id && e.Kind == AttackKind.Retaliation));
    }

    [Fact]
    public void VorpalSwordDoesNotWorkOnCrystalScales()   // 00a673c0: +1 kill unless the target has CRYSTAL_SCALES
    {
        int Killed(bool scales)
        {
            var b = f.Duel("CREATURE_BALOR", 3, "CREATURE_PEASANT", 200, 5, L, R);
            b.Begin();
            var (a, t) = (b.State.Units[0], b.State.Units[1]);
            if (scales) { t.Abilities.Add("ABILITY_CRYSTAL_SCALES"); t.Abilities.Sort(StringComparer.Ordinal); }
            b.State.Active = a.Id;
            Assert.True(b.Apply(new AttackCmd(a.Id, t.Id) { Side = 0 }).Ok);
            return 200 - t.Count;
        }
        Assert.Equal(Killed(false) - 1, Killed(true));
    }

    [Fact]
    public void FireShieldIgnoresOverkill()   // 00a5bcf0: damage taken capped at the holder's HP before the blow
    {
        var b = f.Duel("CREATURE_PEASANT", 3000, "CREATURE_FIRE_ELEMENTAL", 1, 1, L, R);
        b.Begin();
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        int hpBefore = t.TotalHealth, attackerBefore = a.TotalHealth;
        b.State.Active = a.Id;
        Assert.True(b.Apply(new AttackCmd(a.Id, t.Id) { Side = 0 }).Ok);
        Assert.False(t.Alive);
        int burnt = attackerBefore - a.TotalHealth;
        Assert.InRange(burnt, 1, (int)(hpBefore * f.Db.Stats.Get("combat/Creatures/Abilities/FireShield_Damage_Multiplier", 0.2)));
    }

    [Fact]
    public void FearNeverTriggersOnRetaliation()   // FUN_00a629b0: Fear requires !retaliation (+0x21) and a pending retaliation
    {
        foreach (var (b, r) in Attacks("CREATURE_PEASANT", 30, "CREATURE_NIGHTMARE", 40, L, R))
            Assert.DoesNotContain(r.Events.OfType<AbilityTriggered>(), e => e.Ability == Ab.Fear);
        Assert.Contains(Attacks("CREATURE_NIGHTMARE", 20, "CREATURE_PEASANT", 300, L, R),
            x => x.r.Events.OfType<AbilityTriggered>().Any(e => e.Ability == Ab.Fear));
    }
}

/// <summary>The ability trigger chance and roll, exe FUN_00a59990 / FUN_00a5d1b0 / FUN_00a5a170.</summary>
[Collection("game")]
public class AuditProcChanceTests(GameDataFixture f)
{
    Battle Peasants(int a, int t) { var b = f.Duel("CREATURE_PEASANT", a, "CREATURE_PEASANT", t); b.Begin(); return b; }

    [Fact]
    public void ChanceUsesIntegerRatioAndFactorAsExponent()
    {
        var b = Peasants(650, 100);                  // 3A/T = 19.5 → 19: base 44 (the manual's float formula says 44.5)
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        Assert.Equal(44, b.ProcPercent(a, t, 1));
        var even = Peasants(100, 100);               // A = T: base 25 − 3 = 22
        Assert.Equal(22, even.ProcPercent(even.State.Units[0], even.State.Units[1], 1));
        var w = Peasants(100, 300);                  // A < T: 25 − ⌊3·3⌋ = 16
        Assert.Equal(16, w.ProcPercent(w.State.Units[0], w.State.Units[1], 1));
        // factor is an exponent on the failure chance: 100 − ⌊100·0.84^2 + 0.5⌋ = 100 − 71 = 29 (a multiplier would give 32)
        Assert.Equal(29, w.ProcPercent(w.State.Units[0], w.State.Units[1], 2));
        Assert.Equal(75, Peasants(10000, 10).ProcPercent(Peasants(10000, 10).State.Units[0], Peasants(10000, 10).State.Units[1], 1));
        Assert.Equal(5, Peasants(10, 10000).ProcPercent(Peasants(10, 10000).State.Units[0], Peasants(10, 10000).State.Units[1], 1));
    }

    [Fact]
    public void PawStrikeRollsOncePerTileWalked()   // fail = Π (100 − p)·0.01 in floats; round-to-nearest((1 − fail)·100 + 0.5)
    {
        var b = Peasants(100, 300);
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        Assert.Equal(0, b.PawStrikePercent(a, t, 0));
        int p = b.ProcPercent(a, t, f.Db.Stats.Get("combat/Creatures/Fortress/PawStrike_ProcFactor", 1), soldiersLuckDoubles: true);
        float fail = 1f;
        for (int i = 0; i < 3; i++) fail = (100f - p) * fail * 0.01f;
        Assert.Equal((int)Math.Round((double)((1f - fail) * 100f + 0.5f), MidpointRounding.ToEven), b.PawStrikePercent(a, t, 3));
        Assert.True(b.PawStrikePercent(a, t, 3) > b.PawStrikePercent(a, t, 1));
    }
}
