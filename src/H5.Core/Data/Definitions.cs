namespace H5.Core.Data;

// Typed views of the original game data. IDs are kept as the original string
// enum names (CREATURE_FOOTMAN, SPELL_FIREBALL, ...) so data, scripts and saves stay
// readable and map 1:1 to the original content and its Lua scripting API.

public sealed class CreatureDef
{
    public string Id = "";
    /// <summary>In the stock engine's fixed-size table (StockTables): only these take part in random pools.</summary>
    public bool Stock = true;
    public string SourcePath = "";
    public string Name = "";
    public string Description = "";
    public int Attack, Defence, Shots, MinDamage, MaxDamage, Speed, Initiative, Health;
    public bool Flying;
    public int SpellPoints;
    public List<(string Spell, Mastery Mastery)> KnownSpells = new();
    public int Exp, Power, Tier;
    public bool Upgrade;
    public string PairCreature = "";
    public string Town = "";
    public string Element1 = "ELEMENT_NONE", Element2 = "ELEMENT_NONE";
    public int WeeklyGrowth;
    public Resources Cost = new(0, 0, 0, 0, 0, 0, 0);
    public int CombatSize = 1;
    public int Range;
    public string BaseCreature = "";
    public List<string> Upgrades = new();
    public HashSet<string> Abilities = new();
    public string? VisualPath;
    public string? MonsterSharedPath;
    /// <summary>The creature's PatternAttack (null when the xdb has none).</summary>
    public AttackPattern? Pattern;

    public bool IsShooter => Shots > 0;
    public bool IsLarge => CombatSize >= 2;
    public bool Has(string ability) => Abilities.Contains(ability);
    /// <summary>Creature level as used by Cleansing/Warcries: tier for base, tier+1 for upgrades (fan manual p.190).</summary>
    public int Level => Tier + (Upgrade ? 1 : 0);
    public override string ToString() => $"{Id} ({Name})";
}

/// <summary>
/// A PatternAttack object (NDb::SPatternAttack): alternative cell sets ("rotations") relative to the attack anchor, each
/// with its AngleRotateTo in degrees; the DamageTo* coefficients and the DamageAll flag (also allies are hit).
/// Exe offsets: patterns vector +0x44 (stride 0x14, AngleRotateTo +0x10), RotateToMainTarget +0x50,
/// DamageToMainTargetCoefficient +0x54, DamageToOtherTargetsCoefficient +0x58, DamageAll +0x5c.
/// </summary>
public sealed class AttackPattern
{
    public List<(List<(int X, int Y)> Cells, float Angle)> Rotations = new();
    public bool RotateToMainTarget;
    public float MainCoefficient = 1, OtherCoefficient = 1;
    public bool DamageAll;
}

public sealed class CombatAbilityDef
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string ActivatedSpell = "SPELL_NONE";
}

public sealed class SpellDef
{
    public string Id = "";
    /// <summary>In the stock engine's fixed-size table (StockTables): only these take part in random pools.</summary>
    public bool Stock = true;
    public string SourcePath = "";
    public string Name = "";
    public string Description = "";
    public int Level;
    public string School = "";
    public int RequiredHeroLevel;
    public bool IsAimed;
    public int ManaCost;
    /// <summary>Indexed by mastery 0..3 (None, Basic, Advanced, Expert).</summary>
    public PowerFormula[] Damage = Array.Empty<PowerFormula>();
    public PowerFormula[] Duration = Array.Empty<PowerFormula>();
    public bool IsAreaAttack;
    public bool CanSelectDead;
    public string Target = "";
    public string Element = "ELEMENT_NONE";

    public PowerFormula DamageAt(Mastery m) => Damage.Length == 0 ? default : Damage[Math.Clamp((int)m, 0, Damage.Length - 1)];
    public PowerFormula DurationAt(Mastery m) => Duration.Length == 0 ? default : Duration[Math.Clamp((int)m, 0, Duration.Length - 1)];
    public bool IsCombatSchool => School is "MAGIC_SCHOOL_DESTRUCTIVE" or "MAGIC_SCHOOL_DARK" or "MAGIC_SCHOOL_LIGHT"
        or "MAGIC_SCHOOL_SUMMONING" or "MAGIC_SCHOOL_RUNIC" or "MAGIC_SCHOOL_WARCRIES";
    public override string ToString() => $"{Id} ({Name})";
}

