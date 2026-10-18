using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace H5.Assets.Scenes;

/// <summary>
/// The original engine's camera placement record (Rod, Pitch, Yaw, Roll, FOV, clip
/// distances, Anchor) used by dialog-scene cameras, adventure-map dialog cameras and
/// MoveCamera. Field order and offsets come from its serializer (H5_Game.exe 009fedd0).
///
/// Orientation (exe 007cd120): angles are radians; the rotation is built from half-angle
/// quaternions about the world axes — Pitch about X, Roll about Y, Yaw about Z — so the
/// camera's forward axis is +Y and its up axis +Z before rotation:
///   forward = (−sin Yaw·cos Pitch, cos Yaw·cos Pitch, sin Pitch)
/// i.e. negative pitch looks down. Anchor is the point looked at, in world units
/// (2 per map tile); the eye sits Rod units behind it along −forward. Coordinates are
/// the original's Z-up space; the client maps them to Godot.
/// </summary>
public readonly record struct CameraPos(float Rod, float Pitch, float Yaw, float Roll, float Fov,
    float ClipMin, float ClipMax, Vector3 Anchor)
{
    public static readonly CameraPos Default = new(30, -0.7f, 0, 0, 35, 1, 500, Vector3.Zero);

    public Vector3 Forward => new(-MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch));

    /// <summary>Up vector after Pitch and Roll (the exe rotates (0,0,1) by the same quaternion).</summary>
    public Vector3 Up => Vector3.Transform(Vector3.UnitZ, Rotation);

    public Quaternion Rotation =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Yaw) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, Pitch) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, Roll);

    public Vector3 Eye => Anchor - Forward * Rod;

    public static CameraPos Read(XElement? pos)
    {
        if (pos == null) return Default;
        static float F(XElement? e, float d) =>
            e != null && float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        var a = pos.Element("Anchor");
        return new CameraPos(F(pos.Element("Rod"), 30), F(pos.Element("Pitch"), 0), F(pos.Element("Yaw"), 0),
            F(pos.Element("Roll"), 0), F(pos.Element("FOV"), 35), F(pos.Element("ClipDistanceMin"), 1),
            F(pos.Element("ClipDistanceMax"), 500),
            new Vector3(F(a?.Element("x"), 0), F(a?.Element("y"), 0), F(a?.Element("z"), 0)));
    }

    /// <summary>
    /// Interpolates between two cameras the way a dialog camera set moves from its start
    /// to its finish camera. Yaw takes the shortest way unless a direction is forced
    /// (<paramref name="direction"/> 1 = increasing yaw, −1 = decreasing) and adds
    /// <paramref name="circles"/> full turns.
    /// </summary>
    public static CameraPos Lerp(in CameraPos a, in CameraPos b, float t, int direction = 0, int circles = 0)
    {
        float dy = b.Yaw - a.Yaw;
        const float Tau = MathF.PI * 2;
        dy %= Tau;
        if (direction == 0)
        {
            if (dy > MathF.PI) dy -= Tau;
            else if (dy < -MathF.PI) dy += Tau;
        }
        else if (direction > 0) { if (dy < 0) dy += Tau; dy += circles * Tau; }
        else { if (dy > 0) dy -= Tau; dy -= circles * Tau; }
        float L(float x, float y) => x + (y - x) * t;
        return new CameraPos(L(a.Rod, b.Rod), L(a.Pitch, b.Pitch), a.Yaw + dy * t, L(a.Roll, b.Roll), L(a.Fov, b.Fov),
            L(a.ClipMin, b.ClipMin), L(a.ClipMax, b.ClipMax), Vector3.Lerp(a.Anchor, b.Anchor, t));
    }
}
