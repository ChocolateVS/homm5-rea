using System.Globalization;
using System.Xml.Linq;
using H5.Assets.Terrain;
using H5.Core.Xdb;

namespace H5Reborn.Arena;

/// <summary>A scenery object placed on an arena map (tile coordinates, radians about +Z).</summary>
public sealed record ArenaStatic(string ModelPath, float X, float Y, float Z, float Rot, bool TerrainAligned);

/// <summary>Sun, ambient, fog and sky settings from a Lights/_(AmbientLight) xdb.</summary>
public sealed class ArenaLight
{
    public System.Numerics.Vector3 LightColor = new(0.5f), AmbientColor = new(0.3f), ShadeColor = new(0.3f), FogColor = new(0.5f);
    public float Pitch = 45, Yaw = 45, FogStart = 1000, FogDistance = 1000;
    public string? SkyDomeModel, SkyCubeTexture;
    public string? DistanceFog;
}

/// <summary>
/// Everything needed to build a combat battlefield, read from the original data:
/// AdventureFlybyScene (Scenes/CombatArenas/*.xdb) → AdvMapDesc map → GroundTerrain.bin,
/// static scenery and lighting. See docs/formats/arena.md for the grid placement rule.
/// </summary>
public sealed class ArenaDefinition
{
    public string ScenePath = "", MapPath = "";
    /// <summary>Battlefield size in cells (14×14 for sieges with fortifications).</summary>
    public int FieldWidth = 12, FieldHeight = 10;
    /// <summary>Map floor the battle takes place on (0 surface, 1 underground).</summary>
    public int Floor;
    public int MountX, MountY;
    public float MountRot, AdditionalHeight;
    public TerrainData Terrain = null!;
    /// <summary>Texture (xdb) path per terrain layer, parallel to Terrain.Layers.</summary>
    public List<string?> LayerTextures = new();
    public List<ArenaStatic> Statics = new();
    public ArenaLight Light = new();
    public string? ObstaclesGroup, Music, AmbientSoundSet, CamerasSet;

    static float F(XElement? e, float def = 0) =>
        e == null || string.IsNullOrWhiteSpace(e.Value) ? def : float.Parse(e.Value, CultureInfo.InvariantCulture);

    static System.Numerics.Vector3 V(XElement? e, System.Numerics.Vector3 def) =>
        e == null ? def : new(F(e.Element("x")), F(e.Element("y")), F(e.Element("z")));

    /// <summary>Accepts a short arena name ("Grass_Big_01") or a full scene xdb path.</summary>
    public static string ResolveScenePath(string nameOrPath)
    {
        var fs = Game.Instance.Fs;
        if (fs.Exists(nameOrPath)) return nameOrPath;
        foreach (var dir in new[] { "Scenes/CombatArenas/", "GameMechanics/CombatArenaTypes/" })
            foreach (var cand in new[] { $"{dir}{nameOrPath}.xdb", $"{dir}{nameOrPath}.(AdventureFlybyScene).xdb" })
                if (fs.Exists(cand)) return cand;
        throw new FileNotFoundException($"No combat arena named '{nameOrPath}'");
    }

