using H5.Core.Data;

namespace H5.Combat.Tests;

[Collection("game")]
public class DataTests(GameDataFixture f)
{
    [Fact]
    public void AllTablesLoad()
    {
        var db = f.Db;
        Assert.True(db.Creatures.Count >= 170, $"creatures: {db.Creatures.Count}");
        Assert.True(db.Spells.Count >= 300, $"spells: {db.Spells.Count}");
        Assert.True(db.Abilities.Count >= 150);
        Assert.True(db.Skills.Count >= 200);
        Assert.True(db.Artifacts.Count >= 90);
        Assert.Equal(5, db.WarMachines.Count);
        Assert.Equal(24, db.DuelPresets.Count);
        Assert.True(db.Heroes.Count >= 100);
    }

    [Fact]
    public void EveryCreatureHasSaneStats()
    {
        foreach (var c in f.Db.Creatures.Values.Where(c => c.Tier > 0))
        {
            Assert.True(c.Health > 0, c.Id);
            Assert.True(c.MaxDamage >= c.MinDamage, c.Id);
            Assert.True(c.Initiative > 0, c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Name), c.Id);
            Assert.All(c.Abilities, a => Assert.True(f.Db.Abilities.ContainsKey(a), $"{c.Id}: unknown ability {a}"));
        }
    }

    [Fact]
    public void FootmanMatchesGameData()
    {
        var c = f.Db.Creature("CREATURE_FOOTMAN");
        Assert.Equal((4, 8, 2, 4, 16, 4, 8), (c.Attack, c.Defence, c.MinDamage, c.MaxDamage, c.Health, c.Speed, c.Initiative));
        Assert.Contains("ABILITY_BASH", c.Abilities);
        Assert.Equal("TOWN_HEAVEN", c.Town);
    }

    [Fact]
    public void SpellsHaveMasteryTables()
    {
        var bolt = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        Assert.Equal("Lightning Bolt", bolt.Name);
        Assert.Equal(4, bolt.Damage.Length);
        // Fan manual p.303: SP 10, no Destructive Magic → 121 damage; Expert → 220.
        Assert.Equal(121, bolt.DamageAt(Mastery.None).Eval(10));
        Assert.Equal(220, bolt.DamageAt(Mastery.Expert).Eval(10));
        foreach (var s in f.Db.Spells.Values.Where(s => s.School == "MAGIC_SCHOOL_DESTRUCTIVE"))
            Assert.True(s.Damage.Length is 0 or 4, s.Id);
    }

    [Fact]
    public void DuelPresetsResolveArmiesAndHeroes()
    {
        foreach (var p in f.Db.DuelPresets)
        {
            Assert.NotNull(p.Hero.Shared);
            Assert.Equal(15, p.Hero.Level);   // Experience 35000 → level 15 (dpcoeff.xdb HeroLevel 15)
            Assert.InRange(p.Hero.Army.Count, 1, 7);
            Assert.All(p.Hero.Army, s => Assert.True(f.Db.Creatures.ContainsKey(s.Creature), $"{p.Name}: {s.Creature}"));
            Assert.All(p.Hero.Spells, s => Assert.True(f.Db.Spells.ContainsKey(s), $"{p.Name}: {s}"));
            Assert.All(p.Hero.Artifacts, a => Assert.True(f.Db.Artifacts.ContainsKey(a), $"{p.Name}: {a}"));
            Assert.NotEmpty(p.Hero.Skills);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(999, 1)]
    [InlineData(1000, 2)]
    [InlineData(34140, 14)]
    [InlineData(34141, 15)]
    [InlineData(35000, 15)]
    public void ExperienceLevels(int exp, int level) => Assert.Equal(level, f.Db.Experience.LevelFor(exp));

    [Fact]
    public void CombatConstantsFromDefaultStats()
    {
        Assert.Equal(0.05, f.Db.Stats.Get("combat/DamageIncrease"));
        Assert.Equal(10, f.Db.Stats.Get("HeroInitiative"));
        Assert.Equal(0.3, f.Db.Stats.Get("combat/Creatures/DefenceBonusOnDefend"));
        Assert.Equal(8, f.Db.Stats.Array("combat/HeroSkills/Melee/HeroDamageLevel1").Length);
    }
}
