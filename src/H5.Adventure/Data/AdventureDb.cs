using System.Xml.Linq;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;
using H5.Adventure.Map;

namespace H5.Adventure.Data;

/// <summary>A Sphinx riddle (GameMechanics/SphinxRiddles/*.xdb, NDb::SSphinxRiddle): answers[0] is the right one.</summary>
public sealed class SphinxRiddle
{
    public string Question = "";
    public List<string> Answers = new();
    public int Experience, Gold;
    public string? Artifact;

    public static SphinxRiddle Parse(XElement root, Func<string?, string?, string?> readText) => new()
    {
        Question = readText(root.Href("Question"), null)?.Trim() ?? "",
        Answers = root.Element("answers")?.Elements("Item").Select(i => readText((string?)i.Attribute("href"), null)?.Trim() ?? "").ToList() ?? new(),
        Experience = root.Int("Experience"),
        Gold = root.Int("Gold"),
        Artifact = root.Str("ArtifactID") is { Length: > 0 } a && a != "ARTIFACT_NONE" ? a : null,
    };
}

/// <summary>A town building level as defined by TownBuildingSharedStats.</summary>
/// <summary>
/// One SGameRumoursSet / MapRumours entry: the rumour's text (resolved at load, as 004c7430 does), its weight and the
/// town type it is told in (TOWN_NO_TYPE = every tavern).
/// </summary>
public sealed record RumourDef(string Text, int Weight, string TownType);

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
    /// <summary>Engine enum value: the item index in Skills.xdb (HERO_SKILL_NONE = 0).</summary>
    public int Index;

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
public sealed partial class AdventureDb
{
    public GameDatabase Core { get; }
    /// <summary>Town type (TOWN_HEAVEN…) → every building level defined for it.</summary>
    public Dictionary<string, List<BuildingDef>> Buildings { get; } = new();
    /// <summary>Town type → AdvMapTownShared MagicSchool_0 / MagicSchool_1 (shared record +0x124 / +0x128, reader 00a0d7ba).</summary>
    public Dictionary<string, (string School0, string School1)> TownMagicSchools { get; } = new();
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
    SharedCatalog? _catalog;

    /// <summary>Shared object descriptors (random towns and dwellings swap theirs at game start).</summary>
    public SharedCatalog Catalog => _catalog ??= new SharedCatalog(_xdb ?? throw new InvalidOperationException("no archives"));

    Dictionary<string, List<string>>? _dwellingGroups;

