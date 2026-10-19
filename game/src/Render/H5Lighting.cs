using System.Globalization;
using System.Xml.Linq;
using Godot;

namespace H5Reborn.Render;

/// <summary>
/// Scene lighting parameters for the original renderer reproduction (H5Lit shaders):
/// the AmbientLight record's colours and sun direction, plus its DistanceFog/HeightFog,
/// published as global shader uniforms. Each 3D scene calls <see cref="Apply"/> when it
/// becomes active (scenes are shown one at a time).
///
/// From H5_Game.exe: per-vertex colours are Ambient + Light·max(N·L,0) + Shade·max(−N·L,0)
/// (Incident replaces Light inside cast shadows), clamped, and the pixel shader outputs
/// saturate(4·tex·colour·c29); the net factor is <see cref="ScaleFor"/> (c29 not traced).
/// Fog: FUN_009add40 / FUN_009ade70.
/// </summary>
public static class H5Lighting
{
    public const float LightScale = 2f;

    /// <summary>
    /// Net output factor. Inferred, not traced: Whitening lights (101 stock records, median
    /// Light+Ambient 0.56) and the others (24, median 0.36; underground/night sets far lower)
    /// both reach full brightness on sunlit faces only if Whitening ones use 2 and the rest the
    /// pixel shader's full 4.
    /// </summary>
    public static float ScaleFor(XElement? light) =>
        string.Equals((string?)light?.Element("Whitening"), "false", StringComparison.OrdinalIgnoreCase) ? 4f : LightScale;

    static bool _registered;

