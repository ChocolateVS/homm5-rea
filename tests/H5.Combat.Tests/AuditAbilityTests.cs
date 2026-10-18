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
