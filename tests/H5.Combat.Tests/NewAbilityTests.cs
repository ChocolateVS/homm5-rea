using H5.Core.Data;

namespace H5.Combat.Tests;

[Collection("game")]
public class NewAbilityTests(GameDataFixture f)
{
    [Fact]
    public void TreacheryChangesSidesBelowThirtyPercent()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 50, "CREATURE_GOBLIN", 100, posA: new Cell(4, 4), posB: new Cell(5, 4));
        var goblins = b.State.Units[1];
        Assert.True(goblins.Has(Ab.Treachery));
        b.DealDamage(goblins, goblins.MaxHealthCache * 65, 0, AttackKind.Melee, null);   // 35 left: stays
        Assert.Equal(1, goblins.Side);
        b.DealDamage(goblins, goblins.MaxHealthCache * 10, 0, AttackKind.Melee, null);   // 25 left: betrays
        Assert.Equal(0, goblins.Side);
        b.DealDamage(goblins, goblins.MaxHealthCache * 5, 1, AttackKind.Melee, null);    // only once
        Assert.Equal(0, goblins.Side);
    }

    [Fact]
    public void PackHunterWolvesJoinTheAttack()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot("CREATURE_WOLF", 20), new ArmySlot("CREATURE_WOLF", 20) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 500) } },
            3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        s.Units[0].Pos = new Cell(4, 4); s.Units[1].Pos = new Cell(4, 5); s.Units[2].Pos = new Cell(5, 4);
        var b = new Battle(f.Db, s);
        var events = new List<BattleEvent>();
        b.Begin();
        // Force the first wolf stack to act.
        s.Active = s.Units[0].Id;
        var r = b.Apply(new AttackCmd(s.Units[0].Id, s.Units[2].Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events, e => e is AbilityTriggered { Ability: Ab.PackHunter } at && at.UnitId == s.Units[1].Id);
    }

    [Fact]
    public void PowerFeedRaisesHeroSpellPowerUpToKnowledge()
    {
        var s = BattleSetup.CreateDuel(f.Db, 6, 3, 5, skipTactics: true, arena: "Grass_Big_01");
        var b = new Battle(f.Db, s);
        var h = s.Heroes[0];
        var druid = BattleSetup.AddCreature(f.Db, s, 0, "CREATURE_HIGH_DRUID", 40, new Cell(0, 0), summoned: false);
        var sp = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        double before = b.HeroSpellPower(h, sp);
        h.Links[Ab.PowerFeed] = druid.Id;
        double after = b.HeroSpellPower(h, sp);
        Assert.Equal(Math.Min(Battle.CreatureSpellPower(40, druid.WeeklyGrowth), h.Knowledge), after - before, 3);
        druid.Count = 0;
        Assert.Equal(before, b.HeroSpellPower(h, sp), 3);
    }

    [Fact]
    public void EldritchAuraBlocksMageAttacksOnNeighbours()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot("CREATURE_MAGI", 20) } },
            new ArmySetup { Army = { new ArmySlot("CREATURE_COMBAT_MAGE", 10), new ArmySlot("CREATURE_PEASANT", 50) } },
            9, obstacles: Array.Empty<Cell>(), skipTactics: true);
        if (!s.Units[0].Has(Ab.MagicAttack) || !s.Units[1].Has(Ab.EldritchAura)) return;   // data guard
        s.Units[0].Pos = new Cell(0, 0); s.Units[1].Pos = new Cell(10, 4); s.Units[2].Pos = new Cell(10, 5);
        var b = new Battle(f.Db, s);
        b.Begin();
        s.Active = s.Units[0].Id;
        int hp = s.Units[2].TotalHealth;
        var r = b.Apply(new ShootCmd(s.Units[0].Id, s.Units[2].Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events, e => e is Missed { Reason: "eldritch_aura" });
    }
}
