using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Who can learn a spell (hero spell interface vfunc 0xe0 = FUN_00b4b8c0).</summary>
[Collection("adv")]
public class SpellLearnTests(Fixture f)
{
    static HeroState Mage(Engine.Adventure g)
    {
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.Skills.Clear();
        h.Perks.Clear();
        h.Artifacts.Clear();
        h.Level = 30;
        return h;
    }

    [Fact]
    public void CirclesNeedSchoolMasteryPlusTwo()
    {
        var g = f.NewGame();
        var h = Mage(g);
        Assert.True(g.CanLearnSpell(h, "SPELL_MAGIC_ARROW"));          // circle 1
        Assert.False(g.CanLearnSpell(h, "SPELL_FIREBALL"));            // circle 3, no Destructive
        h.Skills.Add(new SkillEntry("HERO_SKILL_DESTRUCTIVE_MAGIC", Mastery.Basic));
        Assert.True(g.CanLearnSpell(h, "SPELL_FIREBALL"));
        Assert.False(g.CanLearnSpell(h, "SPELL_CHAIN_LIGHTNING"));     // circle 4 needs Advanced
        h.Skills[0] = new SkillEntry("HERO_SKILL_DESTRUCTIVE_MAGIC", Mastery.Expert);
        Assert.True(g.CanLearnSpell(h, "SPELL_ARMAGEDDON"));
    }

    [Fact]
    public void WisdomOpensTheThirdCircleOnly()
    {
        var g = f.NewGame();
        var h = Mage(g);
        h.Perks.Add("HERO_SKILL_WISDOM");
        Assert.True(g.CanLearnSpell(h, "SPELL_FIREBALL"));
        Assert.False(g.CanLearnSpell(h, "SPELL_CHAIN_LIGHTNING"));
    }

    [Fact]
    public void BloodRageBlocksMagicAndAllowsWarcries()
    {
        var g = f.NewGame();
        var h = Mage(g);
        h.Skills.Add(new SkillEntry("HERO_SKILL_DEMONIC_RAGE", Mastery.Basic));
        Assert.False(g.CanLearnSpell(h, "SPELL_MAGIC_ARROW"));
        var warcry = g.Core.Spells.Values.First(s => s.School == "MAGIC_SCHOOL_WARCRIES" && s.RequiredHeroLevel <= 30);
        Assert.True(g.CanLearnSpell(h, warcry.Id));
        h.Skills.Clear();
        Assert.False(g.CanLearnSpell(h, warcry.Id));
    }

    [Fact]
    public void AdventureSpellsNeedTheHeroLevel()
    {
        var g = f.NewGame();
        var h = Mage(g);
        int need = g.Core.Spell("SPELL_TOWN_PORTAL")!.RequiredHeroLevel;
        Assert.True(need > 1);
        h.Level = need - 1;
        Assert.False(g.CanLearnSpell(h, "SPELL_TOWN_PORTAL"));
        h.Level = need;
        Assert.True(g.CanLearnSpell(h, "SPELL_TOWN_PORTAL"));        // no school mastery needed
    }

    [Fact]
    public void MassSpellsNeedTheirMasterPerk()
    {
        var g = f.NewGame();
        var h = Mage(g);
        h.Skills.Add(new SkillEntry("HERO_SKILL_LIGHT_MAGIC", Mastery.Expert));
        if (g.Core.Spell("SPELL_MASS_BLESS") == null) return;
        Assert.False(g.CanLearnSpell(h, "SPELL_MASS_BLESS"));
        h.Perks.Add("HERO_SKILL_MASTER_OF_BLESSING");
        Assert.True(g.CanLearnSpell(h, "SPELL_MASS_BLESS"));
    }
}

/// <summary>Blacksmith prices (FUN_00cceae0 / FUN_00ccea20).</summary>
[Collection("adv")]
public class BlacksmithPriceTests(Fixture f)
{
    [Fact]
    public void NativeOrSpecialisedMachineAtCostOthersTriple()
    {
        var g = f.NewGame();
        var t = g.S.Towns.First(x => x.Owner == g.Current.Index);
        t.Type = "TOWN_HEAVEN";
        t.Specialization = null;
        string native = g.Db.NativeWarMachine["TOWN_HEAVEN"];
        foreach (var m in new[] { "WAR_MACHINE_BALLISTA", "WAR_MACHINE_FIRST_AID_TENT", "WAR_MACHINE_AMMO_CART" })
        {
            int gold = g.Core.WarMachines[m].Cost.Gold;
            Assert.Equal(m == native ? gold : 3 * gold, g.WarMachinePrice(t, m));
        }
        // TOWN_BONUS_BALLISTA makes the ballista native-priced.
        t.Specialization = "GameMechanics/TownSpecialization/Academy/Random/Ayer.xdb";
        Assert.Equal(g.Core.WarMachines["WAR_MACHINE_BALLISTA"].Cost.Gold, g.WarMachinePrice(t, "WAR_MACHINE_BALLISTA"));
    }
}
