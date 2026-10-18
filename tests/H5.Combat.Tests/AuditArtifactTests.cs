using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Artifact grants in the creature HasAbility override (exe FUN_008a34c0).</summary>
[Collection("game")]
public class AuditArtifactTests(GameDataFixture f)
{
    [Fact]
    public void UnicornHornBowRemovesRangePenalty()
    {
        var hero = f.Db.DuelPresets[0].Hero;
        var withBow = new HeroInstance { Shared = hero.Shared, Level = hero.Level, Artifacts = { "UNICORN_HORN_BOW" } };
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = withBow, Army = { new ArmySlot("CREATURE_ARCHER", 20), new ArmySlot("CREATURE_FOOTMAN", 20) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 20) } }, 3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        Assert.True(s.Units.Single(u => u.CreatureId == "CREATURE_ARCHER").Has(Ab.NoRangePenalty));
        Assert.False(s.Units.Single(u => u.CreatureId == "CREATURE_FOOTMAN").Has(Ab.NoRangePenalty));
    }

    [Fact]
    public void ShawlOfTheGreatLichNegatesEnemyMindImmunity()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 10);
        var t = b.State.Units[1];
        t.Abilities.Add(Ab.ImmuneMind);
        Assert.True(b.MindImmune(t));
        b.State.Heroes[0].Present = true;
        b.State.Heroes[0].Artifacts.Add("ARTIFACT_SHAWL_OF_GREAT_LICH");
        Assert.False(b.MindImmune(t));
    }
}