public sealed class SkillDef
{
    public string Id = "";
    public string Name = "";
    public string SkillType = "";
    public string HeroClass = "";
    public string BasicSkillId = "";
    public List<string> Prerequisites = new();
    public bool IsPerk => SkillType is "SKILLTYPE_STANDART_PERK" or "SKILLTYPE_SPECIAL_PERK" or "SKILLTYPE_PERK";
}

public sealed class ArtifactDef
{
    public string Id = "";
    /// <summary>In the stock engine's fixed-size table (StockTables): only these take part in random pools.</summary>
    public bool Stock = true;
    public string Name = "";
    public string Description = "";
    public string Type = "";
    public string Slot = "";
    public int CostOfGold;
    public int Attack, Defence, Knowledge, SpellPower, Morale, Luck;
}

public sealed class HeroClassDef
{
    public string Id = "";
    public string Name = "";
    /// <summary>Level-up probability per attribute: Attack, Defence, SpellPower, Knowledge (sums to 100).</summary>
    public int[] AttributeChances = new int[4];
    public string Town = "";
}

public sealed class WarMachineDef
{
    public string Id = "";
    public int Shots, Initiative, Health, CombatSize;
    /// <summary>Indexed by War Machines mastery 0..4.</summary>
    public int[] Offense = Array.Empty<int>(), Defense = Array.Empty<int>();
    public double[] MinDamageFactor = Array.Empty<double>(), MaxDamageFactor = Array.Empty<double>();
    public Resources Cost = new(0, 0, 0, 0, 0, 0, 0);
    /// <summary>The machine's CreatureVisual xdb (name, icons, character, shot).</summary>
    public string? VisualPath;
}

public sealed record SkillEntry(string SkillId, Mastery Mastery);
public sealed record ArmySlot(string Creature, int Count)
{
    /// <summary>Crafted effects of the creature artifact this stack carries (Artificer), or -1 for none.</summary>
    public int ArtifactEffects { get; init; } = -1;
    /// <summary>The creature artifact the stack carries (army stack +0x2c), or null.</summary>
    public MicroArtifact? MicroArtifact { get; init; }
}

/// <summary>A hero as defined by an AdvMapHeroShared descriptor (class, base stats, starting skills).</summary>
public sealed class HeroDef
{
    public string Id = "";       // InternalName
    public string SourcePath = "";
    public string Name = "";
    public string Class = "";
    public string Specialization = "";
    public string SpecializationName = "";
    public string SpecializationDescription = "";
    public string Town = "";
    public SkillEntry? PrimarySkill;
    public int Attack, Defence, SpellPower, Knowledge;
    public List<SkillEntry> Skills = new();
    public List<string> Perks = new();
    public List<string> Spells = new();
    public bool Ballista, FirstAidTent, AmmoCart;
    /// <summary>Editable FavoriteEnemies of the shared hero (e.g. Diraya: CREATURE_FAMILIAR).</summary>
    public List<string> FavoriteEnemies = new();
}

/// <summary>A configured hero placed on a map (duel presets use these): overrides the shared hero.</summary>
public sealed class HeroInstance
{
    public string SourcePath = "";
    public string DisplayName = "";
    public HeroDef? Shared;
    public int Experience;
    public int Level;
    public List<ArmySlot> Army = new();
    public List<string> Artifacts = new();
    public List<string> FavoriteEnemies = new();
    public int Attack, Defence, SpellPower, Knowledge;
    public List<SkillEntry> Skills = new();
    public List<string> Perks = new();
    public List<string> Spells = new();
    public bool Ballista, FirstAidTent, AmmoCart;
    /// <summary>Current spell points the hero brings into battle; negative = full (duels, presets).</summary>
    public int Mana = -1;
    /// <summary>Next-battle bonuses (the adventure hero's struct returned by vfunc 0x330): luck, morale, attack, defence,
    /// hit points percent, initiative, speed. Morale and luck are applied by the adventure hand-off itself.</summary>
    public int[] BattleBonus = new int[7];
    public string Class => Shared?.Class ?? "";
    public string Town => Shared?.Town ?? "";
}

public sealed class DuelPreset
{
    public int Index;
    public string Name = "";
    public HeroInstance Hero = new();
    public int PresetPrice;
}
