using System.Xml.Linq;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Adventure.Data;

/// <summary>A town building level as defined by TownBuildingSharedStats.</summary>
public sealed class BuildingDef
{
    public string Path = "";
    public string Town = "";
    public string Type = "";        // TB_DWELLING_1, TB_MAGIC_GUILD, …
    public int Level;               // BLD_UPG_n
    public string Name = "";
    public Resources Cost = new(0, 0, 0, 0, 0, 0, 0);
    public List<string> DependencyPaths = new();
    public List<(string Type, int Level)> Dependencies = new();
    public string? Creature, Creature2;
    /// <summary>"Town Level" required: number of buildings already built (fan manual p.224).</summary>
    public int DevLevelNeeded;
    public override string ToString() => $"{Type}:{Level} {Name}";
}

/// <summary>
/// Skill/perk with per-class prerequisites (Skills.xdb SkillPrerequisites: each item is a
/// hero class and the perks it must already know). A special perk is only offered to the
/// classes listed there (fan manual p.294 "advanced, faction specific with requirements").
/// </summary>
public sealed class PerkDef
{
    public string Id = "";
    public string Type = "";               // SKILLTYPE_SKILL / _STANDART_PERK / _SPECIAL_PERK / _CLASS_PERK / _UINQUE_PERK
    public string BasicSkill = "";         // parent skill (BasicSkillID)
    public string HeroClass = "";
    public Dictionary<string, List<string>> PrereqByClass = new();

    public bool IsSkill => Type == "SKILLTYPE_SKILL";
    public bool IsStandardPerk => Type == "SKILLTYPE_STANDART_PERK";
    public bool IsSpecialPerk => Type == "SKILLTYPE_SPECIAL_PERK";
    public bool IsClassPerk => Type == "SKILLTYPE_CLASS_PERK";
    public bool IsUltimate => Type == "SKILLTYPE_UINQUE_PERK";
}

public sealed class MoonWeekDef
{
    public string Id = "";
    public string Name = "";
    public string Type = "";        // WEEKTYPE_NEUTRAL, …
}

/// <summary>One random creature slot of a bank variant: Creature1 with ChanceOfCreature1, else Creature2.</summary>
public sealed record BankGuardSlot(string Creature1, string Creature2, int Chance1, int Min1, int Max1, int Min2, int Max2);

public sealed class BankReward
{
    public Resources Money = new(0, 0, 0, 0, 0, 0, 0);
    public int Minor, Major, Relic, Spells, SpellLevel;
}

public sealed class BankVariant
{
    public int Chance;
    public List<BankGuardSlot> Guards = new();
    public BankReward Min = new(), Max = new();
}

public sealed class BankDef
{
    public string Id = "";
    public List<BankVariant> Variants = new();
    public int RechargesCount, DaysForRecharge, LuckLossOnEmpty, MoraleLossOnEmpty;
}

/// <summary>
/// Adventure-layer rules data loaded from the original archives on top of the shared
/// <see cref="GameDatabase"/> (creatures, spells, skills, heroes, artifacts).
/// </summary>
public sealed class AdventureDb
{
    public GameDatabase Core { get; }
    /// <summary>Town type (TOWN_HEAVEN…) → every building level defined for it.</summary>
    public Dictionary<string, List<BuildingDef>> Buildings { get; } = new();
    /// <summary>Hero class → probability (percent) of being offered each new skill (HeroClass.xdb SkillsProbs).</summary>
    public Dictionary<string, List<(string Skill, int Prob)>> SkillProbs { get; } = new();
    public List<MoonWeekDef> MoonWeeks { get; } = new();
    public Dictionary<string, BankDef> Banks { get; } = new();
    /// <summary>Artifact id → class (ARTF_CLASS_MINOR/MAJOR/RELIC/GRAIL).</summary>
    public Dictionary<string, string> ArtifactClass { get; } = new();
    /// <summary>Artifacts with Artifacts.xdb CanBeGeneratedToSell = false (never offered by merchants).</summary>
    public HashSet<string> ArtifactsNotForSale { get; } = new(StringComparer.Ordinal);
    /// <summary>Hero class → preferred spells for the town spell shop (HeroClass.xdb).</summary>
    public Dictionary<string, List<string>> PreferredSpells { get; } = new();
    public Dictionary<string, PerkDef> Perks { get; } = new();
    /// <summary>Town type → Resource Silo daily income (TownTypesInfo values/ResourceSiloIncome).</summary>
    public Dictionary<string, Resources> SiloIncome { get; } = new();
    /// <summary>Town type → native war machine sold at normal price by its Blacksmith (TownTypesInfo).</summary>
    public Dictionary<string, string> NativeWarMachine { get; } = new();

    AdventureDb(GameDatabase core) => Core = core;

