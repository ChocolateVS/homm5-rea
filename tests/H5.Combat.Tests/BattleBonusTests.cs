using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>The hero's next-battle bonus struct (exe hero vfunc 0x330) read by the creature stat getters.</summary>
[Collection("game")]
public class BattleBonusTests(GameDataFixture f)
{
    Unit Footman(int[] bonus)
    {
        var hero = f.Db.DuelPresets[0].Hero;
        var hi = new HeroInstance { Shared = hero.Shared, Level = hero.Level, BattleBonus = bonus };
        var s = BattleSetup.Create(f.Db, new ArmySetup { Hero = hi, Army = { new ArmySlot("CREATURE_FOOTMAN", 20) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 20) } }, 3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        return s.Units.Single(u => u.CreatureId == "CREATURE_FOOTMAN");
    }

    [Fact]
    public void HitPointsPercentAttackDefenceSpeedAndInitiative()
    {
        var plain = Footman(new int[7]);
        var b = Footman(new[] { 0, 0, 3, 2, 10, 1, 1 });
        // 004bc470: (clamp(100 + [4], 0, 1000) · hp + 50) / 100.
        Assert.Equal((110 * plain.BaseHealth + 50) / 100, b.BaseHealth);
        Assert.Equal(plain.BaseAttack + 3, b.BaseAttack);
        Assert.Equal(plain.BaseDefence + 2, b.BaseDefence);
        Assert.Equal(plain.BaseSpeed + 1, b.BaseSpeed);
        Assert.True(b.BaseInitiative > plain.BaseInitiative);
    }
}
