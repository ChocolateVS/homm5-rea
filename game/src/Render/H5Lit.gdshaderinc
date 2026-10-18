// Original Heroes V lighting (H5_Game.exe), shared by model and terrain shaders.
//
// Per-vertex colours (MMX kernel FUN_00cf92c0, state FUN_00cf8950, light FUN_008fec90):
//   d = dot(N, L)  (L = direction towards the sun)
//   lit    = Ambient + Light    * max(d,0) + Shade * max(-d,0)
//   shadow = Ambient + Incident * max(d,0) + Shade * max(-d,0)
// each clamped to [0,1] (stored as vertex bytes). The pixel shader picks lit or shadow with
// the shadow map and outputs saturate(K * texture * colour) in gamma space (ps: mul_x4_sat
// with the colour pre-scaled in the vertex shader; K = h5_light_scale, see H5Lighting.cs).
// Fog (FUN_009add40): f = min((MaxDist - viewDepth) / (MaxDist - MinDist),
//                             (MaxZ - worldZ) / (MaxZ - MinZ)), clamped, colour = lerp(fog, c, f).
//
// Godot multiplies DIFFUSE_LIGHT by ALBEDO and converts linear -> sRGB on output, so shaders
// using this file set ALBEDO = 1 and write the finished gamma-space colour, converted to
// linear, into DIFFUSE_LIGHT from the directional light pass.

global uniform vec3 h5_sun_dir;       // world space, towards the sun
global uniform vec3 h5_light_color;
global uniform vec3 h5_ambient_color;
global uniform vec3 h5_shade_color;
global uniform vec3 h5_incident_color;
global uniform float h5_light_scale;
global uniform vec4 h5_fog_params;    // MinDist, MaxDist, MinZ, MaxZ (world units; height = world Y)
global uniform vec3 h5_fog_color;
global uniform float h5_fog_enabled;  // 0 = off, 1 = on

vec3 h5_srgb_to_linear(vec3 c) {
	c = clamp(c, 0.0, 1.0);
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void h5_vertex_colors(vec3 world_normal, out vec3 lit, out vec3 shadowed) {
	float d = dot(normalize(world_normal), normalize(h5_sun_dir));
	float pos = max(d, 0.0), neg = max(-d, 0.0);
	lit = clamp(h5_ambient_color + h5_light_color * pos + h5_shade_color * neg, 0.0, 1.0);
	shadowed = clamp(h5_ambient_color + h5_incident_color * pos + h5_shade_color * neg, 0.0, 1.0);
}

float h5_fog_factor(float view_depth, float world_height) {
	if (h5_fog_enabled < 0.5) return 1.0;
	float dd = h5_fog_params.y - h5_fog_params.x;
	float dz = h5_fog_params.w - h5_fog_params.z;
	float fd = abs(dd) > 1e-4 ? (h5_fog_params.y - view_depth) / dd : 1.0;
	float fz = abs(dz) > 1e-4 ? (h5_fog_params.w - world_height) / dz : 1.0;
	return clamp(min(fd, fz), 0.0, 1.0);
}

vec3 h5_finish(vec3 tex, vec3 vc, float fog) {
	vec3 c = clamp(h5_light_scale * tex * vc, 0.0, 1.0);
	return mix(h5_fog_color, c, fog);
}