    /// <summary>
    /// RPGRoot SharedGroups DWELLINGS_&lt;race&gt; (names 0041a890: HAVEN, PRESERVE, ACADEMY, DUNGEON, NECROPOLIS, INFERNO,
    /// FORTRESS, STRONGHOLD; looked up by 00bcb710) → the group's dwelling shared paths in link order.
    /// </summary>
    public IReadOnlyList<string> DwellingGroup(string townType)
    {
        if (_dwellingGroups == null)
        {
            _dwellingGroups = new(StringComparer.Ordinal);
            if (_xdb != null && _xdb.FileSystem.Exists("GameMechanics/RPGStats/RPGRoot.xdb"))
                foreach (var item in _xdb.Load("GameMechanics/RPGStats/RPGRoot.xdb").Root?.Element("SharedGroups")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                {
                    var id = item.Str("ID");
                    if (!id.StartsWith("DWELLINGS_", StringComparison.Ordinal)) continue;
                    var gp = XdbRef.Parse(item.Href("Group"), "GameMechanics/RPGStats/RPGRoot.xdb").Path;
                    var links = new List<string>();
                    if (gp.Length > 0 && _xdb.FileSystem.Exists(gp))
                        foreach (var l in _xdb.Load(gp).Root?.Element("links")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                            links.Add(XdbRef.Parse((string?)l.Attribute("href"), gp).Path);
                    _dwellingGroups[id["DWELLINGS_".Length..]] = links;
                }
        }
        var key = townType switch { "TOWN_HEAVEN" => "HAVEN", "TOWN_NECROMANCY" => "NECROPOLIS", _ => townType.Replace("TOWN_", "") };
        return _dwellingGroups.GetValueOrDefault(key) ?? new List<string>();
    }
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

    Dictionary<string, int>? _weekIndex;
    readonly Dictionary<string, int[]?> _calendars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MoonWeekID value of a week id (MoonWeeks.xdb is in enum order, types.xml MoonWeekID); 0 if unknown.</summary>
    public int MoonWeekIndex(string? id)
    {
        lock (_calendars)
        {
            _weekIndex ??= MoonWeeks.Select((w, i) => (w.Id, i)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
            return id != null && _weekIndex.TryGetValue(id, out int i) ? i : 0;
        }
    }

    /// <summary>The week id of a MoonWeekID value (WEEK_OF_NOTHING when out of range).</summary>
    public string MoonWeekId(int index) => index >= 0 && index < MoonWeeks.Count ? MoonWeeks[index].Id : "WEEK_OF_NOTHING";

    /// <summary>
    /// A SMoonCalendar (125 MoonWeekID values, [a,b,c] flat) from the game data, or null when missing or not 5×5×5
    /// (005088a0). Cached.
    /// </summary>
    public int[]? MoonCalendarData(string? path)
    {
        if (string.IsNullOrEmpty(path) || _xdb == null) return null;
        lock (_calendars)
            if (_calendars.TryGetValue(path, out var c)) return c;
        var data = _xdb.FileSystem.Exists(path) ? Engine.MoonCalendar.Parse(_xdb.Load(path).Root, MoonWeekIndex) : null;
        lock (_calendars) _calendars[path] = data;
        return data;
    }

    readonly Dictionary<string, Dictionary<int, string>> _enums = new(StringComparer.Ordinal);

    /// <summary>
    /// Name of an enum value from the game's types.xml (e.g. ("CreatureType", 21) → a CREATURE_* id): map data such
    /// as quest parameters stores enums as numbers. Null when unknown.
    /// </summary>
    public string? EnumName(string type, int value)
    {
        if (_xdb == null) return null;
        lock (_enums)
        {
            if (_enums.Count == 0 && _xdb.FileSystem.TryReadAllBytes("types.xml", out var bytes))
            {
                var root = System.Xml.Linq.XDocument.Parse(H5.Core.Xdb.TextDecoding.Decode(bytes)).Root;
                foreach (var t in root?.Descendants("Item").Where(i => i.Element("Type")?.Value == "TYPE_TYPE_ENUM") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                {
                    var name = t.Element("TypeName")?.Value ?? "";
                    var map = new Dictionary<int, string>();
                    foreach (var en in t.Element("Entries")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                        if (int.TryParse(en.Element("Value")?.Value, out int v)) map.TryAdd(v, en.Element("Name")?.Value ?? "");
                    _enums.TryAdd(name, map);
                }
            }
            return _enums.TryGetValue(type, out var m) && m.TryGetValue(value, out var n) ? n : null;
        }
    }

    const string TownTypesInfo = "GameMechanics/RefTables/TownTypesInfo.xdb";
    /// <summary>The random riddle group (path from the exe, 00a94ce8).</summary>
    const string RiddleGroup = "GameMechanics/SphinxRiddles/RandomGroup.xdb";

    /// <summary>Riddles of the random group, in its order (CRiddlesPool fill 00ced5b0).</summary>
    public List<SphinxRiddle> RandomRiddles { get; } = new();

    void LoadRiddles(XdbLoader x)
    {
        if (!x.FileSystem.Exists(RiddleGroup)) return;
        foreach (var item in x.Load(RiddleGroup).Root?.Element("variants")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var p = XdbRef.Parse((string?)item.Attribute("href"), RiddleGroup).Path;
            if (p.Length == 0 || !x.FileSystem.Exists(p) || x.Load(p).Root is not { } r) continue;
            RandomRiddles.Add(SphinxRiddle.Parse(r, (h, cur) => x.ReadText(h, cur ?? p)));
        }
    }

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
        db.LoadRiddles(x);
        db.LoadRumours(x);
        db.LoadMonsterGroups(x);
        return db;
    }

    /// <summary>The common rumours (exe path string 00e0d0f0: GameMechanics/Rumours/CommonGameRumours, SGameRumoursSet).</summary>
    public List<RumourDef> CommonRumours { get; } = new();

    void LoadRumours(XdbLoader x)
    {
        const string set = "GameMechanics/Rumours/CommonGameRumours.(GameRumoursSet).xdb";
        if (!x.FileSystem.Exists(set)) return;
        foreach (var item in x.Load(set).Root!.Items("rumours"))
        {
            var text = x.ReadText((string?)item.Element("Text")?.Attribute("href"), set)?.Trim() ?? "";
            if (text.Length > 0) CommonRumours.Add(new RumourDef(text, item.Int("Weight"), item.Str("TownType")));
        }
        // SRPGRoot +0x74 (0x1c-byte items ID / TextFileRef, searched by name in 00bcb7b0): the rumour formats.
        const string root = "GameMechanics/RPGStats/RPGRoot.xdb";
        if (!x.FileSystem.Exists(root)) return;
        foreach (var item in x.Load(root).Root?.Element("Texts")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if ((string?)item.Element("ID") is { } id && id.StartsWith("RUMOUR_FORMAT_", StringComparison.Ordinal))
                RumourFormats[id] = H5.Core.Xdb.XdbRef.Parse((string?)item.Element("TextFileRef")?.Attribute("href"), root).Path;
    }

    /// <summary>A text file referenced from xdb (resolved like 004c7430), null without archives or file.</summary>
    public string? ReadText(string? href, string? currentFile = null) => _xdb?.ReadText(href, currentFile)?.Trim();

    /// <summary>RPGRoot RUMOUR_FORMAT_* text ids → their text file (00bcb7b0; an unknown id gives "").</summary>
    public Dictionary<string, string> RumourFormats { get; } = new(StringComparer.Ordinal);

    /// <summary>The format file of an RPGRoot rumour format id, "" when the table has none (00bcb7b0).</summary>
    public string RumourFormat(string id) => RumourFormats.TryGetValue(id, out var p) ? p : "";

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
        int index = -1;
        foreach (var item in x.Load(table).Root!.Element("objects")!.Elements("Item"))
        {
            index++;
            var id = item.Str("ID");
            var o = item.Element("obj");
            if (id.Length == 0 || id == "HERO_SKILL_NONE" || o == null) continue;
            var p = new PerkDef { Id = id, Index = index, Type = o.Str("SkillType"), BasicSkill = o.Str("BasicSkillID"), HeroClass = o.Str("HeroClass") };
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
            TownMagicSchools[town] = (root.Str("MagicSchool_0"), root.Str("MagicSchool_1"));
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
        foreach (var item in StockTables.Items(x.FileSystem, table, x.Load(table).Root))
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
