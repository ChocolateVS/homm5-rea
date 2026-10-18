using Godot;

namespace H5Reborn.FrontEnd;

/// <summary>
/// The original projection (H5_Game.exe 00cf7c30): with H/W ≤ 0.75 the FOV is horizontal;
/// taller screens keep the vertical extent of 4:3 (tan(v/2) = 0.75·tan(fov/2)).
/// Extension (user-requested ultrawide support, same rule as the combat camera): screens wider
/// than 16:9 keep the vertical extent of 16:9 and show more at the sides (Hor+), so 4:3 to 16:9
/// frame exactly like the original.
/// </summary>
public static class ExeProjection
{
    public static void Apply(Camera3D cam, float fovDeg, Vector2 viewportSize)
    {
        float hw = viewportSize.X > 0 ? viewportSize.Y / viewportSize.X : 0.75f;
        float half = Mathf.DegToRad(fovDeg) * 0.5f;
        if (hw < 0.5625f)
        {
            cam.KeepAspect = Camera3D.KeepAspectEnum.Height;
            cam.Fov = Mathf.RadToDeg(2f * Mathf.Atan(0.5625f * Mathf.Tan(half)));
        }
        else if (hw <= 0.75f)
        {
            cam.KeepAspect = Camera3D.KeepAspectEnum.Width;
            cam.Fov = fovDeg;
        }
        else
        {
            cam.KeepAspect = Camera3D.KeepAspectEnum.Height;
            cam.Fov = Mathf.RadToDeg(2f * Mathf.Atan(0.75f * Mathf.Tan(half)));
        }
    }
}