    XdbLoader? _xdb;
    readonly Dictionary<string, string?> _townBonus = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<string, bool> _scenarioHero = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// AdvMapHeroShared ScenarioHero flag: campaign-only heroes (e.g. GhostFSLord) that taverns never offer
    /// unless a map lists them as available.
    /// </summary>
    public bool IsScenarioHero(HeroDef def)
    {
        if (_xdb == null || string.IsNullOrEmpty(def.SourcePath)) return false;
        lock (_scenarioHero)
        {
            if (_scenarioHero.TryGetValue(def.SourcePath, out var v)) return v;
            v = _xdb.FileSystem.Exists(def.SourcePath)
                && string.Equals(_xdb.Load(def.SourcePath).Root?.Element("ScenarioHero")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            _scenarioHero[def.SourcePath] = v;
            return v;
        }
    }

    /// <summary>TOWN_BONUS_* of a (TownSpecialization) xdb (TownState.Specialization), or null. Cached.</summary>
    public string? TownSpecializationBonus(string? specPath)
    {
        if (string.IsNullOrEmpty(specPath) || _xdb == null) return null;
        lock (_townBonus)
        {
            if (_townBonus.TryGetValue(specPath, out var b)) return b;
            b = _xdb.FileSystem.Exists(specPath) ? _xdb.Load(specPath).Root?.Element("Bonus")?.Value.Trim() : null;
            _townBonus[specPath] = b;
            return b;
        }
    }

    const string TownTypesInfo = "GameMechanics/RefTables/TownTypesInfo.xdb";

    public static AdventureDb Load(GameDatabase core, PakFileSystem fs)
    {
        var x = new XdbLoader(fs);
        var db = new AdventureDb(core) { _xdb = x };
        db.LoadBuildings(x, fs);
        db.LoadHeroClasses(x);
        db.LoadMoonWeeks(x);
        db.LoadBanks(core.Stats.Root);
        db.LoadArtifactClasses(x);
        db.LoadPerks(x);
        db.LoadTownTypes(x);
        return db;
    }

    void LoadTownTypes(XdbLoader x)
    {
        foreach (var item in x.Load(TownTypesInfo).Root!.Element("objects")!.Elements("Item"))
        {
            var v = item.Element("obj")?.Element("values");
            if (v == null) continue;
            SiloIncome[item.Str("ID")] = Resources.Parse(v.Element("ResourceSiloIncome"));
            NativeWarMachine[item.Str("ID")] = v.Str("NativeWarMachine");
        }
    }

    void LoadPerks(XdbLoader x)
    {
        const string table = "GameMechanics/RefTables/Skills.xdb";
        foreach (var item in x.Load(table).Root!.Element("objects")!.Elements("Item"))
        {
            var id = item.Str("ID");
            var o = item.Element("obj");
            if (id.Length == 0 || id == "HERO_SKILL_NONE" || o == null) continue;
            var p = new PerkDef { Id = id, Type = o.Str("SkillType"), BasicSkill = o.Str("BasicSkillID"), HeroClass = o.Str("HeroClass") };
            foreach (var pre in o.Items("SkillPrerequisites"))
                p.PrereqByClass[pre.Str("Class")] = pre.StrItems("dependenciesIDs");
            Perks[id] = p;
        }
    }

    public RpgStats Stats => Core.Stats;

    public BuildingDef? Building(string town, string type, int level) =>
        Buildings.TryGetValue(town, out var list) ? list.FirstOrDefault(b => b.Type == type && b.Level == level) : null;

    public int MaxLevel(string town, string type) =>
        Buildings.TryGetValue(town, out var list) ? list.Where(b => b.Type == type).Select(b => b.Level).DefaultIfEmpty(0).Max() : 0;

    void LoadBuildings(XdbLoader x, PakFileSystem fs)
    {
        foreach (var path in fs.Enumerate("MapObjects/").Where(p => p.EndsWith(".(AdvMapTownShared).xdb", StringComparison.OrdinalIgnoreCase)))
        {
            var root = x.Load(path).Root!;
            var town = root.Str("Type");
            if (town.Length == 0 || Buildings.ContainsKey(town)) continue;
            var list = new List<BuildingDef>();
            foreach (var item in root.Element("buildings")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var bp = XdbRef.Parse((string?)item.Attribute("href"), path).Path;
                if (bp.Length == 0 || !fs.Exists(bp)) continue;
                var b = x.Load(bp).Root!;
                list.Add(new BuildingDef
                {
                    Path = bp, Town = town, Type = b.Str("Type"), Level = Map.MapLoader.UpgradeLevel(b.Str("Upgrade")),
                    Name = x.ReadText(b.Href("NameFileRef"), bp)?.Trim() ?? "",
                    Cost = Resources.Parse(b.Element("Cost")),
                    DependencyPaths = b.Element("dependencies")?.Elements("Item").Select(i => XdbRef.Parse((string?)i.Attribute("href"), bp).Path).ToList() ?? new(),
                    Creature = Cr(b.Str("Creature")), Creature2 = Cr(b.Str("Creature2")),
                    DevLevelNeeded = b.Int("DevLevelNeeded"),
                });
            }
            var byPath = list.ToDictionary(b => b.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var b in list)
                foreach (var d in b.DependencyPaths)
                    if (byPath.TryGetValue(d, out var dep)) b.Dependencies.Add((dep.Type, dep.Level));
            Buildings[town] = list;
        }
    }

    static string? Cr(string s) => s.Length > 0 && s != "CREATURE_UNKNOWN" ? s : null;

    void LoadHeroClasses(XdbLoader x)
    {
        const string table = "GameMechanics/RefTables/HeroClass.xdb";
        foreach (var item in x.Load(table).Root!.Element("objects")!.Elements("Item"))
        {
            var id = item.Str("ID");
            var obj = item.Element("obj");
            SkillProbs[id] = obj.Items("SkillsProbs").Select(i => (i.Str("SkillID"), i.Int("Prob"))).ToList();
            PreferredSpells[id] = obj.StrItems("PreferredSpellsFromSpellShop");
        }
    }

    void LoadMoonWeeks(XdbLoader x)
    {
        const string table = "GameMechanics/RefTables/MoonWeeks.xdb";
        foreach (var item in x.Load(table).Root!.Element("objects")!.Elements("Item"))
        {
            var obj = item.Element("obj");
            MoonWeeks.Add(new MoonWeekDef
            {
                Id = item.Str("ID"), Type = obj.Str("Type"),
                Name = x.ReadText(obj.Href("NameFileRef"), table)?.Trim() is { Length: > 0 } n ? n : obj.Str("Name"),
            });
        }
    }

    void LoadBanks(XElement stats)
    {
        foreach (var b in stats.Element("Banks")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var def = new BankDef
            {
                Id = b.Name.LocalName, RechargesCount = b.Int("RechargesCount"), DaysForRecharge = b.Int("DaysForRecharge"),
                LuckLossOnEmpty = b.Int("LuckLossOnEmpty"), MoraleLossOnEmpty = b.Int("MoraleLossOnEmpty"),
            };
            foreach (var v in b.Items("Variants"))
            {
                var bv = new BankVariant { Chance = v.Int("ChanceOfVariant"), Min = Reward(v.Element("RewardMin")), Max = Reward(v.Element("RewardMax")) };
                foreach (var c in v.Items("Creatures"))
                    bv.Guards.Add(new BankGuardSlot(c.Str("Creature1"), c.Str("Creature2"), c.Int("ChanceOfCreature1"),
                        c.Int("MinCount1"), c.Int("MaxCount1"), c.Int("MinCount2"), c.Int("MaxCount2")));
                def.Variants.Add(bv);
            }
            Banks[def.Id] = def;
        }
    }

    static BankReward Reward(XElement? r) => new()
    {
        Money = Resources.Parse(r?.Element("Money")), Minor = r.Int("MinorArtifactsCount"), Major = r.Int("MajorArtifactsCount"),
        Relic = r.Int("RelicArtifactsCount"), Spells = r.Int("RandomSpellsCount"), SpellLevel = r.Int("RandomSpellsLevel"),
    };

    void LoadArtifactClasses(XdbLoader x)
    {
        const string table = "GameMechanics/RefTables/Artifacts.xdb";
        foreach (var item in x.Load(table).Root!.Element("objects")!.Elements("Item"))
        {
            var obj = item.Element("obj");
            var t = obj.Str("Type");
            if (t.StartsWith("ARTF_CLASS_", StringComparison.Ordinal) && item.Str("ID") != "ARTIFACT_NONE") ArtifactClass[item.Str("ID")] = t;
            if (obj.Str("CanBeGeneratedToSell") == "false") ArtifactsNotForSale.Add(item.Str("ID"));
        }
    }

    /// <summary>Building type → DefaultStats bank id (creature banks / battle sites).</summary>
    public static string? BankFor(string buildingType) => buildingType switch
    {
        "BUILDING_CRYPT" => "BankCrypt",
        "BUILDING_PYRAMID" => "BankPyramid",
        // The only NAGA_BANK descriptor is MapObjects/MagiVault_Snow; its guards in the
        // manual (p.265, Vault of the Mages) match BankMagiVault.
        "BUILDING_NAGA_BANK" => "BankMagiVault",
        "BUILDING_DRAGON_UTOPIA" => "BankDragonUtopia",
        "BUILDING_CYCLOPS_STOCKPILE" => "BankElementalsStockpile",
        // Wrecked ships (MapObjects/Water/Damaged_Boats_2); BUILDING_SHIP_GALEON is a boardable ship, not a bank.
        "BUILDING_DEMOLISH" => "BankDemolish",
        "BUILDING_UNKEMPT" => "BankUnkempt",
        "BUILDING_DWARVEN_TREASURE" => "BankDwarvenTreasure",
        "BUILDING_BLOOD_TEMPLE" => "BankBloodTemple",
        "BUILDING_TREANT_THICKET" => "BankTreantThicket",
        "BUILDING_GARGOYLE_STONEVAULT" => "BankGargoyleStonevault",
        "BUILDING_SUNKEN_TEMPLE" => "BankSunkenTemple",
        _ => null,
    };
}
