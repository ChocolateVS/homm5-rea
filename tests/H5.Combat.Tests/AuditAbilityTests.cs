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
    public void FearNeverTriggersOnRetaliation()   // FUN_00a629b0: Fear requires !retaliation (+0x21) and a pending retaliation
    {
        foreach (var (b, r) in Attacks("CREATURE_PEASANT", 30, "CREATURE_NIGHTMARE", 40, L, R))
            Assert.DoesNotContain(r.Events.OfType<AbilityTriggered>(), e => e.Ability == Ab.Fear);
        Assert.Contains(Attacks("CREATURE_NIGHTMARE", 20, "CREATURE_PEASANT", 300, L, R),
            x => x.r.Events.OfType<AbilityTriggered>().Any(e => e.Ability == Ab.Fear));
    }
}
