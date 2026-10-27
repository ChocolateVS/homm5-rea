using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using H5.Assets.Terrain;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Adventure.Map;

/// <summary>Reads an adventure map (map.xdb, terrain, texts, script) into an <see cref="AdvMap"/>.</summary>
public sealed class MapLoader
{
    readonly GameDatabase _db;
    readonly SharedCatalog _catalog;

    public MapLoader(GameDatabase db, PakFileSystem fs) : this(db, new SharedCatalog(new XdbLoader(fs))) { }

    public MapLoader(GameDatabase db, SharedCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public SharedCatalog Catalog => _catalog;

    public AdvMap Load(MapCatalog.MapRef map)
    {
        using var files = map.Open(_catalog.Xdb.FileSystem);
        var m = Load(files);
        m.Id = map.Id;
        return m;
    }

    public AdvMap Load(MapFiles files)
    {
        var path = files.MapXdbPath;
        var root = files.LoadXml(path).Root ?? throw new InvalidDataException($"{path}: empty");
        var m = new AdvMap
        {
            Id = files.MapDir, Source = files.Source,
            Version = root.Int("Version"), TilesX = root.Int("TileX"), TilesY = root.Int("TileY"),
            HasSurface = root.Bool("HasSurface", true), HasUnderground = root.Bool("HasUnderground"),
            BorderSize = Math.Clamp(root.Int("BorderSize", 1), 1, 20),
            InitialFloor = root.Int("InitialFloor"), CustomGameMap = root.Bool("CustomGameMap"),
            HeroMaxLevel = root.Int("HeroMaxLevel"), CustomTeams = root.Bool("CustomTeams"),
            RandomMoons = root.Bool("RandomMoons"),
            // Map moons (State, RotationRate; read by 0041bdd0 via 00508680), MoonCalendar (+0x244) and
            // MoonCalendarModifications/BlockMonstersWeeks (+0x224, reader 00ad6160).
            Moons = root.Items("moons").Select(i => (i.Int("State"), i.Int("RotationRate"))).ToList(),
            MoonCalendar = root.Href("MoonCalendar") is { Length: > 0 } mc ? XdbRef.Parse(mc, path).Path : null,
            BlockMonstersWeeks = root.Element("MoonCalendarModifications").Bool("BlockMonstersWeeks"),
            Name = files.ReadText(root.Href("NameFileRef"))?.Trim() ?? "",
            Description = files.ReadText(root.Href("DescriptionFileRef"))?.Trim() ?? "",
            SpellIds = root.StrItems("spellIDs"), ArtifactIds = root.StrItems("artifactIDs"),
            AvailableHeroes = root.Items("AvailableHeroes").Select(i => i.Value.Trim()).Where(s => s.Length > 0).ToList(),
            Rumours = root.Items("MapRumours")
                .Select(i => new H5.Adventure.Data.RumourDef(files.ReadText(i.Href("Text"))?.Trim() ?? "", i.Int("Weight"), i.Str("TownType")))
                .Where(r => r.Text.Length > 0).ToList(),
        };

        ReadPlayers(root, m);
        ReadReserveHeroes(root, m, files);
        ReadObjectives(root, m, files);
        ReadScript(root, m, files);

        if (m.HasSurface || !m.HasUnderground)
            m.Floors[0] = ReadFloor(files, root.Href("GroundTerrainFileName"), 0, m);
        if (m.HasUnderground)
            m.Floors[1] = ReadFloor(files, root.Href("UndergroundTerrainFileName"), 1, m);

        int index = 0;
        foreach (var item in root.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var e = item.Elements().FirstOrDefault();
            if (e == null) continue;
            try
            {
                var o = ReadObject(e, files);
                o.Index = index++;
                o.Uid = (string?)item.Attribute("id") ?? "";
                m.Objects.Add(o);
            }
            catch (Exception ex) { m.Warnings.Add($"object {e.Name.LocalName}: {ex.Message}"); }
        }
        ApplyStaticBlocking(m);
        return m;
    }

    // ----------------------------------------------------------------- players & goals

    static readonly Regex XPointerId = new(@"id\((item_[^)]+)\)", RegexOptions.Compiled);

    static string? UidFromHref(string? href) =>
        href == null ? null : XPointerId.Match(href) is { Success: true } mt ? mt.Groups[1].Value : null;

    static void ReadPlayers(XElement root, AdvMap m)
    {
        int i = 0;
        foreach (var p in root.Items("players"))
        {
            var tf = p.Element("TavernFilter");
            m.Players.Add(new MapPlayer
            {
                Index = i++, Active = p.Bool("ActivePlayer"), Team = p.Int("Team"),
                CanBeHuman = p.Bool("CanBeHumanPlayer"), CanBeComputer = p.Bool("CanBeComputerPlayer"),
                Race = p.Str("Race"), Colour = p.Str("Colour"), Behaviour = p.Str("Behaviour"),
                MainTownUid = UidFromHref(p.Href("MainTown")), MainHeroUid = UidFromHref(p.Href("MainHero")),
                HeroInTown = p.Bool("HeroInTown"), DefaultBonus = p.Str("DefaultBonus"),
                CanBeDisabled = p.Bool("CanBeDisabled"), CanChangeBonus = p.Bool("CanChangeBonus"),
                ReserveHeroes = p.Items("ReserveHeroes").Select(x => x.Value.Trim()).Where(s => s.Length > 0).ToList(),
                BannedHeroes = tf.StrItems("BannedHeroes"), AllowedHeroes = tf.StrItems("AllowedHeroes"),
                BannedHeroRaces = tf.StrItems("BannedHeroesRaces"),
            });
        }
    }

    static void ReadObjectives(XElement root, AdvMap m, MapFiles files)
    {
        var obj = root.Element("Objectives");
        if (obj == null) return;
        foreach (var (group, primary) in new[] { ("Primary", true), ("Secondary", false) })
        {
            var g = obj.Element(group);
            if (g == null) continue;
            if (primary) m.DieInWeekWithoutTowns = g.Element("Common").Bool("DieInWeekWithoutTowns");
            foreach (var o in g.Element("Common").Items("Objectives"))
                m.Objectives.Add(ReadObjective(o, primary, -1, files));
            int player = 0;
            foreach (var ps in g.Items("PlayerSpecific"))
            {
                foreach (var o in ps.Items("Objectives"))
                    m.Objectives.Add(ReadObjective(o, primary, player, files));
                player++;
            }
        }
    }

    static MapObjective ReadObjective(XElement o, bool primary, int player, MapFiles files) => new()
    {
        Primary = primary, Player = player, Name = o.Str("Name"), Kind = o.Str("Kind"),
        Parameters = o.StrItems("Parameters"), Timeout = o.Int("Timeout", -1), Holdout = o.Int("Holdout", -1),
        InstantVictory = o.Bool("InstantVictory"), InitiallyActive = o.Bool("IsInitialyActive", true),
        NeedComplete = o.Bool("NeedComplete", true),
        Caption = files.ReadText(o.Href("CaptionFileRef"))?.Trim(),
    };

    static void ReadScript(XElement root, AdvMap m, MapFiles files)
    {
        var href = root.Href("MapScript");
        if (href == null) return;
        var scriptXdb = files.Resolve(href);
        if (!files.Exists(scriptXdb)) return;
        var s = files.LoadXml(scriptXdb).Root;
        var file = s.Href("FileName");
        var lua = file != null ? files.Resolve(file, scriptXdb) : scriptXdb[..scriptXdb.LastIndexOf('.')] + ".lua";
        m.ScriptPath = lua;
        if (files.TryReadAllBytes(lua, out var b)) m.ScriptSource = TextDecoding.Decode(b);
    }

    // ----------------------------------------------------------------- terrain

    MapFloor? ReadFloor(MapFiles files, string? href, int floor, AdvMap m)
    {
        if (href == null) return null;
        var p = files.Resolve(href);
        if (!files.TryReadAllBytes(p, out var bytes)) { m.Warnings.Add($"floor {floor}: missing {p}"); return null; }
        var t = TerrainReader.Read(bytes);
        int w = t.TilesX, h = t.TilesY;
        var f = new MapFloor
        {
            Index = floor, Width = w, Height = h, Data = t,
            Terrain = new TerrainClass[w * h], TileType = new string[w * h], Blocked = new bool[w * h],
        };
        var layerTypes = t.Layers.Select(l => _catalog.TileType(l.TilePath)).ToArray();
        f.LayerTypes = layerTypes.Select(TerrainPassability.TypeId).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // The tile's type is the topmost layer that covers at least half of it
                // (average of its four corner weights); layer 0 is the base.
                int chosen = 0;
                for (int li = t.Layers.Count - 1; li > 0; li--)
                {
                    var g = t.Layers[li].Weights;
                    int sum = g[x, y] + g[x + 1, y] + g[x, y + 1] + g[x + 1, y + 1];
                    if (sum >= 4 * 128) { chosen = li; break; }
                }
                var type = layerTypes.Length > 0 ? layerTypes[chosen] : "";
                f.TileType[y * w + x] = type;
                f.Terrain[y * w + x] = SharedCatalog.Classify(type);
            }
        return f;
    }

