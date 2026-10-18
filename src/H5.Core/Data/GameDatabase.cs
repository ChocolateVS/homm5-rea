using System.Xml.Linq;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Core.Data;

/// <summary>
/// All rules data needed by the game, loaded from the original archives.
/// Load once per process (it reads a few thousand xdb files) and share; it is immutable after loading.
/// </summary>
public sealed class GameDatabase
{
    public Dictionary<string, CreatureDef> Creatures { get; } = new();
    public Dictionary<string, CombatAbilityDef> Abilities { get; } = new();
    public Dictionary<string, SpellDef> Spells { get; } = new();
    public Dictionary<string, SkillDef> Skills { get; } = new();
    public Dictionary<string, ArtifactDef> Artifacts { get; } = new();
    public Dictionary<string, HeroClassDef> HeroClasses { get; } = new();
    public Dictionary<string, WarMachineDef> WarMachines { get; } = new();
    public Dictionary<string, HeroDef> Heroes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DuelPreset> DuelPresets { get; } = new();
    /// <summary>Artifact set effect id (ARTFSET_EFFECT_*) → member artifacts (DefaultStats ArtifactSets).</summary>
    public Dictionary<string, List<string>> ArtifactSets { get; } = new();
    /// <summary>Combat arena scenes by name (Scenes/CombatArenas), with their obstacle groups.</summary>
    public Dictionary<string, CombatArenaDef> Arenas { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Siege layout per town type (TOWN_HEAVEN, …): walls, gate, towers, moat, siege arena.</summary>
    public Dictionary<string, TownCombatDef> TownCombat { get; private set; } = new();
    /// <summary>Fortification hit points by building type (WALL, GATE, BIG_TOWER, …).</summary>
    public Dictionary<string, ArenaBuildingStatsDef> ArenaBuildingStats { get; private set; } = new();
    /// <summary>Moat damage and effects per town type.</summary>
    public Dictionary<string, MoatDef> Moats { get; private set; } = new();
    public RpgStats Stats { get; private set; } = null!;
    public ExperienceTable Experience { get; } = new();
    public List<string> Warnings { get; } = new();

    const string CreatureTable = "GameMechanics/RefTables/Creatures.xdb";
    const string SpellTable = "GameMechanics/RefTables/UndividedSpells.xdb";
    const string AbilityTable = "GameMechanics/RefTables/CombatAbilities.xdb";
    const string SkillTable = "GameMechanics/RefTables/Skills.xdb";
    const string ArtifactTable = "GameMechanics/RefTables/Artifacts.xdb";
    const string HeroClassTable = "GameMechanics/RefTables/HeroClass.xdb";
    const string WarMachineTable = "GameMechanics/RefTables/WarMachines.xdb";
    const string StatsFile = "GameMechanics/RPGStats/DefaultStats.xdb";
    const string DuelPresetsFile = "UI/MPDMLobby/presets.(DuelPresets).xdb";

    XdbLoader _x = null!;

    public static GameDatabase Load(PakFileSystem fs)
    {
        var db = new GameDatabase { _x = new XdbLoader(fs) };
        db.Stats = new RpgStats(db._x.Load(StatsFile).Root!);
        db.LoadAbilities();
        db.LoadCreatures();
        db.LoadSpells();
        db.LoadSkills();
        db.LoadArtifacts();
        db.LoadHeroClasses();
        db.LoadWarMachines();
        db.LoadHeroes(fs);
        db.LoadDuelPresets();
        db.LoadArtifactSets();
        db.Arenas = ArenaLoader.Load(db._x);
        db.TownCombat = SiegeLoader.LoadTownCombat(db._x);
        db.ArenaBuildingStats = SiegeLoader.LoadBuildingStats(db._x);
        db.Moats = SiegeLoader.LoadMoats(db._x);
        db._x = null!;
        return db;
    }

    public CreatureDef Creature(string id) =>
        Creatures.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"Unknown creature {id}");

