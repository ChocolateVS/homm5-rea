using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Creature abilities confirmed against H5_Game.exe in docs/rules/audit/combat_sweep_3.md.</summary>
[Collection("game")]
public class AuditSweep3Tests(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    static ApplyResult Use(Battle b, Unit u, string ability, Unit? t = null)
    {
        b.State.Active = u.Id;
        return b.Apply(new UseAbilityCmd(u.Id, ability) { Side = u.Side, TargetId = t?.Id });
    }

    Battle WithHeroes(string mine, int n, string theirs, int m)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = { new ArmySlot(mine, n) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = { new ArmySlot(theirs, m) } },
            3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        return new Battle(f.Db, s);
    }

    // exe 004ba9d0 (base morale: Undead → 0, Mechanical → Artificial Glory ? max(0, base) : 0, Bravery ≥ 1), 00a6acd0
    // (effects; Mechanical returns before the Bravery minimums) and the roll gate 00a6ba00 (Undead only): ELEMENTAL is never
    // tested, so elementals take their hero's morale like living stacks. The engine forced it to 0.
    [Fact]
    public void ElementalsTakeMorale()
    {
        var b = WithHeroes("CREATURE_FIRE_ELEMENTAL", 10, "CREATURE_PEASANT", 10);
        b.State.Heroes[0].Morale = 3;
        var el = b.State.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_FIRE_ELEMENTAL");
        var zombie = Add(b, 0, "CREATURE_ZOMBIE", 10, new Cell(1, 1));
        Assert.True(b.MoraleOf(el) > 0, $"elemental morale {b.MoraleOf(el)}");
        Assert.Equal(0, b.MoraleOf(zombie));
    }

    // exe 004ba9d0 (004baa6f): a Mechanical stack whose hero has Artificial Glory gets max(0, base) as its base morale,
    // then the effect sum, then max(0, ·) — returned before the Bravery minimums (00a6acd0 @ 00a6ae7b). A −2 base with a
    // +2 effect is +2 (the engine summed first: 0).
    [Fact]
    public void ArtificialGloryClampsTheGolemBaseMorale()
    {
        var b = WithHeroes("CREATURE_IRON_GOLEM", 10, "CREATURE_PEASANT", 10);
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.ArtificialGlory);
        h.Morale = -2;
        var golem = b.State.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_IRON_GOLEM");
        Assert.Equal(0, b.MoraleOf(golem));
        golem.Effects.Add(new Effect { Id = Fx.RallyingCry, Value = 2, Remaining = 2 });
        Assert.Equal(2, b.MoraleOf(golem));
    }

    // exe FUN_00977a80 (00977b57..00977c5f): before protection the spell damage is truncated after × MarkOfFire_Fire-
    // DamageMultiplier (fire spell on a marked target) and after × VulnerabilityDamageCoef (flag 0x40 from an adjacent
    // Marble Gargoyle). The engine folded both into the protection share, which is clamped to 1, so neither applied.
    [Fact]
    public void VulnerabilityAndMarkOfFireMultiplyTheSpellDamage()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 900, 1, new Cell(1, 1), new Cell(8, 5));
        var pe = b.State.Units[1];
        var fireball = f.Db.Spell("SPELL_FIREBALL")!;
        Assert.Equal(100, b.DealSpellDamageFor(pe, fireball, 100, 0));
        Add(b, 0, "CREATURE_MARBLE_GARGOYLE", 5, new Cell(9, 5));
        double coef = (float)f.Db.Stats.Get("combat/Creatures/Academy/VulnerabilityDamageCoef", 1.5);
        Assert.Equal((int)(coef * 100), b.DealSpellDamageFor(pe, fireball, 100, 0));
        pe.Effects.Add(new Effect { Id = Fx.MarkOfFire, Remaining = 2 });
        double mark = (float)f.Db.Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2);
        Assert.Equal((int)(coef * (int)(mark * 100)), b.DealSpellDamageFor(pe, fireball, 100, 0));
    }

    // exe FUN_00a60be0: Flamestrike's Mark of Fire goes through FUN_00bbf160 with the cast record +0x38 = 0, so
    // FUN_0097f610 case 0xf6 refuses a target with Immunity to Fire (or to Magic); the damage itself is FUN_00a5c790 with a
    // fixed luck multiplier of 1.0 and no strike effects.
    [Fact]
    public void FlamestrikeMarkSkipsFireImmuneTargets()
    {
        var b = f.Duel("CREATURE_WARLORD", 10, "CREATURE_FIRE_ELEMENTAL", 10, 1, new Cell(1, 1), new Cell(8, 5));
        b.Begin();
        var u = b.State.Units[0]; var el = b.State.Units[1];
        var r = Use(b, u, Ab.Flamestrike, el);
        Assert.True(r.Ok, r.Error);
        Assert.False(el.HasEffect(Fx.MarkOfFire));
        var b2 = f.Duel("CREATURE_WARLORD", 10, "CREATURE_PEASANT", 200, 1, new Cell(1, 1), new Cell(8, 5));
        b2.Begin();
        Assert.True(Use(b2, b2.State.Units[0], Ab.Flamestrike, b2.State.Units[1]).Ok);
        Assert.True(b2.State.Units[1].HasEffect(Fx.MarkOfFire));
    }

    // exe FUN_0097f610 case 0xaa: Harm Touch refuses Crystal Scales and MECHANICAL targets (golems; war machines answer
    // HasAbility(9) too, FUN_00a75160).
    [Fact]
    public void HarmTouchRefusesMechanicalTargets()
    {
        var b = f.Duel("CREATURE_WRAITH", 5, "CREATURE_IRON_GOLEM", 10, 1, new Cell(5, 5), new Cell(6, 5));
        b.Begin();
        Assert.False(Use(b, b.State.Units[0], Ab.HarmTouch, b.State.Units[1]).Ok);
    }

    // exe FUN_0097f610 case 0xad (0097f8be..0097f93e): Repair needs something to repair — a wounded top creature or, for a
    // creature stack, fewer creatures than at the start; a stack at full strength is refused (the charge is kept).
    [Fact]
    public void RepairNeedsADamagedTarget()
    {
        var b = f.Duel("CREATURE_MASTER_GREMLIN", 20, "CREATURE_PEASANT", 10, 1, new Cell(1, 1), new Cell(12, 9));
        var golem = Add(b, 0, "CREATURE_IRON_GOLEM", 5, new Cell(2, 1));
        golem.InitialCount = golem.Count;
        b.Begin();
        var g = b.State.Units[0];
        Assert.False(Use(b, g, Ab.Repair, golem).Ok);
        golem.TopHealth -= 3;
        var r = Use(b, g, Ab.Repair, golem);
        Assert.True(r.Ok, r.Error);
    }

    // exe FUN_00b61950 case 0x45: ABILITY_CASTER is derived from the known spells (school ≠ SPECIAL / NONE, Dark Ritual
    // excluded; RUNIC / WARCRIES count); no creature lists it. Defile Magic (FUN_0097f610 case 0x146) needs it on a creature
    // target, and the creature initiative artifacts (004bc31e / 004bc380: Dragon Bone Greaves / Teeth Necklace) test it.
    [Fact]
    public void CasterIsDerivedFromTheKnownSpells()
    {
        bool C(string id) => Battle.ExeCasterSpells(f.Db, f.Db.Creatures[id].KnownSpells.Select(k => k.Spell));
        Assert.True(C("CREATURE_MAGI"));
        Assert.False(C("CREATURE_GENIE"));            // SPELL_ABILITY_RANDOM_CAST_DARK only
        Assert.False(C("CREATURE_MASTER_GREMLIN"));   // SPELL_ABILITY_REPAIR only
        var b = f.Duel("CREATURE_GOBLIN_DEFILER", 20, "CREATURE_GENIE", 5, 1, new Cell(1, 1), new Cell(8, 5));
        b.Begin();
        Assert.False(Use(b, b.State.Units[0], Ab.DefileMagic, b.State.Units[1]).Ok);
        var b2 = f.Duel("CREATURE_GOBLIN_DEFILER", 20, "CREATURE_MAGI", 5, 1, new Cell(1, 1), new Cell(8, 5));
        b2.Begin();
        var r = Use(b2, b2.State.Units[0], Ab.DefileMagic, b2.State.Units[1]);
        Assert.True(r.Ok, r.Error);
        var h = new HeroState { Present = true };
        h.Artifacts.Add("DRAGON_TEETH_NECKLACE");
        var magi = f.Db.Creatures["CREATURE_MAGI"];
        Assert.Equal((float)(110 * magi.Initiative * 0.01f), (float)SkillAuditH.CreatureInitiative(f.Db, h, magi, "WEEK_OF_NOTHING"), 3);
    }
}