    /// <summary>
    /// Static passability, as the exe sets up the world: the terrain kinds of every floor (<see cref="TerrainPassability"/>,
    /// 00990800, with the objects' passable tiles), sea from the terrain kind, then the objects' blocked tiles that are
    /// not entrances, and the blocked set of the pathfinder's object map (<see cref="MapFloor.ObjectBlocked"/>). The
    /// object pass 009952d0 (00991650 / 00990bc0 / 00990d40) only writes the tile's object fields (+0x10 / +0x14), never
    /// the kind; objects' action tiles stay enterable over any terrain (<see cref="MapObject.ActionTiles"/>, applied by
    /// the pathfinder, not here).
    /// </summary>
    public static void ApplyStaticBlocking(AdvMap m)
    {
        for (int fi = 0; fi < m.Floors.Length; fi++)
        {
            var f = m.Floors[fi];
            if (f?.Data == null) continue;
            var open = new HashSet<(int, int)>();
            foreach (var o in m.Objects)
                if (o.Pos.Floor == fi)
                    foreach (var p in o.Shared.Passable)
                    {
                        var t = Footprint.Place(o.Pos, p, o.Rot);
                        open.Add((t.X, t.Y));
                    }
            TerrainPassability.Build(f, m.BorderSize, f.LayerTypes, open);
            for (int i = 0; i < f.Terrain.Length && i < f.Kind.Length; i++)
            {
                if (f.Kind[i] == TileKind.Water) f.Terrain[i] = TerrainClass.Water;
                else if (f.Terrain[i] is TerrainClass.Water or TerrainClass.River)
                    f.Terrain[i] = SharedCatalog.Classify(TerrainPassability.TypeNames.ElementAtOrDefault(f.Ground[i]) ?? "");
                f.Blocked[i] = f.TerrainBlocked(i % f.Width, i / f.Width);
            }
        }
        foreach (var f in m.Floors)
            if (f != null) f.ObjectBlocked = new bool[f.Width * f.Height];
        foreach (var o in m.Objects)
        {
            var f = m.Floor(o.Pos.Floor);
            if (f == null) continue;
            var active = new HashSet<Tile>(o.ActiveTiles);
            bool multi = o.Kind != ObjectKind.Hero && o.MultiTile;
            foreach (var b in o.BlockedTiles)
            {
                if (!f.InBounds(b.X, b.Y)) continue;
                if (!active.Contains(b)) f.Blocked[b.Y * f.Width + b.X] = true;
                if (multi) f.ObjectBlocked[b.Y * f.Width + b.X] = true;
            }
        }
    }

