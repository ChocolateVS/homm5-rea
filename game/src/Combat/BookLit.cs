using System.Globalization;
using System.Xml.Linq;
using Godot;
using H5Reborn.Render;

namespace H5Reborn.Combat;

/// <summary>
/// The 3D spellbook's materials lit by its own AmbientLight record (SpellBookDesc Light,
/// Lights/_(AmbientLight)/Interface/SpellBook.xdb) with the original renderer's formula (see H5Lit.gdshaderinc /
/// H5Lighting): per-vertex colour clamp(Ambient + Light·max(N·L,0) + Shade·max(−N·L,0)), output
/// saturate(scale·texel·colour) in gamma space, sun from Pitch/Yaw, scale from Whitening. The book is drawn in its own
/// viewport over the live battle / map, whose light owns the shared H5Lit globals, so the record's values are material
/// uniforms here.
/// </summary>
public static class BookLit
{
    public readonly record struct Light(Vector3 LightColor, Vector3 Ambient, Vector3 Shade, Vector3 SunDir, float Scale);

    const string Body = @"
shader_type spatial;
render_mode unshaded, specular_disabled, CULL, BLEND;
uniform sampler2D tex : filter_linear_mipmap, repeat_enable;   // raw (gamma-space) texels
uniform bool has_tex = true;
uniform float opacity = 1.0;
uniform float alpha_scissor = -1.0;
uniform bool self_illum = false;
uniform vec3 light_color;
uniform vec3 ambient_color;
uniform vec3 shade_color;
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform float light_scale = 2.0;
uniform vec3 emission = vec3(0.0);   // linear, added (hover / bookmark glow)
varying vec3 v_c;
vec3 to_linear(vec3 c) {
	c = clamp(c, 0.0, 1.0);
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}
void vertex() {
	float d = dot(normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz), normalize(sun_dir));
	v_c = clamp(ambient_color + light_color * max(d, 0.0) + shade_color * max(-d, 0.0), 0.0, 1.0);
}
void fragment() {
	vec4 t = has_tex ? texture(tex, UV) : vec4(1.0);
	if (alpha_scissor >= 0.0 && t.a < alpha_scissor) discard;
	vec3 c = self_illum ? t.rgb : clamp(light_scale * t.rgb * v_c, 0.0, 1.0);
	vec3 lin = to_linear(c) + emission;
	// The Compatibility renderer works in sRGB (OUTPUT_IS_SRGB) and writes ALBEDO as is.
	ALBEDO = OUTPUT_IS_SRGB ? mix(lin * 12.92, 1.055 * pow(max(lin, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), lin)) : lin;
	ALPHA_LINE
}
";

    static readonly Dictionary<(bool Blend, bool TwoSided), Shader> Shaders = new();

    static Shader Get(bool blend, bool twoSided)
    {
        if (Shaders.TryGetValue((blend, twoSided), out var s)) return s;
        var code = Body.Replace("CULL", twoSided ? "cull_disabled" : "cull_back")
            .Replace("BLEND", blend ? "blend_mix, depth_draw_opaque" : "blend_mix")
            .Replace("ALPHA_LINE", blend ? "ALPHA = t.a * opacity;" : "");
        return Shaders[(blend, twoSided)] = new Shader { Code = code };
    }

    /// <summary>The record's colours, sun (H5Lighting.SunDirection) and output factor (H5Lighting.ScaleFor).</summary>
    public static Light Load(XElement? l)
    {
        Vector3 V(string n, Vector3 def) => l?.Element(n) is { } e
            ? new Vector3(F(e.Element("x")), F(e.Element("y")), F(e.Element("z")))
            : def;
        float F(XElement? e) => e != null && float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        float P(string n, float def) => l?.Element(n) is { } e ? F(e) : def;
        return new Light(V("LightColor", new Vector3(0.35f, 0.35f, 0.35f)), V("AmbientColor", new Vector3(0.2f, 0.2f, 0.2f)),
            V("ShadeColor", Vector3.Zero), H5Lighting.SunDirection(P("Pitch", 45), P("Yaw", 45)), H5Lighting.ScaleFor(l));
    }

    public static ShaderMaterial Create(Light light, Texture2D? texture, bool blend, bool twoSided, float alphaScissor = -1f, bool selfIllum = false)
    {
        var m = new ShaderMaterial { Shader = Get(blend, twoSided) };
        m.SetShaderParameter("tex", texture);
        m.SetShaderParameter("has_tex", texture != null);
        m.SetShaderParameter("alpha_scissor", alphaScissor);
        m.SetShaderParameter("self_illum", selfIllum);
        m.SetShaderParameter("light_color", light.LightColor);
        m.SetShaderParameter("ambient_color", light.Ambient);
        m.SetShaderParameter("shade_color", light.Shade);
        m.SetShaderParameter("sun_dir", light.SunDir);
        m.SetShaderParameter("light_scale", light.Scale);
        return m;
    }

    /// <summary>The same surface as a book material: texture, alpha mode, culling and unshaded (L_NONE / L_SELFILLUM)
    /// taken from the Godot material ModelBuilder built for it.</summary>
    public static ShaderMaterial From(Light light, StandardMaterial3D s) => Create(light, s.AlbedoTexture,
        s.Transparency is BaseMaterial3D.TransparencyEnum.Alpha or BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass,
        s.CullMode == BaseMaterial3D.CullModeEnum.Disabled,
        s.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? s.AlphaScissorThreshold : -1f,
        s.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded);

    public static void SetTexture(ShaderMaterial m, Texture2D? tex)
    {
        m.SetShaderParameter("tex", tex);
        m.SetShaderParameter("has_tex", tex != null);
        m.SetShaderParameter("opacity", tex == null ? 0f : 1f);
    }

    public static Texture2D? Texture(ShaderMaterial m) => m.GetShaderParameter("tex").AsGodotObject() as Texture2D;

    /// <summary>Added glow, given like a Godot emission colour (sRGB) and energy; null clears it.</summary>
    public static void SetGlow(ShaderMaterial m, Color? glow, float energy = 1f)
    {
        var c = glow is { } g ? g.SrgbToLinear() * energy : Colors.Black;
        m.SetShaderParameter("emission", new Vector3(c.R, c.G, c.B));
    }
}
