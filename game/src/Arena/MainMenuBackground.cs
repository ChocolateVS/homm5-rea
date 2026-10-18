using System.Globalization;
using System.Xml.Linq;
using Godot;
using H5.Assets.Model;
using H5.Core.Xdb;
using H5Reborn.Effects;
using H5Reborn.Render;

namespace H5Reborn.Arena;

/// <summary>
/// The animated 3D backdrop behind the main menu, built like the original
/// (CMainMenuScreen, H5_Game.exe 00694610): the root-table ArenaDesc
/// Arenas/Interface/MainMenu/MainMenu.(ArenaDesc).xdb is loaded into a 3D view and
/// <list type="bullet">
/// <item>every ArenaEnvObject is placed with its Model and plays its AnimSet's idle00 in a loop
/// (the Haven troops, griffins, banners…);</item>
/// <item>the ArenaDesc effects are started at the scene origin (their Locator is empty);</item>
/// <item>the view is driven by the cameraFlyupRole: an animated "camera" model whose 30 s
/// idle00 clip loops seamlessly. 00694490 → 007d1b80 builds the animated model and wraps it in
/// a track-camera operator (007d2140) that reads the world matrix of the bone named
/// "camera" every frame (007d20e0). The engine camera looks along local +Y with +Z up
/// (docs/rules/CAMERA.md); after the Z-up → Y-up basis change that is exactly a Godot camera
/// (−Z forward, +Y up) carrying the bone's global transform;</item>
/// <item>the operator's FOV / near / far come from initialCamPos (FOV 54.43°, 0.5, 10000):
/// 00694490 passes the ArenaDesc camera record's FOV and clip distances to the operator.</item>
/// </list>
/// Placement matrices (non-zero on the Background card and a few props) are Y-negated relative
/// to mesh space (see <see cref="ParsePlacement"/>). Meant to be placed in a SubViewport behind
/// the menu UI.
/// </summary>
public partial class MainMenuBackground : Node3D
{
    public const string DescPath = "Arenas/Interface/MainMenu/MainMenu.(ArenaDesc).xdb";

    public Camera3D Camera { get; private set; } = null!;

    Skeleton3D? _cameraSkeleton;
    int _cameraBone = -1;
    float _fovDeg = 54.4322f;