    // ----------------------------------------------------------------- objects

    static ObjectKind KindOf(string tag) => tag switch
    {
        "AdvMapStatic" => ObjectKind.Static,
        "AdvMapTreasure" => ObjectKind.Treasure,
        "AdvMapMonster" => ObjectKind.Monster,
        "AdvMapBuilding" => ObjectKind.Building,
        "AdvMapArtifact" => ObjectKind.Artifact,
        "AdvMapMine" => ObjectKind.Mine,
        "AdvMapAbanMine" => ObjectKind.AbandonedMine,
        "AdvMapDwelling" => ObjectKind.Dwelling,
        "AdvMapShrine" => ObjectKind.Shrine,
        "AdvMapTown" => ObjectKind.Town,
        "AdvMapHero" => ObjectKind.Hero,
        "AdvMapPrison" => ObjectKind.Prison,
        "AdvMapGarrison" => ObjectKind.Garrison,
        "AdvMapShipyard" => ObjectKind.Shipyard,
        "AdvMapTent" => ObjectKind.Tent,
        "AdvMapSign" => ObjectKind.Sign,
        "AdvMapHillFort" => ObjectKind.HillFort,
        "AdvMapSeerHut" => ObjectKind.SeerHut,
        "AdvMapDwarvenWarren" => ObjectKind.DwarvenWarren,
        "AdvMapCartographer" => ObjectKind.Cartographer,
        "AdvMapSphinx" => ObjectKind.Sphinx,
        "AdvMapStand" => ObjectKind.Stand,
        _ => ObjectKind.Unknown,
    };