    public SpellDef? Spell(string id) => Spells.TryGetValue(id, out var s) ? s : null;

    // ---------------------------------------------------------------- tables

    IEnumerable<(string Id, XElement Obj, string File)> ReadTable(string table, string objName)
    {
        var root = _x.Load(table).Root!;
        foreach (var item in root.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var id = item.Str("ID");
            var objRef = item.Element(objName);
            if (id.Length == 0 || objRef == null) continue;
            var href = (string?)objRef.Attribute("href");
            if (string.IsNullOrEmpty(href) && !objRef.HasElements) continue;
            XElement? obj; string file;
            if (string.IsNullOrEmpty(href)) { obj = objRef; file = table; }   // fields written directly under <obj>
            else if (href.StartsWith("#n:inline")) { obj = objRef.Elements().FirstOrDefault() ?? objRef; file = table; } // <Obj><Type>…</Type></Obj>
            else
            {
                var r = XdbRef.Parse(href, table);
                if (!_x.FileSystem.Exists(r.Path)) { Warnings.Add($"{table}: {id} -> missing {r.Path}"); continue; }
                obj = _x.Load(r.Path).Root; file = r.Path;
            }
            if (obj != null) yield return (id, obj, file);
        }
    }

    string Text(XElement? e, string name, string file) => _x.ReadText(e.Href(name), file)?.Trim() ?? "";

    void LoadAbilities()
    {
        foreach (var (id, o, f) in ReadTable(AbilityTable, "obj"))
            Abilities[id] = new CombatAbilityDef
            {
                Id = id,
                Name = Text(o, "NameFileRef", f),
                Description = Text(o, "DescriptionFileRef", f),
                ActivatedSpell = o.Str("ActivatedSpell", "SPELL_NONE"),
            };
    }

    void LoadCreatures()
    {
        foreach (var (id, o, f) in ReadTable(CreatureTable, "Obj"))
        {
            if (id == "CREATURE_UNKNOWN") continue;
            var c = new CreatureDef
            {
                Id = id, SourcePath = f,
                Attack = o.Int("AttackSkill"), Defence = o.Int("DefenceSkill"), Shots = o.Int("Shots"),
                MinDamage = o.Int("MinDamage"), MaxDamage = o.Int("MaxDamage"), Speed = o.Int("Speed"),
                Initiative = o.Int("Initiative"), Flying = o.Bool("Flying"), Health = o.Int("Health"),
                SpellPoints = o.Int("SpellPoints"), Exp = o.Int("Exp"), Power = o.Int("Power"),
                Tier = o.Int("CreatureTier"), Upgrade = o.Bool("Upgrade"), PairCreature = o.Str("PairCreature"),
                Town = o.Str("CreatureTown"), WeeklyGrowth = o.Int("WeeklyGrowth"), Cost = Resources.Parse(o.Element("Cost")),
                CombatSize = Math.Max(1, o.Int("CombatSize", 1)), Range = o.Int("Range"), BaseCreature = o.Str("BaseCreature"),
                Upgrades = o.StrItems("Upgrades"), Abilities = o.StrItems("Abilities").ToHashSet(),
                Element1 = o.Element("MagicElement").Str("First", "ELEMENT_NONE"),
                Element2 = o.Element("MagicElement").Str("Second", "ELEMENT_NONE"),
                VisualPath = XdbRef.Parse(o.Href("Visual"), f).Path is { Length: > 0 } vp ? vp : null,
                MonsterSharedPath = XdbRef.Parse(o.Href("MonsterShared"), f).Path is { Length: > 0 } mp ? mp : null,
            };
            foreach (var ks in o.Items("KnownSpells"))
                c.KnownSpells.Add((ks.Str("Spell"), MasteryParse.Parse(ks.Str("Mastery"))));
            if (c.VisualPath != null && _x.FileSystem.Exists(c.VisualPath))
            {
                var vis = _x.Load(c.VisualPath).Root;
                c.Name = Text(vis, "CreatureNameFileRef", c.VisualPath);
                c.Description = Text(vis, "DescriptionFileRef", c.VisualPath);
            }
            if (c.Name.Length == 0) c.Name = id;
            Creatures[id] = c;
        }
    }

