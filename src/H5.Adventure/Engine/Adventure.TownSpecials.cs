using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Haven Training Grounds: train a stack of the hero's army into the next Haven creature line.
/// Exe: CTrainCreatureCmd (vtable 0xe0e3fc, execute 0x427fb0) → the town's creature transformer
/// (CAdvMapTown sub-object +0x120, vtable 0xe957e0) slot +0x10 = FUN_00cd07f0 with mode 0.
/// </summary>
public sealed record TrainCreatureCmd(int TownObject, int HeroId, int Slot) : AdvCommand;

/// <summary>
/// Necropolis Shrine of the Netherworld: turn a living stack into the undead creature of its tier.
/// Exe: CTurnCreatureToUndeadCmd (vtable 0xe0e468, execute 0x428110) → transformer slot +0x10 mode 3.
/// <paramref name="HeroId"/> −1 = the town garrison.
/// </summary>
public sealed record TransformCreatureCmd(int TownObject, int HeroId, int Slot) : AdvCommand;

/// <summary>
/// Inferno Sacrificial Pit: sacrifice a stack of the hero's army for experience.
/// Exe: CSacrificeCreatureForExperienceCmd (vtable 0xe0e420, execute 0x428030) → transformer mode 1.
/// </summary>
public sealed record SacrificeForExperienceCmd(int TownObject, int HeroId, int Slot) : AdvCommand;

/// <summary>
/// Dungeon Ritual Pit: sacrifice a stack for the town's growth bonus. <paramref name="HeroId"/> −1 = the garrison.
/// Exe: CSacrificeCreatureForGrowthCmd (vtable 0xe0e444, execute 0x4280b0) → transformer mode 2.
/// </summary>
public sealed record SacrificeForGrowthCmd(int TownObject, int HeroId, int Slot) : AdvCommand;

/// <summary>Result of the transformer's quote FUN_00cd36c0 (first field of its output).</summary>
public enum TrainStatus
{
    Ok = 0, NoStack = 1, NoHero = 2, NoBuilding = 3, NotTrainable = 4, NoDwelling = 5, TopTier = 6,
    NotKnight = 7, NoResources = 8, LimitReached = 9,
}

/// <summary>A training quote: status, target creature, count and price.</summary>
public sealed record TrainQuote(TrainStatus Status, string? Target, int Count, Resources Cost);

// Town creature transformer (exe FUN_00cd36c0 quote, FUN_00cd07f0 apply), Haven part.
public sealed partial class Adventure
{
    /// <summary>
    /// The training lines of FUN_00cd36c0 (stack table at 00cd38xx): five groups of {base, upgrade, alternative
    /// upgrade}; a creature of group n trains into the base creature of group n + 1. Griffins are not in the table
    /// (cannot be trained); the Cavalier line is the last group (status 6).
    /// </summary>
    static readonly string[][] TrainLines =
    {
        new[] { "CREATURE_PEASANT", "CREATURE_MILITIAMAN", "CREATURE_LANDLORD" },
        new[] { "CREATURE_ARCHER", "CREATURE_MARKSMAN", "CREATURE_LONGBOWMAN" },
        new[] { "CREATURE_FOOTMAN", "CREATURE_SWORDSMAN", "CREATURE_VINDICATOR" },
        new[] { "CREATURE_PRIEST", "CREATURE_CLERIC", "CREATURE_ZEALOT" },
        new[] { "CREATURE_CAVALIER", "CREATURE_PALADIN", "CREATURE_CHAMPION" },
    };

    /// <summary>The dwelling that must be built for the target of group n + 1 (same table: TB 8, 9, 0xb, 0xc).</summary>
    static readonly string[] TrainDwellings = { "", "TB_DWELLING_2", "TB_DWELLING_3", "TB_DWELLING_5", "TB_DWELLING_6" };

