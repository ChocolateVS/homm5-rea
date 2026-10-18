shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;

// Original AM_OVERLAY alpha mode: "overlay" blend of the texture over what is
// already on screen, weighted by the texture's alpha.
uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_tex = true;
uniform sampler2D screen : hint_screen_texture, filter_linear;

vec3 overlay(vec3 base, vec3 blend) {
	return mix(2.0 * base * blend, 1.0 - 2.0 * (1.0 - base) * (1.0 - blend), step(0.5, base));
}

void fragment() {
	vec4 t = has_tex ? texture(tex, UV) : vec4(0.5, 0.5, 0.5, 0.0);
	vec3 base = texture(screen, SCREEN_UV).rgb;
	ALBEDO = mix(base, overlay(base, t.rgb), t.a);
	ALPHA = 1.0;
}
