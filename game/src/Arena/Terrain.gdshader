shader_type spatial;
render_mode cull_back, specular_disabled, ambient_light_disabled, diffuse_lambert;

#include "res://src/Render/H5Lit.gdshaderinc"

// Original terrain: layer 0 is the base; every later layer is laid over the
// result with its per-vertex weight, in draw order. Texels are sampled raw (gamma
// space) and lit like the original renderer (see H5Lit.gdshaderinc).
uniform sampler2DArray layers : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D weights0 : filter_linear, repeat_disable;
uniform sampler2D weights1 : filter_linear, repeat_disable;
uniform sampler2D weights2 : filter_linear, repeat_disable;
uniform sampler2D weights3 : filter_linear, repeat_disable;
uniform int layer_count = 1;
uniform float tiles_per_texture = 8.0;

varying vec3 v_lit;
varying vec3 v_shadow;
varying float v_fog;
varying vec3 v_tex;

float weight_of(int i, vec4 w0, vec4 w1, vec4 w2, vec4 w3) {
	vec4 w = i < 4 ? w0 : (i < 8 ? w1 : (i < 12 ? w2 : w3));
	int c = i % 4;
	return c == 0 ? w.r : (c == 1 ? w.g : (c == 2 ? w.b : w.a));
}

void vertex() {
	vec3 wn = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
	vec3 lit_c, shadow_c;
	h5_vertex_colors(wn, lit_c, shadow_c);
	v_lit = lit_c;
	v_shadow = shadow_c;
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_fog = h5_fog_factor(-(VIEW_MATRIX * vec4(wp, 1.0)).z, wp.y);
}

void fragment() {
	vec2 uv = UV / tiles_per_texture;
	vec4 w0 = texture(weights0, UV2);
	vec4 w1 = texture(weights1, UV2);
	vec4 w2 = texture(weights2, UV2);
	vec4 w3 = texture(weights3, UV2);
	vec3 col = texture(layers, vec3(uv, 0.0)).rgb;
	for (int i = 1; i < layer_count; i++) {
		float w = weight_of(i, w0, w1, w2, w3);
		if (w > 0.002) {
			col = mix(col, texture(layers, vec3(uv, float(i))).rgb, w);
		}
	}
	v_tex = col;
	ALBEDO = vec3(1.0);
}

void light() {
	if (LIGHT_IS_DIRECTIONAL) {
		DIFFUSE_LIGHT += h5_srgb_to_linear(h5_finish(v_tex, mix(v_shadow, v_lit, ATTENUATION), v_fog));
	} else {
		float ndl = max(dot(NORMAL, LIGHT), 0.0);
		DIFFUSE_LIGHT += h5_srgb_to_linear(clamp(h5_light_scale * v_tex * LIGHT_COLOR * ndl * ATTENUATION / PI, 0.0, 1.0)) * v_fog;
	}
}
