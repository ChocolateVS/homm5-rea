shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, shadows_disabled;

// Fog of war ("shroud"): a full-screen pass that reconstructs the world position of
// every pixel from the depth buffer and blacks out map tiles the viewing player has
// not explored. One texel per tile with bilinear filtering gives the soft edge; the
// area outside the map is unexplored and stays black, as in the original.
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform sampler2D shroud : filter_linear, repeat_disable;
uniform vec2 map_size = vec2(72.0);

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	float depth = texture(depth_tex, SCREEN_UV).x;
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	view.xyz /= view.w;
	vec3 world = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;
	vec2 uv = vec2(world.x, -world.z) / map_size;
	float explored = 0.0;
	if (uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0)
		explored = texture(shroud, uv).r;
	ALBEDO = vec3(0.0);
	ALPHA = clamp(1.0 - explored, 0.0, 1.0);
}
