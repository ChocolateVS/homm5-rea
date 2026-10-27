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
    /// <summary>
    /// Heroes by InternalName. A few shared heroes reuse another's InternalName (Tan/Cyrus,
    /// Orlando Haven/Inferno); the entry whose file name matches the name wins here, and every
    /// variant stays reachable through <see cref="HeroesByPath"/> / <see cref="HeroesByName"/>.
    /// </summary>
    public Dictionary<string, HeroDef> Heroes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every shared hero by its (AdvMapHeroShared) xdb path.</summary>
    public Dictionary<string, HeroDef> HeroesByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>InternalName → all shared heroes using it.</summary>
    public Dictionary<string, List<HeroDef>> HeroesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DuelPreset> DuelPresets { get; } = new();
    /// <summary>Artifact set effect id (ARTFSET_EFFECT_*) → member artifacts (DefaultStats ArtifactSets).</summary>
    public Dictionary<string, List<string>> ArtifactSets { get; } = new();
    /// <summary>Combat arena scenes by name (Scenes/CombatArenas), with their obstacle groups.</summary>
    public Dictionary<string, CombatArenaDef> Arenas { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Terrain/size → arena lists the original picks battlefields from (CombatArenaTypes.xdb).</summary>
    public CombatArenaTable ArenaTable { get; private set; } = new();
    /// <summary>Siege layout per town type (TOWN_HEAVEN, …): walls, gate, towers, moat, siege arena.</summary>
    public Dictionary<string, TownCombatDef> TownCombat { get; private set; } = new();
    /// <summary>Fortification hit points by building type (WALL, GATE, BIG_TOWER, …).</summary>
    public Dictionary<string, ArenaBuildingStatsDef> ArenaBuildingStats { get; private set; } = new();
    /// <summary>Moat damage and effects per town type.</summary>
    public Dictionary<string, MoatDef> Moats { get; private set; } = new();
    public RpgStats Stats { get; private set; } = null!;
    /// <summary>DefaultStats combat/Creatures/Haven/ChampionChargePattern and combat/Creatures/Dungeon/AcidicHydraPattern.</summary>
    public AttackPattern? ChampionChargePattern { get; private set; }
    public AttackPattern? AcidicHydraPattern { get; private set; }
    /// <summary>DefaultStats combat/Creatures/ConjuredPhoenix: the RPGCombatUniqueCreatureStats record (exe RPGStats +0x530).</summary>
    public System.Xml.Linq.XElement? ConjuredPhoenix { get; private set; }
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
        db.LoadStatsPatterns();
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
        db.ArenaTable = CombatArenaTable.Load(db._x);
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
                Id = id, SourcePath = f, Stock = StockTables.Kept(_x.FileSystem, CreatureTable, id),
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
            c.Pattern = ParsePattern(_x.Resolve(o.Element("PatternAttack"), f));
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

    static AttackPattern? ParsePattern(XElement? e)
    {
        if (e == null) return null;
        var p = new AttackPattern
        {
            RotateToMainTarget = e.Bool("RotateToMainTarget"),
            MainCoefficient = (float)e.Dbl("DamageToMainTargetCoefficient", 1),
            OtherCoefficient = (float)e.Dbl("DamageToOtherTargetsCoefficient", 1),
            DamageAll = e.Bool("DamageAll"),
        };
        foreach (var r in e.Items("Patterns"))
            p.Rotations.Add((r.Items("cells").Select(c => (c.Int("x"), c.Int("y"))).ToList(), (float)r.Dbl("AngleRotateTo")));
        return p.Rotations.Count == 0 ? null : p;
    }

    void LoadStatsPatterns()
    {
        var creatures = _x.Load(StatsFile).Root?.Element("combat")?.Element("Creatures");
        ChampionChargePattern = ParsePattern(_x.Resolve(creatures?.Element("Haven")?.Element("ChampionChargePattern"), StatsFile));
        AcidicHydraPattern = ParsePattern(_x.Resolve(creatures?.Element("Dungeon")?.Element("AcidicHydraPattern"), StatsFile));
        ConjuredPhoenix = _x.Resolve(creatures?.Element("ConjuredPhoenix"), StatsFile);
    }

    void LoadSpells()
    {
        foreach (var (id, o, f) in ReadTable(SpellTable, "Obj"))
        {
            if (o.Name.LocalName != "Spell") continue;
            Spells[id] = new SpellDef
            {
                Id = id, SourcePath = f, Stock = StockTables.Kept(_x.FileSystem, SpellTable, id),
                Name = Text(o, "NameFileRef", f),
                Description = Text(o, "LongDescriptionFileRef", f),
                Level = o.Int("Level"), School = o.Str("MagicSchool"), RequiredHeroLevel = o.Int("RequiredHeroLevel"),
                IsAimed = o.Bool("IsAimed"), ManaCost = o.Int("TrainedCost"),
                Damage = o.Items("damage").Select(PowerFormula.Parse).ToArray(),
                Duration = o.Items("duration").Select(PowerFormula.Parse).ToArray(),
                IsAreaAttack = o.Bool("IsAreaAttack"), CanSelectDead = o.Bool("CanSelectDead"),
                Target = o.Str("Target"), Element = o.Str("Element", "ELEMENT_NONE"),
                ResourceCost = o.Element("sSpellCost") is { } sc && Resources.Parse(sc) is var rc && rc != new Resources(0, 0, 0, 0, 0, 0, 0) ? rc : null,
            };
        }
    }

    /// <summary>AIRacesValues children in town-type order (TOWN_HEAVEN = 3 … TOWN_STRONGHOLD = 10).</summary>
    static readonly string[] AiRaces = { "Haven", "Sylvan", "Academy", "Dungeon", "Necropolis", "Inferno", "Fortress", "Stronghold" };

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
            if (o.Element("AIRacesValues") is { } ai)
                for (int r = 0; r < AiRaces.Length; r++)
                {
                    var v = ai.Element(AiRaces[r]);
                    s.AiRaceValues[3 * r] = v.Int("CommanderValue");
                    s.AiRaceValues[3 * r + 1] = v.Int("CollectorSupplierValue");
                    s.AiRaceValues[3 * r + 2] = v.Int("FreelancerValue");
                }
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
                Id = id, Stock = StockTables.Kept(_x.FileSystem, ArtifactTable, id),
                Name = Text(o, "NameFileRef", f), Description = Text(o, "DescriptionFileRef", f),
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
        foreach (var (id, o, f) in ReadTable(WarMachineTable, "Obj"))
            WarMachines[id] = new WarMachineDef
            {
                VisualPath = XdbRef.Parse(o.Href("Visual"), f).Path is { Length: > 0 } vp ? vp : null,
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
            h.FavoriteEnemies.AddRange(ed.Items("FavoriteEnemies").Select(e => e.Value.Trim()).Where(v => v.StartsWith("CREATURE_")));
            if (h.Id.Length == 0) h.Id = Path.GetFileName(path).Split('.')[0];
            RegisterHero(h);
        }
    }

    /// <summary>Adds a shared hero to the lookups (also used for map-embedded/campaign variants).</summary>
    public void RegisterHero(HeroDef h)
    {
        HeroesByPath[h.SourcePath] = h;
        if (!HeroesByName.TryGetValue(h.Id, out var list)) HeroesByName[h.Id] = list = new();
        list.RemoveAll(x => x.SourcePath.Equals(h.SourcePath, StringComparison.OrdinalIgnoreCase));
        list.Add(h);
        static bool NameMatches(HeroDef d) => Path.GetFileName(d.SourcePath).Split('.')[0].Equals(d.Id, StringComparison.OrdinalIgnoreCase);
        if (!Heroes.TryGetValue(h.Id, out var cur) || (!NameMatches(cur) && NameMatches(h))) Heroes[h.Id] = h;
    }

    public HeroDef? HeroByPath(string sharedPath) =>
        HeroesByPath.TryGetValue(PakFileSystem.Normalize(sharedPath), out var h) ? h : null;

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
        var shared = HeroByPath(sharedPath);
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

