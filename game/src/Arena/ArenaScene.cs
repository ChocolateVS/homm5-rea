using System.Globalization;
using Godot;
using H5.Assets.Model;
using H5.Core.Xdb;
using H5Reborn.Render;

namespace H5Reborn.Arena;

/// <summary>
/// A combat battlefield assembled from the original arena data: terrain, scenery,
/// lighting, sky, the combat camera and the grid overlay. Creatures are placed with
/// <see cref="PlaceCreature"/>; the combat UI/presentation layer drives it.
/// </summary>
public partial class ArenaScene : Node3D
{
    public ArenaDefinition Definition { get; private set; } = null!;
    public ArenaGrid Grid { get; private set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    public GridOverlay Overlay { get; private set; } = null!;
    public Node3D Units { get; private set; } = null!;

    /// <summary>Combat camera: Cameras/CombatArena/CombatArena.xdb (see docs/formats/arena.md).</summary>
    public const string CombatCameraPath = "Cameras/CombatArena/CombatArena.xdb";

    readonly Dictionary<string, (ArrayMesh Mesh, ModelAsset Model)?> _meshCache = new(StringComparer.OrdinalIgnoreCase);

    public static ArenaScene Create(string arenaNameOrPath)
    {
        var scene = new ArenaScene { Name = "Arena" };
        scene.Build(ArenaDefinition.Load(arenaNameOrPath));
        return scene;
    }

    void Build(ArenaDefinition def)
    {
        Definition = def;
        Grid = new ArenaGrid(def);

        AddChild(TerrainBuilder.Build(def.Terrain, def.LayerTextures, def.AdditionalHeight));
        BuildStatics();
        BuildLighting(def.Light);

        Overlay = new GridOverlay(Grid) { Name = "GridOverlay" };
        AddChild(Overlay);
        Units = new Node3D { Name = "Units" };
        AddChild(Units);

        Camera = BuildCamera();
        AddChild(Camera);
        Camera.MakeCurrent();
    }

    // ---- scenery -------------------------------------------------------------------------

    void BuildStatics()
    {
        var root = new Node3D { Name = "Scenery" };
        AddChild(root);
        var loader = new ModelLoader(Game.Instance.Xdb);
        foreach (var s in Definition.Statics)
        {
            if (GetMesh(loader, s.ModelPath) is not { } entry) continue;
            // An object's origin sits at the centre of map tile floor(Pos) (docs/formats/arena.md).
            float mx = s.X + 0.5f, my = s.Y + 0.5f;
            var node = new MeshInstance3D { Mesh = entry.Mesh };
            node.Position = ArenaGrid.MapToWorld(mx, my, Definition.Terrain.SampleHeight(mx, my) + Definition.AdditionalHeight + s.Z);
            // Rotation about map +Z maps to rotation about Godot +Y by the same angle.
            node.Rotation = new Vector3(0, s.Rot, 0);
            root.AddChild(node);
        }
    }

    (ArrayMesh Mesh, ModelAsset Model)? GetMesh(ModelLoader loader, string modelPath)
    {
        if (_meshCache.TryGetValue(modelPath, out var cached)) return cached;
        (ArrayMesh, ModelAsset)? result = null;
        try
        {
            var model = loader.Load(modelPath);
            result = (ModelBuilder.BuildMesh(model, skinned: false), model);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] arena model failed {modelPath}: {e.Message}");
        }
        _meshCache[modelPath] = result;
        return result;
    }

    // ---- lighting / sky ------------------------------------------------------------------

    static Color C(System.Numerics.Vector3 v, float scale = 1) =>
        new(Math.Min(1, v.X * scale), Math.Min(1, v.Y * scale), Math.Min(1, v.Z * scale));

