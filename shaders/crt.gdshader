shader_type canvas_item;
render_mode unshaded;

// Analogue FPV video feed: barrel distortion, chromatic fringing, scanlines, a phosphor mask,
// sensor noise, wobble, a slow interference band and a vignette. Purely cosmetic, so it may use TIME.

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;
uniform float strength : hint_range(0.0, 1.0) = 1.0;
uniform float curvature = 0.10;
uniform float aberration = 0.0022;
uniform float noise_amount = 0.07;

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

void fragment() {
	vec2 uv = SCREEN_UV;
	vec2 cc = uv * 2.0 - 1.0;
	float r2 = dot(cc, cc);

	// Barrel distortion, scaled so the middle of each edge still touches the border and the corners are cut.
	vec2 warped = cc * (1.0 + curvature * strength * r2) / (1.0 + curvature * strength);
	vec2 duv = warped * 0.5 + 0.5;

	// Horizontal jitter plus a faint line wobble.
	float band_pos = fract(TIME * 0.11);
	float band = smoothstep(0.06, 0.0, abs(duv.y - band_pos));
	duv.x += (sin(duv.y * 420.0 + TIME * 9.0) * 0.0006 + band * 0.004 * sin(TIME * 40.0)) * strength;

	vec3 col;
	vec2 fringe = warped * aberration * strength * (1.0 + 2.0 * r2);
	col.r = texture(screen_tex, duv + fringe).r;
	col.g = texture(screen_tex, duv).g;
	col.b = texture(screen_tex, duv - fringe).b;

	// Scanlines and RGB phosphor stripes, in screen pixels so they stay crisp at any resolution.
	float line = 0.5 + 0.5 * sin(FRAGCOORD.y * 3.14159265 * 0.66);
	col *= mix(1.0, 0.72 + 0.28 * line, strength);
	vec3 mask = vec3(1.0);
	int stripe = int(mod(floor(FRAGCOORD.x), 3.0));
	mask[stripe] = 1.12;
	col *= mix(vec3(1.0), mask * 0.94, 0.55 * strength);

	// Grain, interference band, contrast and a cool tint like a cheap camera.
	col += (hash(SCREEN_UV * vec2(900.0, 520.0) + fract(TIME) * 37.0) - 0.5) * noise_amount * strength;
	col += band * 0.05 * strength;
	col = (col - 0.5) * (1.0 + 0.12 * strength) + 0.5;
	col = mix(col, col * vec3(0.92, 1.0, 1.04), strength);

	float vignette = 1.0 - smoothstep(0.35, 1.5, r2) * 0.55 * strength;
	col *= vignette;

	// Outside the curved screen: black bezel with a soft edge.
	vec2 edge = smoothstep(vec2(0.0), vec2(0.004), duv) * smoothstep(vec2(0.0), vec2(0.004), 1.0 - duv);
	col *= edge.x * edge.y;

	COLOR = vec4(col, 1.0);
}
