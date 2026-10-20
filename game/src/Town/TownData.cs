using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Xdb;
using H5Reborn.Adventure;

namespace H5Reborn.Town;

/// <summary>
/// Client-side view of one town building level: the engine's BuildingDef plus the
/// presentation fields of its TownBuildingSharedStats (scene object, city-plan slot,
/// icon, texts) that the rules engine does not need.
/// </summary>
public sealed class BuildingView
{
    public required BuildingDef Def;
    public string ModObjectName = "";
    public string UIObjectName = "";
    public int UIStackPosition;
    public string? IconHref;
    public string Name = "";
    public string Description = "";
}

public static class TownData
{
    static readonly Dictionary<string, string?> _interiors = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, BuildingView> _views = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The town type's 3D interior scene (AdvMapTownShared &lt;Interior&gt;), or null.</summary>
    public static string? InteriorOf(string townType)
    {
        if (_interiors.TryGetValue(townType, out var cached)) return cached;
        var g = Game.Instance;
        string? found = null;
        foreach (var path in g.Fs.Enumerate("MapObjects/").Where(p => p.EndsWith(".(AdvMapTownShared).xdb", StringComparison.OrdinalIgnoreCase)))
        {
            var root = g.Xdb.Load(path).Root!;
            if (!string.Equals((string?)root.Element("Type"), townType, StringComparison.OrdinalIgnoreCase)) continue;
            var href = (string?)root.Element("Interior")?.Attribute("href");
            if (!string.IsNullOrEmpty(href)) found = XdbRef.Parse(href, path).Path;
            break;
        }
        _interiors[townType] = found;
        return found;
    }

    public static BuildingView View(BuildingDef def)
    {
        if (_views.TryGetValue(def.Path, out var v)) return v;
        var x = Game.Instance.Xdb;
        var root = x.Load(def.Path).Root!;
        v = new BuildingView
        {
            Def = def,
            ModObjectName = (string?)root.Element("ModObjectName") ?? "",
            UIObjectName = (string?)root.Element("UIObjectName") ?? "",
            UIStackPosition = (int?)root.Element("UIStackPosition") ?? 0,
            Name = def.Name,
            Description = x.ReadText((string?)root.Element("DescriptionFileRef")?.Attribute("href"), def.Path)?.Trim() ?? "",
        };
        var icon = (string?)root.Element("Icon")?.Attribute("href");
        if (!string.IsNullOrEmpty(icon)) v.IconHref = XdbRef.Parse(icon, def.Path).Path;
        _views[def.Path] = v;
        return v;
    }

    public static IEnumerable<BuildingDef> Defs(AdventureDb db, TownState t) =>
        db.Buildings.TryGetValue(t.Type, out var list) ? list : Enumerable.Empty<BuildingDef>();

    /// <summary>The level of a building type the town would build next, or null when maxed/not allowed by the map.</summary>
    public static BuildingDef? Next(AdventureDb db, TownState t, string type)
    {
        int next = t.Level(type) + 1;
        if (next > t.MaxLevel.GetValueOrDefault(type)) return null;
        return db.Building(t.Type, type, next);
    }

    /// <summary>
    /// The exe's build-check code for a cell level (town build screen 0065bab0): levels already built are
    /// <see cref="BuildDenial.AlreadyBuilt"/>, levels past the next one <see cref="BuildDenial.Prereq"/>, the next
    /// level asks the town (00cd6a90 via <see cref="H5.Adventure.Engine.Adventure.BuildCheck"/>).
    /// </summary>
    public static BuildDenial Check(AdventureSession s, TownState t, BuildingDef def)
    {
        if (t.Level(def.Type) >= def.Level) return BuildDenial.AlreadyBuilt;
        if (def.Level != t.Level(def.Type) + 1) return BuildDenial.Prereq;
        return s.Game.BuildCheck(t, def.Type, s.S.Players.First(p => p.Index == t.Owner).Purse);
    }

    /// <summary>Dev only (`--town-build-all`): builds every level the map allows.</summary>
    public static void GrantAllBuildings(AdventureSession s, TownState t)
    {
        foreach (var def in Defs(s.Db, t))
            if (def.Level <= t.MaxLevel.GetValueOrDefault(def.Type) && t.Level(def.Type) < def.Level)
                t.Built[def.Type] = def.Level;
        s.Game.StockArtifactMerchants();
    }
}
