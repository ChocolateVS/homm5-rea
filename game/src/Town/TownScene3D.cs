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


    readonly ModelLoader _loader = new(Game.Instance.Xdb);
    MeshInstance3D? _sky;

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

        var (main, set) = ReadLimits(desc, DescPath);
        _limits = main ?? NoLimits;
        _limitSet = set;
        Camera = new Camera3D { Name = "Camera", KeepAspect = Camera3D.KeepAspectEnum.Height };
        AddChild(Camera);
        SetCamera(desc.Element("initialCamPos"));
        Camera.MakeCurrent();
        // The dome is authored around the viewer; centre it on the initial eye.
        if (_sky != null) _sky.Position = Camera.Position;
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
        if (OS.GetCmdlineUserArgs().Contains("--town-debug-untextured"))
            for (int i = 0; i < asset.Materials.Count; i++)
                if (asset.Materials[i].DiffuseTexture is not { } t || !Game.Instance.Fs.Exists(t))
                    GD.Print($"[town] untextured material {i} in {modelPath}: {asset.Materials[i].DiffuseTexture ?? "<none>"}");
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
        // Original-renderer lighting and fog for H5Lit materials (the town scene owns the screen).
        Render.H5Lighting.Apply(light, DescPath, owner: this);
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
                _sky = new MeshInstance3D { Name = "SkyDome", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(_sky);
            }
        }
        var sun = new DirectionalLight3D { Name = "Sun", LightColor = C("LightColor", 2f), ShadowEnabled = true, DirectionalShadowMaxDistance = 600 };
        float p = Mathf.DegToRad(F(light.Element("Pitch"), 45)), y = Mathf.DegToRad(F(light.Element("Yaw"), 45));
        var toSun = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Sin(p), -Mathf.Cos(p) * Mathf.Sin(y));
        AddChild(sun);
        sun.LookAtFromPosition(Vector3.Zero, -toSun, Vector3.Up);
    }

    // ------------------------------------------------------------------ camera

    /// <summary>One CameraLimits record (fields in the exe's order, 0093c030); pitch in degrees.</summary>
    public record struct CamLimits(float MinRod, float MaxRod, float MinZ, float MaxZ, float MinPitch, float MaxPitch);

    /// <summary>
    /// CTownOperator's limits before any are set (base hand-operator ctor 007d5990 → +0x64): rod and height
    /// ±10⁶, pitch 0..0. Used by town scenes without a CameraLimits record.
    /// </summary>
    static readonly CamLimits NoLimits = new(-1e6f, 1e6f, -1e6f, 1e6f, 0, 0);

    /// <summary>The ArenaDesc CameraLimits (inline or a separate .xdb) and its CameraLimitsSet entries.</summary>
    static (CamLimits? Main, List<CamLimits> Set) ReadLimits(XElement desc, string descPath)
    {
        CamLimits? Read(XElement? item)
        {
            if (item == null) return null;
            var l = item.Descendants("limits").FirstOrDefault();
            if (l == null)
            {
                var href = (string?)item.Attribute("href");
                if (string.IsNullOrEmpty(href) || href.StartsWith("#n:inline")) return null;
                var path = XdbRef.Parse(href, descPath).Path;
                if (path.Length == 0 || !Game.Instance.Fs.Exists(path)) return null;
                l = Game.Instance.Xdb.Load(path).Root?.Descendants("limits").FirstOrDefault();
                if (l == null) return null;
            }
            float F(string n) => l.Element(n) is { } e && float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return new CamLimits(F("MinRod"), F("MaxRod"), F("MinZ"), F("MaxZ"), F("MinPitch"), F("MaxPitch"));
        }
        var set = new List<CamLimits>();
        foreach (var it in desc.Element("CameraLimitsSet")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (Read(it) is { } c) set.Add(c);
        return (Read(desc.Element("CameraLimits")), set);
    }

    /// <summary>SCameraPos as CTownOperator keeps it: rod, pitch / yaw (radians), anchor in mesh space (Z up).</summary>
    struct Pose { public float Rod, Pitch, Yaw; public System.Numerics.Vector3 Anchor; }

    Pose _initial, _cur, _tgt;
    float _fovDeg = 54.4322f;
    CamLimits _limits = NoLimits;
    List<CamLimits> _limitSet = new();
    bool _hand;
    Character? _flyRig;
    int _flyBone = -1;
    /// <summary>Camera input accumulated since the last frame (the exe's input values, read and cleared each update).</summary>
    float _inYaw, _inPitch, _inLift, _inZoom;

    /// <summary>True while the town's fly-up camera (cameraFlyupRole) drives the view.</summary>
    public bool FlybyActive => _flyRig != null && !_hand;

    /// <summary>
    /// Initial pose from a (Camera) record, as the town configurator's default pose (007d3ad0 → 007ce1a0):
    /// Pos is the eye; pitch / yaw = Rot.x / Rot.y (degrees → radians); anchor = eye + Rod · forward; FOV and
    /// the clip distances come with it (applied when ClipDistanceMin &lt; ClipDistanceMax).
    /// </summary>
    public void SetCamera(XElement? cam)
    {
        float F(string a, string b, float d = 0) =>
            cam?.Element(a)?.Element(b) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        float G(string a, float d) => cam?.Element(a) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        var eye = new System.Numerics.Vector3(F("Pos", "x"), F("Pos", "y"), F("Pos", "z"));
        var p = new Pose { Rod = G("Rod", 50), Pitch = Mathf.DegToRad(F("Rot", "x")), Yaw = Mathf.DegToRad(F("Rot", "y")) };
        p.Anchor = eye + MeshDir(p.Pitch, p.Yaw) * p.Rod;
        _initial = _cur = _tgt = p;
        _fovDeg = G("FOV", 54.4322f);
        float near = G("ClipDistanceMin", 1), far = G("ClipDistanceMax", 1500);
        // The far plane stays at least 3000 so the sky dome (drawn as scene geometry here) is not clipped.
        if (near < far) { Camera.Near = Math.Max(0.05f, near); Camera.Far = Math.Max(far, 3000); }
        ApplyPose(_cur);
        if (OS.GetCmdlineUserArgs().Contains("--town-debug-cam"))
            GD.Print($"[H5] town cam eye={eye} anchor={p.Anchor} pitch={F("Rot", "x")} yaw={F("Rot", "y")} rod={p.Rod} limits={_limits} set={_limitSet.Count}");
    }

    /// <summary>Forward of qZ(yaw)·qX(pitch) applied to +Y, in mesh space (docs/rules/CAMERA.md).</summary>
    static System.Numerics.Vector3 MeshDir(float pitch, float yaw) =>
        new(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));

    static Vector3 Dir(float pitch, float yaw) => ModelBuilder.ToGodot(MeshDir(pitch, yaw));

    void ApplyPose(in Pose p)
    {
        var eye = ModelBuilder.ToGodot(p.Anchor - MeshDir(p.Pitch, p.Yaw) * p.Rod);
        var fwd = Dir(p.Pitch, p.Yaw);
        Camera.GlobalTransform = new Transform3D(Basis.LookingAt(fwd, Mathf.Abs(fwd.Y) > 0.999f ? Vector3.Forward : Vector3.Up), eye);
        ApplyProjection();
    }

    /// <summary>The exe's projection (00cf7c30): FOV horizontal up to 4:3 (see FrontEnd.ExeProjection).</summary>
    void ApplyProjection()
    {
        if (IsInsideTree()) FrontEnd.ExeProjection.Apply(Camera, _fovDeg, GetViewport().GetVisibleRect().Size);
    }

    /// <summary>
    /// The town screen's camera (CTownScreen 0061c5b0): with `town_enable_flyby` (DAT_00f36490, default 1) and a
    /// cameraFlyupRole model the fly-up plays first (00616d20: an animated camera from the role's model and
    /// AnimSet, FOV / clip from initialCamPos); otherwise the hand camera at once (<see cref="EndFlyby"/>).
    /// </summary>
    public void StartTownCamera(bool flyby)
    {
        var desc = Game.Instance.Xdb.Load(DescPath).Root!;
        var role = desc.Element("cameraFlyupRole");
        var modelPath = Href(role?.Element("Model"), DescPath);
        var setPath = Href(role?.Element("AnimSet"), DescPath);
        if (flyby && modelPath != null && Game.Instance.Fs.Exists(modelPath) && setPath != null && Game.Instance.Fs.Exists(setPath))
        {
            try
            {
                var rig = CharacterBuilder.BuildFromModel(modelPath, setPath, "CameraRole");
                foreach (var mi in rig.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>()) mi.Visible = false;
                AddChild(rig);
                int bone = rig.Skeleton.FindBone("camera");
                var first = rig.Player.GetAnimationList().FirstOrDefault()?.Replace(Character.Lib, "");
                if (bone >= 0 && first != null)
                {
                    // The role's idle00 is a long tour around the town (Necropolis 53.9 s) that the screen never ends
                    // by itself (00616d20 keeps no end time; only input sets +0x294). It loops like the main menu's
                    // role clip (007d1b80). TODO(exe): looping not traced inside the animated model player (007d1920).
                    rig.Play(rig.Has("idle00") ? "idle00" : first, loop: true);
                    _flyRig = rig;
                    _flyBone = bone;
                    _hand = false;
                    UpdateFlyby();
                    return;
                }
                rig.QueueFree();
            }
            catch (Exception e) { GD.PushWarning($"[H5] town fly-up {setPath}: {e.Message}"); }
        }
        EndFlyby();
    }

    /// <summary>
    /// Switches to the hand camera (00616bf0 + 00617690; called on the first left button down / up and by the
    /// building and HUD actions, all of which set screen +0x294): a new camera at initialCamPos and a
    /// CTownOperator with the scene's CameraLimits (007d3bd0) and CameraLimitsSet (0061f7a0), its pose set at once.
    /// </summary>
    public void EndFlyby()
    {
        if (_hand) return;
        _hand = true;
        _flyRig?.QueueFree();
        _flyRig = null;
        _cur = _tgt = _initial;
        _inYaw = _inPitch = _inLift = _inZoom = 0;
        ApplyPose(_cur);
    }

    /// <summary>
    /// set_camera_default_pos (HOME, 00616300 → 006153e0): the configurator's default pose is put on the
    /// camera only; the operator keeps its target, so the view glides back from initialCamPos to it.
    /// </summary>
    public void DefaultPos()
    {
        if (!_hand) return;
        _cur = _initial;
        ApplyPose(_cur);
    }

    /// <summary>Camera input in the exe's units (input_a2.cfg common camera bindings), consumed by the next update.</summary>
    public void AddInput(float rotate, float pitch, float forward, float zoom)
    {
        _inYaw += rotate;
        _inPitch += pitch;
        _inLift += forward;
        _inZoom += zoom;
    }

    public override void _Process(double delta)
    {
        if (Camera == null) return;
        if (_flyRig != null && !_hand) { UpdateFlyby(); return; }
        if (_hand) UpdateHand(Math.Min((float)delta, 0.1f));
        else ApplyProjection();
    }

    void UpdateFlyby()
    {
        if (_flyRig == null || _flyBone < 0) return;
        var t = _flyRig.Skeleton.GlobalTransform * _flyRig.Skeleton.GetBoneGlobalPose(_flyBone);
        Camera.GlobalTransform = t.Orthonormalized();
        ApplyProjection();
        if (_debugCam && Engine.GetProcessFrames() % 30 == 0)
            GD.Print($"[H5] town fly-up t={_flyRig.Player.CurrentAnimationPosition:0.00}/{_flyRig.Player.CurrentAnimationLength:0.00} eye(mesh)={Mesh(Camera.GlobalPosition)} fwd={Mesh(-Camera.GlobalBasis.Z)}");
    }

    readonly bool _debugCam = OS.GetCmdlineUserArgs().Contains("--town-debug-cam");
    static Vector3 Mesh(Vector3 g) => new(g.X, -g.Z, g.Y);

    // CTownOperator::Update (007d5550) constants.
    const float YawPerInput = 0.7f;      // DAT_00f3fe4c × (strafe + rotate + rotate_mouse)
    const float PitchPerInput = 0.5f;    // DAT_00f3fe48 × (pitch_mouse + pitch)
    const float LiftPerInput = 15f;      // DAT_00f3fe40 × (forward_mouse + forward) → anchor height
    const float ZoomPerInput = 15f;      // DAT_00f3fe44 × (zoom + zoom_mouse) → rod
    const float YawLimit = 90f;          // ±90 (radians): yaw is effectively free
    // Glide of the shown pose to the target (007cccb0 / 007ccc10, docs/rules/CAMERA.md); eps 0.03 for the anchor.
    const double RodBase = 1.0 / 300.0, AngleBase = 1.0 / 9000.0, AnchorBase = 1.1111111e-4;
    const float RodEps = 0.02f, PitchEps = 0.01f, AngleEps = 1e-5f, AnchorEps = 0.03f;

    void UpdateHand(float dt)
    {
        if (dt <= 0) return;
        // Limits blended by the shown anchor height over the CameraLimitsSet records containing it (weight =
        // distance to the nearer edge / range, normalised); the main record when none does. Only rod and pitch
        // come from the set; the height range always from the main record.
        var lim = _limits;
        if (_limitSet.Count > 0)
        {
            float z = _cur.Anchor.Z, w = 0, r0 = 0, r1 = 0, p0 = 0, p1 = 0;
            foreach (var e in _limitSet)
            {
                if (!(e.MinZ < z && z < e.MaxZ)) continue;
                float d = Math.Min(e.MaxZ - z, z - e.MinZ) * (1f / (e.MaxZ - e.MinZ));
                r0 += d * e.MinRod; r1 += d * e.MaxRod; p0 += d * e.MinPitch; p1 += d * e.MaxPitch; w += d;
            }
            if (w > 0) lim = lim with { MinRod = r0 / w, MaxRod = r1 / w, MinPitch = p0 / w, MaxPitch = p1 / w };
        }
        _tgt.Yaw = Step(_tgt.Yaw, _inYaw * YawPerInput, -YawLimit, YawLimit);
        _tgt.Pitch = Step(_tgt.Pitch, _inPitch * PitchPerInput, Mathf.DegToRad(lim.MinPitch), Mathf.DegToRad(lim.MaxPitch));
        _tgt.Anchor.Z = Step(_tgt.Anchor.Z, _inLift * LiftPerInput, lim.MinZ, lim.MaxZ);
        _tgt.Rod = Step(_tgt.Rod, _inZoom * ZoomPerInput, lim.MinRod, lim.MaxRod);
        _inYaw = _inPitch = _inLift = _inZoom = 0;

        _cur.Rod = Approach(_tgt.Rod, _cur.Rod, RodBase, dt, RodEps);
        _cur.Pitch = Approach(_tgt.Pitch, _cur.Pitch, AngleBase, dt, PitchEps);
        float dYaw = _cur.Yaw - _tgt.Yaw;
        _cur.Yaw = _tgt.Yaw + (Mathf.PosMod(dYaw + Mathf.Pi, Mathf.Tau) - Mathf.Pi);
        _cur.Yaw = Approach(_tgt.Yaw, _cur.Yaw, AngleBase, dt, AngleEps);
        _cur.Anchor.X = Approach(_tgt.Anchor.X, _cur.Anchor.X, AnchorBase, dt, AnchorEps);
        _cur.Anchor.Y = Approach(_tgt.Anchor.Y, _cur.Anchor.Y, AnchorBase, dt, AnchorEps);
        _cur.Anchor.Z = Approach(_tgt.Anchor.Z, _cur.Anchor.Z, AnchorBase, dt, AnchorEps);
        ApplyPose(_cur);
    }

    /// <summary>007d4340 with equal soft and hard limits: add the input, then clamp (min first, max last).</summary>
    static float Step(float v, float input, float min, float max)
    {
        v += input;
        if (v < min) v = min;
        if (max < v) v = max;
        return v;
    }

    /// <summary>FUN_007ccc10: target + (current − target)·base^dt, snapping inside eps.</summary>
    static float Approach(float target, float current, double basePerSecond, float dt, float eps)
    {
        if (Mathf.Abs(target - current) < eps) return target;
        return (float)(target + (current - target) * Math.Pow(basePerSecond, dt));
    }

    /// <summary>Dev (`--town-orbit=yaw,pitch`): turns the hand camera's target by degrees, unclamped, at once.</summary>
    public void Orbit(float dYawDeg, float dPitchDeg, float zoom)
    {
        EndFlyby();
        _tgt.Yaw += Mathf.DegToRad(dYawDeg);
        _tgt.Pitch += Mathf.DegToRad(dPitchDeg);
        _tgt.Rod *= zoom;
        _cur = _tgt;
        ApplyPose(_cur);
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
