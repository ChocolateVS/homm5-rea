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
// Fog of war (adventure map, FUN_005b1260 / FUN_00cf9420 / vs constants c30 in FUN_00896430):
// w = A*t + B*(1-t), sampled per vertex from the corner grids (WarFogGrid), multiplies the vertex
// colour, and the fog factor becomes clamp(f + 1 - w): unexplored ground is black, not fogged.
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
global uniform sampler2D h5_warfog_a;  // new grid (R8, one texel per tile corner)
global uniform sampler2D h5_warfog_b;  // previous grid
global uniform vec4 h5_warfog;         // x = t (A weight), y = enabled, z/w = texel index masks (nextPow2 - 1)

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

// Bilinear sample of a fog grid at corner coordinates, indices masked like the exe's fixed-point
// sampler (FUN_00cf9420: index & (W - 2), W = nextPow2 + 1), so the neighbour texel always exists.
float h5_warfog_grid(sampler2D g, vec2 c) {
	ivec2 i = ivec2(floor(c));
	vec2 f = c - floor(c);
	ivec2 m = ivec2(h5_warfog.zw);
	ivec2 i0 = i & m;
	float a = texelFetch(g, i0, 0).r, b = texelFetch(g, i0 + ivec2(1, 0), 0).r;
	float d = texelFetch(g, i0 + ivec2(0, 1), 0).r, e = texelFetch(g, i0 + ivec2(1, 1), 0).r;
	return mix(mix(a, b, f.x), mix(d, e, f.x), f.y);
}

// Fog-of-war brightness at a world position (1 = explored / no fog of war).
float h5_warfog_at(vec3 world_pos) {
	if (h5_warfog.y < 0.5) return 1.0;
	vec2 c = vec2(world_pos.x, -world_pos.z) * 0.5;   // 2 world units per tile; map +y is world -Z
	return h5_warfog_grid(h5_warfog_a, c) * h5_warfog.x + h5_warfog_grid(h5_warfog_b, c) * (1.0 - h5_warfog.x);
}

// Vertex colour times w; fog factor 1 + max(f, 0) - w (clamped by the hardware).
vec3 h5_finish(vec3 tex, vec3 vc, float fog, float war) {
	vec3 c = clamp(h5_light_scale * tex * vc * war, 0.0, 1.0);
	return mix(h5_fog_color, c, clamp(fog + 1.0 - war, 0.0, 1.0));
}
