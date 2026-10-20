using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Magic and rune hero perks confirmed against H5_Game.exe (docs/rules/audit/combat_sweep_4.md).</summary>
[Collection("game")]
public class AuditSweep4Tests(GameDataFixture f)
{
    Battle Make((string id, int count)[] mine, (string id, int count)[] theirs, ulong seed = 7)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); h.SetPieces.Clear(); }
        return new Battle(f.Db, s);
    }

    static int Hp(Unit u) => (u.Count - 1) * u.MaxHealthCache + u.TopHealth;

    // exe FUN_00b4b440 @ 00b4b4c1..00b4b50e: Power of Haste / Stone raise the mastery to Advanced when the BASE spell
    // (FUN_00a34110: mass → base) is Haste (0x18) / Stoneskin (0x19) — the single-target spells too, not only the mass ones.
    [Fact]
    public void PowerOfHasteAndStoneRaiseTheSingleSpellsToo()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var h = b.State.Heroes[0];
        h.Skills.RemoveAll(s => s.SkillId == Sk.Light);
        h.Perks.Add(Sk.PowerOfSpeed);
        h.Perks.Add(Sk.PowerOfEndurance);
        Assert.Equal(Mastery.Advanced, b.HeroMastery(h, f.Db.Spell("SPELL_HASTE")!));
        Assert.Equal(Mastery.Advanced, b.HeroMastery(h, f.Db.Spell("SPELL_MASS_HASTE")!));
        Assert.Equal(Mastery.Advanced, b.HeroMastery(h, f.Db.Spell("SPELL_STONESKIN")!));
        Assert.Equal(Mastery.None, b.HeroMastery(h, f.Db.Spell("SPELL_BLESS")!));
    }

    // exe FUN_00983790 @ 00983ab3..00983b6c, FUN_004e5cb0, FUN_0097d600: Warlock's Luck is one roll per cast for the spells
    // of FUN_004e5cb0 (Holy Word too, not Plague; Disrupting Ray only for the Disrupter); the multiplier (> 1 only) scales
    // every target's damage after protection, chopped.
    [Fact]
    public void WarlocksLuckIsOneRollPerCastOverTheDamageList()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var h = b.State.Heroes[0];
        Assert.True(Battle.WarlocksLuckApplies("SPELL_HOLY_WORD", h));
        Assert.True(Battle.WarlocksLuckApplies("SPELL_LAND_MINE", h));
        Assert.False(Battle.WarlocksLuckApplies("SPELL_PLAGUE", h));
        Assert.False(Battle.WarlocksLuckApplies("SPELL_DISRUPTING_RAY", h));
        h.Specialization = "HERO_SPEC_DISRUPTER";
        Assert.True(Battle.WarlocksLuckApplies("SPELL_DISRUPTING_RAY", h));
        Assert.Equal(15, Battle.ApplyWarlocksLuck(7, 2.25));
        Assert.Equal(20, Battle.ApplyWarlocksLuck(10, 2));

        // Fireball over a peasant crowd: at most one luck event per cast (one roll for all targets); bad luck never fires.
        int lucky = 0;
        for (ulong seed = 1; seed <= 40; seed++)
            foreach (int luck in new[] { 5, -5 })
            {
                var c = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 500), ("CREATURE_PEASANT", 500) }, seed);
                var ev = c.Begin();
                var ch = c.State.Heroes[0];
                ch.Perks.Add(Sk.WarlocksLuck);
                ch.Luck = luck;
                var p1 = c.State.Units.First(u => u.Side == 1);
                ev.Clear();
                c.ResolveSpell(Actor.Hero(0), 0, f.Db.Spell("SPELL_FIREBALL")!, Mastery.Expert, 10, null, p1.Pos, fromHero: true);
                int procs = ev.OfType<LuckProc>().Count();
                Assert.True(procs <= 1);
                if (luck < 0) Assert.Equal(0, procs);
                lucky += procs;
            }
        Assert.InRange(lucky, 5, 35);
    }

    // exe FUN_00983790 @ 00983e41..00983ec4 → FUN_009834b0: Mark of the Wizard casts an aimed, non-mass spell once more on
    // the marked stack — also when it was the target — and that cast is paid again (vf 0x220 / vf 0x224 = FUN_00bbf610);
    // without the mana for it, there is no second cast.
    [Fact]
    public void MarkOfTheWizardRecastsOnTheMarkedStackAndPaysAgain()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 500), ("CREATURE_PEASANT", 500) });
        b.Begin();
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.MarkOfWizard);
        if (!h.Spells.Contains("SPELL_MAGIC_ARROW")) h.Spells.Add("SPELL_MAGIC_ARROW");
        var arrow = f.Db.Spell("SPELL_MAGIC_ARROW")!;
        int cost = b.HeroManaCost(h, arrow);
        var (a, t) = (b.State.Units.First(u => u.Side == 1), b.State.Units.Last(u => u.Side == 1));
        h.Links[Sk.MarkOfWizard] = a.Id;

        h.Mana = 100;
        int ha = Hp(a), ht = Hp(t);
        b.State.Active = Actor.Hero(0);
        Assert.True(b.Apply(new CastSpellCmd(Actor.Hero(0), "SPELL_MAGIC_ARROW", t.Id) { Side = 0 }).Ok);
        Assert.True(Hp(a) < ha && Hp(t) < ht);
        Assert.Equal(100 - 2 * cost, h.Mana);

        // Target = marked stack: two casts on it. With mana for one cast only: one cast.
        h.Mana = cost;
        ha = Hp(a);
        b.State.Active = Actor.Hero(0);
        Assert.True(b.Apply(new CastSpellCmd(Actor.Hero(0), "SPELL_MAGIC_ARROW", a.Id) { Side = 0 }).Ok);
        int single = ha - Hp(a);
        Assert.Equal(0, h.Mana);
        Assert.True(single > 0);
    }

    // exe FUN_00bbfbf0 (hero interface vf 0x2c, from FUN_00a6cf90 / FUN_00a6cd80): Empathy moves the hero ±0.1 on good / bad
    // morale, capped at 1.25; 3+ Lions set pieces give the same.
    [Fact]
    public void EmpathyWorksBothWaysCappedAtOneAndAQuarter()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var h = b.State.Heroes[0];
        var u = b.State.Units.First(x => x.Side == 0);
        h.Atb = 0.5;
        b.EmpathyOnMorale(u, good: false);
        Assert.Equal(0.5, h.Atb);
        h.Perks.Add(Sk.Empathy);
        b.EmpathyOnMorale(u, good: false);
        Assert.Equal(0.4, h.Atb, 5);
        h.Atb = 1.2;
        b.EmpathyOnMorale(u, good: true);
        Assert.Equal(1.25, h.Atb, 5);
        h.Perks.Remove(Sk.Empathy);
        h.SetPieces[Specials.Lions] = 3;
        h.Atb = 0.5;
        b.EmpathyOnMorale(u, good: true);
        Assert.Equal(0.6, h.Atb, 5);
    }

    // exe FUN_00c562f0: Might over Magic stores the barbarian's spell power / 2 (integer) as effect 0x135 on the enemy hero,
    // subtracted by the spell power getter (00bc01d5). The engine subtracted a fractional half.
    [Fact]
    public void MightOverMagicSubtractsTheIntegerHalf()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var (h, e) = (b.State.Heroes[0], b.State.Heroes[1]);
        var bolt = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        h.SpellPower = 20;
        e.SpellPower = 5;
        double plain = b.HeroSpellPower(h, bolt);
        e.Perks.Add(Sk.MightOverMagic);
        Assert.Equal(plain - 2, b.HeroSpellPower(h, bolt));
    }

    // exe 00983790 @ 0098410f..00984190: Sun Fire deals trunc(level · clamp(SunFireDamagePerHeroLevel, 0.1, 1000)) to the
    // casting creature straight through FUN_00a616e0 — fire-immune casters are hurt too.
    [Fact]
    public void SunFireIgnoresFireImmunity()
    {
        var b = Make(new[] { ("CREATURE_FIRE_ELEMENTAL", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var el = b.State.Units.First(x => x.Side == 0);
        var e = b.State.Heroes[1];
        e.Perks.Add(Sk.ManaBurst);
        e.Level = 7;
        int before = Hp(el);
        b.ManaBurst(el);
        Assert.Equal(70, before - Hp(el));
    }

    // exe 0097f610 (cases 0xbd / 0x160): the Plague Tent cannot target war machines, Mechanical, Elemental or Undead stacks.
    [Fact]
    public void PlagueTentRefusesUndead()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_SKELETON", 20) });
        b.Begin();
        var tent = BattleSetup.AddWarMachine(f.Db, b.State, 0, "WAR_MACHINE_FIRST_AID_TENT");
        b.State.Heroes[0].Perks.Add(Sk.PlagueTent);
        var sk = b.State.Units.First(x => x.Side == 1 && x.CreatureId == "CREATURE_SKELETON");
        b.State.Active = tent.Id;
        var r = b.Apply(new UseAbilityCmd(tent.Id, "PLAGUE_TENT", sk.Id) { Side = 0 });
        Assert.False(r.Ok);
    }

    // exe FUN_004e41c0 / FUN_004e3bc0 from FUN_0097c9f0: an incoming opposite spell against an Eternal Light effect gets a
    // resistance base of 50, plus the usual additions, in one roll.
    [Fact]
    public void EternalLightIsAFiftyPercentResistanceBase()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var own = b.State.Units.First(x => x.Side == 0);
        var (h, e) = (b.State.Heroes[0], b.State.Heroes[1]);
        var slow = f.Db.Spell("SPELL_SLOW")!;
        b.ResolveSpell(Actor.Hero(0), 0, f.Db.Spell("SPELL_HASTE")!, Mastery.Basic, 5, own, null, fromHero: true);
        Assert.True(own.HasEffect("SPELL_HASTE"));
        Assert.Equal(0, b.EternalLightResistance(own, slow));
        h.Perks.Add(Sk.EternalLight);
        Assert.Equal(50, b.EternalLightResistance(own, slow));
        Assert.Equal(0, b.EternalLightResistance(own, f.Db.Spell("SPELL_CURSE")!));
        int resisted = 0;
        for (int i = 0; i < 200; i++) if (!b.SpellHits(1, slow, own, hostile: true, e)) resisted++;
        Assert.InRange(resisted, 60, 140);
    }

    // exe FUN_008a11c0 → FUN_004e5f60: Detain <school> also lowers a creature caster's ATB after its cast.
    [Fact]
    public void DetainSlowsCreatureCasters()
    {
        var b = Make(new[] { ("CREATURE_DRUID", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var u = b.State.Units.First(x => x.Side == 0);
        var bolt = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        Assert.Equal(0, b.CreatureCastAtb(u, bolt));
        b.State.Heroes[1].Perks.Add(Sk.DetainDestructive);
        Assert.Equal(-0.25, b.CreatureCastAtb(u, bolt), 5);
        Assert.Equal(0, b.CreatureCastAtb(u, f.Db.Spell("SPELL_BLESS")!));
    }

    // exe FUN_00419730: Tap Runes restores round(knowledge · 0.5) mana (round half even), not the truncation.
    [Fact]
    public void TapRunesRounds()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 20) });
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.TapRunes);
        h.MaxMana = 100;
        (h.Knowledge, h.Mana) = (7, 0);
        b.TapRunes(h);
        Assert.Equal(4, h.Mana);
        (h.Knowledge, h.Mana) = (5, 0);
        b.TapRunes(h);
        Assert.Equal(2, h.Mana);
    }

    // exe FUN_004e5c20: Master of Storms clamps the target ATB to [0, 1] before the drop.
    [Fact]
    public void MasterOfStormsClampsTheAtbFirst()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 500) });
        b.State.Heroes[0].Perks.Add(Sk.MasterStorms);
        var p = b.State.Units.First(x => x.Side == 1);
        p.Atb = 1.2;
        b.ResolveSpell(Actor.Hero(0), 0, f.Db.Spell("SPELL_LIGHTNING_BOLT")!, Mastery.Basic, 1, p, null, fromHero: true);
        Assert.Equal(0.7, p.Atb, 5);
    }

    // exe 00984a00 case 0x42 (00985135..0098532d): Banish deals level·Unsummon_DamagePerHeroLevel + Summoning mastery ·
    // Unsummon_DamagePerSkillMastery to the summoned stacks of the OPPOSING ARMY (vf 0x10 → vf 0x64) — a hypnotised one too.
    [Fact]
    public void BanishHitsTheOpposingArmyEvenWhenHypnotised()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 500) });
        b.Begin();
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.Banish);
        h.Level = 5;
        var p = b.State.Units.First(x => x.Side == 1);
        p.Summoned = true;
        b.AddEffect(p, new Effect { Id = "SPELL_HYPNOTIZE", SourceSide = 0, Remaining = 3 });
        int before = Hp(p);
        b.State.Active = Actor.Hero(0);
        Assert.True(b.Apply(new UseAbilityCmd(Actor.Hero(0), "BANISH") { Side = 0 }).Ok);
        Assert.Equal(50 + 10 * (int)h.SkillLevel(Sk.Summoning), before - Hp(p));
    }
}
