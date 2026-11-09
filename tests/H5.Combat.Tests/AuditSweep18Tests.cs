using H5.Core.Data;
namespace H5.Combat.Tests;

/// <summary>Sweep 18 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_18.md).</summary>
[Collection("game")]
public class AuditSweep18Tests(GameDataFixture f)
{
    const string Aod = "SPELL_ABILITY_AVATAR_OF_DEATH";

    Battle Necro(int level, int power, int knowledge, int mana = 500)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = [new ArmySlot("CREATURE_SKELETON", 10)] },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = [new ArmySlot("CREATURE_PEASANT", 10)] },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var hh in s.Heroes) { hh.Specialization = ""; hh.Artifacts.Clear(); hh.Perks.Clear(); hh.Skills.Clear(); }
        var h = s.Heroes[0];
        h.Specialization = "HERO_SPEC_AVATAR_OF_DEATH";
        h.Spells = new() { Aod };
        h.Level = level; h.SpellPower = power; h.Knowledge = knowledge; h.Mana = mana;
        var b = new Battle(f.Db, s);
        b.Begin();
        b.State.Active = Actor.Hero(0);
        return b;
    }

    // exe 00984a00 case 0xc8 → FUN_00862570 / FUN_00862320 / FUN_00862970, FUN_004bcd60 …: one creature of the record's
    // prototype for the caster's side, its stats from the AvatarOfDeath_Creature record at the hero's level, power and
    // knowledge with divisor 1, the abilities of the record up to the level, the cost level · AvatarOfDeath_ManaCostPerLevel,
    // the hero linked to it (vf 0x298(0xc8, …)), an ATB from the battle RNG's [0, 0.25) (FUN_00a73e70).
    [Fact]
    public void AvatarOfDeathSummonsTheRecordsCreature()
    {
        var b = Necro(13, 6, 5);
        var h = b.State.Heroes[0];
        Assert.Contains(Aod, b.HeroSpells(h));
        int power = (int)b.HeroSpellPower(h, f.Db.Spell(Aod)!);
        int before = b.State.Units.Count;
        var r = b.Apply(new CastSpellCmd(Actor.Hero(0), Aod) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(before + 1, b.State.Units.Count);
        var av = b.State.Units[^1];
        var rec = f.Db.AvatarOfDeath!;
        double F(string n) => double.Parse(rec.Element(n)!.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(rec.Element("PrototypeCreature")!.Value.Trim(), av.CreatureId);
        Assert.Equal(0, av.Side);
        Assert.Equal(1, av.Count);
        Assert.True(av.Summoned);
        Assert.Equal((int)(F("Health") + 13 * F("Health_PerLevel") + power * F("Health_PerPower") + 5 * F("Health_PerKnowledge")), av.BaseHealth);
        Assert.Equal((int)(F("DamageMax") + 13 * F("DamageMax_PerLevel") + power * F("DamageMax_PerPower")), av.BaseMaxDamage);
        Assert.Equal((int)(F("Offence") + 13 * F("Offence_PerLevel")), av.BaseAttack);
        var want = rec.Element("Abilities")!.Elements("Item")
            .Where(i => int.Parse(i.Element("Level")!.Value) <= 13)
            .SelectMany(i => i.Element("Abilities")!.Elements("Item").Select(a => a.Value.Trim())).ToList();
        Assert.Equal(want, av.Abilities);
        Assert.True(av.Atb is >= 0 and < 0.25);
        Assert.Equal(500 - 13 * (int)f.Db.Stats.Get("combat/HeroSpecializations/Necromancer/AvatarOfDeath_ManaCostPerLevel", -1), h.Mana);
        Assert.Equal(av.Id, h.Links[Aod]);
    }

    // exe 0097c320 → FUN_00862470: not offered (and refused) while the hero has a unit linked for 0xc8; the avatar's death
    // (FUN_008a6e20 @ 008a7492..008a74f2) sets its army hero's mana to 0 (vf 0x22c(0)) and drops the link (vf 0x2b0).
    [Fact]
    public void OneAvatarAtATimeAndItsDeathTakesTheMana()
    {
        var b = Necro(10, 4, 4);
        var h = b.State.Heroes[0];
        Assert.True(b.Apply(new CastSpellCmd(Actor.Hero(0), Aod) { Side = 0 }).Ok);
        var av = b.State.Unit(h.Links[Aod])!;
        b.State.Active = Actor.Hero(0);
        Assert.DoesNotContain(b.GetLegalActions()!.Spells, o => o.Spell == Aod);
        Assert.False(b.Apply(new CastSpellCmd(Actor.Hero(0), Aod) { Side = 0 }).Ok);
        Assert.True(h.Mana > 0);
        b.Kill(av, -1);
        Assert.Equal(0, h.Mana);
        Assert.False(h.Links.ContainsKey(Aod));
    }

    // exe FUN_00860f20: the first column from the caster's own edge (exe x = 2 … W − 3 attacker, W − 3 … 2 defender) with
    // a free anchor of the creature's size; its rows top down; no RNG → the first.
    [Fact]
    public void TheSummonPositionIsTheFirstFreeColumnFromTheOwnEdge()
    {
        var b = Necro(1, 1, 1);
        var s = b.State;
        foreach (var u in s.Units.ToList()) u.Count = 0;
        s.Obstacles.Clear();
        Assert.Equal(new Cell(0, 0), b.ExeSummonPosition(0, 1, null));
        Assert.Equal(new Cell(s.Width - 1, 0), b.ExeSummonPosition(1, 1, null));
        Assert.Equal(new Cell(0, 0), b.ExeSummonPosition(0, 2, null));        // anchor (1, 1): column 0 has no 2×2 anchor
        Assert.Equal(new Cell(s.Width - 2, 0), b.ExeSummonPosition(1, 2, null));
    }
    // exe FUN_00bbc8e0 (00bbc90b..00bbc923): SPELL_ABILITY_DASH counts down at the turn start (vf 0x268 = FUN_00a72c80),
    // expiring below 1; the turn end leaves it alone.
    [Fact]
    public void DashCountsDownAtTheTurnStart()
    {
        var b = Necro(1, 1, 1);
        var u = b.State.Units.First(x => x.Side == 1 && x.Kind == UnitKind.Creature);
        u.Effects.Add(new Effect { Id = Fx.Dash, Actions = 2, Positive = true, Dispellable = false });
        Assert.False(b.ExeTurnStartTail(u));
        Assert.Equal(1, u.GetEffect(Fx.Dash)!.Actions);
        b.ExeTurnStartTail(u);
        Assert.False(u.HasEffect(Fx.Dash));
    }

    // exe FUN_00bbb900: poison counts down first (expiring below 1) and still hits; Set Afire on a Mark of Fire carrier
    // × clamp(MarkOfFire_FireDamageMultiplier, 0, 1000), chop; straight through the damage path (no fire immunity).
    [Fact]
    public void DamageOverTimeTicksInTheTurnStartTail()
    {
        var b = Necro(1, 1, 1);
        var u = b.State.Units.First(x => x.Side == 1 && x.Kind == UnitKind.Creature);
        u.Count = 50;
        int hp = u.TotalHealth;
        u.Effects.Add(new Effect { Id = Fx.Poison, Value = 3, Actions = 1 });
        u.Effects.Add(new Effect { Id = Fx.MarkOfFire, Remaining = 100 });
        u.Effects.Add(new Effect { Id = Fx.Ignite, Value = 5, Remaining = 100 });
        b.ExeTurnStartTail(u);
        float mof = Math.Clamp((float)f.Db.Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2), 0f, 1000f);
        Assert.Equal(hp - 3 - (int)(mof * 5.0), u.TotalHealth);
        Assert.False(u.HasEffect(Fx.Poison));
        Assert.True(u.HasEffect(Fx.Ignite));
    }

    // exe FUN_00bbb390: a gated stack's first turn ends the gating effect, sets its ATB to clamp(GatedStartATB, 0, 1)
    // and it does not act (FUN_00bbcb40 acts only while ATB ≥ 1).
    [Fact]
    public void AGatedStackArrivesWithoutActing()
    {
        var b = Necro(1, 1, 1);
        var u = b.State.Units.First(x => x.Side == 1 && x.Kind == UnitKind.Creature);
        u.Effects.Add(new Effect { Id = Battle.GatedEffect, Positive = true, Dispellable = false });
        u.Atb = 1;
        Assert.True(b.ExeTurnStartTail(u));
        Assert.False(u.HasEffect(Battle.GatedEffect));
        Assert.Equal(Math.Clamp(f.Db.Stats.Get("combat/HeroSkills/DemonLord/Gating/GatedStartATB", 0), 0, 1), u.Atb, 6);
    }
    // exe FUN_00c50920: every defiler whose 0x146 links hold the caster counts in N(g); a defiler's turn start
    // (FUN_00bbc8e0) removes the shared effect and clears its links. 00c50b38..00c50b74: the caster term is 1 for a
    // creature with a tier (record +0x8c) or a base creature (+0x100).
    [Fact]
    public void EveryLinkedDefilerCounts()
    {
        var b = f.Duel("CREATURE_GOBLIN_DEFILER", 50, "CREATURE_DRUID", 20, 1, new Cell(2, 5), new Cell(9, 5));
        var g2 = BattleSetup.AddCreature(f.Db, b.State, 0, "CREATURE_GOBLIN_DEFILER", 30, new Cell(2, 8), summoned: false);
        b.Begin();
        var g1 = b.State.Units[0];
        var dr = b.State.Units.First(u => u.CreatureId == "CREATURE_DRUID");
        foreach (var g in new[] { g1, g2 })
        {
            b.State.Active = g.Id;
            var r = b.Apply(new UseAbilityCmd(g.Id, "ABILITY_DEFILE_MAGIC", dr.Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
        }
        Assert.Equal(2, b.DefilersOf(dr).Count);
        Assert.Equal(1, b.DefileCasterTerm(dr));
        b.ExeTurnStartTail(g1);
        Assert.False(dr.HasEffect("FX_DEFILED"));
    }
}


