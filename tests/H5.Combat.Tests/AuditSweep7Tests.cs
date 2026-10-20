using System.Reflection;
using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Creature spell effects confirmed against H5_Game.exe (docs/rules/audit/combat_sweep_7.md).</summary>
[Collection("game")]
public class AuditSweep7Tests(GameDataFixture f)
{
    Battle Make((string id, int count)[] mine, (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, obstacles: Array.Empty<Cell>(), skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        return new Battle(f.Db, s);
    }

    static Unit Mine(Battle b, string id) => b.State.Units.First(x => x.Side == 0 && x.CreatureId == id);
    static Unit Theirs(Battle b, string id) => b.State.Units.First(x => x.Side == 1 && x.CreatureId == id);

    static bool BeginTurn(Battle b, int id) =>
        (bool)typeof(Battle).GetMethod("BeginTurn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, new object[] { id })!;

    // exe FUN_0097c320 case 0x41: Counterspell is armed only against a hero, with at least 1 mana, and never against a
    // HERO_CLASS_BARBARIAN (enemy hero vf 0x258 != 8).
    [Fact]
    public void CounterspellNeedsAnEnemyCasterHeroAndMana()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var h = b.State.Heroes[0];
        var e = b.State.Heroes[1];
        h.Perks.Add("HERO_SKILL_COUNTERSPELL");
        h.Mana = 10;
        e.Class = "HERO_CLASS_WIZARD";
        Assert.Contains(b.HeroAbilities(h), a => a.Id == "COUNTERSPELL");
        e.Class = "HERO_CLASS_BARBARIAN";
        Assert.DoesNotContain(b.HeroAbilities(h), a => a.Id == "COUNTERSPELL");
        e.Class = "HERO_CLASS_WIZARD";
        h.Mana = 0;
        Assert.DoesNotContain(b.HeroAbilities(h), a => a.Id == "COUNTERSPELL");
    }

    // exe FUN_00978670 (via FUN_00983790 for every caster): FUN_00a349f0 / FUN_00a345e0 leave warcries, special spells and
    // Dark Ritual alone; the enemy pays twice the caster's normal cost.
    [Fact]
    public void CounterspellSkipsWarcriesAndUsesTheNormalCost()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var e = b.State.Heroes[1];
        e.Links["COUNTERSPELL"] = 1;
        e.Mana = 20;
        var warcry = f.Db.Spells.Values.First(s => s.School == "MAGIC_SCHOOL_WARCRIES");
        Assert.False(b.Counterspelled(0, warcry, 1));
        Assert.True(e.Links.ContainsKey("COUNTERSPELL"));
        var bolt = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        Assert.False(b.Counterspelled(0, bolt, 11));      // 22 > 20 mana
        Assert.True(b.Counterspelled(0, bolt, 10));
        Assert.Equal(0, e.Mana);
        Assert.False(e.Links.ContainsKey("COUNTERSPELL"));
    }

