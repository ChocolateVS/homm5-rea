using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Hero specialisation / dynamic ability / spell cost rules confirmed against H5_Game.exe (docs/rules/AUDIT_COMBAT.md).</summary>
[Collection("game")]
public class AuditSpecTests(GameDataFixture f)
{
    Battle WithHero(string spec, string[] perks, params (string id, int count)[] army)
    {
        var hero = f.Db.DuelPresets[0].Hero;
        var s = BattleSetup.Create(f.Db, new ArmySetup { Hero = hero, Army = army.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 50) } }, 3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        return new Battle(f.Db, s);
    }

    static BattleState Setup(GameDatabase db, Action<HeroState> configure, params (string id, int count)[] army)
    {
        var s = BattleSetup.Create(db, new ArmySetup { Hero = db.DuelPresets[0].Hero, Army = army.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 50) } }, 3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        configure(s.Heroes[0]);
        return s;
    }

    // exe FUN_008a34c0: HERO_SPEC_GRIFFINS gives basic Griffins (creature 7) Battle Dive.
    [StockFact]
    public void GriffonCommanderGivesGriffinsBattleDive()
    {
        var preset = f.Db.DuelPresets[0].Hero;   // Ving, HERO_SPEC_GRIFFINS
        var s = BattleSetup.Create(f.Db, new ArmySetup { Hero = preset, Army = { new ArmySlot("CREATURE_GRIFFIN", 10), new ArmySlot("CREATURE_FOOTMAN", 10) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 50) } }, 3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        Assert.Equal("HERO_SPEC_GRIFFINS", s.Heroes[0].Specialization);
        Assert.True(s.Units.Single(u => u.CreatureId == "CREATURE_GRIFFIN").Has(Ab.BattleDive));
        Assert.False(s.Units.Single(u => u.CreatureId == "CREATURE_FOOTMAN").Has(Ab.BattleDive));
    }

    // exe FUN_008a34c0: Phantom images are Incorporeal and lose Flesh and Blood; Vampirism makes Undead.
    [Fact]
    public void DynamicAbilitiesFollowTheOriginal()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 10);
        var u = b.State.Units[0];
        Assert.False(u.Has(Ab.Incorporeal));
        u.IsClone = true;
        Assert.True(u.Has(Ab.Incorporeal));
        u.IsClone = false;
        u.Effects.Add(new Effect { Id = "FX_RUNE_ETHEREALNESS", Remaining = 1 });
        Assert.True(u.Has(Ab.Incorporeal));
        Assert.False(u.Has(Ab.Undead));
        u.Effects.Add(new Effect { Id = "SPELL_VAMPIRISM", Remaining = 1 });
        Assert.True(u.Has(Ab.Undead));
    }

    // exe FUN_00a34740 + FUN_009781e0: spell cost chain.
    [Fact]
    public void SpellCostsMatchTheOriginalChain()
    {
        var s = Setup(f.Db, h => { h.Perks.Clear(); h.Artifacts.Clear(); h.Specialization = ""; }, ("CREATURE_FOOTMAN", 10));
        var b = new Battle(f.Db, s);
        var h = s.Heroes[0];
        var bless = f.Db.Spell("SPELL_BLESS")!;          // Light
        var fireball = f.Db.Spell("SPELL_FIREBALL")!;    // Destructive
        Assert.Equal(bless.ManaCost, b.HeroManaCost(h, bless));
        h.Specialization = "HERO_SPEC_WHITE_KNIGHT";
        Assert.Equal(bless.ManaCost - 1, b.HeroManaCost(h, bless));            // Light only
        Assert.Equal(fireball.ManaCost, b.HeroManaCost(h, fireball));
        h.Specialization = "";
        h.Perks.Add(Sk.ArcaneTraining);                                          // round(min(0.8c, c-1))
        Assert.Equal((int)Math.Round(Math.Min(fireball.ManaCost * 0.8, fireball.ManaCost - 1.0), MidpointRounding.ToEven), b.HeroManaCost(h, fireball));
        h.Perks.Clear();
        h.Artifacts.Add("RING_OF_MAGI");
        Assert.Equal(fireball.ManaCost / 2, b.HeroManaCost(h, fireball));
    }

    // exe FUN_00989fd0 / FUN_00989790 / FUN_0098a430: White Knight doubles Prayer's bonus.
    [StockFact]
    public void WhiteKnightDoublesPrayer()
    {
        var s = Setup(f.Db, h => { h.Specialization = "HERO_SPEC_WHITE_KNIGHT"; h.Perks.Add(Sk.Benediction); }, ("CREATURE_FOOTMAN", 10));
        var b = new Battle(f.Db, s);
        b.Begin();
        var footman = s.Units.Single(u => u.CreatureId == "CREATURE_FOOTMAN");
        int att = b.AttackOf(footman), def = b.DefenceOf(footman);
        s.Active = Actor.Hero(0);
        var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "BENEDICTION") { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(att + 2, b.AttackOf(footman));
        Assert.Equal(def + 2, b.DefenceOf(footman));
    }
}
