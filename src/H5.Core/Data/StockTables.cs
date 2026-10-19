using System.Runtime.CompilerServices;
using System.Xml.Linq;
using H5.Core.IO;

namespace H5.Core.Data;

/// <summary>
/// The fixed sizes of the stock engine's reference tables (TotE 3.1 <c>H5_Game.exe</c>, also <c>H5_Game_PEST.exe</c>).
/// Each table loader receives a constant count (0084c9bf UndividedSpells 0x161, 0084ca7f Artifacts 0x61,
/// 0084cb3f Skills 0xdd, 00ae261f Creatures 0xb4, 00ae29df HeroClass 9). On load (FUN_008525e0 / FUN_00aef760 and
/// their twins) it formats "wrong amount of objects (%d) in table %s, %d expected" when the xdb holds another number
/// of items (only a string, FUN_00a47fd0, nothing stops), sizes the table to the count and fills slot i with the
/// item whose ID enum value is i (FUN_00aefa60). Items whose ID value is the count or more are never used.
/// MMH55's types.xml / RefTables carry 1000 creatures, 379 spells, 196 artifacts, 251 skills and 25 hero classes,
/// so on the user's target (stock engine, no MMH55 DLL) those extra entries do not exist.
/// docs/mmh55/W1_DATA.md §1.
/// </summary>
public static class StockTables
{
    public const string Creatures = "GameMechanics/RefTables/Creatures.xdb";
    public const string Spells = "GameMechanics/RefTables/UndividedSpells.xdb";
    public const string Artifacts = "GameMechanics/RefTables/Artifacts.xdb";
    public const string Skills = "GameMechanics/RefTables/Skills.xdb";
    public const string HeroClasses = "GameMechanics/RefTables/HeroClass.xdb";

    /// <summary>Table → (types.xml enum of its IDs, the stock engine's count).</summary>
    public static readonly IReadOnlyDictionary<string, (string Enum, int Count)> Limits =
        new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            [Creatures] = ("CreatureType", 180),
            [Spells] = ("SpellID", 353),
            [Artifacts] = ("ArtifactEffect", 97),
            [Skills] = ("SkillID", 221),
            [HeroClasses] = ("HeroClass", 9),
        };

    sealed class Enums { public readonly Dictionary<string, Dictionary<string, int>> ByType = new(StringComparer.Ordinal); }
    static readonly ConditionalWeakTable<PakFileSystem, Enums> Cache = new();

    /// <summary>Value of <paramref name="name"/> in the types.xml enum <paramref name="type"/>, or null.</summary>
    public static int? EnumValue(PakFileSystem fs, string type, string name)
    {
        var e = Cache.GetValue(fs, Parse);
        return e.ByType.TryGetValue(type, out var m) && m.TryGetValue(name, out var v) ? v : null;
    }

    static Enums Parse(PakFileSystem fs)
    {
        var e = new Enums();
        if (!fs.TryReadAllBytes("types.xml", out var bytes)) return e;
        XElement? root;
        try { root = XDocument.Parse(H5.Core.Xdb.TextDecoding.Decode(bytes)).Root; } catch { return e; }
        foreach (var t in root?.Descendants("Item").Where(i => i.Element("Type")?.Value == "TYPE_TYPE_ENUM") ?? Enumerable.Empty<XElement>())
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var en in t.Element("Entries")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                if (int.TryParse(en.Element("Value")?.Value, out int v) && en.Element("Name")?.Value is { Length: > 0 } n) map.TryAdd(n, v);
            e.ByType.TryAdd(t.Element("TypeName")?.Value ?? "", map);
        }
        return e;
    }

    /// <summary>
    /// Whether the stock engine keeps the table item with this ID: its enum value is below the table's count.
    /// IDs missing from types.xml (or tables without a fixed count) are kept.
    /// </summary>
    public static bool Kept(PakFileSystem fs, string table, string id)
    {
        if (!Limits.TryGetValue(table.TrimStart('/'), out var lim)) return true;
        return EnumValue(fs, lim.Enum, id) is not { } v || v < lim.Count;
    }

    /// <summary>The table's <c>objects/Item</c> elements the stock engine keeps (see <see cref="Kept"/>).</summary>
    public static IEnumerable<XElement> Items(PakFileSystem fs, string table, XElement? root) =>
        (root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            .Where(i => Kept(fs, table, i.Element("ID")?.Value ?? ""));
}
