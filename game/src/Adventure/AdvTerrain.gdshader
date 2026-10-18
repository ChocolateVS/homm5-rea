shader_type spatial;
render_mode cull_back, specular_disabled, ambient_light_disabled, diffuse_lambert;

#include "res://src/Render/H5Lit.gdshaderinc"

// Adventure-map terrain. Same blending as the combat terrain (layer 0 is the base,
// every later layer is laid over the result with its per-vertex weight, in draw
// order) but with up to 32 layers, as adventure maps use 20+ tile types.
// Layers whose tile has the ReflectiveWater material are drawn as moving water.
// Texels are sampled raw (gamma space) and lit like the original renderer
// (per-vertex colours, shadow selection, fog: see H5Lit.gdshaderinc).
uniform sampler2DArray layers : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D weights0 : filter_linear, repeat_disable;
uniform sampler2D weights1 : filter_linear, repeat_disable;
uniform sampler2D weights2 : filter_linear, repeat_disable;
uniform sampler2D weights3 : filter_linear, repeat_disable;
uniform sampler2D weights4 : filter_linear, repeat_disable;
uniform sampler2D weights5 : filter_linear, repeat_disable;
uniform sampler2D weights6 : filter_linear, repeat_disable;
uniform sampler2D weights7 : filter_linear, repeat_disable;
uniform int layer_count = 1;
uniform float tiles_per_texture = 8.0;
uniform float layer_water[32];

varying vec3 v_lit;
varying vec3 v_shadow;
varying float v_fog;
varying vec3 v_tex;

vec4 weight_texel(int t, vec2 uv) {
	if (t == 0) return texture(weights0, uv);
	if (t == 1) return texture(weights1, uv);
	if (t == 2) return texture(weights2, uv);
	if (t == 3) return texture(weights3, uv);
	if (t == 4) return texture(weights4, uv);
	if (t == 5) return texture(weights5, uv);
	if (t == 6) return texture(weights6, uv);
	return texture(weights7, uv);
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
	vec3 col = texture(layers, vec3(uv, 0.0)).rgb;
	vec4 w4 = vec4(0.0);
	for (int i = 1; i < layer_count; i++) {
		int c = i % 4;
		if (c == 0 || i == 1) w4 = weight_texel(i / 4, UV2);
		float w = c == 0 ? w4.r : (c == 1 ? w4.g : (c == 2 ? w4.b : w4.a));
		if (w > 0.002) {
			vec2 luv = uv;
			if (layer_water[i] > 0.5) luv += vec2(TIME * 0.012, TIME * 0.007);
			col = mix(col, texture(layers, vec3(luv, float(i))).rgb, w);
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
