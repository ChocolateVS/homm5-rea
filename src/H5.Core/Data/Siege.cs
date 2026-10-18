using System.Xml.Linq;
using H5.Core.Xdb;

namespace H5.Core.Data;

// Town siege data: the fortification layout of every town type (AdvMapTownShared <Combat>,
// an AdvMapTownCombat with ArenaBuilding entries), building hit points (RefTables/ArenaBuildingsStats)
// and moats (RefTables/TownTypesInfo <Moat>). Building coordinates are in the engine's internal siege
// grid, which is the 14x14 field plus a 2-column / 1-row margin (18x16, see docs/formats/arena.md).

/// <summary>An effect played when a building is destroyed (two stages: RuinLevel 0 and 1).</summary>
public sealed class ArenaBuildingDestroyEffect
{
    public int Upgrade;
    public int RuinLevel;
    public string? Effect;
    public string? Sound;
    public string? Locator;
}

/// <summary>One fortification piece (ArenaBuilding): wall section, gate, tower or moat.</summary>
public sealed class ArenaBuildingDef
{
    /// <summary>WALL, GATE, LEFT_TOWER, BIG_TOWER, RIGHT_TOWER, MOAT or MAGIC_WALL.</summary>
    public string Type = "";
    /// <summary>Position of the building's model anchor in internal siege-grid tiles.</summary>
    public int PosX, PosY, SizeX, SizeY;
    public string? Object;       // (ArenaModObject) with the model and its damage states
    public string? Locator;
    public string? Shooter;      // (Character) shown on a tower
    public string? Shot;         // projectile visual of a tower
    public string? HitEffect, HitSound;
    public string? NameText;
    public List<ArenaBuildingDestroyEffect> DestroyEffects = new();
    /// <summary>Internal-grid tiles blocked while intact / once destroyed.</summary>
    public List<(int X, int Y)> BlockedTiles = new(), BlockedTilesForDestroyed = new();
}

/// <summary>The siege layout of a town type.</summary>
public sealed class TownCombatDef
{
    public string Town = "";
    /// <summary>The AdvMapTownShared xdb this layout was read from.</summary>
    public string SharedPath = "";
    public string? ArenaDesc;     // (ArenaDesc) of the siege arena
    public string? Scenery;       // AdvMapDesc of the siege battlefield terrain
    public int MountX, MountY;
    public double MountRot;
    public string GroundType = "";
    public List<ArenaBuildingDef> Buildings = new();
}

/// <summary>Base hit points of a building type and the bonus per fortification level (Fort, Citadel, Castle).</summary>
public sealed class ArenaBuildingStatsDef
{
    public string Type = "";
    public int BaseHealth;
    public int[] HealthFortBonuses = Array.Empty<int>();

    public int HealthAt(int fortLevel) =>
        BaseHealth + (fortLevel >= 1 && fortLevel <= HealthFortBonuses.Length ? HealthFortBonuses[fortLevel - 1] : 0);
}

/// <summary>A spell cast by a moat (or a moat mine) on the creature that enters it.</summary>
public sealed class MoatSpellDef
{
    public string Spell = "SPELL_NONE";
    public int Chance = 100;
    public Mastery Mastery;
    public int SpellPower;
}

public sealed class MoatMineDef
{
    public string? Effect, ExplosionEffect;
    public MoatSpellDef SpellToCast = new();
    public MoatSpellDef SpellToCastOnFriend = new();
}

public sealed class MoatDef
{
    public string Town = "";
    /// <summary>Damage at full town level (170 × the town's moat multiplier).</summary>
    public int MaxDamage = 170;
    public List<MoatSpellDef> Spells = new();
    public List<MoatMineDef> Mines = new();
}

internal static class SiegeLoader
{
    const string BuildingStatsTable = "GameMechanics/RefTables/ArenaBuildingsStats.xdb";
    const string TownTypesTable = "GameMechanics/RefTables/TownTypesInfo.xdb";

    public static Dictionary<string, ArenaBuildingStatsDef> LoadBuildingStats(XdbLoader x)
    {
        var result = new Dictionary<string, ArenaBuildingStatsDef>();
        if (!x.FileSystem.Exists(BuildingStatsTable)) return result;
        foreach (var item in x.Load(BuildingStatsTable).Root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var obj = item.Element("obj");
            var id = item.Str("ID");
            if (obj == null || id.Length == 0) continue;
            result[id] = new ArenaBuildingStatsDef
            {
                Type = id,
                BaseHealth = obj.Int("BaseHealth"),
                HealthFortBonuses = obj.Items("HealthFortBonuses").Select(i => int.TryParse(i.Value.Trim(), out var v) ? v : 0).ToArray(),
            };
        }
        return result;
    }

