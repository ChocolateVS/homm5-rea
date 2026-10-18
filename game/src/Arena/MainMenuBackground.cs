using System.Globalization;
using System.Xml.Linq;
using Godot;
using H5.Assets.Model;
using H5.Core.Xdb;
using H5Reborn.Render;

namespace H5Reborn.Arena;

/// <summary>
/// The 3D backdrop behind the main menu: Arenas/Interface/MainMenu/MainMenu.(ArenaDesc).xdb
/// (walls, terrain, Haven troops, dead bodies, props) lit by its AmbientLight and seen
/// from initialCamPos. Meant to be placed in a SubViewport behind the menu UI.
/// Currently static: the troops' idle animations and the camera fly-up
/// (cameraFlyupRole) are not played yet.
/// </summary>
public partial class MainMenuBackground : Node3D
{
    public const string DescPath = "Arenas/Interface/MainMenu/MainMenu.(ArenaDesc).xdb";

    public Camera3D Camera { get; private set; } = null!;

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
            try
            {
                var model = loader.Load(modelPath);
                var mi = new MeshInstance3D { Name = System.IO.Path.GetFileNameWithoutExtension(objPath), Mesh = ModelBuilder.BuildMesh(model, skinned: false) };
                mi.Transform = ParsePlacement(obj.Element("placement")?.Value);
                AddChild(mi);
            }
            catch (Exception e) { GD.PushWarning($"[H5] menu object {objPath}: {e.Message}"); }
        }

        var lightRef = desc.Element("AmbientLights")?.Elements("Item").FirstOrDefault();
        if (xdb.Resolve(lightRef, DescPath) is { } light) AddLighting(light);

        Camera = BuildCamera(desc.Element("initialCamPos"));
        AddChild(Camera);
        Camera.MakeCurrent();
    }

    /// <summary>64-byte hex: a 4×4 float row-vector matrix; all zeros means "as exported" (identity).</summary>
    static Transform3D ParsePlacement(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 128) return Transform3D.Identity;
        var bytes = Convert.FromHexString(hex[..128]);
        if (bytes.All(b => b == 0)) return Transform3D.Identity;
        float F(int i) => BitConverter.ToSingle(bytes, i * 4);
        var m = new System.Numerics.Matrix4x4(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7),
            F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
        // The ArenaDesc frame is mesh space with Y negated (see BuildCamera): conjugate by diag(1,−1,1).
        var flip = System.Numerics.Matrix4x4.CreateScale(1, -1, 1);
        return ModelBuilder.ToGodot(flip * m * flip);
    }

    /// <summary>
    /// Pos is the eye and Rot = (pitch, yaw) in degrees, in the ArenaDesc frame: mesh space
    /// (Z-up) with Y negated, the same frame as the placement matrices. In that frame the
    /// horizontal view direction is (sin yaw, −cos yaw). Evidence: with this reading the
    /// camera stands at the front of the battle scene facing the troops (whose meshes are
    /// exported in place, at Godot +Z), with the Background card far behind them; with Y
    /// not negated the camera faces away from every troop.
    /// </summary>
    static Camera3D BuildCamera(XElement? cam)
    {
        float F(string a, string b, float d = 0) =>
            cam?.Element(a)?.Element(b) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;
        float G(string a, float d) => cam?.Element(a) is { } e ? float.Parse(e.Value, CultureInfo.InvariantCulture) : d;

        // Frame (x, y, z) → mesh space (x, −y, z) → Godot (x, z, y).
        var eye = new Vector3(F("Pos", "x"), F("Pos", "z"), F("Pos", "y"));
        float pitch = Mathf.DegToRad(F("Rot", "x")), yaw = Mathf.DegToRad(F("Rot", "y"));
        var dirFrame = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), -Mathf.Cos(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch));
        var dir = new Vector3(dirFrame.X, dirFrame.Z, dirFrame.Y);
        var c = new Camera3D
        {
            Name = "Camera", Fov = G("FOV", 54.4f), KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = G("ClipDistanceMin", 0.5f), Far = G("ClipDistanceMax", 10000f),
        };
        c.LookAtFromPosition(eye, eye + dir, Vector3.Up);
        return c;
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
        var sun = new DirectionalLight3D { Name = "Sun", LightColor = C("LightColor", 2f), ShadowEnabled = true };
        float p = Mathf.DegToRad(F(light.Element("Pitch"), 45)), y = Mathf.DegToRad(F(light.Element("Yaw"), 45));
        var toSun = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Sin(p), -Mathf.Cos(p) * Mathf.Sin(y));
        AddChild(sun);
        sun.LookAtFromPosition(Vector3.Zero, -toSun, Vector3.Up);
    }
}