    void BuildLighting(ArenaLight light)
    {
        // The original lights in an overbright (×2) range: LightColor ~0.5 is a normal sun.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = C(light.FogColor),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = C(light.AmbientColor, 2f),
            AmbientLightEnergy = 1f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        if (light.FogStart < 900)
        {
            env.FogEnabled = true;
            env.FogLightColor = C(light.FogColor);
            env.FogDensity = 1f / Math.Max(10f, light.FogDistance);
        }
        AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightColor = C(light.LightColor, 2f),
            LightEnergy = 1f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 80,
        };
        // Pitch = sun elevation, Yaw = azimuth from map +X towards +Y (degrees).
        float p = Mathf.DegToRad(light.Pitch), y = Mathf.DegToRad(light.Yaw);
        var toSunMap = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Cos(p) * Mathf.Sin(y), Mathf.Sin(p));
        var toSun = new Vector3(toSunMap.X, toSunMap.Z, -toSunMap.Y);
        AddChild(sun);
        sun.LookAtFromPosition(Vector3.Zero, -toSun, Mathf.Abs(toSun.Y) > 0.99f ? Vector3.Forward : Vector3.Up);

        if (light.SkyDomeModel is { } sky && Game.Instance.Fs.Exists(sky))
        {
            try
            {
                var model = new ModelLoader(Game.Instance.Xdb).Load(sky);
                var mesh = ModelBuilder.BuildMesh(model, skinned: false);
                for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                    if (mesh.SurfaceGetMaterial(i) is StandardMaterial3D m)
                    {
                        m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                        m.DisableFog = true;
                        m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                    }
                var dome = new MeshInstance3D
                {
                    Name = "SkyDome", Mesh = mesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                dome.Position = Grid.GridCenter with { Y = 0 };
                AddChild(dome);
            }
            catch (Exception e) { GD.PushWarning($"[H5] sky dome failed: {e.Message}"); }
        }
    }

    // ---- camera --------------------------------------------------------------------------

    // Default combat view, fitted to the original combat screenshots in the stock manual
    // (Game Manual 3.0.pdf p.14): an axis-aligned view looking north (script docs: yaw 0 =
    // north), the 12-cell axis across the screen, the near edge being grid row 0. Fitting the
    // projected grid corners gives pitch 46.2°, vertical FOV 31.2°, eye 24.8 cells from a
    // target 4.27 cells in from the near edge (0 px residual). The stock camera file
    // (Cameras/CombatArena/CombatArena.xdb, DefaultCamera of _(CamerasSet)/CombatArenas.xdb)
    // supplies only the clip planes here: its Rot (-30, -66.8) gives no axis-aligned yaw under
    // any reading, and the exe's Pos/Rot/Rod → view conversion hasn't been traced yet.
    public const float DefaultPitchDeg = 46.2f, DefaultFovDeg = 31.2f, DefaultDistance = 24.8f, DefaultTargetDepth = 4.27f;

    Camera3D BuildCamera()
    {
        var cam = new Camera3D { Name = "CombatCamera", Near = 1f, Far = 500f, Fov = DefaultFovDeg, KeepAspect = Camera3D.KeepAspectEnum.Height };
        if (Game.Instance.Fs.Exists(CombatCameraPath))
        {
            var c = Game.Instance.Xdb.Load(CombatCameraPath).Root!;
            cam.Near = float.Parse(c.Element("ClipDistanceMin")!.Value, CultureInfo.InvariantCulture);
            cam.Far = float.Parse(c.Element("ClipDistanceMax")!.Value, CultureInfo.InvariantCulture);
        }
        PlaceCamera(cam, DefaultPitchDeg, 0f, DefaultDistance, new Vector2(ArenaGrid.Width * 0.5f, DefaultTargetDepth));
        return cam;
    }

    /// <summary>
    /// Orbit placement: looks at a grid point (cell units) from <paramref name="distance"/> cells,
    /// pitched down by <paramref name="pitchDeg"/>, yaw 0 = looking north (towards higher rows).
    /// </summary>
    public void PlaceCamera(Camera3D cam, float pitchDeg, float yawDeg, float distance, Vector2 gridTarget)
    {
        var target = Grid.CellCenter(gridTarget.X, gridTarget.Y);
        float p = Mathf.DegToRad(pitchDeg), y = Mathf.DegToRad(yawDeg);
        // North is Godot −Z; the eye sits south of the target (+Z) rotated by yaw about Up.
        var back = new Vector3(Mathf.Sin(y), 0, Mathf.Cos(y)) * (distance * Mathf.Cos(p));
        var eye = target + back + new Vector3(0, distance * Mathf.Sin(p), 0);
        cam.LookAtFromPosition(eye, target, Vector3.Up);
    }

    // ---- units ---------------------------------------------------------------------------

    /// <summary>
    /// Places a creature on the battlefield: centred on its cells (size 1 or 2), facing
    /// the enemy side, playing its idle animation. Returns the animated
    /// <see cref="Character"/>, or a bare static model if the character can't be built.
    /// </summary>
    public Node3D PlaceCreature(string creatureId, Vector2I cell, int size, int side)
    {
        Node3D node;
        if (CreatureModels.CharacterPathFor(creatureId) is { } characterPath && Game.Instance.Fs.Exists(characterPath))
        {
            try { node = CharacterBuilder.Build(characterPath); }
            catch (Exception e)
            {
                GD.PushWarning($"[H5] character failed {characterPath}: {e.Message}");
                node = StaticCreature(creatureId);
            }
        }
        else node = StaticCreature(creatureId);
        node.Name = $"{creatureId}_{cell.X}_{cell.Y}";
        node.Position = Grid.StackCenter(cell, size);
        node.Rotation = new Vector3(0, ArenaGrid.FacingYaw(side), 0);
        Units.AddChild(node);
        return node;
    }

    Node3D StaticCreature(string creatureId)
    {
        var node = new Node3D();
        if (CreatureModels.ModelPathFor(creatureId) is { } modelPath
            && GetMesh(new ModelLoader(Game.Instance.Xdb), modelPath) is { } entry)
            node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = entry.Mesh });
        return node;
    }

    /// <summary>Picks the grid cell under a screen position, or null.</summary>
    public Vector2I? CellAtScreen(Vector2 screenPos) =>
        Grid.CellAtRay(Camera.ProjectRayOrigin(screenPos), Camera.ProjectRayNormal(screenPos));
}