    public static Dictionary<string, MoatDef> LoadMoats(XdbLoader x)
    {
        var result = new Dictionary<string, MoatDef>();
        if (!x.FileSystem.Exists(TownTypesTable)) return result;
        foreach (var item in x.Load(TownTypesTable).Root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var moat = item.Element("obj")?.Element("Moat");
            var id = item.Str("ID");
            if (moat == null || id.Length == 0) continue;
            result[id] = new MoatDef
            {
                Town = id,
                MaxDamage = moat.Int("MoatMaxDamage", 170),
                Spells = moat.Items("MoatSpells").Select(ReadMoatSpell).ToList(),
                Mines = moat.Items("MoatMines").Select(m => new MoatMineDef
                {
                    Effect = m.Href("Effect"),
                    ExplosionEffect = m.Href("ExplosionEffect"),
                    SpellToCast = ReadMoatSpell(m.Element("SpellToCast")),
                    SpellToCastOnFriend = ReadMoatSpell(m.Element("SpellToCastOnFriend")),
                }).ToList(),
            };
        }
        return result;
    }

    static MoatSpellDef ReadMoatSpell(XElement? e) => new()
    {
        Spell = e.Str("Spell", "SPELL_NONE"),
        Chance = e.Int("Chance", 100),
        Mastery = MasteryParse.Parse(e.Str("SpellMastery")),
        SpellPower = e.Int("SpellPower"),
    };

    /// <summary>Reads the siege layout of every town type from MapObjects/*.(AdvMapTownShared).xdb.</summary>
    public static Dictionary<string, TownCombatDef> LoadTownCombat(XdbLoader x)
    {
        var result = new Dictionary<string, TownCombatDef>();
        foreach (var path in x.FileSystem.Enumerate("MapObjects/")
                     .Where(p => p.EndsWith(".(AdvMapTownShared).xdb", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var root = x.Load(path).Root;
            var town = root.Str("Type");
            if (root == null || town.Length == 0) continue;
            var combat = x.Resolve(root.Element("Combat"), path);
            if (combat == null || combat.Name.LocalName != "AdvMapTownCombat") continue;
            // The combat descriptor may live in its own file; hrefs inside it are relative to that file.
            var combatFile = ResolvedFile(root.Element("Combat"), path);
            var def = new TownCombatDef
            {
                Town = town,
                SharedPath = path,
                ArenaDesc = Abs(combat.Href("ArenaDesc"), combatFile),
                GroundType = combat.Str("GroundType"),
            };
            var flyby = x.Resolve(combat.Element("ArenaMap"), combatFile);
            if (flyby != null)
            {
                def.Scenery = Abs(flyby.Href("Scenery"), combatFile);
                var mount = flyby.Element("MountPoint");
                def.MountX = mount?.Element("Pos").Int("x") ?? 0;
                def.MountY = mount?.Element("Pos").Int("y") ?? 0;
                def.MountRot = mount.Dbl("Rot");
            }
            foreach (var item in combat.Element("walls")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var b = x.Resolve(item, combatFile);
                if (b == null) continue;
                var bFile = ResolvedFile(item, combatFile);
                def.Buildings.Add(ReadBuilding(b, bFile));
            }
            // Several town models share a type (e.g. the Hill Castle variant has no layout of its own); keep the first.
            result.TryAdd(town, def);
        }
        return result;
    }

    static ArenaBuildingDef ReadBuilding(XElement b, string file) => new()
    {
        Type = b.Str("Type"),
        PosX = b.Element("Pos").Int("x"),
        PosY = b.Element("Pos").Int("y"),
        SizeX = b.Element("Size").Int("x"),
        SizeY = b.Element("Size").Int("y"),
        Object = Abs(b.Href("Object"), file),
        Locator = Abs(b.Href("Locator"), file),
        Shooter = Abs(b.Href("Shooter"), file),
        Shot = Abs(b.Href("Shot"), file),
        HitEffect = Abs(b.Href("HitEffect"), file),
        HitSound = Abs(b.Href("HitSound"), file),
        NameText = Abs(b.Href("NameFileRef"), file),
        DestroyEffects = b.Items("DestroyEffects").Select(d => new ArenaBuildingDestroyEffect
        {
            Upgrade = d.Int("Upgrade"),
            RuinLevel = d.Int("RuinLevel"),
            Effect = Abs(d.Href("Effect"), file),
            Sound = Abs(d.Href("SoundEffect"), file),
            Locator = Abs(d.Href("Locator"), file),
        }).ToList(),
        // Some stock entries list a tile twice; duplicates are dropped.
        BlockedTiles = b.Items("blockedTiles").Select(t => (t.Int("x"), t.Int("y"))).Distinct().ToList(),
        BlockedTilesForDestroyed = b.Items("blockedTilesForDestroyed").Select(t => (t.Int("x"), t.Int("y"))).Distinct().ToList(),
    };

    static string ResolvedFile(XElement? refElement, string currentFile)
    {
        var href = (string?)refElement?.Attribute("href");
        if (string.IsNullOrEmpty(href) || href.StartsWith("#")) return currentFile;
        return XdbRef.Parse(href, currentFile).Path;
    }

    /// <summary>An href made absolute (pak path + pointer) so consumers need no context.</summary>
    static string? Abs(string? href, string file)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var r = XdbRef.Parse(href, file);
        return r.Pointer == null ? "/" + r.Path : $"/{r.Path}#{r.Pointer}";
    }
}
