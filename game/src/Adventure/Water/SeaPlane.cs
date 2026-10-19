using Godot;
using H5.Adventure.Map;

namespace H5Reborn.Adventure.Water;

/// <summary>
/// The adventure-map sea: one water surface per floor drawn with the stock sea material
/// (/_(Material)/dev/ReflectiveWater: Textures/Terrain/Water/Water + ReflectiveWaterBump,
/// M_REFLECT_WATER, DielMirror 0.9, AM_TRANSPARENT, self-illuminated; H5_Game.exe FUN_00b6df40 loads it).
/// The terrain's plateau levels (chunk 7 ≈ height × 8) put the sea bed at 0 and a shore band at exactly
/// height 1.0 (level 8) below the first land level (≥ 1.6), so the surface sits at <see cref="Level"/> = 1.0:
/// land hides it, lowered ground shows water. TODO(exe): the plane height itself was not traced.
/// Shader is Compatibility-renderer safe (docs/WEB_PORT.md).
/// </summary>
public static class SeaPlane
{
    public const float Level = 1.0f;

    static Shader? _shader;

    /// <summary>True when the floor has sea (vertices at plateau level 0).</summary>
    public static bool HasSea(MapFloor floor) =>
        floor.Data?.VertexFlags is { } lv && lv.Data.Any(v => v == 0) && floor.Data.Heights.Data.Any(h => h < Level);

    public static MeshInstance3D? Build(MapFloor floor, float unit)
    {
        if (!HasSea(floor)) return null;
        float w = floor.Width * unit, d = floor.Height * unit;
        var mesh = new PlaneMesh { Size = new Vector2(w, d), SubdivideWidth = 0, SubdivideDepth = 0 };
        return new MeshInstance3D
        {
            Name = "Sea",
            Mesh = mesh,
            Position = new Vector3(w / 2, Level, -d / 2),
            MaterialOverride = Material(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    static ShaderMaterial Material()
    {
        _shader ??= new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_back, specular_disabled, depth_draw_never;
                #include "res://src/Render/H5Lit.gdshaderinc"
                uniform sampler2D water_tex : source_color, filter_linear_mipmap, repeat_enable;
                uniform sampler2D bump : hint_normal, filter_linear_mipmap, repeat_enable;
                uniform bool has_tex = false;
                uniform bool has_bump = false;
                uniform vec3 sky_color : source_color = vec3(0.55, 0.62, 0.70);
                uniform vec3 deep_color : source_color = vec3(0.06, 0.09, 0.12);
                uniform float mirror = 0.9;
                // Shallow water near the coast fades out with the depth of the ground below
                // (scene depth), hiding the flat plane's jagged cut through the per-tile terrain.
                uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
                uniform float shore_fade = 0.45;
                varying vec3 world_pos;
                varying float war;
                void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; war = h5_warfog_at(world_pos); }
                void fragment() {
                    vec2 uv = world_pos.xz * 0.04;
                    vec3 n = vec3(0.0, 1.0, 0.0);
                    if (has_bump) {
                        vec3 a = texture(bump, uv * 2.0 + vec2(0.02, 0.013) * TIME).xyz * 2.0 - 1.0;
                        vec3 b = texture(bump, uv * 3.4 - vec2(0.011, 0.017) * TIME).xyz * 2.0 - 1.0;
                        vec3 t = normalize(a + b);
                        n = normalize(vec3(t.x, 2.5, t.y));
                    }
                    vec4 base = has_tex ? texture(water_tex, uv + n.xz * 0.02) : vec4(deep_color, 0.85);
                    vec3 view = normalize(CAMERA_POSITION_WORLD - world_pos);
                    float fresnel = pow(1.0 - clamp(dot(view, n), 0.0, 1.0), 3.0);
                    vec3 r = reflect(-view, n);
                    vec3 refl = mix(deep_color * 2.0, sky_color, sqrt(clamp(r.y, 0.0, 1.0)));
                    ALBEDO = mix(base.rgb, refl, mirror * (0.35 + 0.65 * fresnel));
                    // Fog of war darkens the sea like the lit geometry (colour × w, in gamma space).
                    ALBEDO *= h5_srgb_to_linear(vec3(war));
                    float d = texture(depth_tex, SCREEN_UV).x;
                    #if CURRENT_RENDERER == RENDERER_COMPATIBILITY
                    d = d * 2.0 - 1.0; // OpenGL NDC depth is -1..1
                    #endif
                    vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, d, 1.0);
                    vec3 ground = (INV_VIEW_MATRIX * vec4(vp.xyz / vp.w, 1.0)).xyz;
                    float shore = clamp((world_pos.y - ground.y) / shore_fade, 0.0, 1.0);
                    ALPHA = clamp(max(base.a, 0.6) + fresnel * 0.4, 0.0, 1.0) * shore;
                }
                """,
        };
        var m = new ShaderMaterial { Shader = _shader };
        var tex = Game.Instance.LoadTexture("Textures/Terrain/Water/Water.xdb");
        var bump = Game.Instance.LoadTexture("Textures/Terrain/Water/ReflectiveWaterBump.xdb");
        m.SetShaderParameter("has_tex", tex != null);
        if (tex != null) m.SetShaderParameter("water_tex", tex);
        m.SetShaderParameter("has_bump", bump != null);
        if (bump != null) m.SetShaderParameter("bump", bump);
        m.SetShaderParameter("sky_color", Render.WaterMaterial.SkyColor);
        m.SetShaderParameter("deep_color", Render.WaterMaterial.DeepColor);
        return m;
    }
}
