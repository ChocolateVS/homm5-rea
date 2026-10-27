using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Battle-end experience per H5_Game.exe (docs/rules/audit/combat_heroes_skills_h.md).</summary>
[Collection("adv")]
public class AuditBattleExpTests(Fixture f)
{
    // exe FUN_00b4bbb0 (CHero vcall 0x19c): exp + (5·Enlightenment + 10·Turban + 20·Chain Mail [+ Educational set])·exp/100
    // in integers — the engine's double multiplier lost a point (100 · 1.15 = 114.999… → 114).
    [Fact]
    public void ExperienceModifierIsIntegerPercent()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Owner == g.Current.Index);
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_LEARNING");
        h.Skills.Add(new SkillEntry("HERO_SKILL_LEARNING", Mastery.Basic));
        h.Artifacts.Add("HELM_OF_ENLIGHTMENT");
        Assert.Equal(115, g.ExperienceModifiers(h, 100));
    }

    // exe FUN_00af3430: +500 for a lost enemy hero first, Talented Warrior ×(0.02·level + 1), then the hero modifier,
    // and the week (Folly / 2, Honor × 2) on the final amount — the engine halved before the +500 and before Enlightenment.
    [Fact]
    public void BattleExperienceAppliesTheWeekLast()
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Owner == g.Current.Index);
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_LEARNING");
        h.Artifacts.Clear();
        h.Specialization = "";
        g.S.MoonWeek = "WEEK_OF_FOLLY";
        Assert.Equal((1001 + 500) / 2, g.BattleExperience(h, 1001, opponentHeroLost: true, 1));
        g.S.MoonWeek = "WEEK_OF_HONOR";
        Assert.Equal(1001 * 2, g.BattleExperience(h, 1001, opponentHeroLost: false, 1));
        g.S.MoonWeek = "WEEK_OF_NOTHING";
        h.Specialization = "HERO_SPEC_TALENTED_WARRIOR"; h.Level = 10;
        Assert.Equal((long)((10 * 0.02 + 1.0) * 1500), g.BattleExperience(h, 1000, opponentHeroLost: true, 1));
    }

    // exe FUN_00bbf610: the hero's cast subtracts from the CHero's own mana field — the combat hero is the adventure
    // hero, so a battle starts with the hero's current spell points and the spent points stay spent. The engine
    // refilled the hero for every battle and never took the spent mana off.
    [Fact]
    public void HeroManaCarriesIntoAndOutOfBattle()
    {
        var g = f.NewGame();
        var town = g.S.Towns.First(t => t.Owner >= 0);
        var attacker = g.S.Heroes.First(h => h.Alive && h.Owner != town.Owner);
        Array.Fill(town.Garrison, null);
        town.Garrison[0] = new State.Stack("CREATURE_PEASANT", 1);
        g.S.Battle = new State.PendingBattle
        {
            AttackerHero = attacker.Id, DefenderObject = town.Object, Where = town.Pos,
            DefenderArmy = new() { new State.Stack("CREATURE_PEASANT", 1) }, Siege = true,
        };
        attacker.Mana = 3;
        var state = g.CreatePendingBattle(5);
        Assert.Equal(3, state.Heroes[0].Mana);
        var r = g.Apply(new Engine.ResolveBattleCmd(0, attacker.Army.Live().Select(st => new ArmySlot(st.Creature, st.Count)).ToList(), new()) { Player = g.Current.Index, HeroMana = new[] { 1, 0 } });
        Assert.Equal(1, attacker.Mana);
    }

    // exe FUN_00419f00 / FUN_00419ad0: runes are paid from the owner's adventure resources — the battle starts with the
    // player's purse and the resources spent on runes (H5.Combat HeroState.ResourcesSpent) are taken off afterwards.
    [Fact]
    public void RuneResourcesCarryIntoAndOutOfBattle()
    {
        var g = f.NewGame();
        var town = g.S.Towns.First(t => t.Owner >= 0);
        var attacker = g.S.Heroes.First(h => h.Alive && h.Owner != town.Owner);
        Array.Fill(town.Garrison, null);
        town.Garrison[0] = new State.Stack("CREATURE_PEASANT", 1);
        g.S.Battle = new State.PendingBattle
        {
            AttackerHero = attacker.Id, DefenderObject = town.Object, Where = town.Pos,
            DefenderArmy = new() { new State.Stack("CREATURE_PEASANT", 1) }, Siege = true,
        };
        var purse = g.S.Players[attacker.Owner].Purse;
        purse[Purse.Wood] = 7; purse[Purse.Gem] = 2;
        var state = g.CreatePendingBattle(5);
        Assert.Equal(7, state.Heroes[0].Resources![Purse.Wood]);
        var spent = new int[14];
        spent[Purse.Wood] = 3; spent[Purse.Gem] = 1;
        g.Apply(new Engine.ResolveBattleCmd(0, attacker.Army.Live().Select(st => new ArmySlot(st.Creature, st.Count)).ToList(), new())
            { Player = g.Current.Index, ResourcesSpent = spent });
        Assert.Equal(4, purse[Purse.Wood]);
        Assert.Equal(1, purse[Purse.Gem]);
    }
}
