using Godot;

namespace H5Reborn.Render;

/// <summary>
/// M_REFLECT_WATER (town lakes and rivers, e.g. Arenas/Town/Rampart/Lake.xdb): a texture-less,
/// self-illuminated mirror whose normal is perturbed by a scrolling bump map
/// (/Textures/Terrain/Water/ReflectiveWaterBump). DielMirror = 1 means the surface shows the
/// reflected environment. The original reflects the scene into a render target; here the sky
/// and horizon colours of the scene stand in for the reflection (Compatibility-renderer safe),
/// which keeps the look (dark, sky-tinted, rippling) without a second camera.
/// </summary>
public static class WaterMaterial
{
    static Shader? _shader;

    public static Color SkyColor = new(0.55f, 0.62f, 0.70f);
    public static Color DeepColor = new(0.06f, 0.09f, 0.12f);

    public static Material Create(Texture2D? bump)
    {
        _shader ??= new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_back, specular_disabled;
                #include "res://src/Render/H5Lit.gdshaderinc"
                uniform sampler2D bump : hint_normal, filter_linear_mipmap, repeat_enable;
                uniform bool has_bump = false;
                uniform vec3 sky_color : source_color = vec3(0.55, 0.62, 0.70);
                uniform vec3 deep_color : source_color = vec3(0.06, 0.09, 0.12);
                varying vec3 world_pos;
                varying float war;
                void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; war = h5_warfog_at(world_pos); }
                void fragment() {
                    vec3 n = vec3(0.0, 1.0, 0.0);
                    if (has_bump) {
                        vec2 uv = world_pos.xz * 0.08;
                        vec3 a = texture(bump, uv + vec2(0.02, 0.013) * TIME).xyz * 2.0 - 1.0;
                        vec3 b = texture(bump, uv * 1.7 - vec2(0.011, 0.017) * TIME).xyz * 2.0 - 1.0;
                        vec3 t = normalize(a + b);
                        n = normalize(vec3(t.x, 2.5, t.y));
                    }
                    vec3 view = normalize(CAMERA_POSITION_WORLD - world_pos);
                    float fresnel = pow(1.0 - clamp(dot(view, n), 0.0, 1.0), 3.0);
                    vec3 r = reflect(-view, n);
                    float sky = clamp(r.y, 0.0, 1.0);
                    vec3 refl = mix(deep_color * 2.0, sky_color, sqrt(sky));
                    ALBEDO = mix(deep_color, refl, 0.35 + 0.65 * fresnel);
                    ALBEDO *= h5_srgb_to_linear(vec3(war)); // adventure fog of war (1 elsewhere)
                }
                """,
        };
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("has_bump", bump != null);
        if (bump != null) m.SetShaderParameter("bump", bump);
        m.SetShaderParameter("sky_color", SkyColor);
        m.SetShaderParameter("deep_color", DeepColor);
        return m;
    }
}
