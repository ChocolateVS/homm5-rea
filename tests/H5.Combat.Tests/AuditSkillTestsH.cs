using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Hero and artifact rules confirmed against H5_Game.exe (docs/rules/audit/combat_heroes_skills_h.md).</summary>
[Collection("game")]
public class AuditSkillTestsH(GameDataFixture f)
{
    HeroInstance Hero(int preset, params string[] artifacts)
    {
        var src = f.Db.DuelPresets[preset].Hero!;
        var h = new HeroInstance
        {
            Shared = src.Shared, Level = src.Level, DisplayName = src.DisplayName,
            Attack = src.Attack, Defence = src.Defence, SpellPower = src.SpellPower, Knowledge = src.Knowledge,
        };
        h.Artifacts.AddRange(artifacts);
        return h;
    }

    (Battle b, BattleState s) Setup(HeroInstance mine, HeroInstance theirs, params string[] creatures)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = mine, Army = creatures.Select(c => new ArmySlot(c, 10)).ToList() },
            new ArmySetup { Hero = theirs, Army = { new ArmySlot("CREATURE_IMP", 10) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        return (new Battle(f.Db, s), s);
    }

    static Unit Of(BattleState s, string id) => s.Units.First(u => u.CreatureId == id && u.Side == 0);

    // exe FUN_004bc190 (004bc2bb..004bc418): army artifacts give a percent p (Ring of Haste +20, Ring of Celerity +10 each,
    // Dragon Scale Armor large / Shield small +5, Greaves walkers +10, Wing Mantle flyers +10, Teeth Necklace casters +10,
    // Eye Ring shooters +10, Ogre Club / Shield −5) and initiative = (p + 100) · init · 0.01. The engine had none of them.
    [Fact]
    public void ArmyArtifactsScaleCreatureInitiative()
    {
        double Init(string creature, params string[] arts)
            => Of(Setup(Hero(0, arts), Hero(3), creature).s, creature).BaseInitiative;
        double archer = Init("CREATURE_ARCHER"), griffin = Init("CREATURE_GRIFFIN"), footman = Init("CREATURE_FOOTMAN");
        Assert.Equal((float)(120 * (float)archer * 0.01f), Init("CREATURE_ARCHER", "RING_OF_HASTE", "RING_OF_HASTE"), 4);   // +20 once
        Assert.Equal((float)(110 * (float)archer * 0.01f), Init("CREATURE_ARCHER", "DRAGON_EYE_RING"), 4);
        Assert.Equal(archer, Init("CREATURE_ARCHER", "DRAGON_WING_MANTLE"), 4);
        Assert.Equal((float)(110 * (float)griffin * 0.01f), Init("CREATURE_GRIFFIN", "DRAGON_WING_MANTLE"), 4);
        Assert.Equal((float)(110 * (float)footman * 0.01f), Init("CREATURE_FOOTMAN", "DRAGON_BONE_GRAVES"), 4);
        Assert.Equal((float)(105 * (float)footman * 0.01f), Init("CREATURE_FOOTMAN", "DRAGON_SCALE_SHIELD"), 4);   // small
        Assert.Equal(footman, Init("CREATURE_FOOTMAN", "DRAGON_SCALE_ARMOR"), 4);
        Assert.Equal((float)(90 * (float)footman * 0.01f), Init("CREATURE_FOOTMAN", "ARTIFACT_OGRE_CLUB", "ARTIFACT_OGRE_SHIELD"), 4);
    }

    // exe FUN_004bc190 (004bc1f0..004bc25b): HERO_SPEC_FURIOUS and HERO_SPEC_MASTER_OF_INITIATIVE multiply the creature
    // initiative by 1 + clamp(Furious_InitiativeBonusBase + level·Furious_InitiativeBonusPerLevel, 0, 5); the engine had
    // an invented 1 + 0.01·level factor in InitiativeOf for Master of Initiative only.
    [Fact]
    public void FuriousSpecialisationsScaleCreatureInitiative()
    {
        var plain = Hero(0); plain.Level = 10;
        double base0 = Of(Setup(plain, Hero(3), "CREATURE_FOOTMAN").s, "CREATURE_FOOTMAN").BaseInitiative;
        foreach (var spec in new[] { "HERO_SPEC_FURIOUS", "HERO_SPEC_MASTER_OF_INITIATIVE" })
        {
            var (b, s) = Setup(plain, Hero(3), "CREATURE_FOOTMAN");
            s.Heroes[0].Specialization = spec;
            var fresh = BattleSetup.AddCreature(f.Db, s, 0, "CREATURE_FOOTMAN", 5, new Cell(0, 0), summoned: false);
            float k = Math.Clamp(10 * (float)f.Db.Stats.Get("combat/HeroSpecializations/Demonlord/Furious_InitiativeBonusPerLevel", 0.01)
                                 + (float)f.Db.Stats.Get("combat/HeroSpecializations/Demonlord/Furious_InitiativeBonusBase", 0), 0f, 5f);
            Assert.Equal((k + 1f) * (float)base0, fresh.BaseInitiative, 4);
            Assert.Equal(fresh.BaseInitiative, b.InitiativeOf(fresh), 4);   // no further spec factor at combat time
        }
    }

    // exe 00989fd0 / 00989c40 tails: the ENEMY hero's Nightmarish Ring and Cloak of Mourning take 2 morale each; Cloak of
    // Mourning and Jinxing Band take 2 luck each. The engine had none of these.
    [Fact]
    public void EnemyArtifactsLowerMoraleAndLuck()
    {
        var (b0, s0) = Setup(Hero(0), Hero(3), "CREATURE_FOOTMAN");
        var (b1, s1) = Setup(Hero(0), Hero(3, "NIGHTMARISH_RING", "CLOAK_OF_MOURNING", "ARTIFACT_JINXING_BAND"), "CREATURE_FOOTMAN");
        // the enemy hero's own stats from the artifacts do not touch our stack; compare our footmen only
        Assert.Equal(b0.MoraleOf(Of(s0, "CREATURE_FOOTMAN")) - 4, b1.MoraleOf(Of(s1, "CREATURE_FOOTMAN")));
        Assert.Equal(b0.LuckOf(Of(s0, "CREATURE_FOOTMAN")) - 4, b1.LuckOf(Of(s1, "CREATURE_FOOTMAN")));
    }
}
