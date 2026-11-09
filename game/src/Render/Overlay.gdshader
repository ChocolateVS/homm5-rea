shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;

// Original AM_OVERLAY / AM_OVERLAY_ZWRITE alpha modes: an ordinary alpha blend (SRCALPHA, INVSRCALPHA).
// H5_Game.exe 00b83010 turns AlphaMode k into descriptor flag k << 4; 00898e10 makes AM_OVERLAY generic-material
// mode 1 (byte 0x10), AM_OVERLAY_ZWRITE mode 3 (byte 0) — the same mode AM_TRANSPARENT falls back to — and
// 00899720 queues modes 1..3 alike with the non-additive flags (0x140; AddPlaced gives 0x80, the SRCALPHA/ONE
// variant, cf. 00899350 blend 4 / 5 in the 009ad560 table). No "overlay" colour formula is involved.
uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has_tex = true;
// Alpha multiplier (the combat grid's per-kind fade, CombatSelectionDesc *FadeValue; 1 for ground decals).
uniform float fade = 1.0;

void fragment() {
	vec4 t = has_tex ? texture(tex, UV) : vec4(0.5, 0.5, 0.5, 0.0);
	ALBEDO = t.rgb;
	ALPHA = t.a * fade;
}