    /// <summary>
    /// Town abilities used by the transformer (CAdvMapTown +0x44 = FUN_00cce1c0 over the table at 00f7c998:
    /// ability, town type, building, level). 0 = Haven TB_SPECIAL_1, 1 = Haven TB_SPECIAL_2.
    /// </summary>
    public const string TrainingGrounds = "TB_SPECIAL_1", HallOfHeroes = "TB_SPECIAL_2";

    /// <summary>
    /// Weekly training limit (FUN_00cd1850): Haven only; TrainingPointsPerWeek_Upgraded with the Hall of Heroes,
    /// TrainingPointsPerWeek with the Training Grounds, otherwise 0.
    /// </summary>
    public int TrainingPointsMax(TownState t)
    {
        if (t.Type != "TOWN_HEAVEN") return 0;
        const string k = "combat/HeroSkills/Knight/Training/";
        if (t.Level(HallOfHeroes) >= 1) return (int)Core.Stats.Get(k + "TrainingPointsPerWeek_Upgraded", 20);
        if (t.Level(TrainingGrounds) >= 1) return (int)Core.Stats.Get(k + "TrainingPointsPerWeek", 7);
        return 0;
    }

    /// <summary>
    /// Cost discount in percent (FUN_00cd36c0 at 00cd3a08, Training struct at RPGStats+0x564):
    /// (Training mastery − 1)·SkillTraining_CostDiscountPerLevel + ExpertTrainer_CostDiscount with Expert Trainer
    /// + (Suzerain: Base + ((level − 1) / LevelDivisor)·PerDividedLevel).
    /// </summary>
    public int TrainingDiscount(HeroState h)
    {
        const string k = "combat/HeroSkills/Knight/Training/";
        int d = ((int)h.MasteryOf("HERO_SKILL_TRAINING") - 1) * (int)Core.Stats.Get(k + "SkillTraining_CostDiscountPerLevel", 15);
        if (h.MasteryOf("HERO_SKILL_EXPERT_TRAINER") > Mastery.None || h.HasPerk("HERO_SKILL_EXPERT_TRAINER"))
            d += (int)Core.Stats.Get(k + "ExpertTrainer_CostDiscount", 35);
        if (h.Specialization == "HERO_SPEC_SUZERAIN")
        {
            int div = Math.Max(1, (int)Core.Stats.Get(k + "Suserain_CostDiscount/LevelDivisor", 3));
            d += (int)Core.Stats.Get(k + "Suserain_CostDiscount/Base", 1)
                 + (h.Level - 1) / div * (int)Core.Stats.Get(k + "Suserain_CostDiscount/PerDividedLevel", 1);
        }
        return d;
    }

    /// <summary>
    /// Training quote for the hero's stack (FUN_00cd36c0 mode 0). Order of the checks as in the exe: stack, hero,
    /// Knight, Training Grounds, training line, target dwelling, weekly limit, then the price
    /// (target creature cost × count × max((100 − discount)·BaseCostMultiplier·0.01, 0), each resource rounded
    /// as FUN_009d0d10) against the purse (FUN_009d0500 ratio &lt; 1).
    /// </summary>
    public TrainQuote TrainingQuote(TownState t, HeroState? h, int slot)
    {
        var none = new Resources(0, 0, 0, 0, 0, 0, 0);
        if (h == null) return new(TrainStatus.NoHero, null, 0, none);
        if (slot is < 0 or >= State.Army.Slots || h.Army[slot] is not { Count: > 0 } s) return new(TrainStatus.NoStack, null, 0, none);
        if (h.Class != "HERO_CLASS_KNIGHT") return new(TrainStatus.NotKnight, null, s.Count, none);
        if (t.Type != "TOWN_HEAVEN" || t.Level(TrainingGrounds) < 1) return new(TrainStatus.NoBuilding, null, s.Count, none);
        int group = Array.FindIndex(TrainLines, g => g.Contains(s.Creature));
        if (group < 0) return new(TrainStatus.NotTrainable, null, s.Count, none);
        if (group == TrainLines.Length - 1) return new(TrainStatus.TopTier, null, s.Count, none);
        string target = TrainLines[group + 1][0];
        if (t.Level(TrainDwellings[group + 1]) < 1) return new(TrainStatus.NoDwelling, target, s.Count, none);
        if (t.TrainingPoints < s.Count) return new(TrainStatus.LimitReached, target, s.Count, none);
        const string k = "combat/HeroSkills/Knight/Training/";
        float mult = (float)Core.Stats.Get(k + "BaseCostMultiplier", 3);
        // x87: FILD (100 − d), FMUL float mult, FMUL float 0.01 (00e15a80), FSTP float; FUN_00465640 = max(x, 0).
        float factor = (float)((double)(100 - TrainingDiscount(h)) * mult * (double)0.01f);
        factor = Math.Max(factor, 0f);
        float scale = (float)(s.Count * (double)factor);
        var td = Core.Creatures.GetValueOrDefault(target);
        var cost = Round(td?.Cost ?? none, scale);
        var p = S.Players.First(x => x.Index == h.Owner);
        if (!p.Purse.CanAfford(cost)) return new(TrainStatus.NoResources, target, s.Count, cost);
        return new(TrainStatus.Ok, target, s.Count, cost);
    }