    public static int PlayerIndex(string id) =>
        id.StartsWith("PLAYER_", StringComparison.Ordinal) && int.TryParse(id[7..], out var n) && n >= 1 ? n - 1 : -1;

    static List<ArmySlot> Army(XElement e, string name) =>
        e.Items(name).Where(s => s.Str("Creature") is { Length: > 0 } c && c != "CREATURE_UNKNOWN" && s.Int("Count") > 0)
            .Select(s => new ArmySlot(s.Str("Creature"), s.Int("Count"))).ToList();

    /// <summary>RndSource / LinkToPlayer / LinkToTown of a town or dwelling (readers 00a0a490 / 00a09de0).</summary>
    static void ReadRandomLink(XElement e, MapObject o)
    {
        o.RndSource = e.Str("RndSource") switch { "RND_PLAYER" => 1, "RND_TOWN" => 2, "RND_OWN" => 3, _ => 0 };
        o.LinkToPlayer = PlayerIndex(e.Str("LinkToPlayer"));
        o.LinkToTownUid = UidFromHref(e.Href("LinkToTown"));
        o.PlacedSharedPath = o.SharedPath;
        o.PlacedShared = o.Shared;
        o.PlacedTownType = o.TownType;
    }

    /// <summary>
    /// A Pos component as the exe turns it into a tile: the float (SAdvMapObject +0x44 / +0x48, reader 00a082d0)
    /// rounded to the nearest, ties to even (FISTP in the object set-up 0041d360 / 0041cfe0). Editor maps store
    /// many statics at fractional positions ("9.00006", "33.5"); an integer parse put them at 0.
    /// </summary>
    static int TileCoord(XElement? pos, string name) =>
        (int)MathF.Round((float)pos.Dbl(name), MidpointRounding.ToEven);

