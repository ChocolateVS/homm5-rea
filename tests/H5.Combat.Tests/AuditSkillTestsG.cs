using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Magic-side hero skill rules confirmed against H5_Game.exe (docs/rules/audit/combat_heroes_skills_g.md).</summary>
[Collection("game")]
public class AuditSkillTestsG(GameDataFixture f)
{
    (Battle b, BattleState s) Setup((string id, int count)[] mine, (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Perks.Clear(); h.Skills.Clear(); h.Artifacts.Clear(); }
        return (new Battle(f.Db, s), s);
    }

    static Unit Of(BattleState s, string id) => s.Units.First(u => u.CreatureId == id);
    SpellDef Spell(string id) => f.Db.Spell(id)!;

    // exe FUN_0097af50 @0097afc0: Boneward (HERO_SKILL_SPELLPROOF_BONES) ×0.8 for any Destructive spell (school +0x88 == 0)
    // on every stack of the hero — no undead condition (the engine required an undead target).
    [Fact]
    public void BonewardProtectsLivingTroopsFromDestructiveSpellsOnly()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        var imp = Of(s, "CREATURE_IMP");
        Assert.Equal(1.0, b.SpellDamageMultiplier(imp, Spell("SPELL_FIREBALL"), 0), 6);
        s.Heroes[1].Perks.Add("HERO_SKILL_SPELLPROOF_BONES");
        Assert.Equal(0.8, b.SpellDamageMultiplier(imp, Spell("SPELL_FIREBALL"), 0), 6);
        Assert.Equal(1.0, b.SpellDamageMultiplier(imp, Spell("SPELL_HOLY_WORD"), 0), 6);   // Light school: unaffected
    }

    // exe FUN_0097af50 @0097b009: Sap Magic (HERO_SKILL_MAGIC_CUSHION) ×0.8 only for Destructive spells whose caster is a
    // hero (caster vfunc 0x78 non-null) — creature-cast spells and other schools are unaffected.
    [Fact]
    public void SapMagicOnlyReducesDestructiveSpellsOfHeroes()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        var imp = Of(s, "CREATURE_IMP");
        s.Heroes[1].Perks.Add("HERO_SKILL_MAGIC_CUSHION");
        Assert.Equal(0.8, b.SpellDamageMultiplier(imp, Spell("SPELL_LIGHTNING_BOLT"), 0, casterIsHero: true), 6);
        Assert.Equal(1.0, b.SpellDamageMultiplier(imp, Spell("SPELL_LIGHTNING_BOLT"), 0, casterIsHero: false), 6);
        Assert.Equal(1.0, b.SpellDamageMultiplier(imp, Spell("SPELL_HOLY_WORD"), 0, casterIsHero: true), 6);
    }

    // exe FUN_0097af50 @0097b051..0097b0ae: Luck of the Barbarian multiplies by clamp(1 − 1·MagicProofPerLuck, 0, 1) when
    // the stack's luck (vfunc 0x1ac) is positive — SETG gives 1, so it does not scale with the luck value.
    [Fact]
    public void LuckOfTheBarbarianIsAFlatFactorWhenLuckIsPositive()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_GOBLIN", 10) });
        var gob = Of(s, "CREATURE_GOBLIN");
        s.Heroes[1].Perks.Add("HERO_SKILL_LUCK_OF_THE_BARBARIAN");
        float per = (float)f.Db.Stats.Get("combat/HeroSkills/Barbarian/LuckOfTheBarbarian_MagicProofPerLuck", 0.05);
        s.Heroes[1].Luck = 0;
        int luck0 = b.LuckOf(gob);
        if (luck0 <= 0) Assert.Equal(1.0, b.SpellDamageMultiplier(gob, Spell("SPELL_FIREBALL"), 0), 6);
        s.Heroes[1].Luck = 3 - luck0;                         // stack luck 3
        Assert.Equal(3, b.LuckOf(gob));
        Assert.Equal(1 - (double)per, b.SpellDamageMultiplier(gob, Spell("SPELL_FIREBALL"), 0), 6);
    }

    // exe FUN_00977a80: Exorcism (HERO_SKILL_ELEMENTAL_OVERKILL) doubles damage against summoned stacks for every damage
    // spell that reaches it through FUN_0097d600 — there is no school check (the engine limited it to Destructive).
    [Fact]
    public void ExorcismDoublesEveryDamageSpellAgainstSummonedStacks()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        var imp = Of(s, "CREATURE_IMP");
        imp.Summoned = true;
        s.Heroes[0].Perks.Add("HERO_SKILL_ELEMENTAL_OVERKILL");
        Assert.Equal(200, b.PreProtectionPerks(100, imp, Spell("SPELL_HOLY_WORD"), s.Heroes[0]));
        Assert.Equal(200, b.PreProtectionPerks(100, imp, Spell("SPELL_FIREBALL"), s.Heroes[0]));
        imp.Summoned = false;
        Assert.Equal(100, b.PreProtectionPerks(100, imp, Spell("SPELL_HOLY_WORD"), s.Heroes[0]));
    }

    // exe FUN_0097d740 ← 00a5c000 (ECX = 4, Ice Bolt) / 00a5ed90 (ECX = 5, Fireball): the perks' extra elemental damage
    // goes through the spell protection routine FUN_0097d2a0 with no caster: Protection gives 14 %, so 10 becomes
    // trunc(0.86·10 + 0.5) = 9; an element-immune target takes nothing; Mark of Fire does not double it.
    [Fact]
    public void PerkElementalDamageIsReducedByProtectionOfItsStandInSpell()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10), ("CREATURE_FIRE_ELEMENTAL", 5) });
        var imp = Of(s, "CREATURE_IMP");
        Assert.Equal(10, b.PerkElementalAmount(10.0, imp, "SPELL_ICE_BOLT", simulate: true));
        s.Heroes[1].Perks.Add("HERO_SKILL_PROTECTION");
        Assert.Equal(9, b.PerkElementalAmount(10.0, imp, "SPELL_ICE_BOLT", simulate: true));
        Assert.Equal(9, b.PerkElementalAmount(10.0, imp, "SPELL_FIREBALL", simulate: true));
        imp.Effects.Add(new Effect { Id = Fx.MarkOfFire, Remaining = 2 });
        Assert.Equal(9, b.PerkElementalAmount(10.0, imp, "SPELL_FIREBALL", simulate: true));
        Assert.Equal(0, b.PerkElementalAmount(10.0, Of(s, "CREATURE_FIRE_ELEMENTAL"), "SPELL_FIREBALL", simulate: true));
    }
}
