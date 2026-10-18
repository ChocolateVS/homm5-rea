using System.Xml.Linq;
using H5.Core.Xdb;

namespace H5.Core.Data;

/// <summary>One combat obstacle (ArenaObjects/_(ArenaObstacle)/**): model and footprint in grid tiles.</summary>
public sealed class ArenaObstacleDef
{
    /// <summary>Pak path of the ArenaObstacle xdb (identifies the obstacle for the client).</summary>
    public string Path = "";
    public string? ModelPath;
    /// <summary>Blocked tiles relative to the anchor (for fixed groups: absolute internal-grid cells).</summary>
    public List<(int X, int Y)> Tiles = new();
}

/// <summary>ArenaObstaclesGroup: candidate obstacles for random placement, or a fixed layout.</summary>
public sealed class ArenaObstacleGroupDef
{
    public string Path = "";
    public bool FixedObstacles;
    public List<ArenaObstacleDef> Obstacles = new();
}

/// <summary>A combat arena scene (Scenes/CombatArenas/*.xdb, an AdventureFlybyScene).</summary>
public sealed class CombatArenaDef
{
    /// <summary>Scene name, e.g. "Grass_Big_01".</summary>
    public string Name = "";
    public string ScenePath = "";
    public ArenaObstacleGroupDef? Obstacles;
}

internal static class ArenaLoader
{
    public const string ScenesDir = "Scenes/CombatArenas/";

    public static Dictionary<string, CombatArenaDef> Load(XdbLoader x)
    {
        var result = new Dictionary<string, CombatArenaDef>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, ArenaObstacleGroupDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in x.FileSystem.Enumerate(ScenesDir).Where(p => p.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var root = x.Load(path).Root;
            if (root == null || root.Name.LocalName != "AdventureFlybyScene") continue;
            var arena = new CombatArenaDef
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                ScenePath = path,
            };
            var groupRef = root.Element("ObstaclesGroup");
            var href = (string?)groupRef?.Attribute("href");
            if (!string.IsNullOrEmpty(href))
            {
                if (href.StartsWith("#n:inline"))
                {
                    var inline = groupRef!.Elements().FirstOrDefault();
                    if (inline != null) arena.Obstacles = ReadGroup(x, inline, path, path + "#inline");
                }
                else
                {
                    var r = XdbRef.Parse(href, path);
                    if (!groups.TryGetValue(r.Path, out var g) && x.FileSystem.Exists(r.Path))
                    {
                        g = ReadGroup(x, x.Load(r.Path).Root!, r.Path, r.Path);
                        groups[r.Path] = g;
                    }
                    arena.Obstacles = g;
                }
            }
            result[arena.Name] = arena;
        }
        return result;
    }

    static ArenaObstacleGroupDef ReadGroup(XdbLoader x, XElement e, string file, string id)
    {
        var g = new ArenaObstacleGroupDef { Path = id, FixedObstacles = e.Str("FixedObstacles") == "true" };
        foreach (var item in e.Element("links")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var obj = x.Resolve(item, file);
            if (obj == null) continue;
            var href = (string?)item.Attribute("href") ?? "";
            var objPath = href.StartsWith("#") || href.Length == 0 ? file : XdbRef.Parse(href, file).Path;
            var def = new ArenaObstacleDef { Path = objPath };
            var model = obj.Href("Model");
            if (!string.IsNullOrEmpty(model)) def.ModelPath = XdbRef.Parse(model, objPath).Path;
            foreach (var t in obj.Element("blockedTiles")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                def.Tiles.Add((t.Int("x"), t.Int("y")));
            g.Obstacles.Add(def);
        }
        return g;
    }
}