/// <summary>
/// Hero experience thresholds as H5_Game.exe builds them (00b4c6b0, table at 01026d90 indexed by level): levels 1..12
/// are the constants at 00f755e0; each later step is the previous step × 1.2 (00e12f1c) up to level 24, then
/// × (level − 13) × 0.1 (00e12f10), rounded to nearest and clamped to [0, 2³¹−1]; level 41 and above read 2³¹−1.
/// The products are single precision: D3D9 CreateDevice (00a3fe20, flags 0x50 / 0x20) is called without
/// D3DCREATE_FPU_PRESERVE, so the x87 runs at 24-bit precision (only levels 37–40 depend on this). The Editor
/// Documentation table (HOMM5_Hero_Level_and_Experience.pdf) used before is 1 lower from level 18 on and drifts.
/// </summary>
public sealed class ExperienceTable
{
    static readonly long[] _levels = Build();

    static long[] Build()
    {
        int[] first = { 0, 1000, 2000, 3200, 4600, 6200, 8000, 10000, 12200, 14700, 17500, 20600 };
        var t = new long[40];
        for (int i = 0; i < first.Length; i++) t[i] = first[i];
        int step = first[^1] - first[^2], total = first[^1];
        for (int level = 13; level <= 40; level++)
        {
            float x = level <= 24 ? (float)step * 1.2f : (float)(level - 13) * (float)step * 0.1f;
            step = (int)MathF.Round(x, MidpointRounding.ToEven);
            total = (int)Math.Clamp((long)total + step, 0, int.MaxValue);
            t[level - 1] = total;
        }
        return t;
    }

    /// <summary>Experience needed for a level (1-based).</summary>
    public long For(int level) => level > _levels.Length ? int.MaxValue : _levels[Math.Max(level, 1) - 1];

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