    public override void _Ready()
    {
        var xdb = Game.Instance.Xdb;
        var desc = xdb.Load(DescPath).Root!;
        var loader = new ModelLoader(xdb);

        foreach (var item in desc.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var objPath = XdbRef.Parse((string?)item.Attribute("href"), DescPath).Path;
            if (!Game.Instance.Fs.Exists(objPath)) continue;
            var obj = xdb.Load(objPath).Root!;
            var modelPath = XdbRef.Parse((string?)obj.Element("Model")?.Attribute("href"), objPath).Path;
            if (string.IsNullOrEmpty(modelPath) || !Game.Instance.Fs.Exists(modelPath)) continue;
            var animSetHref = (string?)obj.Element("AnimSet")?.Attribute("href");
            var animSetPath = string.IsNullOrEmpty(animSetHref) ? null : XdbRef.Parse(animSetHref, objPath).Path;
            var name = System.IO.Path.GetFileNameWithoutExtension(objPath);
            try
            {
                Node3D node;
                if (animSetPath != null && Game.Instance.Fs.Exists(animSetPath))
                    node = CharacterBuilder.BuildFromModel(modelPath, animSetPath, name);
                else
                {
                    var model = loader.Load(modelPath);
                    node = new MeshInstance3D { Name = name, Mesh = ModelBuilder.BuildMesh(model, skinned: false) };
                }
                node.Transform = ParsePlacement(obj.Element("placement")?.Value);
                AddChild(node);
            }
            catch (Exception e) { GD.PushWarning($"[H5] menu object {objPath}: {e.Message}"); }
        }

        var lightRef = desc.Element("AmbientLights")?.Elements("Item").FirstOrDefault();
        if (xdb.Resolve(lightRef, DescPath) is { } light) AddLighting(light);

        var cam = desc.Element("initialCamPos");
        float G(string a, float d) => cam?.Element(a) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        _fovDeg = G("FOV", 54.4322f);
        Camera = new Camera3D { Name = "Camera", Near = G("ClipDistanceMin", 0.5f), Far = G("ClipDistanceMax", 10000f) };
        AddChild(Camera);
        Camera.MakeCurrent();
        BuildCameraRole(desc.Element("cameraFlyupRole"));

        foreach (var fx in desc.Element("effects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var href = (string?)fx.Element("Effect")?.Attribute("href");
            if (string.IsNullOrEmpty(href)) continue;
            var player = EffectPlayer.Play(href, this);
            if (player == null) continue;
            player.AutoFree = false;
            player.Seek(PreRoll);
        }
        // The scene clock starts at 10 s (00694610 updates the scene to 10000 then 10001 ms before the
        // track camera starts at that moment), so smoke, fire and idles are already running while the
        // camera clip begins at 0.
        foreach (var c in FindChildren("*", nameof(Character), recursive: false, owned: false).OfType<Character>())
        {
            if (c.Name == "CameraRole" || !c.Player.IsPlaying()) continue;
            var len = c.Player.CurrentAnimationLength;
            if (len > 0) c.Player.Seek(PreRoll % len, update: true);
        }
        UpdateCamera();
    }

    const float PreRoll = 10f;

    public override void _Process(double delta) => UpdateCamera();

    /// <summary>The flyup role: an animated model whose bone "camera" carries the view.</summary>
    void BuildCameraRole(XElement? role)
    {
        if (role == null) return;
        var modelPath = XdbRef.Parse((string?)role.Element("Model")?.Attribute("href"), DescPath).Path;
        var setPath = XdbRef.Parse((string?)role.Element("AnimSet")?.Attribute("href"), DescPath).Path;
        if (string.IsNullOrEmpty(modelPath) || !Game.Instance.Fs.Exists(modelPath)) return;
        var rig = CharacterBuilder.BuildFromModel(modelPath, Game.Instance.Fs.Exists(setPath) ? setPath : null, "CameraRole");
        // The camera model carries a small frustum mesh used only in the editor; never drawn in game.
        foreach (var mi in rig.FindChildren("*", nameof(MeshInstance3D), recursive: true, owned: false).OfType<MeshInstance3D>())
            mi.Visible = false;
        AddChild(rig);
        _cameraSkeleton = rig.Skeleton;
        _cameraBone = rig.Skeleton.FindBone("camera");
    }

    void UpdateCamera()
    {
        if (Camera == null) return;
        if (_cameraSkeleton != null && _cameraBone >= 0)
        {
            var t = _cameraSkeleton.GlobalTransform * _cameraSkeleton.GetBoneGlobalPose(_cameraBone);
            Camera.GlobalTransform = t.Orthonormalized();
        }
        ApplyProjection();
    }

    void ApplyProjection() => FrontEnd.ExeProjection.Apply(Camera, _fovDeg, GetViewport().GetVisibleRect().Size);

    /// <summary>64-byte hex: a 4×4 float row-vector matrix in mesh space; all zeros means identity.</summary>
    static Transform3D ParsePlacement(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 128) return Transform3D.Identity;
        var bytes = Convert.FromHexString(hex[..128]);
        if (bytes.All(b => b == 0)) return Transform3D.Identity;
        float F(int i) => BitConverter.ToSingle(bytes, i * 4);
        var m = new System.Numerics.Matrix4x4(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7),
            F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
        // Placements are written with Y negated relative to mesh/skeleton space: conjugate by
        // diag(1,−1,1). Evidence: the only significant one, the Background sky card
        // (translation −36.7, 130.8, 23.9), lands far behind the castle walls as a backdrop
        // this way (y ≈ −197), and behind the animated camera (y ≈ +65, never visible) otherwise.
        var flip = System.Numerics.Matrix4x4.CreateScale(1, -1, 1);
        return ModelBuilder.ToGodot(flip * m * flip);
    }

    void AddLighting(XElement light)
    {
        // Original-renderer lighting and fog for H5Lit materials (the menu scene owns the screen).
        H5Lighting.Apply(light, DescPath);
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
        var sun = new DirectionalLight3D { Name = "Sun", LightColor = C("LightColor", 2f), ShadowEnabled = true };
        float p = Mathf.DegToRad(F(light.Element("Pitch"), 45)), y = Mathf.DegToRad(F(light.Element("Yaw"), 45));
        var toSun = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Sin(p), -Mathf.Cos(p) * Mathf.Sin(y));
        AddChild(sun);
        sun.LookAtFromPosition(Vector3.Zero, -toSun, Vector3.Up);
    }
}
