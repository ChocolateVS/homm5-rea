using Godot;

namespace H5Reborn.Render;

/// <summary>
/// Variants of the H5Lit shader (original lighting, see H5Lit.gdshaderinc). Godot fixes cull
/// and blend modes per shader, so each combination is a generated shader, cached.
/// </summary>
public static class H5LitMaterials
{
    public enum Blend { Opaque, AlphaTest, Alpha, Additive }

    static readonly Dictionary<(Blend, bool), Shader> _shaders = new();
    static string? _body;

    static Shader Get(Blend blend, bool twoSided)
    {
        if (_shaders.TryGetValue((blend, twoSided), out var s)) return s;
        _body ??= Godot.FileAccess.GetFileAsString("res://src/Render/H5Lit.gdshader");
        string mode = "specular_disabled, ambient_light_disabled, diffuse_lambert"
            + (twoSided ? ", cull_disabled" : ", cull_back")
            + blend switch
            {
                Blend.Alpha => ", blend_mix, depth_draw_never",
                Blend.Additive => ", blend_add, depth_draw_never, shadows_disabled",
                _ => "",
            };
        var code = _body.Replace("render_mode specular_disabled, ambient_light_disabled, diffuse_lambert;",
            $"render_mode {mode};" + (blend is Blend.Alpha or Blend.Additive ? "\n#define H5_BLEND" : ""));
        s = new Shader { Code = code };
        _shaders[(blend, twoSided)] = s;
        return s;
    }

    public static ShaderMaterial Create(Texture2D? texture, Blend blend, bool twoSided, bool selfIllum = false,
        bool receiveShadow = true, bool affectedByFog = true)
    {
        var m = new ShaderMaterial { Shader = Get(blend, twoSided) };
        if (texture != null) m.SetShaderParameter("tex", texture);
        m.SetShaderParameter("alpha_scissor", blend == Blend.AlphaTest ? 0.5f : -1f);
        m.SetShaderParameter("self_illum", selfIllum ? 1f : 0f);
        m.SetShaderParameter("receive_shadow", receiveShadow ? 1f : 0f);
        m.SetShaderParameter("use_fog", affectedByFog);
        return m;
    }

    /// <summary>Turns an H5Lit material into an unlit, fog-free, two-sided backdrop (sky domes).</summary>
    public static ShaderMaterial AsBackdrop(ShaderMaterial m, bool alpha)
    {
        var tex = m.GetShaderParameter("tex").As<Texture2D>();
        var b = Create(tex, alpha ? Blend.Alpha : Blend.Opaque, twoSided: true, selfIllum: true, receiveShadow: false, affectedByFog: false);
        return b;
    }

    public static bool IsH5Lit(Material? m) => m is ShaderMaterial sm && _shaders.ContainsValue(sm.Shader);
}
