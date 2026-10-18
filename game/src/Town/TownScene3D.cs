using System.Globalization;
using System.Xml.Linq;
using Godot;
using H5.Assets.Model;
using H5.Core.Xdb;
using H5Reborn.Effects;
using H5Reborn.Render;

namespace H5Reborn.Town;

/// <summary>
/// The 3D interior of a town: the faction's (ArenaDesc) from its AdvMapTownShared
/// &lt;Interior&gt;. ArenaEnvObjects are always shown; each ArenaModObject is a building
/// whose `upgrades[level].ruins[0]` model is shown for the level built in the town
/// (level 0 = empty slot). Buildings are matched to town state through
/// TownBuildingSharedStats.ModObjectName (exe 00ad7b90 reads it next to UIObjectName).
/// Objects that no building names keep their DefUpgLevel.
/// </summary>
public partial class TownScene3D : Node3D
{
    public sealed class Building
    {
        public string Name = "";
        public int DefLevel;
        /// <summary>Per level: model path, anim set, (effect href, locator) list.</summary>
        public List<(string? Model, string? AnimSet, List<(string Effect, string Locator)> Effects)> Levels = new();
        public Node3D Root = null!;
        public int ShownLevel = -1;
        public Node3D? Current;
        public StaticBody3D? Pick;
    }

