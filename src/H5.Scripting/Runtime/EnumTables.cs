using System.Text.RegularExpressions;
using H5.Core.IO;

namespace H5.Scripting.Runtime;

/// <summary>
/// Numeric ids used by scripts ↔ game data ids. The engine's enums equal the item index in
/// the RefTables (Creatures.xdb, Skills.xdb, Artifacts.xdb, UndividedSpells.xdb) — verified
/// against every constant in common.lua/advmap-startup.lua with zero mismatches.
/// </summary>
public sealed class EnumTables
{
    public readonly IdTable Creatures, Skills, Artifacts, Spells;

    public static readonly string[] Towns =
        { "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD" };

    /// <summary>TOWN_BUILDING_* values 0..25 → engine building type (TB_*).</summary>
    public static readonly string[] Buildings =
    {
        "TB_TOWN_HALL", "TB_FORT", "TB_MARKETPLACE", "TB_SHIPYARD", "TB_TAVERN", "TB_BLACKSMITH", "TB_MAGIC_GUILD",
        "TB_DWELLING_1", "TB_DWELLING_2", "TB_DWELLING_3", "TB_DWELLING_4", "TB_DWELLING_5", "TB_DWELLING_6", "TB_DWELLING_7",
        "TB_GRAIL", "TB_WONDER",
        "TB_SPECIAL_0", "TB_SPECIAL_1", "TB_SPECIAL_2", "TB_SPECIAL_3", "TB_SPECIAL_4", "TB_SPECIAL_5",
        "TB_SPECIAL_6", "TB_SPECIAL_7", "TB_SPECIAL_8", "TB_SPECIAL_9",
    };

    public static readonly string[] WarMachines = { "", "WAR_MACHINE_BALLISTA", "WAR_MACHINE_CATAPULT", "WAR_MACHINE_FIRST_AID_TENT", "WAR_MACHINE_AMMO_CART" };

    public static readonly string[] Stats = { "experience", "attack", "defence", "spellpower", "knowledge", "luck", "morale", "move", "mana" };

    public static readonly string[] Masteries = { "MASTERY_NONE", "MASTERY_BASIC", "MASTERY_ADVANCED", "MASTERY_EXPERT", "MASTERY_EXTRA_EXPERT" };

    public EnumTables(PakFileSystem fs)
    {
        Creatures = Load(fs, "GameMechanics/RefTables/Creatures.xdb");
        Skills = Load(fs, "GameMechanics/RefTables/Skills.xdb");
        Artifacts = Load(fs, "GameMechanics/RefTables/Artifacts.xdb");
        Spells = Load(fs, "GameMechanics/RefTables/UndividedSpells.xdb");
    }

    static readonly Regex IdRx = new(@"<ID>([A-Za-z_0-9]+)</ID>", RegexOptions.Compiled);

    static IdTable Load(PakFileSystem fs, string path)
    {
        var text = System.Text.Encoding.UTF8.GetString(fs.ReadAllBytes(path));
        return new IdTable(IdRx.Matches(text).Select(m => m.Groups[1].Value).ToList());
    }

    public static int TownIndex(string town) => Array.IndexOf(Towns, town);
    public static string? Town(int i) => i >= 0 && i < Towns.Length ? Towns[i] : null;
    public static string? Building(int i) => i >= 0 && i < Buildings.Length ? Buildings[i] : null;
    public static int BuildingIndex(string tb) => Array.IndexOf(Buildings, tb);
}

public sealed class IdTable
{
    readonly List<string> _names;
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public IdTable(List<string> names)
    {
        _names = names;
        for (int i = 0; i < names.Count; i++) _index.TryAdd(names[i], i);
    }

    public string? Name(int id) => id >= 0 && id < _names.Count ? _names[id] : null;
    public int Id(string name) => _index.TryGetValue(name, out var i) ? i : -1;
    public int Count => _names.Count;
}
