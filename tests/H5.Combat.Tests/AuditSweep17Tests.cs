using H5.Core.Data;
namespace H5.Combat.Tests;

/// <summary>Sweep 17 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_17.md).</summary>
[Collection("game")]
public class AuditSweep17Tests(GameDataFixture f)
{
    Battle Make()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = [new ArmySlot("CREATURE_FOOTMAN", 10)] },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = [new ArmySlot("CREATURE_PEASANT", 10)] },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        return new Battle(f.Db, s);
    }

    SpellDef Sp(string id) => f.Db.Spell(id)!;

    // exe FUN_00a34740 (00a3474a..00a34768, 00a347b1): a mass (00a33e20) / empowered (00a33f70) version costs its BASE
    // spell's +0x90 × 2, not its own record's (stock: Mass Weakness 14 → 2 · 5, Empowered Deep Freeze 11 → 2 · 11).
    [Fact]
    public void AVersionCostsTwiceItsBaseSpell()
    {
        var b = Make();
        var h = b.State.Heroes[0];
        foreach (var (v, bas) in new[] { ("SPELL_MASS_WEAKNESS", "SPELL_WEAKNESS"), ("SPELL_MASS_HASTE", "SPELL_HASTE"),
                     ("SPELL_EMPOWERED_DEEP_FREEZE", "SPELL_DEEP_FREEZE"), ("SPELL_EMPOWERED_FIREBALL", "SPELL_FIREBALL") })
        {
            Assert.Equal(2 * Sp(bas).ManaCost, b.HeroBaseManaCost(h, Sp(v)));
            Assert.Equal(2 * Sp(bas).ManaCost, b.HeroManaCost(h, Sp(v)));
            Assert.Equal(2 * Sp(bas).ManaCost, b.ExeSpellBaseCost(null, Sp(v)));   // the creature path (009781e0, EDX = 0)
        }
    }

    // exe FUN_00a34740 (00a34785..00a3479b): Avatar of Death (0xc8) with a hero costs level · RPGStats +0x888
    // (combat/HeroSpecializations/Necromancer/AvatarOfDeath_ManaCostPerLevel) instead of its record's cost.
    [Fact]
    public void AvatarOfDeathCostsLevelTimesTheStat()
    {
        var b = Make();
        var h = b.State.Heroes[0];
        var aod = Sp("SPELL_ABILITY_AVATAR_OF_DEATH");
        h.Level = 7;
        int per = (int)f.Db.Stats.Get("combat/HeroSpecializations/Necromancer/AvatarOfDeath_ManaCostPerLevel", -1);
        Assert.True(per > 0);
        Assert.Equal(7 * per, b.HeroBaseManaCost(h, aod));
        Assert.Equal(aod.ManaCost, b.ExeSpellBaseCost(null, aod));
    }

    // exe FUN_00a34740 (00a34886 SETLE): a warcry whose War Leader reduction reaches 0 or below costs 0 (was at least 1).
    [Fact]
    public void AWarcryCostCanReachZero()
    {
        var b = Make();
        var h = b.State.Heroes[0];
        var cry = f.Db.Spells.Values.Where(s => s.School == "MAGIC_SCHOOL_WARCRIES" && s.ManaCost > 0).OrderBy(s => s.ManaCost).First();
        h.Specialization = "HERO_SPEC_WAR_LEADER";
        h.Level = 200;
        Assert.Equal(0, b.HeroBaseManaCost(h, cry));
        Assert.Equal(0, b.HeroManaCost(h, cry));
    }

    static bool NextRollBelow(Rng r, int limit) =>
        new Rng { S0 = r.S0, S1 = r.S1, S2 = r.S2, S3 = r.S3 }.Range(1, 100) < limit;

    // exe FUN_004d90d0 (vf 0x1c8 / vf 0x160 at the crossing) → FUN_00a6b8f0 / FUN_008a8140 → FUN_00a6cd80: bad morale is
    // rolled when the gauge crosses 1.0 in the tick and only sets ATB to BadMorale_ATBValue (vf 0x18c) — no turn starts
    // or ends (the retaliation is not reset, the stack has not acted).
    [Fact]
    public void BadMoraleStrikesAtTheCrossingWithoutATurn()
    {
        var b = Make();
        b.Begin();
        var a = b.State.Units.First(u => u.Side == 0);
        var d = b.State.Units.First(u => u.Side == 1);
        b.State.Heroes[0].Morale = -5;
        int m = b.MoraleOf(a);
        Assert.True(m < 0);
        for (ulong seed = 1; !NextRollBelow(b.State.Rng, 10 * Math.Min(5, -m)); seed++) b.State.Rng = new Rng(seed);
        a.Atb = 0.99f; d.Atb = 0; a.RetaliationsLeft = 0; a.HasActed = false;
        b.ClockTick((uint)Math.Ceiling(0.02 * b.FullTurnTime(a)));
        Assert.Equal((double)(float)f.Db.Stats.Get("combat/LuckAndMorale/BadMorale_ATBValue", 0.5), a.Atb);
        Assert.Equal(0, a.RetaliationsLeft);
        Assert.False(a.HasActed);
    }

    // exe FUN_004d90d0: the hooks run only on a crossing (ATB before < 1.0); a stack already at 1.0 does not roll, and
    // the turn start no longer rolls bad morale.
    [Fact]
    public void NoBadMoraleWithoutACrossing()
    {
        var b = Make();
        b.Begin();
        var a = b.State.Units.First(u => u.Side == 0);
        var d = b.State.Units.First(u => u.Side == 1);
        b.State.Heroes[0].Morale = -5;
        for (ulong seed = 1; !NextRollBelow(b.State.Rng, 10 * Math.Min(5, -b.MoraleOf(a))); seed++) b.State.Rng = new Rng(seed);
        a.Atb = 1.1f; d.Atb = 0;
        b.ClockTick(1);
        Assert.True(a.Atb >= 1);
    }

    // exe 004e57d0 (Eval ≤ 0 → 0) and FUN_00a72ae0 / FUN_004e3d50: a 0-length effect is created and stays in force until
    // the next clock tick of any length flags it expired (vf 0x28 = FUN_00a6d330 skips it); the engine made it 1 turn.
    [Fact]
    public void AZeroLengthEffectLastsUntilTheNextTick()
    {
        var b = Make();
        b.Begin();
        var a = b.State.Units.First(u => u.Side == 0);
        var sp = f.Db.Spells.Values.First(s => s.Id == "SPELL_BLESS");
        var m = Mastery.Basic;
        Assert.True(sp.DurationAt(m).Eval(0) <= 0, "Bless duration at power 0");
        var apply = typeof(Battle).GetMethod("ApplyEffectSpell", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.True((bool)apply.Invoke(b, new object?[] { Actor.Hero(0), 0, sp, m, 0.0, a, 1, b.State.Heroes[0] })!);
        Assert.True(a.HasEffect("SPELL_BLESS"));
        b.ClockTick(1);
        Assert.False(a.HasEffect("SPELL_BLESS"));
    }

    // exe FUN_00a6a260: the ATB tick (FUN_00a6a0b0) runs before the effect update (vf 0x18 → FUN_00a72ae0), so the tick
    // that ends a Slow still advances the gauge at the slowed rate.
    [Fact]
    public void TheTickUsesTheInitiativeBeforeTheEffectsExpire()
    {
        var b = Make();
        b.Begin();
        var a = b.State.Units.First(u => u.Side == 0);
        var d = b.State.Units.First(u => u.Side == 1);
        a.Atb = 0; d.Atb = 0;
        a.Effects.Add(new Effect { Id = "SPELL_SLOW", Value = 0.5, Remaining = 1e-6, Power = 1, Mastery = Mastery.Basic });
        double slowed = b.FullTurnTime(a);
        a.Effects.Clear();
        double normal = b.FullTurnTime(a);
        Assert.NotEqual(normal, slowed);
        a.Effects.Add(new Effect { Id = "SPELL_SLOW", Value = 0.5, Remaining = 1e-6, Power = 1, Mastery = Mastery.Basic });
        b.ClockTick(100);
        Assert.False(a.HasEffect("SPELL_SLOW"));
        Assert.Equal((double)(float)(100f / slowed), a.Atb);
    }
}
