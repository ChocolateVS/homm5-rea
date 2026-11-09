using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Combat audit sweep 14, adventure side of two perks (docs/rules/audit/combat_sweep_14.md).</summary>
[Collection("adv")]
public class AuditSweep14Tests(Fixture f)
{
    static HeroState Hero(Engine.Adventure g)
    {
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.PendingLevelUps.Clear();
        return h;
    }

    // exe 00b54770 case 0xb5 (00b55815..00b55980): once per hero (+0x2ac), min(120, (day0 / 7 + 1) · 15) creatures of
    // FUN_0045b8e0(army, CREATURE_GOBLIN): the first upgrade when a slot is empty, else the upgrades (reversed) then
    // the goblin the army already holds; none → nothing.
    [Fact]
    public void DefendUsAllGivesGoblinsByTheWeekOnce()
    {
        var g = f.NewGame("A2M1");
        var h = Hero(g);
        var gob = g.Core.Creatures["CREATURE_GOBLIN"];
        Assert.NotEmpty(gob.Upgrades);
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_PEASANT", 5);
        g.S.Day = 15;                                       // day counter 14 → week 3 → 45
        int def = h.Defence;
        g.ScriptLearnPerk(h, "HERO_SKILL_DEFEND_US_ALL");
        Assert.Equal(def + 2, h.Defence);
        Assert.Equal(45, h.Army.Live().Where(s => s.Creature == gob.Upgrades[0]).Sum(s => s.Count));

        // A full army that holds plain goblins gets them as goblins; the reward is given only once.
        var h2 = h;
        h2.Perks.Remove("HERO_SKILL_DEFEND_US_ALL");
        h2.PerkRewardsClaimed.Remove("HERO_SKILL_DEFEND_US_ALL");   // as a hero that never had the perk
        h2.Army = Army.Empty();
        string[] ids = { "CREATURE_PEASANT", "CREATURE_ARCHER", "CREATURE_FOOTMAN", "CREATURE_GOBLIN", "CREATURE_GRIFFIN", "CREATURE_PRIEST", "CREATURE_CAVALIER" };
        for (int i = 0; i < Army.Slots; i++) h2.Army[i] = new Stack(ids[i], 1);
        g.S.Day = 200;                                      // (199 / 7 + 1) · 15 = 435 → 120
        g.ScriptLearnPerk(h2, "HERO_SKILL_DEFEND_US_ALL");
        Assert.Equal(121, h2.Army[3]!.Count);
        h2.Perks.Remove("HERO_SKILL_DEFEND_US_ALL");                // learned again (Memory Mentor): the flag stays
        g.ScriptLearnPerk(h2, "HERO_SKILL_DEFEND_US_ALL");
        Assert.Equal(121, h2.Army[3]!.Count);
    }

    // exe 00b54770 case 0xba (00b559a7..00b55a3f): vf 0x200(0x160bd300, 0, {[4] = Bodybuilding_HPBonusPercent}) unless
    // vf 0x1fc already holds it — a next-battle hit point bonus (FUN_004bc470 via hero vf 0x330), not added twice.
    [Fact]
    public void BodybuildingAddsANextBattleHitPointBonus()
    {
        var g = f.NewGame("A2M1");
        var h = Hero(g);
        h.NextBattleHitPoints = 0;
        h.VisitedSinceBattle.Clear();
        int pct = (int)g.Core.Stats.Get("combat/HeroSkills/Barbarian/Bodybuilding_HPBonusPercent", 10);
        g.ScriptLearnPerk(h, "HERO_SKILL_BODYBUILDING");
        Assert.Equal(pct, h.NextBattleHitPoints);
        h.Perks.Remove("HERO_SKILL_BODYBUILDING");
        g.ScriptLearnPerk(h, "HERO_SKILL_BODYBUILDING");
        Assert.Equal(pct, h.NextBattleHitPoints);
    }
}