    // exe FUN_0097c320 case 0x13d: Invisibility is refused next to an enemy (FUN_008a4b70 aura flag 1, vf 0x21c(1)).
    [Fact]
    public void InvisibilityIsRefusedNextToAnEnemy()
    {
        var b = f.Duel("CREATURE_STALKER", 10, "CREATURE_PEASANT", 20, 1, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        var u = b.State.Units[0];
        Assert.DoesNotContain(b.UnitAbilities(u), a => a.Id == Ab.Invisibility);
        b.State.Units[1].Pos = new Cell(10, 5);
        Assert.Contains(b.UnitAbilities(u), a => a.Id == Ab.Invisibility);
    }

    // exe FUN_00a618f0: the roots go on any surviving creature stack the treants strike, flyers included.
    [Fact]
    public void RootsHoldFlyers()
    {
        var b = f.Duel("CREATURE_TREANT", 20, "CREATURE_GRIFFIN", 30, 1, new Cell(5, 5), new Cell(7, 5));
        b.Begin();
        var tr = b.State.Units[0];
        var gr = b.State.Units[1];
        Assert.True(gr.Flying);
        b.State.Active = tr.Id;
        var r = b.Apply(new AttackCmd(tr.Id, gr.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.True(!gr.Alive || gr.HasEffect(Fx.Entangled));
    }

    // exe FUN_00981650 cases 0x9a / 0x15a: Battle Dive and Pack Dive are refused while rooted (effect 0xac);
    // FUN_00982010 case 0x20: Teleport refuses a rooted target, case 0x14: Hypnotize a seduced one.
    [Fact]
    public void RootedStacksCannotDiveOrBeTeleported()
    {
        var b = Make(new[] { ("CREATURE_ROYAL_GRIFFIN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var g = Mine(b, "CREATURE_ROYAL_GRIFFIN");
        Assert.Contains(b.UnitAbilities(g), a => a.Id == Ab.BattleDive);
        b.AddEffect(g, new Effect { Id = Fx.Entangled, SourceUnit = -1, SourceSide = 1 });
        Assert.DoesNotContain(b.UnitAbilities(g), a => a.Id == Ab.BattleDive);
        var free = new Cell(g.Pos.X, g.Pos.Y + 3);
        Assert.Equal("target is rooted",
            b.ValidateSpellTarget(Actor.Hero(0), 0, f.Db.Spell("SPELL_TELEPORT")!, Mastery.Expert, g.Id, free));
        var p = Theirs(b, "CREATURE_PEASANT");
        b.AddEffect(p, new Effect { Id = Fx.Seduced, SourceUnit = -1, SourceSide = 1 });
        Assert.Equal("target is seduced",
            b.ValidateSpellTarget(Actor.Hero(0), 0, f.Db.Spell("SPELL_HYPNOTIZE")!, Mastery.Expert, p.Id, null));
    }

    // exe FUN_00a597a0: against a sleeping target (0x150) an attacker of the CREATURE_VAMPIRE family (FUN_004babf0 ==
    // 0x23: BaseCreature, else own id) uses its maximum damage.
    [Fact]
    public void SleepingStrikeMaxDamageIsTheVampireFamily()
    {
        foreach (var (id, fam) in new[] { ("CREATURE_VAMPIRE", true), ("CREATURE_VAMPIRE_LORD", true), ("CREATURE_NOSFERATU", true), ("CREATURE_PEASANT", false) })
        {
            var b = f.Duel(id, 5, "CREATURE_PEASANT", 20, 1, new Cell(5, 5), new Cell(6, 5));
            Assert.Equal(fam, b.VampireFamily(b.State.Units[0]));
        }
    }

    // exe FUN_008a32b0 / FUN_008a4800: under Rage of the Forest (0x149) defence = d − d/2 and attack + d/2 from the
    // current full defence d (FUN_008a22f0), not the value stored at the cast.
    [Fact]
    public void RageOfTheForestUsesTheCurrentDefence()
    {
        var b = f.Duel("CREATURE_ANGER_TREANT", 5, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(9, 5));
        var u = b.State.Units[0];
        int d0 = b.DefenceOf(u), a0 = b.AttackOf(u);
        b.AddEffect(u, new Effect { Id = Fx.RageOfForest, Value = 1, Positive = true, Dispellable = false });
        Assert.Equal(d0 - d0 / 2, b.DefenceOf(u));
        Assert.Equal(a0 + d0 / 2, b.AttackOf(u));
        b.AddEffect(u, new Effect { Id = "SPELL_STONESKIN", Value = 7, Positive = true });
        int d1 = b.DefenceOf(u, rageFull: true);
        Assert.Equal(d0 + 7, d1);
        Assert.Equal(d1 - d1 / 2, b.DefenceOf(u));
    }

    // exe FUN_008a2130: Bear Roar health = trunc((BearRoar_HealthBonus + 1.0001f) · hp), x87, chop.
    [Fact]
    public void BearRoarHealthUsesTheExeEpsilon()
    {
        Assert.Equal(33, Battle.BearRoarHealth(30, 0.1));
        Assert.Equal(1100, Battle.BearRoarHealth(1000, 0.1));
        Assert.Equal(11001, Battle.BearRoarHealth(10000, 0.1));   // (0.1f + 1.0001f) · 10000 = 11001.0001…
    }

    // exe FUN_0097c1d0: Power Feed is refused while the hero carries effect 0x14d from any living stack.
    [Fact]
    public void PowerFeedOncePerHeroWhileTheFeederLives()
    {
        var b = Make(new[] { ("CREATURE_HIGH_DRUID", 10), ("CREATURE_HIGH_DRUID", 12) }, new[] { ("CREATURE_PEASANT", 20) });
        var h = b.State.Heroes[0];
        h.Class = "HERO_CLASS_RANGER";
        var d = b.State.Units.Where(x => x.Side == 0 && x.CreatureId == "CREATURE_HIGH_DRUID").ToList();
        Assert.True(b.PowerFeedAvailable(d[1], h));
        h.Links[Ab.PowerFeed] = d[0].Id;
        Assert.False(b.PowerFeedAvailable(d[1], h));
        d[0].Count = 0;
        Assert.True(b.PowerFeedAvailable(d[1], h));
    }

    // exe FUN_00bbc8e0 (creature turn start): a Defile Magic stack (0x6d) releases every 0x146 it placed.
    [Fact]
    public void DefileMagicEndsAtTheDefilersTurnStart()
    {
        var b = f.Duel("CREATURE_GOBLIN_DEFILER", 50, "CREATURE_DRUID", 20, 1, new Cell(2, 5), new Cell(9, 5));
        b.Begin();
        var g = b.State.Units[0];
        var dr = b.State.Units[1];
        b.AddEffect(dr, new Effect { Id = "FX_DEFILED", SourceUnit = g.Id, SourceSide = 0, Value = 50, Positive = false, Dispellable = false });
        BeginTurn(b, g.Id);
        Assert.False(dr.HasEffect("FX_DEFILED"));
    }

    // exe FUN_00a5e1b0: Fear's immunity is the spell application check (FUN_00982010, spell 0xa0) — Anti-Magic too.
    [Fact]
    public void FearUsesTheSpellApplicationCheck()
    {
        var b = f.Duel("CREATURE_PEASANT", 5, "CREATURE_PEASANT", 20, 1, new Cell(5, 5), new Cell(6, 5));
        var t = b.State.Units[1];
        Assert.False(b.FearImmune(t));
        b.AddEffect(t, new Effect { Id = "SPELL_ANTI_MAGIC", Value = 5, Positive = true });
        Assert.True(b.FearImmune(t));
        Assert.False(b.MindImmune(t));
    }

    // exe FUN_004e5c20: Master of Lightning takes clamp(ATB, 0, 1) and removes EmpoweredLightningATBDrop of it.
    [Fact]
    public void MasterOfLightningClampsTheAtbFirst()
    {
        var b = f.Duel("CREATURE_PEASANT", 5, "CREATURE_PEASANT", 20, 1, new Cell(5, 5), new Cell(6, 5));
        var t = b.State.Units[1];
        t.Atb = 1.2;
        b.MasterOfLightningAtb(t);
        float drop = (float)b.Db.Stats.Get("combat/HeroSkills/EmpoweredLightningATBDrop", 0.3);
        Assert.Equal(1f - 1f * drop, (float)t.Atb, 5);
    }
}
