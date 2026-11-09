using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 13 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_13.md).</summary>
[Collection("game")]
public class AuditSweep13Tests(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    static ApplyResult Use(Battle b, Unit u, string ability, Unit? t = null, Cell? cell = null)
    {
        b.State.Active = u.Id;
        return b.Apply(new UseAbilityCmd(u.Id, ability) { Side = u.Side, TargetId = t?.Id, Cell = cell });
    }

    (Battle b, Unit a, Unit t) Make(string mine, int countA, string theirs, int countB, Cell posA, Cell posB, ulong seed = 7)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot(mine, countA) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot(theirs, countB) } },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        foreach (var u in s.Units.Where(u => u.IsWarMachine).ToList()) s.Units.Remove(u);
        var a = s.Units.First(u => u.Side == 0 && u.CreatureId == mine);
        var t = s.Units.First(u => u.Side == 1 && u.CreatureId == theirs);
        a.Pos = a.StartPos = posA;
        t.Pos = t.StartPos = posB;
        return (new Battle(f.Db, s), a, t);
    }

    // exe FUN_00982010 (side test 009823e3..00982424, own-caster refusal 0098247f) → FUN_0097f610 case 0x9d → FUN_0097a230:
    // a friendly living stack whose top creature is wounded (vf 0x1fc(1) = FUN_008a40c0) is a valid target and is healed
    // by 100 × the angels' count (FUN_009789b0 case 0x9d); an unwounded full stack, the caster, an enemy and an Undead
    // ally are not; a dead ally whose cells are taken is refused rather than moved (FUN_00977da0 / FUN_00b3f330).
    [Fact]
    public void ResurrectAlliesHealsALivingWoundedAlly()
    {
        var (b, angel, enemy) = Make("CREATURE_ARCHANGEL", 2, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var wounded = Add(b, 0, "CREATURE_FOOTMAN", 10, new Cell(1, 5));
        var full = Add(b, 0, "CREATURE_FOOTMAN", 10, new Cell(1, 8));
        var skel = Add(b, 0, "CREATURE_SKELETON", 10, new Cell(3, 8));
        wounded.TopHealth = 1;
        Assert.Null(b.ResurrectAlliesRefusal(angel, wounded));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, full));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, angel));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, enemy));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, skel));

        b.Begin();
        int before = wounded.TotalHealth;
        var r = Use(b, angel, Ab.ResurrectAllies, wounded);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Math.Min(wounded.InitialCount * wounded.MaxHealthCache, before + 200), wounded.TotalHealth);
    }

    [Fact]
    public void ResurrectAlliesRefusesADeadStackWhosePlaceIsTaken()
    {
        var (b, angel, _) = Make("CREATURE_ARCHANGEL", 2, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var dead = Add(b, 0, "CREATURE_FOOTMAN", 10, new Cell(1, 5));
        dead.Count = 0; dead.TopHealth = 0;
        Assert.Null(b.ResurrectAlliesRefusal(angel, dead));
        var other = Add(b, 0, "CREATURE_PEASANT", 5, new Cell(1, 5));
        Assert.NotNull(b.ResurrectAlliesRefusal(angel, dead));
        other.Pos = new Cell(5, 5);
        Assert.Null(b.ResurrectAlliesRefusal(angel, dead));
    }

    // exe FUN_00987cb1 casts through FUN_0097d7b0, which reads the caster's vf 0x240 (FUN_00a6a700: FUN_004bba60 — the
    // creature list's mastery, 2 when the spell is not listed — the week, then enemy Shatter FUN_004e6190) and vf 0x244
    // (FUN_00a6a7e0: FUN_008a3960, then Shatter / Weaken). The engine cast at a fixed Advanced with the bare creature power.
    [Fact]
    public void RandomCasterMasteryIsAdvancedLessTheEnemyShatter()
    {
        var genie = f.Db.Creatures.Values.First(c => c.Abilities.Contains(Ab.RandomCaster)).Id;
        var (b, a, _) = Make(genie, 20, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var curse = f.Db.Spell("SPELL_CURSE")!;
        var enemy = b.State.Heroes[1];
        enemy.Skills.RemoveAll(s => s.SkillId.StartsWith("HERO_SKILL_SHATTER"));
        Assert.Equal(Mastery.Advanced, b.CreatureCastMastery(a, curse));
        double plain = b.CreatureCastPower(a, curse);
        Assert.Equal(b.CreatureCasterPower(a, a.Count), (int)plain);
        enemy.Skills.Add(new SkillEntry(Sk.ShatterDark, Mastery.Expert));
        Assert.True(b.CreatureCastMastery(a, curse) < Mastery.Advanced);
        Assert.True(b.CreatureCastPower(a, curse) <= plain);
    }

    // exe hive vtable: vf 0x240 → FUN_00a6a700 (thunk 008b1e20) with primary vf 0x20 = FUN_008b0b50 (+0xe0 = SummonHive
    // Spell Mastery), vf 0x244 → FUN_00a6a7e0 (thunk 008b1e30) with primary vf 0x24 = FUN_008b07d0 (+0xc0, the caster's
    // power stored at the summon): the week and the enemy hero's Shatter apply to its Wasp Swarm.
    [Fact]
    public void HiveCastsAtItsStoredPowerLessTheEnemyShatter()
    {
        var (b, _, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var wasp = f.Db.Spell("SPELL_WASP_SWARM")!;
        var hive = new Unit { Id = 900, Side = 0, Pos = new Cell(3, 3), Size = 1, Count = 1, Power = 12 };
        var enemy = b.State.Heroes[1];
        enemy.Skills.RemoveAll(s => s.SkillId.StartsWith("HERO_SKILL_SHATTER"));
        Assert.Equal(Mastery.Expert, b.HiveCastMastery(hive, wasp));
        Assert.Equal(12, (int)b.HiveCastPower(hive, wasp));
        enemy.Skills.Add(new SkillEntry(Sk.ShatterSummoning, Mastery.Expert));
        Assert.True(b.HiveCastMastery(hive, wasp) < Mastery.Expert);
    }

    // exe FUN_0041a1a0 → FUN_00989120(1) after a rune cast by the cast event (flag 1); FUN_008a6c40 → FUN_00a6e400 →
    // FUN_00989130(1) at the stack's turn start; FUN_00989190 refuses while bit 1 is set, for the runes the stack holds a
    // record of (every rune of the hero's book on a Fortress stack, FUN_0041a250).
    [Fact]
    public void ARuneStackGetsOneRunePerTurnOfItsOwn()
    {
        var (b, u, _) = Make("CREATURE_DEFENDER", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var other = Add(b, 0, "CREATURE_FOOTMAN", 10, new Cell(1, 5));
        Assert.Equal(Towns.Fortress, u.Town);
        Assert.False(b.RuneUsedThisTurn(u.Id));
        b.MarkRuneTurn(u.Id); b.MarkRuneTurn(other.Id);
        Assert.True(b.RuneUsedThisTurn(u.Id));
        Assert.False(b.RuneUsedThisTurn(other.Id));   // no records outside Fortress
        b.Begin();
        typeof(Battle).GetMethod("BeginTurn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(b, new object[] { u.Id });
        Assert.False(b.RuneUsedThisTurn(u.Id));
    }
}