    public string DescPath { get; }
    public Camera3D Camera { get; private set; } = null!;
    public Dictionary<string, Building> Buildings { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Named placement points (ArenaLocObject), e.g. the fort screen's creature spots ph1_1…</summary>
    public Dictionary<string, Transform3D> Locators { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Orbit state (Maya-style camera: Pos is the eye, Rod the distance to the centre of interest).</summary>
    Vector3 _pivot;
    float _pitch, _yaw, _rod;
    CamLimits _limits;

    record struct CamLimits(float MinRod, float MaxRod, float MinZ, float MaxZ, float MinPitch, float MaxPitch, bool UsePitch);

    readonly ModelLoader _loader = new(Game.Instance.Xdb);

    public TownScene3D(string descPath)
    {
        DescPath = descPath;
        Name = "TownScene";
    }

    public override void _Ready()
    {
        var xdb = Game.Instance.Xdb;
        var desc = xdb.Load(DescPath).Root!;
        foreach (var item in desc.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            // Objects are either separate xdbs or inline ("#n:inline(ArenaEnvObject)") children of the item.
            var href = (string?)item.Attribute("href") ?? "";
            XElement? obj;
            string objPath;
            if (href.StartsWith("#n:inline")) { obj = item.Elements().FirstOrDefault(); objPath = DescPath; }
            else
            {
                objPath = XdbRef.Parse(href, DescPath).Path;
                obj = !string.IsNullOrEmpty(objPath) && Game.Instance.Fs.Exists(objPath) ? xdb.Load(objPath).Root : null;
            }
            if (obj == null) continue;
            try
            {
                switch (obj.Name.LocalName)
                {
                    case "ArenaModObject": AddBuilding(obj, objPath); break;
                    case "ArenaLocObject":
                        Locators[(string?)obj.Element("Name") ?? ""] = ParsePlacement(obj.Element("placement")?.Value);
                        break;
                    default: AddEnv(obj, objPath); break;
                }
            }
            catch (Exception e) { GD.PushWarning($"[H5] scene object {objPath}: {e.Message}"); }
        }

        foreach (var l in desc.Element("lights")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            AddPointLight(l);
        var ambient = desc.Element("AmbientLights")?.Elements("Item").FirstOrDefault();
        if (xdb.Resolve(ambient, DescPath) is { } light) AddLighting(light);
        foreach (var fx in desc.Element("effects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            PlayEffect(fx, this);

        _limits = ReadLimits(desc);
        Camera = new Camera3D { Name = "Camera", KeepAspect = Camera3D.KeepAspectEnum.Height };
        AddChild(Camera);
        SetCamera(desc.Element("initialCamPos"));
        Camera.MakeCurrent();
    }

    // ------------------------------------------------------------------ objects

    void AddEnv(XElement obj, string objPath)
    {
        var model = Href(obj.Element("Model"), objPath);
        if (model == null) return;
        var node = BuildModel(model, Href(obj.Element("AnimSet"), objPath), System.IO.Path.GetFileNameWithoutExtension(objPath));
        if (node == null) return;
        node.Transform = ParsePlacement(obj.Element("placement")?.Value);
        AddChild(node);
    }

    void AddBuilding(XElement obj, string objPath)
    {
        var b = new Building
        {
            Name = (string?)obj.Element("Name") ?? System.IO.Path.GetFileNameWithoutExtension(objPath),
            DefLevel = (int?)obj.Element("DefUpgLevel") ?? 0,
            Root = new Node3D { Name = System.IO.Path.GetFileNameWithoutExtension(objPath) },
        };
        b.Root.Transform = ParsePlacement(obj.Element("placement")?.Value);
        foreach (var up in obj.Element("upgrades")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var ruin = up.Element("ruins")?.Elements("Item").FirstOrDefault();
            var fx = (ruin?.Element("effects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                .Select(e => ((string?)e.Element("Effect")?.Attribute("href") ?? "", (string?)e.Element("Locator") ?? ""))
                .Where(e => e.Item1.Length > 0).ToList();
            b.Levels.Add((Href(ruin?.Element("Model"), objPath), Href(ruin?.Element("AnimSet"), objPath), fx));
        }
        AddChild(b.Root);
        Buildings[b.Name] = b;
        ShowLevel(b, b.DefLevel);
    }

    /// <summary>Shows a building at a level (0 = nothing). Builds the level's model lazily.</summary>
    public void ShowLevel(Building b, int level)
    {
        level = Math.Clamp(level, 0, Math.Max(0, b.Levels.Count - 1));
        if (b.ShownLevel == level) return;
        b.ShownLevel = level;
        b.Current?.QueueFree();
        b.Current = null;
        b.Pick?.QueueFree();
        b.Pick = null;
        if (level >= b.Levels.Count) return;
        var (model, set, effects) = b.Levels[level];
        if (model == null) return;
        var node = BuildModel(model, set, $"{b.Name}_{level}");
        if (node == null) return;
        b.Root.AddChild(node);
        b.Current = node;
        foreach (var (fx, locator) in effects)
            EffectPlayer.Play(fx, node, LocatorTransform(node, locator));
        b.Pick = BuildPickBody(node, b.Name);
        if (b.Pick != null) b.Root.AddChild(b.Pick);
    }

    public void ShowLevel(string modObjectName, int level)
    {
        if (Buildings.TryGetValue(modObjectName, out var b)) ShowLevel(b, level);
    }

    Node3D? BuildModel(string modelPath, string? animSet, string name)
    {
        if (!Game.Instance.Fs.Exists(modelPath)) return null;
        if (animSet != null && Game.Instance.Fs.Exists(animSet))
        {
            var c = CharacterBuilder.BuildFromModel(modelPath, animSet, name);
            var first = c.Player.GetAnimationList().FirstOrDefault();
            if (first != null && !c.Has("idle00")) c.Play(first.Replace(Character.Lib, ""), loop: true);
            return c;
        }
        var asset = _loader.Load(modelPath);
        var root = new Node3D { Name = name };
        root.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = ModelBuilder.BuildMesh(asset, skinned: false) });
        return root;
    }

    /// <summary>Effects attach to a skeleton bone named by the locator when the model has one; else the model origin.</summary>
    static Transform3D? LocatorTransform(Node3D node, string locator)
    {
        if (string.IsNullOrEmpty(locator)) return null;
        if (node is Character c && c.Skeleton.FindBone(locator) is var bi and >= 0)
            return c.Skeleton.GetBoneGlobalRest(bi);
        return null;
    }

    static StaticBody3D? BuildPickBody(Node3D node, string name)
    {
        var meshes = node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
        if (meshes.Count == 0) return null;
        var body = new StaticBody3D { Name = "Pick", CollisionLayer = 1 << 1, CollisionMask = 0 };
        body.SetMeta("building", name);
        foreach (var mi in meshes)
        {
            if (mi.Mesh?.CreateTrimeshShape() is not { } shape) continue;
            // The body sits beside `node` under the building root, so shapes take node's own transform too.
            body.AddChild(new CollisionShape3D { Shape = shape, Transform = node.Transform * RelativeTo(mi, node) });
        }
        return body;
    }

    /// <summary>Transform of a descendant relative to an ancestor (exclusive), valid outside the tree.</summary>
    static Transform3D RelativeTo(Node3D n, Node3D ancestor)
    {
        var t = Transform3D.Identity;
        for (Node? cur = n; cur != null && cur != ancestor; cur = cur.GetParent())
            if (cur is Node3D c) t = c.Transform * t;
        return t;
    }

    void PlayEffect(XElement fx, Node3D parent)
    {
        var href = (string?)fx.Element("Effect")?.Attribute("href");
        if (string.IsNullOrEmpty(href)) return;
        // Scene effects are authored at their locator's position in the exported scene;
        // without the locator table they play at the origin (the exported frame).
        EffectPlayer.Play(href, parent);
    }

    // ------------------------------------------------------------------ lights

    void AddPointLight(XElement l)
    {
        if ((string?)l.Element("Enabled") == "false") return;
        float F(XElement? e) => e == null ? 0 : float.Parse(e.Value, CultureInfo.InvariantCulture);
        var p = l.Element("Position");
        var c = l.Element("Color");
        var light = new OmniLight3D
        {
            Position = FramePoint(F(p?.Element("x")), F(p?.Element("y")), F(p?.Element("z"))),
            LightColor = new Color(F(c?.Element("x")), F(c?.Element("y")), F(c?.Element("z"))),
            LightEnergy = F(l.Element("Intensity")),
            OmniRange = Math.Max(1, F(l.Element("Radius"))),
            OmniAttenuation = 1,
        };
        AddChild(light);
    }

    void AddLighting(XElement light)
    {
        float F(XElement? e, float d = 0) => e == null ? d : float.Parse(e.Value, CultureInfo.InvariantCulture);
        Color C(string tag, float scale = 1)
        {
            var e = light.Element(tag);
            return new Color(Math.Min(1, F(e?.Element("x")) * scale), Math.Min(1, F(e?.Element("y")) * scale), Math.Min(1, F(e?.Element("z")) * scale));
        }
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = C("FogColor"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = C("AmbientColor", 2f),
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        });
        // Sky dome named by the ambient light (as on the adventure map), drawn unshaded behind everything.
        var skyHref = (string?)light.Element("SkyDome")?.Attribute("href");
        if (!string.IsNullOrEmpty(skyHref))
        {
            var skyPath = XdbRef.Parse(skyHref, light.Document?.BaseUri ?? "").Path;
            if (!Game.Instance.Fs.Exists(skyPath)) skyPath = XdbRef.Parse(skyHref).Path;
            if (Game.Instance.Fs.Exists(skyPath))
            {
                var mesh = ModelBuilder.BuildMesh(_loader.Load(skyPath), skinned: false);
                for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                {
                    StandardMaterial3D? sm = mesh.SurfaceGetMaterial(i) switch
                    {
                        StandardMaterial3D std => (StandardMaterial3D)std.Duplicate(),
                        ShaderMaterial shm => new StandardMaterial3D { AlbedoTexture = shm.GetShaderParameter("tex").As<Texture2D>() },
                        _ => null,
                    };
                    if (sm == null) continue;
                    sm.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                    sm.DisableFog = true;
                    sm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                    mesh.SurfaceSetMaterial(i, sm);
                }
                AddChild(new MeshInstance3D { Name = "SkyDome", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = Camera?.GlobalPosition ?? Vector3.Zero });
            }
        }
        var sun = new DirectionalLight3D { Name = "Sun", LightColor = C("LightColor", 2f), ShadowEnabled = true, DirectionalShadowMaxDistance = 600 };
        float p = Mathf.DegToRad(F(light.Element("Pitch"), 45)), y = Mathf.DegToRad(F(light.Element("Yaw"), 45));
        var toSun = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Sin(p), -Mathf.Cos(p) * Mathf.Sin(y));
        AddChild(sun);
        sun.LookAtFromPosition(Vector3.Zero, -toSun, Vector3.Up);
    }

    // ------------------------------------------------------------------ camera

    static CamLimits ReadLimits(XElement desc)
    {
        var l = desc.Element("CameraLimits")?.Descendants("limits").FirstOrDefault();
        float F(string n, float d) => l?.Element(n) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        return new CamLimits(F("MinRod", 1), F("MaxRod", 1e6f), F("MinZ", -1e6f), F("MaxZ", 1e6f),
            F("MinPitch", -89), F("MaxPitch", 89), (string?)l?.Element("UsePitchLimits") == "true");
    }

    /// <summary>
    /// Places the camera from a (Camera)/initialCamPos record: Pos is the eye and Rot =
    /// (pitch, yaw) in degrees in the ArenaDesc frame (mesh space with Y negated; horizontal
    /// view direction see Dir).
    /// Rod (Maya centre of interest) gives the orbit pivot in front of the eye.
    /// </summary>
    public void SetCamera(XElement? cam)
    {
        float F(string a, string b, float d = 0) =>
            cam?.Element(a)?.Element(b) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        float G(string a, float d) => cam?.Element(a) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        var eye = FramePoint(F("Pos", "x"), F("Pos", "y"), F("Pos", "z"));
        _pitch = F("Rot", "x");
        _yaw = F("Rot", "y");
        _rod = G("Rod", 50);
        Camera.Fov = G("FOV", 54.4f);
        Camera.Near = Math.Max(0.05f, G("ClipDistanceMin", 1));
        Camera.Far = Math.Max(G("ClipDistanceMax", 1500), 3000);
        _pivot = eye + Dir(_pitch, _yaw) * _rod;
        ApplyCamera();
        if (OS.GetCmdlineUserArgs().Contains("--town-debug-cam"))
        {
            var box = new Aabb();
            bool first = true;
            foreach (var mi in FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                if (mi.Mesh == null) continue;
                var b = mi.GlobalTransformOrSelf() * mi.GetAabb();
                box = first ? b : box.Merge(b);
                first = false;
            }
            GD.Print($"[H5] town cam eye={eye} pivot={_pivot} pitch={_pitch} yaw={_yaw} rod={_rod} scene={box.Position}..{box.End}");
        }
    }

    /// <summary>
    /// View direction in mesh space (−sin yaw·cos pitch, cos yaw·cos pitch, sin pitch) → Godot. Town interiors
    /// are authored in plain mesh space: with this reading the eye lies inside the scene bounds and looks
    /// into it for all eight towns, while the Y-negated main-menu reading puts every town camera outside.
    /// </summary>
    static Vector3 Dir(float pitchDeg, float yawDeg)
    {
        float p = Mathf.DegToRad(pitchDeg), y = Mathf.DegToRad(yawDeg);
        // Yaw sign checked on the hero screen stand (camera at (−2,−4), yaw −30°, must face the hero at
        // the origin) and the fort hall (yaw −90° must face the creature spots at larger x).
        var m = new System.Numerics.Vector3(-Mathf.Sin(y) * Mathf.Cos(p), Mathf.Cos(y) * Mathf.Cos(p), Mathf.Sin(p));
        return ModelBuilder.ToGodot(m);
    }

    void ApplyCamera()
    {
        var eye = _pivot - Dir(_pitch, _yaw) * _rod;
        Camera.LookAtFromPosition(eye, _pivot, Vector3.Up);
    }

    /// <summary>User orbit (right-drag rotates/pitches, wheel zooms) within the scene's CameraLimits.</summary>
    public void Orbit(float dYaw, float dPitch, float zoom)
    {
        _yaw += dYaw;
        _pitch += dPitch;
        if (_limits.UsePitch) _pitch = Math.Clamp(_pitch, _limits.MinPitch, _limits.MaxPitch);
        _rod = Math.Clamp(_rod * zoom, _limits.MinRod, _limits.MaxRod);
        var eye = _pivot - Dir(_pitch, _yaw) * _rod;
        // MinZ/MaxZ bound the eye height (frame z = Godot y).
        if (eye.Y < _limits.MinZ || eye.Y > _limits.MaxZ)
        {
            var target = Math.Clamp(eye.Y, _limits.MinZ, _limits.MaxZ);
            _pivot.Y += target - eye.Y;
        }
        ApplyCamera();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Scene (mesh-space, Z-up) point → Godot.</summary>
    public static Vector3 FramePoint(float x, float y, float z) => ModelBuilder.ToGodot(new System.Numerics.Vector3(x, y, z));

    static string? Href(XElement? e, string current)
    {
        var h = (string?)e?.Attribute("href");
        if (string.IsNullOrEmpty(h)) return null;
        var p = XdbRef.Parse(h, current).Path;
        return p.Length == 0 ? null : p;
    }

    /// <summary>
    /// 64-byte hex 4×4 float matrix; all zeros means "as exported". Most placements are row-vector
    /// (translation in the last row), but ArenaLocObjects store column-vector matrices (translation in
    /// the last column, last row 0 0 0 1); those are transposed first.
    /// </summary>
    public static Transform3D ParsePlacement(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 128) return Transform3D.Identity;
        var bytes = Convert.FromHexString(hex[..128]);
        if (bytes.All(b => b == 0)) return Transform3D.Identity;
        float F(int i) => BitConverter.ToSingle(bytes, i * 4);
        var m = new System.Numerics.Matrix4x4(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7),
            F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
        bool columnMajor = m.M41 == 0 && m.M42 == 0 && m.M43 == 0 && (m.M14 != 0 || m.M24 != 0 || m.M34 != 0);
        if (columnMajor)
        {
            // ArenaLocObjects are raw Maya transforms: column-vector and Y-up. Transpose to row-vector,
            // then change basis Maya (x, y, z) → mesh (x, −z, y). Checked on the fort hall: the creature
            // spots land on the floor (height ≈ 9) in front of the camera.
            m = System.Numerics.Matrix4x4.Transpose(m);
            var conv = new System.Numerics.Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
            System.Numerics.Matrix4x4.Invert(conv, out var inv);
            m = inv * m * conv;
        }
        return ModelBuilder.ToGodot(m);
    }
}

static class TownSceneDebug
{
    public static Transform3D GlobalTransformOrSelf(this Node3D n)
    {
        var t = Transform3D.Identity;
        for (Node? c = n; c != null; c = c.GetParent()) if (c is Node3D d) t = d.Transform * t;
        return t;
    }
}
