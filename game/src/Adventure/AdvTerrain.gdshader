shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

// Adventure-map terrain. Same blending as the combat terrain (layer 0 is the base,
// every later layer is laid over the result with its per-vertex weight, in draw
// order) but with up to 32 layers, as adventure maps use 20+ tile types.
// Layers whose tile has the ReflectiveWater material are drawn as moving water.
uniform sampler2DArray layers : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
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

void fragment() {
	vec2 uv = UV / tiles_per_texture;
	vec3 col = texture(layers, vec3(uv, 0.0)).rgb;
	float wet = layer_water[0];
	vec4 w4 = vec4(0.0);
	for (int i = 1; i < layer_count; i++) {
		int c = i % 4;
		if (c == 0 || i == 1) w4 = weight_texel(i / 4, UV2);
		float w = c == 0 ? w4.r : (c == 1 ? w4.g : (c == 2 ? w4.b : w4.a));
		if (w > 0.002) {
			vec2 luv = uv;
			if (layer_water[i] > 0.5) luv += vec2(TIME * 0.012, TIME * 0.007);
			col = mix(col, texture(layers, vec3(luv, float(i))).rgb, w);
			wet = mix(wet, layer_water[i], w);
		}
	}
	ALBEDO = col;
	ROUGHNESS = mix(1.0, 0.15, wet);
	SPECULAR = mix(0.0, 0.6, wet);
}
