using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Combat spell rules confirmed against H5_Game.exe (docs/rules/audit/combat_spells.md).</summary>
[Collection("game")]
public class AuditSpellTests(GameDataFixture f)
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

    // exe FUN_008a4330 multiplies the base initiative by one factor per effect (FUN_004e4270): Haste 1+v,
    // Wound 1−Wound_InitiativeDecrease — so Expert Haste (0.4) with a Wound gives ×1.4×0.8, not ×(1+0.4−0.2).
    [Fact]
    public void InitiativeEffectFactorsMultiply()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var u = s.Units.Single(x => x.Side == 0 && x.CreatureId == "CREATURE_FOOTMAN");
        double baseIni = b.InitiativeOf(u);
        b.AddEffect(u, new Effect { Id = "SPELL_HASTE", Value = 0.4, Remaining = 5, Positive = true });
        b.AddEffect(u, new Effect { Id = Fx.Wound, Remaining = 2 });
        Assert.Equal(baseIni * 1.4 * 0.8, b.InitiativeOf(u), 6);
    }

    // exe FUN_004e4270 case 0x18: Expediter (own hero spec HERO_SPEC_HASTE) adds level·Expediter_HasteBonusPerLevel to Haste.
    [Fact]
    public void ExpediterBoostsHaste()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var u = s.Units.Single(x => x.Side == 0 && x.CreatureId == "CREATURE_FOOTMAN");
        double baseIni = b.InitiativeOf(u);
        b.AddEffect(u, new Effect { Id = "SPELL_HASTE", Value = 0.2, Remaining = 5, Positive = true });
        s.Heroes[0].Specialization = "HERO_SPEC_HASTE";
        s.Heroes[0].Level = 10;
        Assert.Equal(baseIni * (1 + 0.2 + 10 * 0.01), b.InitiativeOf(u), 5);
    }

    // exe 00976c0d / 00976c53: spell base damage and the empowered ×1.5 are converted in chop mode (truncated).
    [Fact]
    public void SpellBaseDamageTruncates()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var arrow = f.Db.Spell("SPELL_MAGIC_ARROW")!;
        var emp = f.Db.Spell("SPELL_EMPOWERED_MAGIC_ARROW") ?? arrow;
        Assert.Equal(133, b.SpellBaseDamage(arrow, arrow, Mastery.Expert, 7.7));      // 72 + 8·7.7 = 133.6
        if (emp != arrow) Assert.Equal(199, b.SpellBaseDamage(arrow, emp, Mastery.Expert, 7.625));   // 133·1.5 = 199.5
    }

    // exe FUN_0097d600 / 0097d2a0: protection becomes a truncated whole percent of a float share — Protection alone
    // (0.85f) gives 14 %, so 100 damage becomes 86 (fan manual p.321).
    [Fact]
    public void ProtectionPercentIsTruncated()
    {
        Assert.Equal(86, Battle.ApplyProtectionPercent(100, 0.85, false, Mastery.None));
        Assert.Equal(50, Battle.ApplyProtectionPercent(100, 0.5, false, Mastery.None));
        // Irresistible Magic: immune / resisted targets take (table 00f46f18) 20/40/50/75 %.
        Assert.Equal(50, Battle.ApplyProtectionPercent(100, 1, true, Mastery.Expert));
        Assert.Equal(20, Battle.ApplyProtectionPercent(100, 1, true, Mastery.Basic));
        // 72 % protection with Expert Irresistible Magic → 36 % → 64 damage (fan manual p.322 example).
        Assert.Equal(64, Battle.ApplyProtectionPercent(100, 0.28, false, Mastery.Expert));
    }

    // exe FUN_0097c9f0 tail: Resistance +15, Impregnable level+5, Plate Mail of Stability +20, Boots of Interference +10,
    // Aura of Magic Resistance → trunc(clamp(0.3+1e-4)·100 + sum); clamped to 100.
    [Fact]
    public void MagicResistanceChanceSumsLikeTheExe()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var u = s.Units.Single(x => x.Side == 0 && x.CreatureId == "CREATURE_FOOTMAN");
        Assert.Equal(0, b.MagicResistanceChance(u));
        s.Heroes[0].Perks.Add("HERO_SKILL_RESISTANCE");
        s.Heroes[0].Artifacts.Add("PLATE_MAIL_OF_STABILITY");
        s.Heroes[0].Artifacts.Add("BOOTS_OF_INTERFERENCE");
        Assert.Equal(45, b.MagicResistanceChance(u));
        s.Heroes[0].Specialization = "HERO_SPEC_IMPREGNABLE";
        s.Heroes[0].Level = 60;
        Assert.Equal(100, b.MagicResistanceChance(u));
    }

    // exe FUN_00976db0 (spell 0xd, spec 0x2a): the Disrupter bonus applies to SPELL_DISRUPTING_RAY ("Vulnerability").
    [Fact]
    public void DisrupterDamagesWithDisruptingRay()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 200) });
        s.Heroes[0].Specialization = "HERO_SPEC_DISRUPTER";
        s.Heroes[0].Level = 10;
        s.Heroes[0].Spells.Add("SPELL_DISRUPTING_RAY");
        s.Heroes[0].Mana = 100;
        var b = new Battle(f.Db, s);
        b.Begin();
        var target = s.Units.Single(x => x.Side == 1 && x.CreatureId == "CREATURE_PEASANT");
        // Everyone else defends until the hero's turn comes up.
        for (int i = 0; i < 40 && s.Active != Actor.Hero(0) && s.Active is not null; i++)
        {
            int a = s.Active!.Value;
            int side = Actor.IsHero(a) ? Actor.HeroSide(a) : s.Unit(a)!.Side;
            Assert.True(b.Apply(new DefendCmd(a) { Side = side }).Ok);
        }
        Assert.Equal(Actor.Hero(0), s.Active);
        int hpBefore = target.TotalHealth;
        var r = b.Apply(new CastSpellCmd(Actor.Hero(0), "SPELL_DISRUPTING_RAY", target.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        int expected = (int)((10 - 1) * 10 / 3.0 + 10);   // trunc((lvl−1)·PerLevel/trunc(Div) + Base) = 40
        int dealt = hpBefore - target.TotalHealth;
        // The peasants' own resistance/protection (none here) is the only thing between the formula and the result.
        Assert.True(r.Events.OfType<SpellResisted>().Any() || dealt == expected, $"dealt {dealt}, expected {expected}");
    }

    // exe 00a34836: Arcane Training min(cost·0.8, cost−1) is truncated — a 7-mana spell costs 5, not 6.
    [Fact]
    public void ArcaneTrainingCostTruncates()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var h = s.Heroes[0];
        h.Perks.Add("HERO_SKILL_ARCANE_TRAINING");
        var sp = f.Db.Spells.Values.First(x => x.ManaCost == 7 && x.School is "MAGIC_SCHOOL_DARK" or "MAGIC_SCHOOL_LIGHT" or "MAGIC_SCHOOL_DESTRUCTIVE" or "MAGIC_SCHOOL_SUMMONING");
        Assert.Equal(5, b.HeroManaCost(h, sp));
    }

    // Warcry bonus structs (RPGStats FUN_00946030; formula used by 008a1e50 for Battlecry speed):
    // ((level−1)/LevelDivisor)·PerDividedLevel + Base — level 8 Rallying Cry gives +1 morale, not +2.
    [Fact]
    public void WarcryBonusesUseLevelMinusOne()
    {
        var b = new Battle(f.Db, Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) }));
        Assert.Equal(1, b.WarcryLevelBonus("combat/Spells/Warcries/RallingCry_MoraleBonus", 8));
        Assert.Equal(2, b.WarcryLevelBonus("combat/Spells/Warcries/RallingCry_MoraleBonus", 9));
        Assert.Equal(2, b.WarcryLevelBonus("combat/Spells/Warcries/Battlecry_OffenceBonus", 4));
        Assert.Equal(1, b.WarcryLevelBonus("combat/Spells/Warcries/Battlecry_SpeedBonus", 30));
    }

    // exe FUN_0097d7b0 (0x27): Call of the Wasp multiplies the Wasp Swarm ATB drop by 1 + ((level+2)/3)·perLevel3.
    [Fact]
    public void CallOfTheWaspMultipliesTheAtbDrop()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var wasp = f.Db.Spell("SPELL_WASP_SWARM")!;
        Assert.Equal(0.6, b.WaspAtbDrop(wasp, Mastery.Expert, 5, s.Heroes[0]), 6);
        s.Heroes[0].Specialization = "HERO_SPEC_CALL_OF_THE_WASP";
        s.Heroes[0].Level = 10;
        double per = f.Db.Stats.Get("combat/HeroSpecializations/Ranger/CallOfTheWasp_ATBReducePerLevel3", 0.02);
        Assert.Equal(0.6 * (1 + 4 * (double)(float)per), b.WaspAtbDrop(wasp, Mastery.Expert, 5, s.Heroes[0]), 5);
    }

    // exe FUN_0097c9f0: the target hero's Dwarven Mithral pieces and Ring of Death make its army immune to one spell each.
    [Fact]
    public void ArtifactsGrantSpellImmunities()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var u = s.Units.Single(x => x.Side == 0 && x.CreatureId == "CREATURE_FOOTMAN");
        Assert.False(b.ArtifactSpellImmunity(u, "SPELL_SLOW"));
        s.Heroes[0].Artifacts.Add("DWARVEN_MITHRAL_GREAVES");
        s.Heroes[0].Artifacts.Add("ARTIFACT_RING_OF_DEATH");
        Assert.True(b.ArtifactSpellImmunity(u, "SPELL_SLOW"));
        Assert.True(b.ArtifactSpellImmunity(u, "SPELL_HOLY_WORD"));
        Assert.False(b.ArtifactSpellImmunity(u, "SPELL_BLIND"));
    }

    // exe FUN_0097c9f0 (spell 0x2a): Raise Dead cannot target Elemental or Mechanical creatures.
    [Fact]
    public void RaiseDeadSkipsElementalsAndMechanicals()
    {
        var s = Setup(new[] { ("CREATURE_IRON_GOLEM", 10), ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var golem = s.Units.Single(x => x.CreatureId == "CREATURE_IRON_GOLEM");
        var raise = f.Db.Spell("SPELL_ANIMATE_DEAD")!;
        golem.Count = 5;
        Assert.Equal("cannot raise this creature", b.ValidateSpellTarget(Actor.Hero(0), 0, raise, Mastery.Expert, golem.Id, null));
    }

    // ---- Chain Lightning (exe FUN_0085edb0 / FUN_00a5dbb0) and Elemental Chains (FUN_00977e60 / FUN_004b0d80)

    BattleState ChainSetup(params (int side, string id, int x, int y)[] units)
    {
        var mine = units.Where(u => u.side == 0).Select(u => (u.id, 10)).ToArray();
        var theirs = units.Where(u => u.side == 1).Select(u => (u.id, 10)).ToArray();
        var s = Setup(mine, theirs);
        s.Units.RemoveAll(u => u.IsWarMachine);
        foreach (var side in new[] { 0, 1 })
        {
            var placed = s.Units.Where(u => u.Side == side).ToList();
            var specs = units.Where(u => u.side == side).ToList();
            for (int i = 0; i < specs.Count; i++) placed[i].Pos = new Cell(specs[i].x, specs[i].y);
        }
        return s;
    }

    // 0085e2f0: stacks with protection > 99 % (magic immunity) are never chosen as a hop.
    [Fact]
    public void ChainLightningSkipsFullyProtectedStacks()
    {
        var s = ChainSetup((0, "CREATURE_PEASANT", 3, 3), (1, "CREATURE_BLACK_DRAGON", 4, 3), (1, "CREATURE_PEASANT", 8, 3));
        var b = new Battle(f.Db, s);
        var a = s.Units.Single(u => u.Side == 0);
        var dragon = s.Units.Single(u => u.CreatureId == "CREATURE_BLACK_DRAGON");
        var far = s.Units.Single(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT");
        var chain = b.ChainLightningTargets(a, f.Db.Spell("SPELL_CHAIN_LIGHTNING")!, 1);
        Assert.Equal(new[] { a.Id, far.Id }, chain.Select(u => u.Id).ToArray());
        Assert.DoesNotContain(dragon, chain);
    }

    // FUN_00a5dbb0 collects the equally near stacks and picks one with the battle RNG (not the lowest id).
    [Fact]
    public void ChainLightningBreaksTiesWithTheRng()
    {
        var seen = new HashSet<int>();
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var s = ChainSetup((0, "CREATURE_PEASANT", 5, 5), (1, "CREATURE_PEASANT", 7, 5), (1, "CREATURE_ARCHER", 5, 7));
            s.Rng = new Rng(seed);
            var b = new Battle(f.Db, s);
            var a = s.Units.Single(u => u.Side == 0);
            seen.Add(b.ChainLightningTargets(a, f.Db.Spell("SPELL_CHAIN_LIGHTNING")!, 1)[1].Id);
        }
        Assert.Equal(2, seen.Count);
    }

    // 0085ef1f / @00e0ab70: the float base halves per hop and each hop's base is truncated.
    [Fact]
    public void ChainLightningHopBasesHalveAndTruncate()
    {
        Assert.Equal(new[] { 101, 50, 25, 12 }, Battle.ChainHopBases(101, 4));
        Assert.Equal(new[] { 7, 3, 1, 0 }, Battle.ChainHopBases(7, 4));
    }

    // FUN_004b0d80: m = 1 + Invocation·0.1 + altars·0.1 (single precision), ×2 with Absolute Chains; the extra is
    // trunc(D·m − D) after protection (chop at 00977f58): 100 damage with m = 1.4f gives 39, not 40.
    [Fact]
    public void ElementalChainsUseTheExeMultiplier()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var h = s.Heroes[0];
        var t = s.Units.Single(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT");
        var bolt = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;   // Air → Earth targets
        h.Class = "HERO_CLASS_WARLOCK";
        h.Perks.Add(Sk.ElementalVision);
        h.Skills.RemoveAll(x => x.SkillId == Sk.Invocation);
        h.Skills.Add(new SkillEntry(Sk.Invocation, Mastery.Expert));
        h.ElementalAltars = 1;
        t.Element1 = Elements.Earth;
        Assert.Equal(39, b.ElementalChainExtra(h, bolt, t, 100));
        t.Element1 = Elements.Fire;
        Assert.Equal(0, b.ElementalChainExtra(h, bolt, t, 100));      // not opposite
        h.Perks.Add("HERO_SKILL_ABSOLUTE_CHAINS");
        t.Element1 = Elements.Earth;
        Assert.Equal(79, b.ElementalChainExtra(h, bolt, t, 100));     // m = 1.8f
        h.Class = "HERO_CLASS_KNIGHT";
        t.Element1 = Elements.Earth;
        Assert.Equal(0, b.ElementalChainExtra(h, bolt, t, 100));      // Warlocks only (class 6)
    }

    // FUN_004e5b70 / FUN_00989790 case 0xca: Master of Fire stores (defence + existing crush) / 2 when applied and the
    // defence sum subtracts it, so re-applying does not halve again; Armored stacks are immune.
    [Fact]
    public void MasterOfFireCrushIsAFixedHalfOfTheDefence()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var u = s.Units.Single(x => x.Side == 0 && x.CreatureId == "CREATURE_FOOTMAN");
        int d = b.DefenceOf(u);
        b.ApplyArmorCrush(u, 1);
        Assert.Equal(d - d / 2, b.DefenceOf(u));
        b.ApplyArmorCrush(u, 1);
        Assert.Equal(d - d / 2, b.DefenceOf(u));
    }

    // 004e68ca..004e694b: Set Afire burns for trunc(D · 0.33f) of the damage dealt, D first divided back by the Mark of
    // Fire multiplier (2) when the target is marked.
    [Fact]
    public void SetAfireBurnsAShareOfTheDamageDealt()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var t = s.Units.Single(x => x.Side == 1 && x.CreatureId == "CREATURE_PEASANT");
        Assert.Equal(33, b.SetAfireDamage(t, 100));
        b.AddEffect(t, new Effect { Id = Fx.MarkOfFire, Remaining = 2 });
        Assert.Equal(16, b.SetAfireDamage(t, 100));     // 100 / 2 = 50 → trunc(50 · 0.33f) = 16
    }
}