    void LoadSpells()
    {
        foreach (var (id, o, f) in ReadTable(SpellTable, "Obj"))
        {
            if (o.Name.LocalName != "Spell") continue;
            Spells[id] = new SpellDef
            {
                Id = id, SourcePath = f,
                Name = Text(o, "NameFileRef", f),
                Description = Text(o, "LongDescriptionFileRef", f),
                Level = o.Int("Level"), School = o.Str("MagicSchool"), RequiredHeroLevel = o.Int("RequiredHeroLevel"),
                IsAimed = o.Bool("IsAimed"), ManaCost = o.Int("TrainedCost"),
                Damage = o.Items("damage").Select(PowerFormula.Parse).ToArray(),
                Duration = o.Items("duration").Select(PowerFormula.Parse).ToArray(),
                IsAreaAttack = o.Bool("IsAreaAttack"), CanSelectDead = o.Bool("CanSelectDead"),
                Target = o.Str("Target"), Element = o.Str("Element", "ELEMENT_NONE"),
            };
        }
    }

    void LoadSkills()
    {
        foreach (var (id, o, f) in ReadTable(SkillTable, "obj"))
        {
            var nameRef = o.Element("NameFileRef")?.Elements("Item").FirstOrDefault(i => i.Attribute("href") != null)
                          ?? o.Element("NameFileRef");
            var s = new SkillDef
            {
                Id = id, SkillType = o.Str("SkillType"), HeroClass = o.Str("HeroClass"), BasicSkillId = o.Str("BasicSkillID"),
                Name = _x.ReadText((string?)nameRef?.Attribute("href"), f)?.Trim() ?? "",
            };
            foreach (var p in o.Items("SkillPrerequisites"))
                s.Prerequisites.AddRange(p.StrItems("dependenciesIDs"));
            Skills[id] = s;
        }
    }

    void LoadArtifacts()
    {
        foreach (var (id, o, f) in ReadTable(ArtifactTable, "obj"))
        {
            var m = o.Element("HeroStatsModif");
            Artifacts[id] = new ArtifactDef
            {
                Id = id, Name = Text(o, "NameFileRef", f), Description = Text(o, "DescriptionFileRef", f),
                Type = o.Str("Type"), Slot = o.Str("Slot"), CostOfGold = o.Int("CostOfGold"),
                Attack = m.Int("Attack"), Defence = m.Int("Defence"), Knowledge = m.Int("Knowledge"),
                SpellPower = m.Int("SpellPower"), Morale = m.Int("Morale"), Luck = m.Int("Luck"),
            };
        }
    }

    void LoadHeroClasses()
    {
        foreach (var (id, o, f) in ReadTable(HeroClassTable, "obj"))
        {
            var a = o.Element("AttributeProbs");
            HeroClasses[id] = new HeroClassDef
            {
                Id = id, Name = Text(o, "NameFileRef", f),
                AttributeChances = new[] { a.Int("OffenceProb"), a.Int("DefenceProb"), a.Int("SpellpowerProb"), a.Int("KnowledgeProb") },
            };
        }
    }

