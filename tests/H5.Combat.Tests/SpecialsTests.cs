namespace H5.Combat.Tests;

[Collection("game")]
public class SpecialsTests(GameDataFixture f)
{
    [StockFact]
    public void GriffinSpecialisationResolvesFromGameText()
    {
        var ving = f.Db.DuelPresets[0].Hero.Shared!;
        Assert.Equal("HERO_SPEC_GRIFFINS", ving.Specialization);
        var family = Specials.FamilyFromDescription(f.Db, ving.SpecializationDescription);
        Assert.Contains("CREATURE_GRIFFIN", family);
        Assert.Contains("CREATURE_ROYAL_GRIFFIN", family);
        Assert.Contains("CREATURE_BATTLE_GRIFFIN", family);
    }

    [StockFact]
    public void EveryCreatureSpecialisationTextMapsToCreatures()
    {
        foreach (var h in f.Db.Heroes.Values)
        {
            if (!h.SpecializationDescription.Contains("to their Attack and Defen", StringComparison.OrdinalIgnoreCase)) continue;
            if (!h.SpecializationDescription.Contains("for every two levels")) continue;
            var family = Specials.FamilyFromDescription(f.Db, h.SpecializationDescription);
            Assert.True(family.Count >= 2, $"{h.Id} {h.Specialization}: '{h.SpecializationDescription}' → [{string.Join(",", family)}]");
        }
    }

    [StockFact]
    public void SpecialisationBonusAppliedToStacks()
    {
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 1, skipTactics: true);
        var griffin = s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_ROYAL_GRIFFIN");
        var def = f.Db.Creature("CREATURE_ROYAL_GRIFFIN");
        Assert.Equal(def.Attack + 8, griffin.BaseAttack);    // level 15 → +8
    }

    [StockFact]
    public void DragonSetBonusesFromData()
    {
        // Preset 3 wears four Power of Dragons pieces → +1 to all hero stats.
        var preset = f.Db.DuelPresets[3].Hero;
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 1, skipTactics: true);
        var h = s.Heroes[1];
        int pieces = h.SetPieces.GetValueOrDefault(Specials.Dragonish);
        Assert.True(pieces >= 2);
        int artifactSp = preset.Artifacts.Sum(a => f.Db.Artifacts[a].SpellPower);
        Assert.Equal(preset.SpellPower + artifactSp + (pieces >= 6 ? 4 : 1), h.SpellPower);
    }
}

[Collection("game")]
public class AdvancedMechanicsTests(GameDataFixture f)
{
    /// <summary>Fan manual p.321: 100 Scouts and 1 Hydra; the Scouts die → the Hydra gains +2 Attack.</summary>
    [Fact]
    public void EnragedBonusFollowsPowerShare()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new("CREATURE_SCOUT", 100), new("CREATURE_HYDRA", 1) } },
            new ArmySetup { Army = { new("CREATURE_PEASANT", 10) } }, 1, Array.Empty<Cell>(), skipTactics: true);
        var b = new Battle(f.Db, s);
        var scouts = s.Units.First(u => u.CreatureId == "CREATURE_SCOUT");
        var hydra = s.Units.First(u => u.CreatureId == "CREATURE_HYDRA");
        int before = b.AttackOf(hydra);
        b.DealDamage(scouts, scouts.TotalHealth, -1, AttackKind.Other, "test");
        Assert.Equal(before + 2, b.AttackOf(hydra));
    }

    /// <summary>Fan manual p.323: 10 Shieldguards next to 20 Shieldguards get +3 (Offensive Formation).</summary>
    [Fact]
    public void DwarvenFormationBonus()
    {
        var hero = new H5.Core.Data.HeroInstance { Level = 10, Perks = { Sk.OffensiveFormation } };
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = hero, Army = { new("CREATURE_STOUT_DEFENDER", 10), new("CREATURE_STOUT_DEFENDER", 20) } },
            new ArmySetup { Army = { new("CREATURE_PEASANT", 10) } }, 1, Array.Empty<Cell>(), skipTactics: true);
        var b = new Battle(f.Db, s);
        var small = s.Units[0]; var big = s.Units[1];
        small.Pos = new Cell(3, 3); big.Pos = new Cell(4, 3);
        Assert.Equal(small.BaseAttack + 3, b.AttackOf(small));
        Assert.Equal(big.BaseAttack, b.AttackOf(big));
    }

    [Fact]
    public void BlindedUnitSkipsTurnAndRetaliationIsBlocked()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_FOOTMAN", 10, posA: new Cell(4, 4), posB: new Cell(5, 4));
        var a = b.State.Units[0]; var t = b.State.Units[1];
        b.AddEffect(t, new Effect { Id = "SPELL_BLIND", Remaining = 5, Mastery = H5.Core.Data.Mastery.Expert });
        b.Begin();
        // The blinded stack never becomes the active actor.
        for (int i = 0; i < 3 && b.State.Phase == BattlePhase.Combat; i++)
        {
            Assert.NotEqual(t.Id, b.State.Active);
            if (b.State.Active == a.Id) b.Apply(new DefendCmd(a.Id) { Side = 0 });
        }
    }
}
