using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Hero skill / perk rules confirmed against H5_Game.exe (docs/rules/audit/combat_heroes_skills.md).</summary>
[Collection("game")]
public class AuditSkillTests(GameDataFixture f)
{
    (Battle b, BattleState s) Setup((string id, int count)[] mine, (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes)
            h.Perks.RemoveAll(p => p is "HERO_SKILL_LAST_STAND" or "HERO_SKILL_SOIL_BURN" or "HERO_SKILL_BARBARIAN_SOIL_BURN"
                or "HERO_SKILL_DEAD_LUCK" or "HERO_SKILL_DEATH_SCREAM");
        return (new Battle(f.Db, s), s);
    }

    static Unit Of(BattleState s, string id) => s.Units.First(u => u.CreatureId == id);

    // exe FUN_00a59490 (called from the damage pipeline FUN_00a61600 → FUN_00a594f0): no once-per-combat flag — a
    // stack back at 2+ creatures is saved by Last Stand again.
    [Fact]
    public void LastStandHasNoOncePerCombatLimit()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        s.Heroes[0].Perks.Add("HERO_SKILL_LAST_STAND");
        var footman = Of(s, "CREATURE_FOOTMAN");

        b.DealDamage(footman, 100000, -1, AttackKind.Melee, null);
        Assert.True(footman.Alive);
        Assert.Equal(1, footman.Count);
        Assert.Equal(1, footman.TopHealth);

        footman.Count = 4;                         // e.g. resurrected back to 4 creatures
        footman.TopHealth = footman.MaxHealthCache;
        b.DealDamage(footman, 100000, -1, AttackKind.Melee, null);
        Assert.True(footman.Alive);
        Assert.Equal(1, footman.Count);

