using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Adventure.Data;

public sealed partial class AdventureDb
{
    /// <summary>
    /// SRPGRoot object groups MONSTER_1 … MONSTER_7 and MONSTER_ANY (RPGRoot +0x68, looked up by name in 00bcb710):
    /// each an AdvMapSharedGroup of AdvMapMonsterShared objects, kept here as their creatures (shared +0xf0) in the
    /// group's order, only those whose creature has SubjectOfRandomGeneration (creature def +0xd0) — the filter of
    /// 0041ca20.
    /// </summary>
    public Dictionary<string, List<string>> MonsterGroups { get; } = new(StringComparer.Ordinal);

    const string RpgRoot = "GameMechanics/RPGStats/RPGRoot.xdb";

    void LoadMonsterGroups(XdbLoader x)
    {
        if (!x.FileSystem.Exists(RpgRoot)) return;
        foreach (var item in x.Load(RpgRoot).Root?.Descendants("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            var id = item.Element("ID")?.Value;
            if (id == null || !id.StartsWith("MONSTER_", StringComparison.Ordinal)) continue;
            var group = XdbRef.Parse((string?)item.Element("Group")?.Attribute("href"), RpgRoot).Path;
            if (group.Length == 0 || !x.FileSystem.Exists(group)) continue;
            var list = new List<string>();
            foreach (var link in x.Load(group).Root?.Element("links")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                var shared = XdbRef.Parse((string?)link.Attribute("href"), group).Path;
                if (shared.Length == 0 || !x.FileSystem.Exists(shared)) continue;
                var creature = x.Load(shared).Root?.Element("Creature")?.Value;
                if (creature is not { Length: > 0 } || creature == "CREATURE_UNKNOWN" || !Core.Creatures.TryGetValue(creature, out var cd)) continue;
                if (!cd.Stock || !SubjectOfRandomGeneration(x, cd)) continue;
                list.Add(creature);
            }
            MonsterGroups[id] = list;
        }
    }

    static bool SubjectOfRandomGeneration(XdbLoader x, CreatureDef cd) =>
        cd.SourcePath.Length > 0 && x.FileSystem.Exists(cd.SourcePath) &&
        string.Equals(x.Load(cd.SourcePath).Root?.Element("SubjectOfRandomGeneration")?.Value, "true", StringComparison.OrdinalIgnoreCase);
}