    MapObject ReadObject(XElement e, MapFiles files)
    {
        var tag = e.Name.LocalName;
        var pos = e.Element("Pos");
        var o = new MapObject
        {
            Tag = tag, Kind = KindOf(tag), Name = e.Str("Name"),
            Pos = new Tile(TileCoord(pos, "x"), TileCoord(pos, "y"), Math.Clamp(e.Int("Floor"), 0, 1)),
            Rot = (float)e.Dbl("Rot"),
            Owner = PlayerIndex(e.Str("PlayerID")),
        };
        var sharedHref = e.Href("Shared");
        if (sharedHref != null)
        {
            o.SharedPath = files.Resolve(sharedHref);
            o.Shared = _catalog.Get(o.SharedPath, p => files.Exists(p) ? files.LoadXml(p).Root : null);
        }
        foreach (var c in e.Elements())
            if (!c.HasElements && c.Attribute("href") == null && c.Value.Length > 0)
                o.Props[c.Name.LocalName] = c.Value.Trim();
        o.Guards = Army(e, "armySlots");
        if (e.Href("MessageFileRef") is { Length: > 0 } msg) o.MessageText = files.ReadText(msg)?.Trim();
        if (e.Element("ShipTile") is { } shipTile) o.ShipTile = (shipTile.Int("x"), shipTile.Int("y"));

        switch (o.Kind)
        {
            case ObjectKind.Monster:
                o.Creature = o.Shared.Creature;
                o.MonsterRandomLevel = o.Shared.Type switch
                {
                    "MONSTER_RANDOM_ANY" => 8,
                    { } t when t.StartsWith("MONSTER_RANDOM_L") => int.Parse(t[^1..], CultureInfo.InvariantCulture),
                    _ => 0,
                };
                o.CustomAmount = e.Bool("Custom");
                o.Amount = e.Int("Amount"); o.Amount2 = e.Int("Amount2");
                o.Mood = e.Str("Mood"); o.Courage = e.Str("Courage");
                o.DoesNotGrow = e.Bool("DoesNotGrow");
                var res = Resources.Parse(e.Element("Resources"));
                if (res != new Resources(0, 0, 0, 0, 0, 0, 0)) o.Reward = res;
                o.RewardArtifact = e.Str("ArtifactID") is { Length: > 0 } a && a != "ARTIFACT_NONE" ? a : null;
                foreach (var s in e.Items("AdditionalStacks"))
                {
                    var cr = s.Str("Creature");
                    if (cr.Length > 0 && cr != "CREATURE_UNKNOWN") o.AdditionalStacks.Add(new ArmySlot(cr, s.Int("Amount", s.Int("Count"))));
                }
                break;
            case ObjectKind.Artifact:
                o.ArtifactId = o.Shared.ArtifactId;
                o.SpellId = e.Str("spellID") is { Length: > 0 } sp && sp != "SPELL_NONE" ? sp : null;
                break;
            case ObjectKind.Shrine:
                o.SpellId = e.Str("SpellID") is { Length: > 0 } ss && ss != "SPELL_NONE" ? ss : null;
                break;
            case ObjectKind.SeerHut when e.Element("Quest") is { } q:
            {
                var aw = q.Element("Award");
                var awardRes = aw?.Element("Resources");
                string? Id(string? v, string none) => string.IsNullOrEmpty(v) || v == none ? null : v;
                o.Quest = new SeerQuest
                {
                    Objective = ReadObjective(q, false, -1, files),
                    Description = files.ReadText(q.Href("DescriptionFileRef"))?.Trim(),
                    TakeContribution = q.Bool("TakeContribution"),
                    AwardType = aw.Str("Type") is { Length: > 0 } at ? at : "AWARD_NONE",
                    Experience = aw.Int("Experience"),
                    Resources = new[] { "Wood", "Ore", "Mercury", "Crystal", "Sulfur", "Gem", "Gold" }.Select(r => awardRes.Int(r)).ToArray(),
                    Attribute = aw.Str("Attribute"), AttributeAmount = aw.Int("AttributeAmount"),
                    Artifact = Id(aw.Str("ArtifactID"), "ARTIFACT_NONE"), Spell = Id(aw.Str("SpellID"), "SPELL_NONE"),
                    Creature = Id(aw?.Element("ArmySlot").Str("Creature"), "CREATURE_UNKNOWN"), CreatureCount = aw?.Element("ArmySlot").Int("Count") ?? 0,
                    SpellPoints = aw.Int("SpellPoints"), Morale = aw.Int("Morale"), Luck = aw.Int("Luck"),
                    Skill = Id(aw?.Element("SkillWithMastery").Str("SkillID"), "HERO_SKILL_NONE"), Mastery = aw?.Element("SkillWithMastery").Str("Mastery") ?? "MASTERY_NONE",
                };
                break;
            }
            case ObjectKind.HillFort when e.Element("CreaturesUpgradesFilter") is { } f:
                o.HillFortFilter = ReadUpgradesFilter(f);
                break;
            case ObjectKind.Sphinx:
            {
                // AdvMapSphinx reader 00a02330: Shared, Riddle (href), RandomRiddle.
                o.SphinxRandomRiddle = e.Bool("RandomRiddle");
                var rp = files.Resolve(e.Href("Riddle"));
                if (!o.SphinxRandomRiddle && rp.Length > 0 && files.Exists(rp) && files.LoadXml(rp).Root is { } rr)
                    o.SphinxRiddle = Data.SphinxRiddle.Parse(rr, (h, cur) => files.ReadText(h, cur ?? rp));
                break;
            }
            case ObjectKind.Treasure:
                o.TreasureCustom = e.Bool("IsCustom"); o.TreasureAmount = e.Int("Amount");
                break;
            case ObjectKind.Dwelling:
                o.RandomCreatures = e.Bool("RandomCreatures");
                o.CreaturesEnabled = e.Items("creaturesEnabled").Select(i => i.Value.Trim() == "1" || i.Value.Trim() == "true").ToArray();
                ReadRandomLink(e, o);
                break;
            case ObjectKind.Town:
                o.TownType = o.Shared.TownType;
                ReadRandomLink(e, o);
                o.Specialization = files.Resolve(e.Href("Specialization")) is { Length: > 0 } sp2 ? sp2 : null;
                foreach (var b in e.Items("buildings"))
                    o.Buildings.Add(new TownBuildingInit(b.Str("Type"), UpgradeLevel(b.Str("InitialUpgrade")), UpgradeLevel(b.Str("MaxUpgrade"))));
                o.TownSpells = e.StrItems("spellIDs");
                // AdvMapTown reader 00a0a490: CreaturesUpgradesFilter at +0x130 through the same reader 00a0f620 as the Hill Fort's.
                if (e.Element("CreaturesUpgradesFilter") is { } tf) o.TownFilter = ReadUpgradesFilter(tf);
                if (e.Element("GarrisonHero")?.Element("AdvMapHero") is { } gh && gh.Element("Shared") != null)
                    o.GarrisonHero = LoadHero(gh, files);
                break;
            case ObjectKind.Hero:
            case ObjectKind.Prison:
                o.OverrideMask = e.Int("OverrideMask");
                if (o.Kind == ObjectKind.Hero || e.Element("Editable") != null)
                    o.Hero = LoadHero(e, files);
                break;
        }
        return o;
    }