    void LoadWarMachines()
    {
        foreach (var (id, o, _) in ReadTable(WarMachineTable, "Obj"))
            WarMachines[id] = new WarMachineDef
            {
                Id = id, Shots = o.Int("Shots"), Initiative = o.Int("Initiative"), Health = o.Int("Health"),
                CombatSize = o.Int("CombatSize", 1), Cost = Resources.Parse(o.Element("Cost")),
                Offense = o.StrItems("Offense").Select(s => (int)XmlHelpers.ParseDouble(s)).ToArray(),
                Defense = o.StrItems("Defense").Select(s => (int)XmlHelpers.ParseDouble(s)).ToArray(),
                MinDamageFactor = o.StrItems("MinDamageFactor").Select(s => XmlHelpers.ParseDouble(s)).ToArray(),
                MaxDamageFactor = o.StrItems("MaxDamageFactor").Select(s => XmlHelpers.ParseDouble(s)).ToArray(),
            };
    }

    void LoadHeroes(PakFileSystem fs)
    {
        foreach (var path in fs.Enumerate("MapObjects/").Where(p => p.EndsWith(".(AdvMapHeroShared).xdb", StringComparison.OrdinalIgnoreCase)))
        {
            XElement root;
            try { root = _x.Load(path).Root!; } catch (Exception e) { Warnings.Add($"{path}: {e.Message}"); continue; }
            var ed = root.Element("Editable");
            var h = new HeroDef
            {
                Id = root.Str("InternalName"), SourcePath = path, Class = root.Str("Class"),
                Specialization = root.Str("Specialization"), Town = root.Str("TownType"),
                Name = Text(ed, "NameFileRef", path),
                SpecializationName = Text(root, "SpecializationNameFileRef", path),
                SpecializationDescription = Text(root, "SpecializationDescFileRef", path),
            };
            var ps = root.Element("PrimarySkill");
            if (ps != null && ps.Str("SkillID") is { Length: > 0 } psId && psId != "HERO_SKILL_NONE")
                h.PrimarySkill = new SkillEntry(psId, MasteryParse.Parse(ps.Str("Mastery")));
            ReadEditable(ed, out h.Attack, out h.Defence, out h.SpellPower, out h.Knowledge, h.Skills, h.Perks, h.Spells,
                out h.Ballista, out h.FirstAidTent, out h.AmmoCart);
            if (h.Id.Length == 0) h.Id = Path.GetFileName(path).Split('.')[0];
            Heroes[h.Id] = h;
        }
    }

    static void ReadEditable(XElement? ed, out int att, out int def, out int sp, out int kn,
        List<SkillEntry> skills, List<string> perks, List<string> spells, out bool ballista, out bool tent, out bool cart)
    {
        att = ed.Int("Offence"); def = ed.Int("Defence"); sp = ed.Int("Spellpower"); kn = ed.Int("Knowledge");
        foreach (var s in ed.Items("skills"))
            skills.Add(new SkillEntry(s.Str("SkillID"), MasteryParse.Parse(s.Str("Mastery"))));
        perks.AddRange(ed.StrItems("perkIDs"));
        spells.AddRange(ed.StrItems("spellIDs"));
        ballista = ed.Bool("Ballista"); tent = ed.Bool("FirstAidTent"); cart = ed.Bool("AmmoCart");
    }

    public HeroInstance LoadHeroInstance(XElement root, string path)
    {
        var sharedPath = XdbRef.Parse(root.Href("Shared"), path).Path;
        Heroes.TryGetValue(Heroes.Values.FirstOrDefault(h => h.SourcePath.Equals(sharedPath, StringComparison.OrdinalIgnoreCase))?.Id ?? "", out var shared);
        var hi = new HeroInstance
        {
            SourcePath = path, Shared = shared, DisplayName = root.Str("Name"),
            Experience = root.Int("Experience"),
        };
        hi.Level = Experience.LevelFor(hi.Experience);
        hi.FavoriteEnemies.AddRange(root.Element("Editable").Items("FavoriteEnemies").Select(e => e.Value.Trim()).Where(v => v.StartsWith("CREATURE_")));
        foreach (var s in root.Items("armySlots"))
            if (s.Str("Creature") is { Length: > 0 } cr && cr != "CREATURE_UNKNOWN" && s.Int("Count") > 0)
                hi.Army.Add(new ArmySlot(cr, s.Int("Count")));
        hi.Artifacts.AddRange(root.StrItems("artifactIDs"));
        ReadEditable(root.Element("Editable"), out hi.Attack, out hi.Defence, out hi.SpellPower, out hi.Knowledge,
            hi.Skills, hi.Perks, hi.Spells, out hi.Ballista, out hi.FirstAidTent, out hi.AmmoCart);
        // The class's primary skill (e.g. Training for Knights) is always known.
        if (shared?.PrimarySkill is { } primary && hi.Skills.All(s => s.SkillId != primary.SkillId))
            hi.Skills.Insert(0, primary);
        return hi;
    }

