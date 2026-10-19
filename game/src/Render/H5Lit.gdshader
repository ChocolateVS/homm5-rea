// Opaque / alpha-tested original material lit like H5_Game.exe (see H5Lit.gdshaderinc).
shader_type spatial;
render_mode specular_disabled, ambient_light_disabled, diffuse_lambert;

#include "res://src/Render/H5Lit.gdshaderinc"

uniform sampler2D tex : filter_linear_mipmap, repeat_enable;   // raw (gamma-space) texels
uniform float alpha_scissor = -1.0;   // < 0: opaque
uniform float receive_shadow = 1.0;   // material ReceiveShadow
uniform float self_illum = 0.0;       // L_SELFILLUM: texture shown as is
uniform float opacity = 1.0;
uniform bool use_fog = true;          // material AffectedByFog

varying vec3 v_lit;
varying vec3 v_shadow;
varying float v_fog;
varying float v_war;
varying vec3 v_tex;

void vertex() {
	vec3 wn = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
	vec3 lit_c, shadow_c;
	h5_vertex_colors(wn, lit_c, shadow_c);
	v_lit = lit_c;
	v_shadow = shadow_c;
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float depth = -(VIEW_MATRIX * vec4(wp, 1.0)).z;
	v_fog = use_fog ? h5_fog_factor(depth, wp.y) : 1.0;
	v_war = h5_warfog_at(wp);
}

void fragment() {
	vec4 t = texture(tex, UV);
	if (alpha_scissor >= 0.0 && t.a < alpha_scissor) discard;
	v_tex = t.rgb;
	ALBEDO = vec3(1.0);
	if (self_illum > 0.5) {
		// Unlit: no light pass contributes, so emit the texel directly.
		EMISSION = h5_srgb_to_linear(mix(h5_fog_color, t.rgb * v_war, clamp(v_fog + 1.0 - v_war, 0.0, 1.0)));
		ALBEDO = vec3(0.0);
	}
#ifdef H5_BLEND
	// Only blended variants write ALPHA: any ALPHA write moves a Godot material into the
	// transparent pass (no depth write, drawn last), which made opaque scenery cover objects.
	ALPHA = t.a * opacity;
#endif
}

void light() {
	if (LIGHT_IS_DIRECTIONAL) {
		float s = receive_shadow > 0.5 ? ATTENUATION : 1.0;
		vec3 vc = mix(v_shadow, v_lit, s);
		DIFFUSE_LIGHT += h5_srgb_to_linear(h5_finish(v_tex, vc, v_fog, v_war));
	} else {
		// Dynamic point lights add to the vertex colour in the original; approximate per pixel.
		float ndl = max(dot(NORMAL, LIGHT), 0.0);
		DIFFUSE_LIGHT += h5_srgb_to_linear(clamp(h5_light_scale * v_tex * LIGHT_COLOR * ndl * ATTENUATION / PI, 0.0, 1.0)) * v_fog * v_war;
	}
}