    /// <summary>
    /// A CreaturesUpgradesFilter (reader 00a0f620, shared by AdvMapHillFort +0x84 and AdvMapTown +0x130):
    /// ForbiddenBasicUpgradeTiers, ForbiddenAlterUpgradeTiers, NotUpgradeable, ForbiddenUpgrades.
    /// </summary>
    public static UpgradesFilter ReadUpgradesFilter(XElement f)
    {
        HashSet<int> Tiers(string n) => f.Items(n).Select(i => int.TryParse(i.Value.Trim(), out var t) ? t : 0).Where(t => t > 0).ToHashSet();
        HashSet<string> Ids(string n) => f.Items(n).Select(i => i.Value.Trim()).Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);
        return new UpgradesFilter(Tiers("ForbiddenBasicUpgradeTiers"), Tiers("ForbiddenAlterUpgradeTiers"), Ids("NotUpgradeable"), Ids("ForbiddenUpgrades"));
    }

    /// <summary>
    /// 009aacb0 combines the shared hero's Editable (AdvMapHeroShared +0x168) with the map hero's (AdvMapHero +0xb0, reader
    /// 00a0c660) under its OverrideMask (+0x114) by 009a9cf0: TalismanLevel (+0x60) is the map hero's with mask bit 0x40
    /// set, else the larger of the two.
    /// </summary>
    public static int CombineTalismanLevel(int overrideMask, int shared, int map) =>
        (overrideMask & 0x40) != 0 ? map : Math.Max(shared, map);

    /// <summary>Reserve heroes are inline AdvMapHero objects in each player's &lt;ReserveHeroes&gt; list.</summary>
    void ReadReserveHeroes(XElement root, AdvMap m, MapFiles files)
    {
        int i = 0;
        foreach (var p in root.Items("players"))
        {
            if (i >= m.Players.Count) break;
            var mp = m.Players[i++];
            mp.ReserveHeroes.Clear();
            foreach (var item in p.Items("ReserveHeroes"))
            {
                if (item.Element("AdvMapHero") is { } h)
                {
                    var hi = LoadHero(h, files);
                    mp.ReserveHeroInstances.Add(hi);
                    if (hi.Shared?.Id is { Length: > 0 } id) mp.ReserveHeroes.Add(id);
                }
                else if ((string?)item.Attribute("href") is { Length: > 0 } href && !href.StartsWith('#'))
                {
                    var path = XdbRef.Parse(href, files.MapXdbPath).Path;
                    var def = _db.Heroes.Values.FirstOrDefault(d => d.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (def != null) mp.ReserveHeroes.Add(def.Id);
                }
            }
        }
    }

    /// <summary>
    /// Some campaign heroes use shared files not named *.(AdvMapHeroShared).xdb (e.g.
    /// MapObjects/Stronghold/Gottai.xdb), which the database's catalogue scan does not pick up;
    /// define such a hero from its file on first use so map heroes always have their identity.
    /// </summary>
    void EnsureHeroDef(XElement heroObject, MapFiles files)
    {
        var path = XdbRef.Parse(heroObject.Href("Shared"), files.MapXdbPath).Path;
        if (path.Length == 0 || _db.Heroes.Values.Any(h => h.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        if (!files.Exists(path)) return;
        var root = files.LoadXml(path).Root;
        if (root == null || root.Name.LocalName != "AdvMapHeroShared") return;
        var ed = root.Element("Editable");
        var def = new HeroDef
        {
            Id = root.Str("InternalName") is { Length: > 0 } id ? id : Path.GetFileName(path).Split('.')[0],
            SourcePath = path, Class = root.Str("Class"), Specialization = root.Str("Specialization"), Town = root.Str("TownType"),
            Name = files.ReadText(ed.Href("NameFileRef"), path)?.Trim() ?? "",
        };
        var ps = root.Element("PrimarySkill");
        if (ps != null && ps.Str("SkillID") is { Length: > 0 } psId && psId != "HERO_SKILL_NONE")
            def.PrimarySkill = new SkillEntry(psId, MasteryParse.Parse(ps.Str("Mastery")));
        def.Attack = ed.Int("Offence"); def.Defence = ed.Int("Defence"); def.SpellPower = ed.Int("Spellpower"); def.Knowledge = ed.Int("Knowledge");
        foreach (var s in ed.Items("skills")) def.Skills.Add(new SkillEntry(s.Str("SkillID"), MasteryParse.Parse(s.Str("Mastery"))));
        def.Perks.AddRange(ed.StrItems("perkIDs"));
        def.Spells.AddRange(ed.StrItems("spellIDs"));
        def.Ballista = ed.Bool("Ballista"); def.FirstAidTent = ed.Bool("FirstAidTent"); def.AmmoCart = ed.Bool("AmmoCart");
        def.FavoriteEnemies.AddRange(ed.Items("FavoriteEnemies").Select(e => e.Value.Trim()).Where(v => v.StartsWith("CREATURE_")));
        def.TalismanLevel = ed.Int("TalismanLevel");
        // Campaign variants reuse a stock hero's InternalName (RedHeaven07_Orlando is "Orlando"):
        // keep both definitions, the variant under its file path; scripts still see def.Id.
        if (!_db.Heroes.TryAdd(def.Id, def)) _db.Heroes[path] = def;
        _db.HeroesByPath[def.SourcePath] = def;
    }

    /// <summary>"BLD_UPG_NONE" → 0, "BLD_UPG_1" → 1 … "BLD_UPG_5" → 5.</summary>
    public static int UpgradeLevel(string s) =>
        s.StartsWith("BLD_UPG_", StringComparison.Ordinal) && int.TryParse(s[8..], out var n) ? n : 0;

    HeroInstance LoadHero(XElement e, MapFiles files)
    {
        EnsureHeroDef(e, files);
        var hi = _db.LoadHeroInstance(e, files.MapXdbPath);
        // A placed hero only overrides the shared hero's editable data where the map
        // author changed it; empty sections fall back to the shared definition.
        // TODO(exe): confirm the exact OverrideMask bit meanings (observed: 1 = name).
        if (hi.Shared is { } sh)
        {
            if (hi.Attack == 0 && hi.Defence == 0 && hi.SpellPower == 0 && hi.Knowledge == 0)
            { hi.Attack = sh.Attack; hi.Defence = sh.Defence; hi.SpellPower = sh.SpellPower; hi.Knowledge = sh.Knowledge; }
            if (hi.Skills.All(s => s.SkillId == sh.PrimarySkill?.SkillId))
                foreach (var s in sh.Skills) if (hi.Skills.All(x => x.SkillId != s.SkillId)) hi.Skills.Add(s);
            if (hi.Perks.Count == 0) hi.Perks.AddRange(sh.Perks);
            if (hi.Spells.Count == 0) hi.Spells.AddRange(sh.Spells);
            if (hi.FavoriteEnemies.Count == 0) hi.FavoriteEnemies.AddRange(sh.FavoriteEnemies);
            if (!hi.Ballista && !hi.FirstAidTent && !hi.AmmoCart)
            { hi.Ballista = sh.Ballista; hi.FirstAidTent = sh.FirstAidTent; hi.AmmoCart = sh.AmmoCart; }
            hi.TalismanLevel = CombineTalismanLevel(e.Int("OverrideMask"), sh.TalismanLevel, hi.TalismanLevel);
        }
        var nameRef = e.Element("Editable").Href("NameFileRef");
        if ((e.Int("OverrideMask") & 1) != 0 && files.ReadText(nameRef)?.Trim() is { Length: > 0 } name)
            hi.DisplayName = name;
        else if (hi.Shared != null && string.IsNullOrEmpty(hi.DisplayName))
            hi.DisplayName = hi.Shared.Name;
        return hi;
    }
}