    public static IEnumerable<string> ListArenas() =>
        Game.Instance.Fs.Enumerate("Scenes/CombatArenas/").Where(p => p.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase)).Order();

    public static ArenaDefinition Load(string nameOrPath)
    {
        var path = ResolveScenePath(nameOrPath);
        return Load(Game.Instance.Xdb.Load(path).Root!, path);
    }

    /// <summary>An arena from <see cref="CombatArenaTable"/>: a scene reference or an inline scene.</summary>
    public static ArenaDefinition Load(CombatArenaTable.Entry entry) =>
        entry.InlineScene != null ? Load(entry.InlineScene, CombatArenaTable.Path) : Load(entry.ScenePath!);

    /// <summary>Builds a definition from an AdventureFlybyScene element; hrefs resolve against <paramref name="scenePath"/>.</summary>
    public static ArenaDefinition Load(XElement scene, string scenePath)
    {
        var xdb = Game.Instance.Xdb;
        var def = new ArenaDefinition { ScenePath = scenePath };

        var mount = scene.Element("MountPoint");
        def.MountX = (int)F(mount?.Element("Pos")?.Element("x"));
        def.MountY = (int)F(mount?.Element("Pos")?.Element("y"));
        def.MountRot = F(mount?.Element("Rot"));
        def.AdditionalHeight = F(scene.Element("AdditionalHeight"));
        def.ObstaclesGroup = Href(scene.Element("ObstaclesGroup"), def.ScenePath);
        def.Music = Href(scene.Element("Music"), def.ScenePath);
        def.AmbientSoundSet = Href(scene.Element("AmbientSoundSet"), def.ScenePath);
        def.CamerasSet = Href(scene.Element("CamerasSet"), def.ScenePath);

        def.MapPath = XdbRef.Parse((string?)scene.Element("Scenery")?.Attribute("href"), def.ScenePath).Path;
        LoadMap(def);
        return def;
    }

    /// <summary>
    /// A siege battlefield: the town's siege terrain (TownCombatDef.Scenery) with its mount point
    /// and a 14×14 field; the fortifications themselves come from the siege ArenaDesc.
    /// </summary>
    public static ArenaDefinition FromScenery(string mapPath, int mountX, int mountY, int width, int height)
    {
        var def = new ArenaDefinition
        {
            ScenePath = mapPath, MapPath = XdbRef.Parse(mapPath, null).Path,
            MountX = mountX, MountY = mountY, FieldWidth = width, FieldHeight = height,
        };
        LoadMap(def);
        return def;
    }

    static void LoadMap(ArenaDefinition def)
    {
        var xdb = Game.Instance.Xdb;
        var map = xdb.Load(def.MapPath).Root!;
        // Subterranean arenas play on the map's underground floor (InitialFloor 1, objects on
        // Floor 1, UndergroundTerrain.bin); the surface terrain there is an unused grass plane.
        def.Floor = int.TryParse((string?)map.Element("InitialFloor"), out var floor) ? floor : 0;
        var terrainElement = def.Floor == 1 && (string?)map.Element("UndergroundTerrainFileName")?.Attribute("href") is { Length: > 0 }
            ? "UndergroundTerrainFileName" : "GroundTerrainFileName";
        var terrainPath = XdbRef.Parse((string?)map.Element(terrainElement)?.Attribute("href"), def.MapPath).Path;
        def.Terrain = TerrainReader.Read(Game.Instance.Fs.ReadAllBytes(terrainPath));

        foreach (var layer in def.Terrain.Layers)
        {
            string? tex = null;
            if (!string.IsNullOrEmpty(layer.TilePath) && Game.Instance.Fs.Exists(layer.TilePath))
                tex = Href(xdb.Load(layer.TilePath).Root!.Element("Texture"), layer.TilePath);
            def.LayerTextures.Add(tex);
        }

        var lightRef = (def.Floor == 1 ? map.Element("UndergroundAmbientLights")?.Elements("Item").FirstOrDefault() : null)
                       ?? map.Element("GroundAmbientLights")?.Elements("Item").FirstOrDefault()
                       ?? map.Element("AmbientLight");
        if (xdb.Resolve(lightRef, def.MapPath) is { } light)
            def.Light = ReadLight(light, XdbRef.Parse((string?)lightRef?.Attribute("href"), def.MapPath).Path);

        foreach (var obj in map.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var o = obj.Elements().FirstOrDefault();
            if (o?.Element("Pos") is not { } pos) continue;
            if (int.TryParse((string?)o.Element("Floor"), out var objFloor) && objFloor != def.Floor) continue;
            var sharedPath = Href(o.Element("Shared"), def.MapPath);
            if (sharedPath == null || !Game.Instance.Fs.Exists(sharedPath)) continue;
            var shared = xdb.Load(sharedPath).Root!;
            var model = Href(shared.Element("Model"), sharedPath);
            if (model == null) continue;
            def.Statics.Add(new ArenaStatic(model, F(pos.Element("x")), F(pos.Element("y")), F(pos.Element("z")),
                F(o.Element("Rot")), (string?)shared.Element("TerrainAligned") == "true"));
        }
    }

    static ArenaLight ReadLight(XElement e, string file) => new()
    {
        LightColor = V(e.Element("LightColor"), new(0.5f)),
        AmbientColor = V(e.Element("AmbientColor"), new(0.3f)),
        ShadeColor = V(e.Element("ShadeColor"), new(0.3f)),
        FogColor = V(e.Element("FogColor"), new(0.5f)),
        Pitch = F(e.Element("Pitch"), 45),
        Yaw = F(e.Element("Yaw"), 45),
        FogStart = F(e.Element("FogStartDistance"), 1000),
        FogDistance = F(e.Element("FogDistance"), 1000),
        SkyDomeModel = Href(e.Element("SkyDome"), file),
        SkyCubeTexture = Href(e.Element("Sky"), file),
        DistanceFog = Href(e.Element("DistanceFog"), file),
    };

    static string? Href(XElement? e, string currentFile)
    {
        var h = (string?)e?.Attribute("href");
        if (string.IsNullOrEmpty(h)) return null;
        var r = XdbRef.Parse(h, currentFile);
        return r.IsEmpty ? null : r.Path;
    }
}
