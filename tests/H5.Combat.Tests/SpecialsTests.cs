namespace H5.Combat.Tests;

[Collection("game")]
public class SpecialsTests(GameDataFixture f)
{
    [Fact]
    public void GriffinSpecialisationResolvesFromGameText()
    {
        var ving = f.Db.DuelPresets[0].Hero.Shared!;
        Assert.Equal("HERO_SPEC_GRIFFINS", ving.Specialization);
        var family = Specials.FamilyFromDescription(f.Db, ving.SpecializationDescription);
        Assert.Contains("CREATURE_GRIFFIN", family);
        Assert.Contains("CREATURE_ROYAL_GRIFFIN", family);
        Assert.Contains("CREATURE_BATTLE_GRIFFIN", family);
    }

    [Fact]
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

    [Fact]
    public void SpecialisationBonusAppliedToStacks()
    {
        var s = BattleSetup.CreateDuel(f.Db, 0, 3, 1, skipTactics: true);
        var griffin = s.Units.First(u => u.Side == 0 && u.CreatureId == "CREATURE_ROYAL_GRIFFIN");
        var def = f.Db.Creature("CREATURE_ROYAL_GRIFFIN");
        Assert.Equal(def.Attack + 8, griffin.BaseAttack);    // level 15 → +8
    }

    [Fact]
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