        b.DealDamage(footman, 100000, -1, AttackKind.Melee, null);   // a single creature is not protected
        Assert.False(footman.Alive);
    }

    // exe FUN_00a61a60: flying stacks (unit vfunc 0x190) take no Corrupted Soil damage; walkers take
    // round(level · clamp(SoilBurnDamagePerHeroLevel, 0.1, 1000)).
    [Fact]
    public void CorruptedSoilSparesFlyers()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10), ("CREATURE_GRIFFIN", 5) });
        s.Heroes[0].Perks.Add("HERO_SKILL_SOIL_BURN");
        s.Heroes[0].Level = 12;
        var imp = Of(s, "CREATURE_IMP");
        var griffin = Of(s, "CREATURE_GRIFFIN");
        Assert.True(griffin.Flying);
        Assert.False(imp.Flying);
        int perLevel = (int)f.Db.Stats.Get("combat/HeroSkills/Ranger/SoilBurnDamagePerHeroLevel", 3);
        Assert.Equal(12 * perLevel, b.SoilBurnDamage(imp));
        Assert.Equal(0, b.SoilBurnDamage(griffin));
        Assert.Equal(0, b.SoilBurnDamage(Of(s, "CREATURE_FOOTMAN")));   // own hero's perk doesn't hurt own stacks
    }

    // exe FUN_00989c40 (effect 0x3b): luck −= round(DeathScream_MoralePenalty · multiplier); a Dark Emissary's
    // doubled scream takes two luck points, not one.
    [Fact]
    public void DeathScreamLuckPenaltyScalesWithTheMultiplier()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        var imp = Of(s, "CREATURE_IMP");
        int baseLuck = b.LuckOf(imp);
        imp.Effects.Add(new Effect { Id = Fx.BansheeHowl, Value = 1, Value2 = 1, Remaining = 3 });
        Assert.Equal(baseLuck - 1, b.LuckOf(imp));
        imp.Effects.Clear();
        imp.Effects.Add(new Effect { Id = Fx.BansheeHowl, Value = 2, Value2 = 2, Remaining = 3 });
        Assert.Equal(baseLuck - 2, b.LuckOf(imp));
    }

    // exe FUN_0097d600 → FUN_00977a80: Cold Death's extra creature HP and Exorcism's doubling are applied to the raw
    // spell damage BEFORE protection (Exorcism doubles base + Cold Death bonus).
    [Fact]
    public void ColdDeathAndExorcismActBeforeProtection()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        var hero = s.Heroes[0];
        hero.Perks.RemoveAll(p => p is "HERO_SKILL_DEADLY_COLD" or "HERO_SKILL_ELEMENTAL_OVERKILL");
        var imp = Of(s, "CREATURE_IMP");
        var iceBolt = f.Db.Spells["SPELL_ICE_BOLT"];
        var fireball = f.Db.Spells["SPELL_FIREBALL"];
        int hp = b.MaxHealthOf(imp);

        Assert.Equal(100, b.PreProtectionPerks(100, imp, iceBolt, hero));
        hero.Perks.Add("HERO_SKILL_DEADLY_COLD");
        Assert.Equal(100 + hp, b.PreProtectionPerks(100, imp, iceBolt, hero));
        Assert.Equal(100, b.PreProtectionPerks(100, imp, fireball, hero));   // only the three cold spells

        hero.Perks.Add("HERO_SKILL_ELEMENTAL_OVERKILL");
        imp.Summoned = true;
        Assert.Equal(2 * (100 + hp), b.PreProtectionPerks(100, imp, iceBolt, hero));
        Assert.Equal(200, b.PreProtectionPerks(100, imp, fireball, hero));
    }

    // exe FUN_008a0e30 (speed getter FUN_008a1e50): Familiar Ground gives +RoadHome/SpeedBonus on the
    // RoadHome/NativeTerrain battlefield (grass), nothing elsewhere.
    [StockFact]
    public void FamiliarGroundAddsSpeedOnGrassOnly()
    {
        var (b, s) = Setup(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_IMP", 10) });
        var footman = Of(s, "CREATURE_FOOTMAN");
        s.Terrain = CombatArenaTable.TerrainGrass;
        s.Heroes[0].Perks.Remove("HERO_SKILL_ROAD_HOME");
        int baseSpeed = b.SpeedOf(footman);
        s.Heroes[0].Perks.Add("HERO_SKILL_ROAD_HOME");
        int bonus = (int)f.Db.Stats.Get("combat/HeroSkills/Knight/RoadHome/SpeedBonus", 2);
        Assert.Equal(baseSpeed + bonus, b.SpeedOf(footman));
        s.Terrain = CombatArenaTable.TerrainLava;
        Assert.Equal(baseSpeed, b.SpeedOf(footman));
    }

    // exe FUN_008aaec0: at battle start Absolute Rage gives AbsoluteRageRP and Memory of our Blood then gives nothing;
    // Memory alone gives Range(MinRP/Step, MaxRP/Step)·Step.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbsoluteRageReplacesMemoryOfOurBlood(bool absolute)
    {
        var (b, s) = Setup(new[] { ("CREATURE_GOBLIN", 20) }, new[] { ("CREATURE_PEASANT", 20) });
        var gob = Of(s, "CREATURE_GOBLIN");
        if (!gob.Abilities.Contains("ABILITY_DEMON_RAGED")) gob.Abilities.Add("ABILITY_DEMON_RAGED");
        var h = s.Heroes[0];
        h.Specialization = "";
        h.Perks.RemoveAll(p => p is "HERO_SKILL_POWER_OF_BLOOD" or "HERO_SKILL_ABSOLUTE_RAGE" or "HERO_SKILL_MEMORY_OF_OUR_BLOOD");
        h.Perks.Add("HERO_SKILL_MEMORY_OF_OUR_BLOOD");
        if (absolute) h.Perks.Add("HERO_SKILL_ABSOLUTE_RAGE");
        gob.RagePoints = 0;
        b.Begin();
        if (absolute)
            Assert.Equal((int)f.Db.Stats.Get("combat/Creatures/Stronghold/DemonRage/AbsoluteRageRP", 500), gob.RagePoints);
        else
        {
            Assert.InRange(gob.RagePoints, 50, 100);
            Assert.Equal(0, gob.RagePoints % 10);
        }
    }

    // exe 008ab4a3..008ab51f: player +0x1c8 (towns with ability 0x2c, Pile of Skulls) · PileOfOurFoes_StartRPBonus
    // (RPGStats +0x4a0) at the battle start, outside the hero block, through the rage gain.
    [Fact]
    public void PileOfSkullsGivesStartRagePerTown()
    {
        var (b, s) = Setup(new[] { ("CREATURE_GOBLIN", 20) }, new[] { ("CREATURE_PEASANT", 20) });
        var gob = Of(s, "CREATURE_GOBLIN");
        if (!gob.Abilities.Contains("ABILITY_DEMON_RAGED")) gob.Abilities.Add("ABILITY_DEMON_RAGED");
        var h = s.Heroes[0];
        h.Specialization = "";
        h.Perks.RemoveAll(p => p is "HERO_SKILL_POWER_OF_BLOOD" or "HERO_SKILL_ABSOLUTE_RAGE" or "HERO_SKILL_MEMORY_OF_OUR_BLOOD");
        s.PileOfSkulls[0] = 2;
        gob.RagePoints = 0;
        b.Begin();
        int bonus = (int)f.Db.Stats.Get("combat/Creatures/Stronghold/DemonRage/PileOfOurFoes_StartRPBonus", -1);
        Assert.True(bonus > 0);
        Assert.Equal(2 * bonus, gob.RagePoints);
    }

    // exe FUN_0097d740 (FPU chop, +0.5 at 0097d784): the perks' extra elemental damage rounds half up.
    [Fact]
    public void ChillingPerkDamageRoundsHalfUp()
    {
        Assert.Equal(1, Battle.SpellLikeAmount(9 * (double)0.1f));     // Chilling Steel on 9 damage: 0.9 → 1 (was 0)
        Assert.Equal(1, Battle.SpellLikeAmount(5 * (double)0.1f));
        Assert.Equal(0, Battle.SpellLikeAmount(4 * (double)0.1f));
        Assert.Equal(1, Battle.SpellLikeAmount(10 * (double)0.05f));   // Chilling Bones on 10 damage: 0.5 → 1
        Assert.Equal(0, Battle.SpellLikeAmount(9 * (double)0.05f));
        Assert.Equal(5, Battle.SpellLikeAmount(47 * (double)0.1f));
    }
}
