using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Runes and warcries confirmed against H5_Game.exe (docs/rules/audit/combat_spells_r.md).</summary>
[Collection("game")]
public class AuditRuneWarcryTests(GameDataFixture f)
{
    BattleState Setup((string id, int count)[] mine, (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        return s;
    }

    static Unit Mine(BattleState s, string id) => s.Units.Single(x => x.Side == 0 && x.CreatureId == id);

    // exe FUN_00c54620: a = 1 + Random(4), b = Random(3) + 1 (+1 when a ≤ b) — two distinct exe element codes;
    // FUN_00ad02b0 maps 1 air, 2 fire, 3 water, 4 earth.
    [Fact]
    public void ElementalImmunityRollsTwoDistinctExeElements()
    {
        Assert.Equal(Elements.Air, Battle.ExeElement(1));
        Assert.Equal(Elements.Fire, Battle.ExeElement(2));
        Assert.Equal(Elements.Water, Battle.ExeElement(3));
        Assert.Equal(Elements.Earth, Battle.ExeElement(4));
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < 400; i++)
        {
            var (x, y) = b.RollElementalImmunity();
            Assert.InRange(x, 1, 4);
            Assert.InRange(y, 1, 4);
            Assert.NotEqual(x, y);
            seen.Add((x, y));
        }
        Assert.Equal(12, seen.Count);
    }

