using Godot;
using H5.Assets.Model;

namespace H5Reborn.Effects;

/// <summary>
/// Materials for effect meshes (glows, weapon trails). Effect textures are exported
/// premultiplied (CONVERT_TRANSPARENT) or with zero alpha for additive use
/// (CONVERT_TRANSPARENT_ADD), so AM_TRANSPARENT maps to a premultiplied blend that
/// covers both; L_SELFILLUM is unlit.
/// </summary>
public static class EffectMaterials
{
    static Shader? _premul, _premulLit;

    static Shader Premul(bool lit) => lit
        ? _premulLit ??= Make(lit: true)
        : _premul ??= Make(lit: false);

    static Shader Make(bool lit) => new()
    {
        Code = $$"""
            shader_type spatial;
            render_mode {{(lit ? "" : "unshaded, ")}}blend_premul_alpha, cull_back, depth_draw_never, shadows_disabled;
            uniform sampler2D tex : source_color, filter_linear_mipmap;
            uniform bool two_sided = false;
            // Animated object opacity (Granny "Transparency" tracks); premultiplied, so it scales colour too.
            uniform float opacity = 1.0;
            void fragment() {
                vec4 t = texture(tex, UV);
                ALBEDO = t.rgb * opacity;
                ALPHA = t.a * opacity;
            }
            """,
    };

    /// <summary>Per model material index: an override material, or null to keep ModelBuilder's.</summary>
    public static Material?[] ForModel(ModelAsset asset)
    {
        var result = new Material?[asset.Materials.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var m = asset.Materials[i];
            var tex = m.DiffuseTexture != null ? Game.Instance.LoadTexture(m.DiffuseTexture) : null;
            switch (m.AlphaMode)
            {
                case "AM_TRANSPARENT":
                {
                    var mat = new ShaderMaterial { Shader = Premul(lit: m.LightingMode != "L_SELFILLUM") };
                    var premul = m.DiffuseTexture != null ? EffectAssets.LoadPremultipliedTexture(m.DiffuseTexture) : null;
                    if (premul != null) mat.SetShaderParameter("tex", premul);
                    if (m.TwoSided) mat.Shader = TwoSided(mat.Shader);
                    result[i] = mat;
                    break;
                }
                case "AM_OVERLAY":
                case "AM_OVERLAY_ZWRITE":
                    result[i] = Render.OverlayMaterial.Create(tex, m.TwoSided);
                    break;
                default:
                    if (m.LightingMode == "L_SELFILLUM")
                    {
                        result[i] = new StandardMaterial3D
                        {
                            AlbedoTexture = tex,
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                            Transparency = m.AlphaMode == "AM_ALPHA_TEST" ? BaseMaterial3D.TransparencyEnum.AlphaScissor : BaseMaterial3D.TransparencyEnum.Disabled,
                            CullMode = m.TwoSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
                        };
                    }
                    break;
            }
        }
        return result;
    }

    static readonly Dictionary<Shader, Shader> _twoSided = new();
    static Shader TwoSided(Shader s)
    {
        if (_twoSided.TryGetValue(s, out var t)) return t;
        t = new Shader { Code = s.Code.Replace("cull_back", "cull_disabled") };
        _twoSided[s] = t;
        return t;
    }

    /// <summary>
    /// Applies per-material overrides to a mesh built by ModelBuilder.BuildMesh, whose
    /// surfaces follow the model's parts in order, skipping parts without geometry.
    /// </summary>
    public static void Apply(MeshInstance3D mesh, ModelAsset asset, Material?[] materials)
    {
        int surface = 0;
        foreach (var part in asset.Mesh.Parts)
        {
            if (part.Positions.Length == 0 || part.Indices.Length == 0) continue;
            if (part.MaterialIndex >= 0 && part.MaterialIndex < materials.Length && materials[part.MaterialIndex] is { } m)
                mesh.SetSurfaceOverrideMaterial(surface, m);
            surface++;
        }
    }
}
