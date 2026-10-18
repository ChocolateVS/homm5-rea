using Godot;

namespace H5Reborn.Render;

/// <summary>
/// Material for the original AM_OVERLAY / AM_OVERLAY_ZWRITE alpha modes: ground decals
/// (dirt patches, soft shadows under bushes) and the combat grid. The texture is
/// overlay-blended onto what is already drawn, weighted by its alpha.
/// </summary>
public static class OverlayMaterial
{
    static Shader? _shader;

    public static ShaderMaterial Create(Texture2D? texture, bool twoSided = true)
    {
        _shader ??= GD.Load<Shader>("res://src/Render/Overlay.gdshader");
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("tex", texture);
        m.SetShaderParameter("has_tex", texture != null);
        return m;
    }
}
