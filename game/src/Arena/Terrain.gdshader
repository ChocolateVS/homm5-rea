shader_type spatial;
render_mode cull_back, specular_disabled;

// Original terrain: layer 0 is the base; every later layer is laid over the
// result with its per-vertex weight, in draw order.
uniform sampler2DArray layers : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D weights0 : filter_linear, repeat_disable;
uniform sampler2D weights1 : filter_linear, repeat_disable;
uniform sampler2D weights2 : filter_linear, repeat_disable;
uniform sampler2D weights3 : filter_linear, repeat_disable;
uniform int layer_count = 1;
uniform float tiles_per_texture = 8.0;

float weight_of(int i, vec4 w0, vec4 w1, vec4 w2, vec4 w3) {
	vec4 w = i < 4 ? w0 : (i < 8 ? w1 : (i < 12 ? w2 : w3));
	int c = i % 4;
	return c == 0 ? w.r : (c == 1 ? w.g : (c == 2 ? w.b : w.a));
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
	ALBEDO = col;
	ROUGHNESS = 1.0;
}