/// <summary>Resolves creature IDs to their original visual assets.</summary>
public static class CreatureModels
{
    static Dictionary<string, string>? _creatureFiles;

    /// <summary>GameMechanics/Creature xdb path for a CREATURE_* id (from RefTables/Creatures.xdb).</summary>
    public static string? CreatureFile(string creatureId)
    {
        if (_creatureFiles == null)
        {
            _creatureFiles = new(StringComparer.OrdinalIgnoreCase);
            const string table = "GameMechanics/RefTables/Creatures.xdb";
            foreach (var item in Game.Instance.Xdb.Load(table).Root!.Element("objects")!.Elements("Item"))
            {
                var id = item.Element("ID")?.Value;
                var href = (string?)item.Element("Obj")?.Attribute("href");
                if (id != null && !string.IsNullOrEmpty(href)) _creatureFiles[id] = XdbRef.Parse(href, table).Path;
            }
        }
        return _creatureFiles.GetValueOrDefault(creatureId);
    }

    /// <summary>Creature → CreatureVisual → AnimCharacter (Character xdb path).</summary>
    public static string? CharacterPathFor(string creatureId)
    {
        var xdb = Game.Instance.Xdb;
        if (CreatureFile(creatureId) is not { } file || !Game.Instance.Fs.Exists(file)) return null;
        var visualRef = XdbRef.Parse((string?)xdb.Load(file).Root!.Element("Visual")?.Attribute("href"), file);
        if (visualRef.IsEmpty || !Game.Instance.Fs.Exists(visualRef.Path)) return null;
        var charRef = XdbRef.Parse((string?)xdb.Load(visualRef.Path).Root!.Element("AnimCharacter")?.Attribute("href"), visualRef.Path);
        return charRef.IsEmpty ? null : charRef.Path;
    }

    public static string? ModelPathFor(string creatureId)
    {
        if (CharacterPathFor(creatureId) is not { } character || !Game.Instance.Fs.Exists(character)) return null;
        var r = XdbRef.Parse((string?)Game.Instance.Xdb.Load(character).Root!.Element("Model")?.Attribute("href"), character);
        return r.IsEmpty ? null : r.Path;
    }
}
