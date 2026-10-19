using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Core mechanics and spell leftovers confirmed against H5_Game.exe (docs/rules/audit/combat_sweep_*.md).</summary>
[Collection("game")]
public class AuditSweepTests(GameDataFixture f)
{
    Battle Make((string id, int count)[] mine, (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        return new Battle(f.Db, s);
    }

    static Unit Theirs(Battle b, string id) => b.State.Units.Single(x => x.Side == 1 && x.CreatureId == id);

    // exe FUN_0098ade0 field object, interface vtable 00e61818: every Firewall hit — cast (FUN_0085b790), entry
    // (FUN_0085bfe0 → FUN_0085b530) — computes vf 0x1c = FUN_0098abf0 (FUN_0097d600 with the caster stored at +0x64, so the
    // caster's Phoenix Feather Cape counts) and deals it with vf 0x24 = FUN_0098a920 → the fire rider FUN_004e6770 (Master
    // of Fire). The engine's field hits had no caster: no artifact bonus, no Master of Fire.
    [Fact]
    public void FirewallFieldHitsKeepTheCaster()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 200) });
        var pe = Theirs(b, "CREATURE_PEASANT");
        var h = b.State.Heroes[0];
        b.ResolveSpell(Actor.Hero(0), 0, f.Db.Spell("SPELL_FIREWALL")!, Mastery.Basic, 1, null, new Cell(0, 0), fromHero: true);
        var field = b.State.Fields.Single(x => x.Kind == "FIREWALL");
        Assert.True(field.HeroCaster);
        int plain = b.FirewallHit(pe, field);
        h.Artifacts.Add("PHOENIX_FEATHER_CAPE");
        int cape = b.FirewallHit(pe, field);
        Assert.True(plain > 0 && cape > plain, $"{plain} -> {cape}");
        h.Perks.Add(Sk.MasterFire);
        pe.Effects.RemoveAll(e => e.Id == Fx.ArmorCrushed);
        b.FirewallHit(pe, field);
        Assert.True(pe.HasEffect(Fx.ArmorCrushed));
    }

    // exe FUN_0085b790: the cast hits creature stacks (vf 0x6c) and spawns (vf 0x8c) on the cells; war machines answer
    // neither (vtable 00e70594 slots 0x6c / 0x8c = FUN_00a76cf0 → null) and are not hit.
    [Fact]
    public void FirewallCastSkipsWarMachines()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 200) });
        var wm = BattleSetup.AddWarMachine(f.Db, b.State, 1, "WAR_MACHINE_BALLISTA");
        int count = wm.Count, top = wm.TopHealth;
        b.ResolveSpell(Actor.Hero(0), 0, f.Db.Spell("SPELL_FIREWALL")!, Mastery.Expert, 20, null, wm.Pos, fromHero: true);
        Assert.Equal((count, top), (wm.Count, wm.TopHealth));
    }

    // exe FUN_0097c9f0 @ 0097cf3a: the resistance chance is skipped only for a non-hostile spell on the caster's own side;
    // the damage resolvers roll it (FUN_0097d600 roll flag 1, e.g. 0097da71). A Fireball hitting an own stack can be resisted
    // by its hero's resistance; a Bless on it cannot. The engine never rolled for own stacks.
    [Fact]
    public void HostileSpellsRollResistanceOnOwnStacks()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 200) });
        var own = b.State.Units.First(x => x.Side == 0 && x.CreatureId == "CREATURE_FOOTMAN");
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.MagicResistance);
        h.Artifacts.Add("PLATE_MAIL_OF_STABILITY");
        h.Artifacts.Add("BOOTS_OF_INTERFERENCE");
        Assert.Equal(45, b.MagicResistanceChance(own));
        var fireball = f.Db.Spell("SPELL_FIREBALL")!;
        var bless = f.Db.Spell("SPELL_BLESS")!;
        int resisted = 0, blessResisted = 0;
        for (int i = 0; i < 100; i++)
        {
            if (!b.SpellHits(0, fireball, own, hostile: true, h, area: true)) resisted++;
            if (!b.SpellHits(0, bless, own, hostile: false, h)) blessResisted++;
        }
        Assert.InRange(resisted, 20, 70);
        Assert.Equal(0, blessResisted);
    }

    // exe FUN_00a65260 @ 00a65646 → FUN_00a5ca80: the rune steals before the strike loop (the stolen effect leaves the
    // target before the first blow) and only in the holder's own attack, never in its retaliation.
    [Fact]
    public void MagicControlStealsBeforeTheFirstStrikeAndNotOnRetaliation()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 400, posA: new Cell(5, 5), posB: new Cell(6, 5));
        b.Begin();
        var fm = b.State.Units[0]; var pe = b.State.Units[1];
        fm.Effects.Add(new Effect { Id = Fx.RuneMagicControl, Positive = true, Dispellable = false });
        pe.Effects.Add(new Effect { Id = "SPELL_HASTE", Positive = true, Remaining = 3, School = "MAGIC_SCHOOL_LIGHT" });
        b.State.Active = fm.Id;
        var r = b.Apply(new AttackCmd(fm.Id, pe.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var ev = r.Events.ToList();
        int removed = ev.FindIndex(e => e is EffectRemoved { Effect: "SPELL_HASTE" } x && x.UnitId == pe.Id);
        int firstBlow = ev.FindIndex(e => e is AttackStarted a && a.Attacker == fm.Id);
        Assert.True(removed >= 0 && firstBlow >= 0 && removed < firstBlow, string.Join(" | ", ev));
        Assert.True(fm.HasEffect("SPELL_HASTE"));

        var b2 = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 400, posA: new Cell(5, 5), posB: new Cell(6, 5));
        b2.Begin();
        var fm2 = b2.State.Units[0]; var pe2 = b2.State.Units[1];
        pe2.Effects.Add(new Effect { Id = Fx.RuneMagicControl, Positive = true, Dispellable = false });
        fm2.Effects.Add(new Effect { Id = "SPELL_HASTE", Positive = true, Remaining = 3, School = "MAGIC_SCHOOL_LIGHT" });
        b2.State.Active = fm2.Id;
        Assert.True(b2.Apply(new AttackCmd(fm2.Id, pe2.Id) { Side = 0 }).Ok);
        Assert.True(pe2.HasEffect(Fx.RuneMagicControl));
        Assert.True(fm2.HasEffect("SPELL_HASTE"));
    }
}
