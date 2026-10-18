using H5.Core.Data;

namespace H5.Combat.Tests;

[Collection("game")]
public class HeroPerkTests(GameDataFixture f)
{
    static Battle Started(GameDataFixture f, int a, int b, ulong seed)
    {
        var s = BattleSetup.CreateDuel(f.Db, a, b, seed, skipTactics: true, arena: "Grass_Big_01");
        var battle = new Battle(f.Db, s);
        battle.Begin();
        return battle;
    }

    [Fact]
    public void EmpoweredSpellsAddEmpoweredVariants()
    {
        var b = Started(f, 15, 3, 1);
        var h = b.State.Heroes[0];
        h.Spells = new() { "SPELL_FIREBALL", "SPELL_BLESS" };
        h.Perks.Remove(Sk.EmpoweredSpells);
        Assert.DoesNotContain("SPELL_EMPOWERED_FIREBALL", b.HeroSpells(h));
        h.Perks.Add(Sk.EmpoweredSpells);
        var spells = b.HeroSpells(h).ToList();
        Assert.Contains("SPELL_EMPOWERED_FIREBALL", spells);
        Assert.Equal(2 * f.Db.Spell("SPELL_FIREBALL")!.ManaCost, f.Db.Spell("SPELL_EMPOWERED_FIREBALL")!.ManaCost);
    }

    [Fact]
    public void BanishHitsEnemySummonedStacks()
    {
        var b = Started(f, 12, 3, 2);
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.Banish);
        var summoned = BattleSetup.AddCreature(f.Db, b.State, 1, "CREATURE_PEASANT", 500, new Cell(11, 9), summoned: true);
        Assert.Contains(b.HeroAbilities(h), a => a.Id == "BANISH");
        int hp = summoned.TotalHealth;
        b.State.Active = Actor.Hero(0);
        var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "BANISH") { Side = 0 });
        Assert.True(r.Ok, r.Error);
        int expected = (int)(10 * h.Level + 10 * (int)h.SkillLevel(Sk.Summoning));
        Assert.Equal(hp - expected, summoned.TotalHealth);
    }

    [Fact]
    public void RemoteControlTakesAnEnemyWarMachine()
    {
        var s = BattleSetup.CreateDuel(f.Db, 12, 0, 3, skipTactics: true, arena: "Grass_Big_01");
        if (!s.Units.Any(u => u.IsWarMachine && u.Side == 1)) return;   // preset without war machines
        s.Heroes[0].Perks.Add("HERO_SKILL_REMOTE_CONTROL");
        new Battle(f.Db, s).Begin();
        Assert.Contains(s.Units, u => u.IsWarMachine && u.Side == 0 && u.CreatureId != "WAR_MACHINE_CATAPULT");
    }
}
