using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Retreat / surrender rules from H5_Game.exe CCombatArmy (docs/rules/audit/combat_heroes.md).</summary>
[Collection("game")]
public class AuditRetreatTests(GameDataFixture f)
{
    BattleState Setup(bool heroMine, bool heroTheirs, params (string id, int count)[] mine) =>
        BattleSetup.Create(f.Db,
            new ArmySetup { Hero = heroMine ? f.Db.DuelPresets[0].Hero : null, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = heroTheirs ? f.Db.DuelPresets[3].Hero : null, Army = new() { new ArmySlot("CREATURE_IMP", 10) } },
            7, skipTactics: true, arena: "Grass_Big_01");

    // exe FUN_004d9e20 reason 1: no hero → no retreat at all.
    [Fact]
    public void RetreatNeedsAHero()
    {
        var b = new Battle(f.Db, Setup(false, true, ("CREATURE_FOOTMAN", 10)));
        Assert.Equal(1, b.RetreatRefusal(0, surrender: false));
        b.Begin();
        Assert.False(b.Apply(new SurrenderCmd { Side = 0 }).Ok);
    }

    // exe FUN_004d9e20 reason 5: surrender needs a hero on the enemy side too (no ransom to neutrals); fleeing is fine.
    [Fact]
    public void SurrenderNeedsAnEnemyHero()
    {
        var b = new Battle(f.Db, Setup(true, false, ("CREATURE_FOOTMAN", 10)));
        Assert.Equal(5, b.RetreatRefusal(0, surrender: true));
        Assert.Equal(0, b.RetreatRefusal(0, surrender: false));
    }

    // exe FUN_004d9e20 reason 4 via FUN_00ca6680(7): artifact 7 (SHACKLES_OF_WAR) on EITHER hero forbids both.
    [Fact]
    public void ShacklesOfWarOnEitherHeroForbidsRetreat()
    {
        var s = Setup(true, true, ("CREATURE_FOOTMAN", 10));
        s.Heroes[1].Artifacts.Add("SHACKLES_OF_WAR");
        var b = new Battle(f.Db, s);
        Assert.Equal(4, b.RetreatRefusal(0, surrender: false));
        Assert.Equal(4, b.RetreatRefusal(0, surrender: true));
    }

    // exe FUN_004d9e20 reason 2: the army defending a town cannot retreat.
    [Fact]
    public void TownDefenderCannotRetreat()
    {
        var s = Setup(true, true, ("CREATURE_FOOTMAN", 10));
        s.Siege = new SiegeState();
        var b = new Battle(f.Db, s);
        Assert.Equal(2, b.RetreatRefusal(1, surrender: false));
        Assert.Equal(0, b.RetreatRefusal(0, surrender: false));
    }

    // exe FUN_004da8d0 / FUN_004da9a0: ransom = trunc(sum of creature gold cost x count / 2), summoned stacks excluded.
    [Fact]
    public void SurrenderCostIsHalfTheArmyGoldValue()
    {
        var s = Setup(true, true, ("CREATURE_FOOTMAN", 7), ("CREATURE_ARCHER", 3));
        var b = new Battle(f.Db, s);
        long value = 7L * f.Db.Creatures["CREATURE_FOOTMAN"].Cost.Gold + 3L * f.Db.Creatures["CREATURE_ARCHER"].Cost.Gold;
        Assert.Equal(value, b.ArmyGoldValue(0));
        Assert.Equal(value / 2, b.SurrenderCost(0));

        s.Units.First(u => u.CreatureId == "CREATURE_ARCHER").Summoned = true;
        Assert.Equal(7L * f.Db.Creatures["CREATURE_FOOTMAN"].Cost.Gold / 2, b.SurrenderCost(0));
    }

    // exe FUN_004d9e20 reason 6: only summoned/phantom stacks left → no surrender (fleeing still allowed).
    [Fact]
    public void SurrenderNeedsARealStack()
    {
        var s = Setup(true, true, ("CREATURE_FOOTMAN", 7));
        s.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN").IsClone = true;
        var b = new Battle(f.Db, s);
        Assert.Equal(6, b.RetreatRefusal(0, surrender: true));
        Assert.Equal(0, b.RetreatRefusal(0, surrender: false));
    }
}
