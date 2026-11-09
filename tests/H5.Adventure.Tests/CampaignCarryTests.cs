using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Campaign carry-over (H5_Game.exe 0045edc0 record, 0045f460 save, 009aacb0 / SFLB_CombineEditablesStep 009a9c40 /
/// 009a9cf0 apply with mask 0xec, 00460e80 / 0045fbc0 extra-hero sort).
/// </summary>
[Collection("adv")]
public class CampaignCarryTests(Fixture f)
{
    (Engine.Adventure G, HeroState H) Game()
    {
        var map = f.Map("A2M1");
        var g = Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 1, 5)).Game;
        return (g, g.S.Heroes.First(h => h.Alive));
    }

    /// <summary>
    /// 0045edc0 strips the artifacts (vf 0x98 / 0x94) and stores no army; the mask 0xec takes attributes (0x20),
    /// skills and perks (0x08), spells and the talisman level (0x40) from the record and leaves the war machines
    /// (0x10) and favoured enemies (0x100) to the map; experience (record +4 flag) sets the level (009aa5f0).
    /// </summary>
    [Fact]
    public void RecordCarriesExperienceAttributesSkillsAndSpellsButNotArtifactsArmyOrWarMachines()
    {
        var (g, h) = Game();
        var record = new CampaignHeroRecord
        {
            ScriptName = h.DefId,
            Experience = f.Core.Experience.For(10),
            Attack = 7, Defence = -2, SpellPower = 0, Knowledge = 4,
            Skills = { new SkillEntry("HERO_SKILL_LOGISTICS", Mastery.Advanced) },
            Perks = { "HERO_SKILL_PATHFINDING" },
            Spells = { "SPELL_MAGIC_ARROW" },
        };
        h.Artifacts = new() { "ARTIFACT_RING_OF_LIFE" };
        var army = h.Army.Live().Select(s => (s.Creature, s.Count)).ToList();
        bool ballista = h.Ballista;
        g.ApplyCampaignRecord(h, record);

        Assert.Equal(record.Experience, h.Experience);
        Assert.Equal(10, h.Level);
        Assert.Equal((7, 0, 1, 4), (h.Attack, h.Defence, h.SpellPower, h.Knowledge));
        Assert.Equal("HERO_SKILL_LOGISTICS", Assert.Single(h.Skills).SkillId);
        Assert.Equal(new[] { "HERO_SKILL_PATHFINDING" }, h.Perks);
        Assert.Contains("SPELL_MAGIC_ARROW", h.Spells);
        Assert.Equal(new[] { "ARTIFACT_RING_OF_LIFE" }, h.Artifacts);
        Assert.Equal(army, h.Army.Live().Select(s => (s.Creature, s.Count)).ToList());
        Assert.Equal(ballista, h.Ballista);
    }

    /// <summary>
    /// 0045f460: extra = Count − entries + empty entries, capped at the hero count; 0045fbc0 sorts by experience
    /// (vf 0x1a4) ascending with equal experience reversed, and the heroes are taken from the end.
    /// </summary>
    [Fact]
    public void ExtraHeroesAreTheMostExperiencedOnes()
    {
        HeroState H(string id, long xp) => new() { DefId = id, Experience = xp };
        var heroes = new List<HeroState> { H("a", 100), H("b", 500), H("c", 100), H("d", 300) };
        Assert.Equal(new[] { "b", "d" }, CampaignHeroRecord.ExtraHeroes(heroes, 3, 2, 1).Select(x => x.DefId));
        Assert.Equal(new[] { "b", "d", "a", "c" }, CampaignHeroRecord.ExtraHeroes(heroes, 9, 1, 0).Select(x => x.DefId));
        Assert.Empty(CampaignHeroRecord.ExtraHeroes(heroes, 2, 2, 0));
    }
}
