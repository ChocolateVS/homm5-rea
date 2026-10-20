using H5.Core.Data;
using H5.Core.Xdb;

namespace H5.Adventure.Data;

public sealed partial class AdventureDb
{
    const string MicroArtifactEffectsTable = "GameMechanics/RefTables/MicroArtifactEffects.xdb";
    Resources[]? _microCosts;

    /// <summary>
    /// The price of each micro-artifact effect, indexed by <see cref="MicroEffect"/> (MicroArtifactEffects.xdb, read by
    /// FUN_00ae2740; the cost is the effect record's +0x70, FUN_00c0a630 / FUN_00c0a450).
    /// </summary>
    public Resources MicroArtifactEffectCost(MicroEffect e)
    {
        _microCosts ??= LoadMicroCosts();
        int i = (int)e;
        return i >= 0 && i < _microCosts.Length ? _microCosts[i] : new Resources(0, 0, 0, 0, 0, 0, 0);
    }

    Resources[] LoadMicroCosts()
    {
        var costs = Enumerable.Repeat(new Resources(0, 0, 0, 0, 0, 0, 0), 11).ToArray();
        if (_xdb == null || !_xdb.FileSystem.Exists(MicroArtifactEffectsTable)) return costs;
        foreach (var item in _xdb.Load(MicroArtifactEffectsTable).Root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            var id = (string?)item.Element("ID");
            if (id == null || !Enum.TryParse<MicroEffect>(string.Concat(id[4..].Split('_').Select(w => w[..1] + w[1..].ToLowerInvariant())), out var e)) continue;
            var rec = item.Element("Obj")?.Element("MicroArtifactEffect");
            if (rec != null && (int)e < costs.Length) costs[(int)e] = Resources.Parse(rec.Element("Cost"));
        }
        return costs;
    }
}