    void LoadArtifactSets()
    {
        foreach (var set in Stats.Node("ArtifactSets/Sets")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            ArtifactSets[set.Str("Effect")] = set.Items("Artifacts").Select(a => a.Str("Artifact")).Where(a => a.Length > 0).ToList();
    }

    void LoadDuelPresets()
    {
        var root = _x.Load(DuelPresetsFile).Root!;
        int index = 0;
        foreach (var item in root.Items("presets"))
        {
            var heroRef = XdbRef.Parse(item.Href("PresetHero"), DuelPresetsFile);
            if (heroRef.IsEmpty || !_x.FileSystem.Exists(heroRef.Path)) { Warnings.Add($"duel preset {index}: missing hero"); index++; continue; }
            var hi = LoadHeroInstance(_x.Load(heroRef.Path).Root!, heroRef.Path);
            var name = _x.ReadText(item.Href("PresetNameFileRef"), DuelPresetsFile)?.Trim();
            DuelPresets.Add(new DuelPreset
            {
                Index = index++, Hero = hi, Name = string.IsNullOrEmpty(name) ? hi.DisplayName : name,
                PresetPrice = _x.Load(heroRef.Path).Root.Int("PresetPrice"),
            });
        }
    }
}

/// <summary>Hero experience thresholds (Editor Documentation, HOMM5_Hero_Level_and_Experience.pdf).</summary>
public sealed class ExperienceTable
{
    static readonly long[] _levels =
    {
        0, 1000, 2000, 3200, 4600, 6200, 8000, 10000, 12200, 14700, 17500, 20600, 24320, 28784, 34141, 40569,
        48283, 57539, 68647, 81977, 97972, 117166, 140200, 167839, 201007, 244126, 304491, 395040, 539917, 786208,
        1229533, 2071850, 3756484, 7294215, 15077224, 32978144, 75940353, 183345874, 462600229, 1216586989,
    };

    /// <summary>Experience needed for a level (1-based).</summary>
    public long For(int level) => _levels[Math.Clamp(level, 1, _levels.Length) - 1];

    public int LevelFor(long exp)
    {
        int lvl = 1;
        while (lvl < _levels.Length && exp >= _levels[lvl]) lvl++;
        return lvl;
    }
}

/// <summary>Path-addressable view of DefaultStats.xdb, e.g. Get("combat/Creatures/Haven/Bash_ProcFactor").</summary>
public sealed class RpgStats(XElement root)
{
    public XElement Root => root;

    public XElement? Node(string path)
    {
        XElement? e = root;
        foreach (var part in path.Split('/'))
        {
            if (e == null) return null;
            e = e.Element(part);
        }
        return e;
    }

    public double Get(string path, double def = 0) => XmlHelpers.ParseDouble(Node(path)?.Value, def);
    public string Str(string path, string def = "") => Node(path)?.Value.Trim() ?? def;
    public bool Bool(string path, bool def = false) => Node(path)?.Value.Trim() is { } s ? s == "true" : def;
    public double[] Array(string path) =>
        Node(path)?.Elements("Item").Select(i => XmlHelpers.ParseDouble(i.Value)).ToArray() ?? System.Array.Empty<double>();
}
