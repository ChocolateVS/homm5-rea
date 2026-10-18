using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Hero-side combat rules confirmed against H5_Game.exe (docs/rules/audit/combat_heroes.md).</summary>
[Collection("game")]
public class AuditHeroTests(GameDataFixture f)
{
    BattleState Setup((string id, int count)[] mine, (string id, int count)[] theirs, string? arena = "Grass_Big_01", bool heroes = false, int? terrain = null)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = heroes ? f.Db.DuelPresets[0].Hero : null, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = heroes ? f.Db.DuelPresets[3].Hero : null, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: arena);
        if (terrain is int tt) s.Terrain = tt;
        return s;
    }

    static Unit Of(BattleState s, string id) => s.Units.First(u => u.CreatureId == id);

    // exe FUN_008a4800 / FUN_008a22f0: creatures on their race's native terrain get +1 attack and +1 defence
    // (battlefield vfunc 0x54(race); manual p.260: grass is native to Haven and Sylvan).
    [Fact]
    public void NativeTerrainGivesPlusOneAttackAndDefence()
    {
        var grass = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        Assert.Equal(CombatArenaTable.TerrainGrass, grass.Terrain);
        var b = new Battle(f.Db, grass);
        var footman = Of(grass, "CREATURE_FOOTMAN");
        var imp = Of(grass, "CREATURE_IMP");
        Assert.Equal(footman.BaseAttack + 1, b.AttackOf(footman));
        Assert.Equal(footman.BaseDefence + 1, b.DefenceOf(footman));
        Assert.Equal(imp.BaseAttack, b.AttackOf(imp));       // Inferno: native on lava, not grass
        Assert.Equal(imp.BaseDefence, b.DefenceOf(imp));

        grass.Terrain = CombatArenaTable.TerrainLava;
        Assert.Equal(imp.BaseAttack + 1, b.AttackOf(imp));
        Assert.Equal(footman.BaseAttack, b.AttackOf(footman));
    }

    // exe FUN_008a22f0 (FPU truncation, +0.001, minimum 1): the defend bonus is computed on base + hero + Prayer +
    // native terrain only; spells such as Stone Skin are added afterwards and are not multiplied.
    [Fact]
    public void DefendBonusIsTruncatedOnTheBaseDefenceOnly()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) }, terrain: CombatArenaTable.TerrainLava);
        var b = new Battle(f.Db, s);
        var footman = Of(s, "CREATURE_FOOTMAN");
        int baseDef = footman.BaseDefence;          // not native on lava
        footman.Defending = true;
        int bonus = Math.Max(1, (int)(baseDef * f.Db.Stats.Get("combat/Creatures/DefenceBonusOnDefend", 0.3) + 0.001));
        Assert.Equal(baseDef + bonus, b.DefenceOf(footman));

        footman.Effects.Add(new Effect { Id = "SPELL_STONESKIN", Value = 4, Remaining = 3 });
        Assert.Equal(baseDef + bonus + 4, b.DefenceOf(footman));
    }

    // exe FUN_008a22f0: a defending ABILITY_TAKE_ROOTS creature uses DefenceBonusOnTakeRoots INSTEAD of the defend
    // factor (the two never stack).
    [Fact]
    public void TakeRootsReplacesTheDefendFactor()
    {
        var s = Setup(new[] { ("CREATURE_TREANT_GUARDIAN", 5) }, new[] { ("CREATURE_IMP", 10) }, terrain: CombatArenaTable.TerrainLava);
        var b = new Battle(f.Db, s);
        var treant = Of(s, "CREATURE_TREANT_GUARDIAN");
        Assert.True(treant.Has(Ab.TakeRoots));
        treant.Defending = true;
        treant.Effects.Add(new Effect { Id = Fx.TakeRoots, Positive = true });
        int d = treant.BaseDefence;
        int expected = d + Math.Max(1, (int)(d * f.Db.Stats.Get("combat/Creatures/Sylvan/DefenceBonusOnTakeRoots", 0.5) + 0.001));
        Assert.Equal(expected, b.DefenceOf(treant));
    }

    // exe FUN_008a22f0: the defend bonus is at least 1 (max(1, trunc(d·0.3 + 0.001))).
    [Fact]
    public void DefendBonusIsAtLeastOne()
    {
        var s = Setup(new[] { ("CREATURE_PEASANT", 10) }, new[] { ("CREATURE_IMP", 10) }, terrain: CombatArenaTable.TerrainLava);
        var b = new Battle(f.Db, s);
        var peasant = Of(s, "CREATURE_PEASANT");
        peasant.Defending = true;
        Assert.Equal(peasant.BaseDefence + 1, b.DefenceOf(peasant));
    }

    // exe 00a58de0: the Offence mastery switch has cases 1..3 only — a mastery value of 4 gives no damage bonus.
    [Fact]
    public void OffenceBonusOnlyForMasteriesOneToThree()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 100) }, heroes: true);
        var b = new Battle(f.Db, s);
        var a = Of(s, "CREATURE_FOOTMAN"); var t = s.Units.First(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT");
        var h = s.Heroes[0];
        int Dmg(Mastery m)
        {
            h.Skills.RemoveAll(x => x.SkillId == Sk.Offence);
            if (m != Mastery.None) h.Skills.Add(new SkillEntry(Sk.Offence, m));
            return b.ComputeDamage(a, t, new Battle.Hit { Kind = AttackKind.Melee, FixedRoll = 10 }, ranged: false);
        }
        int none = Dmg(Mastery.None);
        Assert.True(Dmg(Mastery.Expert) > none);
        Assert.Equal(none, Dmg(Mastery.Ultimate));
    }

    // exe 00a6ba00 (good) / 00a6b8f0 (bad): |morale| clamped to 5, success iff Range(1,100) < 10·|morale|
    // (inclusive range: 9 % at ±1, 49 % at ±5 — not the manual's flat 10 % per point).
    [Fact]
    public void MoraleRollUsesTheInclusiveRangeAndClampsAtFive()
    {
        var s = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        var b = new Battle(f.Db, s);
        foreach (int m in new[] { 1, -1, 3, -4, 5, 8, -9 })
            for (int i = 0; i < 200; i++)
            {
                var r = s.Rng;
                var probe = new Rng { S0 = r.S0, S1 = r.S1, S2 = r.S2, S3 = r.S3 };
                bool expected = probe.Range(1, 100) < 10 * Math.Min(5, Math.Abs(m));
                Assert.Equal(expected, b.MoraleRoll(m));
            }
        int hits = 0, n = 20000;
        for (int i = 0; i < n; i++) if (b.MoraleRoll(1)) hits++;
        Assert.InRange(hits / (double)n, 0.075, 0.105);   // 9 %
    }

    // exe FUN_004bc470: Wyvern Tamer gives creatures whose base creature is CREATURE_WYVERN
    // +((level−1)/LevelDivisor)·PerDividedLevel + Base HP (data 1/1/2 → 2·level − 1).
    [Fact]
    public void WyvernTamerHealthUsesLevelMinusOne()
    {
        var s = Setup(new[] { ("CREATURE_PEASANT", 10) }, new[] { ("CREATURE_IMP", 10) }, heroes: true);
        s.Heroes[0].Specialization = "HERO_SPEC_WYVERN_TAMER";
        s.Heroes[0].Level = 10;
        foreach (var id in new[] { "CREATURE_WYVERN", "CREATURE_WYVERN_POISONOUS", "CREATURE_WYVERN_PAOKAI" })
        {
            var u = BattleSetup.AddCreature(f.Db, s, 0, id, 3, new Cell(0, 0), summoned: false);
            int extra = s.Heroes[0].HasPerk(Sk.Toughness) ? 2 : 0;
            Assert.Equal(f.Db.Creature(id).Health + 2 * 10 - 1 + extra, u.BaseHealth);
        }
    }

    // exe FUN_004bc470: the Defender specialisation adds level/5 HP to Defenders and their upgrades.
    [Fact]
    public void DefenderSpecialisationAddsLevelOverFiveHealth()
    {
        var s = Setup(new[] { ("CREATURE_PEASANT", 10) }, new[] { ("CREATURE_IMP", 10) }, heroes: true);
        s.Heroes[0].Specialization = "HERO_SPEC_DEFENDER";
        s.Heroes[0].Level = 14;
        int extra = s.Heroes[0].HasPerk(Sk.Toughness) ? 2 : 0;
        foreach (var id in new[] { "CREATURE_DEFENDER", "CREATURE_STOUT_DEFENDER" })
        {
            var u = BattleSetup.AddCreature(f.Db, s, 0, id, 3, new Cell(0, 0), summoned: false);
            Assert.Equal(f.Db.Creature(id).Health + 14 / 5 + extra, u.BaseHealth);
        }
        var other = BattleSetup.AddCreature(f.Db, s, 0, "CREATURE_BEAR_RIDER", 3, new Cell(0, 0), summoned: false);
        Assert.Equal(f.Db.Creature("CREATURE_BEAR_RIDER").Health + extra, other.BaseHealth);
    }

    // exe FUN_004d8c10: a side qualifies with Tactics unless the enemy hero is a Flagbearer of Darkness
    // (HERO_SPEC_FLAGBEARER_OF_DARKNESS, spec 0x11); the third column goes to a side only if exactly one qualifies.
    [Fact]
    public void TacticsColumnsFollowTheOriginalRule()
    {
        HeroState H(bool tactics, string spec = "") => new() { Present = true, Specialization = spec,
            Perks = tactics ? new List<string> { Sk.Tactics } : new List<string>() };
        const string flag = "HERO_SPEC_FLAGBEARER_OF_DARKNESS";
        Assert.Equal((3, 2), BattleSetup.TacticsColumns(H(true), H(false)));
        Assert.Equal((2, 2), BattleSetup.TacticsColumns(H(true), H(true)));            // both cancelled
        Assert.Equal((2, 2), BattleSetup.TacticsColumns(H(true), H(false, flag)));     // Flagbearer denies it
        Assert.Equal((2, 3), BattleSetup.TacticsColumns(H(true), H(true, flag)));      // only the Flagbearer qualifies
        Assert.Equal((2, 2), BattleSetup.TacticsColumns(H(false, flag), H(false)));
    }
}
