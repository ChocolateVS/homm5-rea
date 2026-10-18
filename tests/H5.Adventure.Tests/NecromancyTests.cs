using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Necromancy raise after battle, H5_Game.exe FUN_00c26040 / FUN_00c264d0 / FUN_00c25f90.</summary>
[Collection("adv")]
public class NecromancyTests(Fixture f)
{
    static HeroState Necromancer(Engine.Adventure g, H5.Core.Data.Mastery mastery)
    {
        var h = g.S.Heroes.First(x => x.Alive && x.Army.StackCount() >= 1);
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_NECROMANCY");
        if (mastery != H5.Core.Data.Mastery.None) h.Skills.Add(new H5.Core.Data.SkillEntry("HERO_SKILL_NECROMANCY", mastery));
        h.Perks.Remove("HERO_SKILL_LORD_OF_UNDEAD");
        h.Artifacts.Clear();
        h.Owner = -2;   // own no towns → no amplifiers / obelisks
        return h;
    }

    // exe FUN_00c26040: percent = RaisePercentBase (10) + RaisePercentPerSkillLevel (10) x mastery (+ pendant 10).
    [Fact]
    public void RaisePercentFollowsTheData()
    {
        var g = f.NewGame();
        var h = Necromancer(g, H5.Core.Data.Mastery.Advanced);
        Assert.Equal(10 + 10 * 2, g.NecromancyPercent(h));
        h.Artifacts.Add("ARTIFACT_NECROMANCER_PENDANT");
        Assert.Equal(10 + 10 * 2 + 10, g.NecromancyPercent(h));
    }

    // exe FUN_00c264d0: raised = min(killed, pct x Health(dead) x killed / 100 / Health(risen)); TransformTable picks
    // the risen creature; undead (Necropolis) dead are never raised (FUN_00c25f90, TOWN_NECROMANCY = 7).
    [Fact]
    public void RaiseConvertsKilledHitPointsIntoTheTransformCreature()
    {
        var g = f.NewGame();
        var h = Necromancer(g, H5.Core.Data.Mastery.Basic);
        for (int i = 0; i < h.Army.Length; i++) h.Army[i] = null;
        h.Army[0] = new Stack("CREATURE_SKELETON", 1);
        int pct = g.NecromancyPercent(h);
        var footman = f.Core.Creatures["CREATURE_FOOTMAN"];
        var zombie = f.Core.Creatures["CREATURE_ZOMBIE"];
        Assert.Equal("CREATURE_ZOMBIE", g.NecromancyRiseOf("CREATURE_FOOTMAN"));
        Assert.Null(g.NecromancyRiseOf("CREATURE_ZOMBIE"));

        var raised = g.NecromancyRaise(h, new[] { ("CREATURE_FOOTMAN", 50), ("CREATURE_ZOMBIE", 50) });
        int expected = (int)Math.Min(50, pct * (long)footman.Health * 50 / 100 / zombie.Health);
        Assert.Single(raised);
        Assert.Equal(("CREATURE_ZOMBIE", expected), raised[0]);
        Assert.Equal(expected, h.Army.Live().Where(s => s.Creature == "CREATURE_ZOMBIE").Sum(s => s.Count));
    }

    // exe FUN_00c25ff0: no Necromancy mastery, no raise.
    [Fact]
    public void NoRaiseWithoutNecromancy()
    {
        var g = f.NewGame();
        var h = Necromancer(g, H5.Core.Data.Mastery.None);
        Assert.Empty(g.NecromancyRaise(h, new[] { ("CREATURE_FOOTMAN", 50) }));
    }
}
