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
}
