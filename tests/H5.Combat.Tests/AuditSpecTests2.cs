using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>More specialisation / skill rules confirmed against H5_Game.exe (docs/rules/AUDIT_COMBAT.md).</summary>
[Collection("game")]
public class AuditSpecTests2(GameDataFixture f)
{
    BattleState Setup(string spec, int level, (string id, int count)[] mine, (string id, int count)[] theirs, string? arena = "Grass_Big_01")
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: arena);
        s.Heroes[0].Specialization = spec;
        s.Heroes[0].Level = level;
        return s;
    }

    // exe FUN_00861120: grass battlefields give Air or Earth elementals.
    [Fact]
    public void GrassArenaSummonsAirOrEarthElementals()
    {
        var s = Setup("", 10, new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        Assert.Equal(CombatArenaTable.TerrainGrass, s.Terrain);
        var b = new Battle(f.Db, s);
        var seen = Enumerable.Range(0, 40).Select(_ => b.TerrainElemental()).ToHashSet();
        Assert.Subset(new HashSet<string> { "CREATURE_AIR_ELEMENTAL", "CREATURE_EARTH_ELEMENTAL" }, seen);
        Assert.Equal(2, seen.Count);
    }

    // exe FUN_00a5b440: Soulhunter raises min(count, level) Manes when an enemy stack dies.
    [Fact]
    public void SoulhunterRaisesManesFromDeadEnemies()
    {
        var s = Setup("HERO_SPEC_SOULHUNTER", 12, new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 30), ("CREATURE_ARCHER", 5) });
        var b = new Battle(f.Db, s);
        b.Begin();
        var peasants = s.Units.Single(u => u.CreatureId == "CREATURE_PEASANT" && u.Side == 1);
        b.Kill(peasants, -1);
        var manes = s.Units.Single(u => u.CreatureId == "CREATURE_MANES");
        Assert.Equal(0, manes.Side);
        Assert.Equal(12, manes.Count);
    }

    // exe FUN_00a5a5a0: Master of Elements summons elementals when one of its own stacks dies.
    [StockFact]
    public void MasterOfElementsSummonsOnOwnLosses()
    {
        var s = Setup("HERO_SPEC_MASTER_OF_ELEMENTS", 7, new[] { ("CREATURE_FOOTMAN", 20), ("CREATURE_ARCHER", 5) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        b.Begin();
        var footmen = s.Units.Single(u => u.CreatureId == "CREATURE_FOOTMAN");
        b.Kill(footmen, -1);
        var elem = s.Units.Single(u => u.CreatureId.EndsWith("_ELEMENTAL"));
        double hp = f.Db.Creature(elem.CreatureId).Health;
        int expected = Math.Max(1, (int)Math.Round(20 * footmen.BaseHealth * 0.25 / hp, MidpointRounding.ToEven)) + (7 - 1) / 3 * 1 + 1;
        Assert.Equal(expected, elem.Count);
        Assert.Equal(0, elem.Side);
    }

    // exe FUN_008a4330: Prudent lowers enemy initiative by level × 1 %.
    [StockFact]
    public void PrudentSlowsEnemies()
    {
        var s = Setup("", 20, new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var peasant = s.Units.Single(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT");
        double before = b.InitiativeOf(peasant);
        s.Heroes[0].Specialization = "HERO_SPEC_PRUDENT";
        Assert.Equal(before * 0.8, b.InitiativeOf(peasant), 3);
    }

    // exe FUN_008a0ea0: Death Tread +SpeedBonus for the siege attacker only.
    [Fact]
    public void DeathTreadOnlyInSieges()
    {
        var s = Setup("", 10, new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var b = new Battle(f.Db, s);
        var footman = s.Units.Single(u => u.Side == 0 && u.CreatureId == "CREATURE_FOOTMAN");
        s.Heroes[0].Perks.Add("HERO_SKILL_DEATH_TREAD");
        int open = b.SpeedOf(footman);
        s.Heroes[0].Perks.Remove("HERO_SKILL_DEATH_TREAD");
        Assert.Equal(open, b.SpeedOf(footman));    // no effect outside a siege
    }
}

[Collection("game")]
public class AuditRageTests(GameDataFixture f)
{
    // exe FUN_008a2c20: Demon Hunter adds ((lvl-1)/1)*2 + 10 rage per event while demons are on the field.
    [Fact]
    public void DemonHunterSpeedsUpRageWhenDemonsFight()
    {
        var b = f.Duel("CREATURE_GOBLIN", 20, "CREATURE_IMP", 20);
        var gob = b.State.Units[0];
        if (!gob.Abilities.Contains("ABILITY_DEMON_RAGED")) gob.Abilities.Add("ABILITY_DEMON_RAGED");
        var h = b.State.Heroes[0];
        h.Present = true; h.Level = 6; h.Specialization = "HERO_SPEC_DEMON_HUNTER";
        int before = gob.RagePoints;
        b.AddRage(gob, 20);
        Assert.Equal(before + 20 + (6 - 1) * 2 + 10, gob.RagePoints);
    }
}