    // exe FUN_004b4460 → FUN_004bb000: Dragonform is refused for every creature whose base creature is Bone, Green, Deep
    // or Fire Dragon (ids 0x29/0x37/0x53/0x68) — decided by id, not by the (localised) name.
    [Fact]
    public void DragonformRefusesDragonLinesById()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_MAGMA_DRAGON", 2), ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        var s = b.State;
        var dragon = Mine(s, "CREATURE_MAGMA_DRAGON");
        var footman = Mine(s, "CREATURE_FOOTMAN");
        Assert.True(b.DragonformForbidden(dragon));
        dragon.Name = "Drache";
        Assert.True(b.DragonformForbidden(dragon));
        Assert.False(b.DragonformForbidden(footman));
    }

    // exe FUN_008d9b30 → FUN_004e4000 / FUN_004e3960 / FUN_004e3ac0: Exorcism removes harmful dispellable timed effects of
    // any spell level; permanent ones, Poison, Wound, Dash, Venom… stay.
    [Fact]
    public void ExorcismHasNoSpellLevelLimit()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        Assert.True(b.ExorcismRemoves(new Effect { Id = "SPELL_PLAGUE", Remaining = 3, SpellLevel = 4 }));
        Assert.False(b.ExorcismRemoves(new Effect { Id = "SPELL_PLAGUE", SpellLevel = 4 }));            // permanent duration
        Assert.False(b.ExorcismRemoves(new Effect { Id = Fx.Poison, Remaining = 3 }));
        Assert.False(b.ExorcismRemoves(new Effect { Id = Fx.Wound, Remaining = 2 }));
        Assert.False(b.ExorcismRemoves(new Effect { Id = "SPELL_ABILITY_VENOM", Remaining = 3 }));
        Assert.False(b.ExorcismRemoves(new Effect { Id = "SPELL_BLESS", Remaining = 3, Positive = true }));
        Assert.False(b.ExorcismRemoves(new Effect { Id = "SPELL_SLOW", Remaining = 3, Dispellable = false }));
    }

    // exe FUN_009bddb0: chance = clamp((caster level − target level + 10)·5, 0, 100), 0 against HOLD_GROUND;
    // FUN_009bdaa0 case 0x125: only HasAbility(IMMUNITY_TO_MIND_CONTROL) makes the target invalid (derived for undead).
    [Fact]
    public void FearMyRoarChanceAndTargets()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        var t = b.State.Units.Single(x => x.Side == 1 && x.CreatureId == "CREATURE_PEASANT");
        t.Level = 1;
        Assert.Equal(100, b.FearMyRoarPercent(12, t));       // (12 − 1 + 10)·5 = 105 → 100
        t.Level = 7;
        Assert.Equal(75, b.FearMyRoarPercent(12, t));        // (12 − 7 + 10)·5
        Assert.Equal(20, b.FearMyRoarPercent(1, t));         // (1 − 7 + 10)·5
        t.Level = 15;
        Assert.Equal(0, b.FearMyRoarPercent(1, t));          // (1 − 15 + 10)·5 < 0 → 0
    }

    [Fact]
    public void FearMyRoarHoldGroundAndImmunity()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        var t = b.State.Units.Single(x => x.Side == 1 && x.CreatureId == "CREATURE_PEASANT");
        t.Level = 3;
        t.Abilities.Add(Ab.HoldGround);
        Assert.Equal(0, b.FearMyRoarPercent(20, t));
        t.Abilities.Remove(Ab.HoldGround);
        Assert.False(b.WarcryTargetInvalid("SPELL_WARCRY_FEAR_MY_ROAR", t));
        t.Abilities.Add(Ab.ImmuneMind);
        Assert.True(b.WarcryTargetInvalid("SPELL_WARCRY_FEAR_MY_ROAR", t));
        // HasAbility(IMMUNITY_TO_MIND_CONTROL) is also derived from Undead (FUN_00b61950 / FUN_00b618a0).
        t.Abilities.Remove(Ab.ImmuneMind);
        t.Abilities.Add(Ab.Undead);
        Assert.True(b.WarcryTargetInvalid("SPELL_WARCRY_FEAR_MY_ROAR", t));
    }

    // exe FUN_009bdaa0 cases 0x122 / 0x124: Rallying Cry and Word of the Chief refuse Undead and Mechanical only —
    // elementals are valid targets.
    [Fact]
    public void RallyingCryAndWordOfTheChiefTargets()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        var u = Mine(b.State, "CREATURE_FOOTMAN");
        foreach (var id in new[] { "SPELL_WARCRY_RALLING_CRY", "SPELL_WARCRY_WORD_OF_THE_CHIEF" })
        {
            u.Abilities.Add(Ab.Elemental);
            Assert.False(b.WarcryTargetInvalid(id, u));
            u.Abilities.Add(Ab.Undead);
            Assert.True(b.WarcryTargetInvalid(id, u));
            u.Abilities.Remove(Ab.Undead);
            u.Abilities.Add(Ab.Mechanical);
            Assert.True(b.WarcryTargetInvalid(id, u));
            u.Abilities.Remove(Ab.Mechanical);
            u.Abilities.Remove(Ab.Elemental);
        }
    }

    // exe FUN_009bdbd0: army rage per warcry level ×Voice of Rage, Call of Blood level·perLevel·m, + hero level for War Leader.
    [Fact]
    public void WarcryRagePointsFollowTheExe()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        var h = b.State.Heroes[0];
        h.Level = 10;
        int l1 = (int)b.Db.Stats.Get("combat/Spells/Warcries/RPToArmyOnWarcryLevel1", 0);
        int l3 = (int)b.Db.Stats.Get("combat/Spells/Warcries/RPToArmyOnWarcryLevel3", 0);
        int per = (int)b.Db.Stats.Get("combat/Spells/Warcries/CallOfBlood_RPPerCasterLevel", 0);
        Assert.Equal(l1, b.WarcryRagePoints("SPELL_WARCRY_RALLING_CRY", h));
        Assert.Equal(l3, b.WarcryRagePoints("SPELL_WARCRY_SHOUT_OF_MANY", h));
        Assert.Equal(10 * per, b.WarcryRagePoints("SPELL_WARCRY_CALL_OF_BLOOD", h));
        h.Specialization = "HERO_SPEC_WAR_LEADER";
        Assert.Equal(l1 + 10, b.WarcryRagePoints("SPELL_WARCRY_RALLING_CRY", h));
        Assert.Equal(10 * per + 10, b.WarcryRagePoints("SPELL_WARCRY_CALL_OF_BLOOD", h));
        h.Specialization = "";
        h.Perks.Add(Sk.ShoutOfRage);
        double mOther = (float)b.Db.Stats.Get("combat/HeroSkills/Barbarian/VoiceOfRage_RageMultOther", 2);
        Assert.Equal((int)(l1 * mOther), b.WarcryRagePoints("SPELL_WARCRY_RALLING_CRY", h));
    }

    // exe FUN_009be6c0 case 0x127 / FUN_009bde00: damage = trunc(creatures·DamagePerCreatureInArmy) over the caster's
    // own-controlled creatures; a stack hypnotized by the enemy doesn't count.
    [Fact]
    public void ShoutOfManyCountsOwnControlledCreatures()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10), ("CREATURE_ARCHER", 7) }, new[] { ("CREATURE_PEASANT", 10) }));
        double coef = (float)b.Db.Stats.Get("combat/Spells/Warcries/ShoutOfMany_DamagePerCreatureInArmy", 1);
        Assert.Equal((int)(17 * coef), b.ShoutOfManyDamage(0));
        var archers = Mine(b.State, "CREATURE_ARCHER");
        b.AddEffect(archers, new Effect { Id = "SPELL_HYPNOTIZE", Remaining = 2, SourceSide = 1 });
        Assert.Equal((int)(10 * coef), b.ShoutOfManyDamage(0));
    }

    // exe FUN_008a1e50: Wound truncates first, Rune of Charge doubles before the Battlecry bonus is added, and the
    // Battlecry speed bonus follows its level formula.
    [Fact]
    public void SpeedAppliesChargeBeforeBattlecry()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        var u = Mine(b.State, "CREATURE_FOOTMAN");
        int baseSpeed = b.SpeedOf(u);
        double dec = (float)b.Db.Stats.Get("combat/Creatures/Fortress/Wound_SpeedDecrease", 0.4);
        int wounded = (int)Math.Floor((1 - dec) * baseSpeed);
        b.AddEffect(u, new Effect { Id = Fx.Wound, Remaining = 2 });
        Assert.Equal(wounded, b.SpeedOf(u));
        b.AddEffect(u, new Effect { Id = Fx.RuneCharge, Actions = 1, Positive = true });
        Assert.Equal(wounded * 2, b.SpeedOf(u));
        int bonus = b.BattlecrySpeedBonus(20);
        b.AddEffect(u, new Effect { Id = Fx.Battlecry, Value2 = bonus, Remaining = 1, Positive = true });
        Assert.Equal(wounded * 2 + bonus, b.SpeedOf(u));
    }
}
