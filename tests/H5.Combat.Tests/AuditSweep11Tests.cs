using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 11 of the combat audit: rows traced against H5_Game.exe (docs/rules/audit/combat_sweep_11.md).</summary>
[Collection("game")]
public class AuditSweep11Tests(GameDataFixture f)
{
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

    // exe FUN_004bad20: trunc(21·log10(inner) − 22 + 0.01) under chop (constants 00e15a80 = 0.01, 00e15a84 = 22,
    // 00e15a88 = 21). 29 creatures of growth 2: inner = 10 + 290 / 2 = 155, 21·log10(155) − 22 = 23.997 → 24 (the engine's
    // floor without the 0.01 gave 23).
    [Fact]
    public void CreatureSpellPowerAddsTheExeEpsilon()
    {
        Assert.Equal(24, Battle.CreatureSpellPower(29, 2));
        Assert.Equal(1, Battle.CreatureSpellPower(0, 2));
    }

    // exe FUN_008a3960 → FUN_008d9460(stack, 8): the demon-rage DEMON_RAGE_BONUS_SPELLPOWER_MULT FloatParam of the
    // stack's rage level (searched down to level 1), × the power, truncated. Sky Daughters (CREATURE_SHAMAN_WITCH) have
    // ×2 at rage level 2 and ×4 at level 3 in DefaultStats; level 1 has none.
    [Fact]
    public void DemonRageMultipliesCreatureSpellPower()
    {
        var (b, u, _) = Make("CREATURE_SHAMAN_WITCH", 20, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        int plain = Battle.CreatureSpellPower(20, Math.Max(1, u.WeeklyGrowth));
        u.RageLevel = 1;
        Assert.Equal(plain, b.CreatureCasterPower(u, u.Count));
        u.RageLevel = 2;
        Assert.Equal(2.0, b.RageSpellPowerMult(u));
        Assert.Equal(2 * plain, b.CreatureCasterPower(u, u.Count));
        u.RageLevel = 3;
        Assert.Equal(4 * plain, b.CreatureCasterPower(u, u.Count));
    }

    // exe FUN_0097b690 (HasSkill 0x5d) → FUN_0085eaa0 → FUN_0085e7c0: the consumed corpse explodes as a hero Fireball at
    // its cell — the record id 0x11f is replaced by 5 — with the power of the corpse (vf 0x244 at 0097b72a: count 0 →
    // FUN_004bad20 = 1) + 1 for a creature with a BaseCreature (FUN_004babd0 at 0097b76b). The engine used
    // SPELL_EFFECT_EXPLODING_CORPSES' own 1 + 1·hero spell power.
    [Fact]
    public void ExplodingCorpsesIsAFireballWithTheCorpsesPower()
    {
        var (b, corpse, peasants) = Make("CREATURE_MILITIAMAN", 5, "CREATURE_PEASANT", 200, new Cell(5, 5), new Cell(6, 5));
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.ConsumeCorpse);
        h.Perks.Add("HERO_SKILL_EXPLODING_CORPSES");
        b.Begin();
        corpse.Count = 0;
        Assert.Equal(2, b.CorpseExplosionPower(corpse));    // 1 (dead stack) + 1 (Militiaman's BaseCreature is the Peasant)
        var fb = f.Db.Spell("SPELL_FIREBALL")!;
        int expected = (int)b.SpellBaseDamage(fb, fb, b.HeroMastery(h, fb), 2);
        int before = peasants.TotalHealth;
        b.State.Active = Actor.Hero(0);
        h.Atb = 1.1;
        var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "CONSUME_CORPSE") { Side = 0, TargetId = corpse.Id });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(expected, before - peasants.TotalHealth);
    }

    [Fact]
    public void ABaseCreatureCorpseHasPowerOne()
    {
        var (b, corpse, _) = Make("CREATURE_PEASANT", 5, "CREATURE_PEASANT", 10, new Cell(5, 5), new Cell(14, 10));
        corpse.Count = 0;
        Assert.Equal(1, b.CorpseExplosionPower(corpse));
    }
}