    string? DoTrainCreature(TrainCreatureCmd c)
    {
        var p = Current;
        var t = S.Town(c.TownObject);
        if (t == null || t.Owner != p.Index) return "not your town";
        var h = S.Hero(c.HeroId);
        if (h == null || h.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
        var q = TrainingQuote(t, h, c.Slot);
        if (q.Status != TrainStatus.Ok) return $"cannot train ({q.Status})";
        // FUN_00cd07f0 mode 0: pay, spend the training points (floor 0), replace the stack by the target creature.
        Give(p, Purse.Scale(q.Cost, -1));
        t.TrainingPoints = Math.Max(0, t.TrainingPoints - q.Count);
        string from = h.Army[c.Slot]!.Creature;
        h.Army[c.Slot] = new Stack(q.Target!, q.Count);
        Emit(new CreatureUpgraded(h.Id, -1, c.Slot, from, q.Target!));
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    /// <summary>Base undead creature per tier 1..7 (FUN_00cd36c0 mode 3 table: 0x1d, 0x1f, 0x21, 0x23, 0x25, 0x27, 0x29).</summary>
    static readonly string[] UndeadByTier =
    {
        "CREATURE_SKELETON", "CREATURE_WALKING_DEAD", "CREATURE_MANES", "CREATURE_VAMPIRE",
        "CREATURE_LICH", "CREATURE_WIGHT", "CREATURE_BONE_DRAGON",
    };

    /// <summary>Town ability 0x12 (table 00f7c998): Necropolis TB_SPECIAL_2.</summary>
    public const string ShrineOfTheNetherworld = "TB_SPECIAL_2";

    Stack?[]? TownArmy(TownState t, HeroState? h) => h != null ? h.Army : t.Garrison;

    /// <summary>
    /// Transform quote (FUN_00cd36c0 mode 3): no stack → 1; no shrine (ability 0x12) → 3; an undead, mechanical
    /// or elemental creature (FUN_00b61ba0 abilities 0xa, 9, 0xc) → 4; target = the undead base creature of the
    /// stack's tier (clamped 1..7), whose dwelling TB_DWELLING_tier must be built (else 5). Free, same count.
    /// </summary>
    public TrainQuote TransformQuote(TownState t, HeroState? h, int slot)
    {
        var none = new Resources(0, 0, 0, 0, 0, 0, 0);
        var army = TownArmy(t, h);
        if (army == null || slot is < 0 or >= State.Army.Slots || army[slot] is not { Count: > 0 } s) return new(TrainStatus.NoStack, null, 0, none);
        if (t.Type != "TOWN_NECROMANCY" || t.Level(ShrineOfTheNetherworld) < 1) return new(TrainStatus.NoBuilding, null, s.Count, none);
        var cd = Core.Creatures.GetValueOrDefault(s.Creature);
        if (cd == null || cd.Abilities.Any(a => a is "ABILITY_UNDEAD" or "ABILITY_MECHANICAL" or "ABILITY_ELEMENTAL"))
            return new(TrainStatus.NotTrainable, null, s.Count, none);
        int tier = Math.Clamp(cd.Tier, 1, 7);
        string target = UndeadByTier[tier - 1];
        if (t.Level($"TB_DWELLING_{tier}") < 1) return new(TrainStatus.NoDwelling, target, s.Count, none);
        return new(TrainStatus.Ok, target, s.Count, none);
    }

    string? DoTransformCreature(TransformCreatureCmd c)
    {
        var p = Current;
        var t = S.Town(c.TownObject);
        if (t == null || t.Owner != p.Index) return "not your town";
        HeroState? h = null;
        if (c.HeroId >= 0)
        {
            h = S.Hero(c.HeroId);
            if (h == null || h.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
        }
        var q = TransformQuote(t, h, c.Slot);
        if (q.Status != TrainStatus.Ok) return $"cannot transform ({q.Status})";
        var army = TownArmy(t, h)!;
        string from = army[c.Slot]!.Creature;
        army[c.Slot] = new Stack(q.Target!, q.Count);
        Emit(new CreatureUpgraded(h?.Id ?? -1, h == null ? t.Object : -1, c.Slot, from, q.Target!));
        Emit(new ArmyChanged(h?.Id ?? -1, h == null ? t.Object : -1));
        return null;
    }

    /// <summary>Town abilities 9 (Inferno TB_SPECIAL_5) and 0x1c (Dungeon TB_SPECIAL_3) of the table at 00f7c998.</summary>
    public const string SacrificialPit = "TB_SPECIAL_5", RitualPit = "TB_SPECIAL_3";

    /// <summary>
    /// Hit points of one creature of the stack with the hero's bonuses (FUN_004bc470, as in H5.Combat.BattleSetup):
    /// base HP (Week of Toughness), Toughness perk +2, 2 per Ring of Life, Wyvern Tamer / Defender specialisations
    /// on the base creature, Ogres 2 and Dragonish 4 (tier) set bonuses. TODO(exe): the final percentage
    /// (hero +0x330 → +0x10, (p·hp + 50) / 100 with p clamped 0..1000) is not decoded and not applied.
    /// </summary>
    public int CreatureHitPoints(HeroState? h, string creature)
    {
        if (!Core.Creatures.TryGetValue(creature, out var c)) return 0;
        int hp = H5.Combat.MoonWeek.Health(c.Health, S.MoonWeek);
        if (h == null) return hp;
        if (h.HasPerk("HERO_SKILL_TOUGHNESS") || h.MasteryOf("HERO_SKILL_TOUGHNESS") > Mastery.None) hp += 2;
        hp += 2 * h.Artifacts.Count(a => a == "RING_OF_LIFE");
        string hpBase = c.BaseCreature is { Length: > 0 } bc && bc != "CREATURE_UNKNOWN" ? bc : c.Id;
        if (h.Specialization == "HERO_SPEC_WYVERN_TAMER" && hpBase == "CREATURE_WYVERN")
        {
            const string w = "combat/HeroSpecializations/Barbarian/WyvernTamer_HealthBonus";
            int div = Math.Max(1, (int)Core.Stats.Get(w + "/LevelDivisor", 1));
            hp += (h.Level - 1) / div * (int)Core.Stats.Get(w + "/PerDividedLevel", 2) + (int)Core.Stats.Get(w + "/Base", 1);
        }
        if (h.Specialization == "HERO_SPEC_DEFENDER" && hpBase == "CREATURE_DEFENDER") hp += h.Level / 5;
        int Pieces(string set) => Core.ArtifactSets.GetValueOrDefault(set)?.Count(h.Artifacts.Contains) ?? 0;
        if (Pieces("ARTFSET_EFFECT_OGRES") >= 2) hp += (int)Core.Stats.Get("ArtifactsSetsEffectsConsts/Ogres_2_Creature_HitPoints", 2);
        if (Pieces("ARTFSET_EFFECT_DRAGONISH") >= 4 && c.Tier == (int)Core.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_4_Creature_Tier", 7))
            hp += (int)Core.Stats.Get("ArtifactsSetsEffectsConsts/Dragonish_4_Creature_HitPoints", 20);
        return hp;
    }

    /// <summary>
    /// Sacrifice quote. Mode 1 (Inferno, FUN_00cd36c0): hero required (2), Sacrificial Pit (3); value =
    /// experience = hit points × count. Mode 2 (Dungeon): Ritual Pit (3); value = hit points × count added to
    /// the town's sacrifice total (FUN_00cd07f0). The army is the hero's (mode 1) or the hero's / garrison (mode 2).
    /// </summary>
    public (TrainStatus Status, long Value) SacrificeQuote(TownState t, HeroState? h, int slot, bool growth)
    {
        if (!growth && h == null) return (TrainStatus.NoHero, 0);
        var army = growth ? TownArmy(t, h) : h!.Army;
        if (army == null || slot is < 0 or >= State.Army.Slots || army[slot] is not { Count: > 0 } s) return (TrainStatus.NoStack, 0);
        bool built = growth ? t.Type == "TOWN_DUNGEON" && t.Level(RitualPit) >= 1 : t.Type == "TOWN_INFERNO" && t.Level(SacrificialPit) >= 1;
        if (!built) return (TrainStatus.NoBuilding, 0);
        return (TrainStatus.Ok, (long)CreatureHitPoints(h, s.Creature) * s.Count);
    }

    /// <summary>
    /// Ritual Pit growth bonuses (FUN_00cd07f0 mode 2, getters CAdvMapTown +0x94 / +0x98 shown as
    /// ritual_pit_info_witches / _minotaurs): witches = largest n ≤ 30 with total ≥ 150·2ⁿ, minotaurs = largest
    /// n ≤ 20 with total ≥ 400·3ⁿ (32-bit products as in the exe).
    /// </summary>
    public static (int Witches, int Minotaurs) RitualPitBonus(int total)
    {
        int w = 0, m = 0;
        unchecked
        {
            while (w < 30 && total >= (1 << (w + 1)) * 150) w++;
            while (m < 20)
            {
                int p = 1;
                for (int k = 0; k <= m; k++) p *= 3;
                if (total < p * 400) break;
                m++;
            }
        }
        return (w, m);
    }

    string? DoSacrifice(int townObject, int heroId, int slot, bool growth)
    {
        var p = Current;
        var t = S.Town(townObject);
        if (t == null || t.Owner != p.Index) return "not your town";
        HeroState? h = null;
        if (heroId >= 0)
        {
            h = S.Hero(heroId);
            if (h == null || h.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
        }
        var (status, value) = SacrificeQuote(t, h, slot, growth);
        if (status != TrainStatus.Ok) return $"cannot sacrifice ({status})";
        var army = growth ? TownArmy(t, h)! : h!.Army;
        army[slot] = null;
        if (growth) t.SacrificedHitPoints = (int)Math.Min(int.MaxValue, t.SacrificedHitPoints + value);
        Emit(new ArmyChanged(h?.Id ?? -1, h == null ? t.Object : -1));
        // Mode 1: hero +0x198 (add experience). TODO(exe): whether that call applies the experience modifiers.
        if (!growth) GainExperience(h!, value);
        return null;
    }

    /// <summary>Weekly update (FUN_00cd2b90): the training points are reset to the town's limit.</summary>
    void ResetTrainingPoints()
    {
        foreach (var t in S.Towns) t.TrainingPoints = TrainingPointsMax(t);
    }

    /// <summary>On a new building (FUN_00cd4a40): points grow by the rise of the limit, never shrink.</summary>
    void TrainingPointsAfterBuild(TownState t, int before)
    {
        int after = TrainingPointsMax(t);
        if (after > before) t.TrainingPoints += after - before;
    }
}