    static void Register()
    {
        if (_registered) return;
        _registered = true;
        void Add(string name, RenderingServer.GlobalShaderParameterType type, Variant value)
        {
            // Registered once per process (GlobalShaderParameterGetList is editor-only).
            RenderingServer.GlobalShaderParameterAdd(name, type, value);
        }
        Add("h5_sun_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.5f, 0.7f, 0.5f).Normalized());
        Add("h5_light_color", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.35f, 0.35f, 0.35f));
        Add("h5_ambient_color", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.2f, 0.2f, 0.2f));
        Add("h5_shade_color", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.15f, 0.15f, 0.15f));
        Add("h5_incident_color", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.15f, 0.15f, 0.15f));
        Add("h5_light_scale", RenderingServer.GlobalShaderParameterType.Float, LightScale);
        Add("h5_fog_params", RenderingServer.GlobalShaderParameterType.Vec4, new Vector4(1000, 2000, -10000, -10001));
        Add("h5_fog_color", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        Add("h5_fog_enabled", RenderingServer.GlobalShaderParameterType.Float, 0f);
        var white = ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.R8, new byte[] { 255 }));
        Add("h5_warfog_a", RenderingServer.GlobalShaderParameterType.Sampler2D, white);
        Add("h5_warfog_b", RenderingServer.GlobalShaderParameterType.Sampler2D, white);
        Add("h5_warfog", RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero);
    }

    public readonly record struct Fog(bool Enabled, Vector3 Color, float MinDist, float MaxDist, float MinZ, float MaxZ);

    /// <summary>Direction towards the sun in Godot space from the record's Pitch/Yaw (degrees, source Z-up).</summary>
    public static Vector3 SunDirection(float pitchDeg, float yawDeg)
    {
        float p = Mathf.DegToRad(pitchDeg), y = Mathf.DegToRad(yawDeg);
        var src = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Cos(p) * Mathf.Sin(y), Mathf.Sin(p));
        return new Vector3(src.X, src.Z, -src.Y).Normalized();
    }

    static ulong _ownerId;

    /// <summary>
    /// The scene whose light the globals hold (the last <see cref="Apply"/> caller). Overlays that bring
    /// their own 3D scene (dialog scenes, the hero screen stand, the town) take the globals over while
    /// the adventure map stays in the tree underneath; the map must not publish its fog of war then.
    /// </summary>
    public static Node? Owner => _ownerId != 0 && GodotObject.InstanceFromId(_ownerId) is Node n && GodotObject.IsInstanceValid(n) ? n : null;

    /// <summary>True while another scene than <paramref name="self"/> owns the globals and is still shown.</summary>
    public static bool OwnedByOther(Node self) =>
        Owner is { } o && o != self && o.IsInsideTree() && (o is not Node3D n3 || n3.IsVisibleInTree());

    /// <summary>Publishes an AmbientLight record (and its fog) to the shaders.</summary>
    public static void Apply(XElement? light, string? lightFile, Func<string, XElement?>? resolve = null, Node? owner = null)
    {
        Register();
        _ownerId = owner?.GetInstanceId() ?? 0;
        Vector3 V(string n, float d)
        {
            var e = light?.Element(n);
            return e == null ? new Vector3(d, d, d) : new Vector3(F(e.Element("x"), d), F(e.Element("y"), d), F(e.Element("z"), d));
        }
        RenderingServer.GlobalShaderParameterSet("h5_light_color", V("LightColor", 0.35f));
        RenderingServer.GlobalShaderParameterSet("h5_ambient_color", V("AmbientColor", 0.2f));
        RenderingServer.GlobalShaderParameterSet("h5_shade_color", V("ShadeColor", 0.15f));
        RenderingServer.GlobalShaderParameterSet("h5_incident_color", V("IncidentShadowColor", 0.15f));
        RenderingServer.GlobalShaderParameterSet("h5_sun_dir", SunDirection(F(light?.Element("Pitch"), 45), F(light?.Element("Yaw"), 45)));
        RenderingServer.GlobalShaderParameterSet("h5_light_scale", ScaleFor(light));
        SetFog(ReadFog(light, lightFile, resolve));
        // Fog of war belongs to the adventure map only; it re-enables it after applying its light.
        DisableWarFog();
    }

    /// <summary>
    /// Fog-of-war grids for the H5Lit shaders (see AdvWorld fog of war): <paramref name="a"/> the newest,
    /// <paramref name="b"/> the previous one, crossfaded by <paramref name="t"/> (A weight); sizes are
    /// nextPow2 + 1 texels per side.
    /// </summary>
    public static void SetWarFog(Texture2D a, Texture2D b, float t, int gridW, int gridH)
    {
        Register();
        RenderingServer.GlobalShaderParameterSet("h5_warfog_a", a);
        RenderingServer.GlobalShaderParameterSet("h5_warfog_b", b);
        RenderingServer.GlobalShaderParameterSet("h5_warfog", new Vector4(t, 1, gridW - 2, gridH - 2));
    }

    /// <summary>Updates only the crossfade weight of the current grids.</summary>
    public static void SetWarFogFade(float t, int gridW, int gridH) =>
        RenderingServer.GlobalShaderParameterSet("h5_warfog", new Vector4(t, 1, gridW - 2, gridH - 2));

    public static void DisableWarFog()
    {
        Register();
        RenderingServer.GlobalShaderParameterSet("h5_warfog", Vector4.Zero);
    }

    public static void SetFog(Fog fog)
    {
        Register();
        RenderingServer.GlobalShaderParameterSet("h5_fog_enabled", fog.Enabled ? 1f : 0f);
        RenderingServer.GlobalShaderParameterSet("h5_fog_color", fog.Color);
        RenderingServer.GlobalShaderParameterSet("h5_fog_params", new Vector4(fog.MinDist, fog.MaxDist, fog.MinZ, fog.MaxZ));
    }

    /// <summary>
    /// The record's DistanceFog (colour, MinDist, MaxDist, MinZDis, MaxZDis — the fog block the exe
    /// hands to FUN_009add40). Without one the fog is off. A HeightFog record supplies a pure height
    /// fog (fog below MaxHeight thickening towards MinHeight).
    /// </summary>
    public static Fog ReadFog(XElement? light, string? lightFile, Func<string, XElement?>? resolve)
    {
        resolve ??= DefaultResolve;
        XElement? Ref(string name)
        {
            var href = (string?)light?.Element(name)?.Attribute("href");
            if (string.IsNullOrEmpty(href)) return null;
            var path = H5.Core.Xdb.XdbRef.Parse(href, lightFile).Path;
            return resolve(path);
        }
        if (Ref("DistanceFog") is { } d)
        {
            var c = d.Element("Color");
            return new Fog(true, new Vector3(F(c?.Element("x"), 0), F(c?.Element("y"), 0), F(c?.Element("z"), 0)),
                F(d.Element("MinDist"), 1000), F(d.Element("MaxDist"), 2000), F(d.Element("MinZDis"), -10000), F(d.Element("MaxZDis"), -10001));
        }
        if (Ref("HeightFog") is { } h)
        {
            var c = h.Element("FogColor");
            // Fog below MaxHeight, full at MinHeight: MinZ = MaxHeight (factor 1), MaxZ = MinHeight (factor 0).
            return new Fog(true, new Vector3(F(c?.Element("x"), 0), F(c?.Element("y"), 0), F(c?.Element("z"), 0)),
                1e6f, 2e6f, F(h.Element("MaxHeight"), 0), F(h.Element("MinHeight"), -1));
        }
        return new Fog(false, Vector3.Zero, 1000, 2000, -10000, -10001);
    }

    static XElement? DefaultResolve(string path) =>
        Game.Instance.Fs.Exists(path) ? Game.Instance.Xdb.Load(path).Root : null;

    static float F(XElement? e, float def) =>
        e != null && float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
}
