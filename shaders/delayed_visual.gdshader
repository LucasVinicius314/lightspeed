// Shows each vertex where it was when the light now reaching the camera left it.
// GPU twin of src/History/RetardedSolver.cs -- keep the two in step.
//
// A point slower than light has exactly one such moment (found by binary search). A point at or above
// light speed can have several, each a separate image of it; for those, DelayedVisual can draw the mesh once
// per image (image_index) and find_image scans back through the history for that image's moment ("Light
// Echoes"), or hide this draw (suppress) when delayed_ray.gdshader renders the part per pixel instead.
shader_type spatial;
render_mode skip_vertex_transform, cull_disabled;

#include "res://shaders/lc_common.gdshaderinc"

uniform vec4 albedo : source_color = vec4(1.0);
uniform bool cpu_mode = false;
uniform bool suppress = false; // this draw is replaced by the per-pixel renderer
uniform int image_index = 0; // which image of the vertex this draw shows (0 = latest)
uniform float speed_bound = 0.0; // upper bound on any vertex's speed, m/s

varying float img_valid; // 0 when this vertex has no such image: its triangles are discarded

const int MAX_STEPS = 14; // enough for 2^14 samples
const int MAX_SCAN = 160; // RetardedSolver.MaxScanIterations

// f(t_s) at sample k: distance to the camera minus how far light travelled since then.
float f_sample(int k, vec3 cam, vec3 v) {
	return distance(cam, sample_pos(k) + rotate(sample_rot(k), v)) - lc_c * (float(lc_count - 1 - k) * DT + lc_age);
}

// f(t_s) a fraction a of the way from sample lo to lo + 1.
float f_at(int lo, float a, vec3 cam, vec3 v) {
	vec3 p;
	vec4 q;
	pose(lo, a, p, q);
	return distance(cam, p + rotate(q, v)) - lc_c * ((float(lc_count - 1 - lo) - a) * DT + lc_age);
}

// Pose at the crossing between samples lo and lo + 1 (f0, f1 have opposite signs): one secant refinement.
void refine(int lo, float f0, float f1, vec3 cam, vec3 v, out vec3 p, out vec4 q) {
	float a = abs(f1 - f0) > 1e-9 ? clamp(-f0 / (f1 - f0), 0.0, 1.0) : 0.5;
	float fa = f_at(lo, a, cam, v);
	float a_lo = 0.0;
	float f_lo = f0;
	float a_hi = 1.0;
	float f_hi = f1;
	if ((fa <= 0.0) == (f0 <= 0.0)) {
		a_lo = a;
		f_lo = fa;
	} else {
		a_hi = a;
		f_hi = fa;
	}
	if (abs(f_hi - f_lo) > 1e-6) {
		a = clamp(a_lo - f_lo * (a_hi - a_lo) / (f_hi - f_lo), 0.0, 1.0);
	}
	pose(lo, a, p, q);
}

// The `want`-th (0 = latest) moment whose light reaches the camera now, scanning back from `last`.
// speed_bound + c bounds how fast f can change, so stretches where |f| is too large to reach zero are skipped.
bool find_image(int want, int first, int last, bool alive, vec3 cam, vec3 v, out vec3 p, out vec4 q) {
	float per_sample = (speed_bound + lc_c) * DT;
	int hi = last;
	float g_hi = f_sample(hi, cam, v);
	int count = 0;

	// Alive and the newest sample's light is already here: that sample is the latest image.
	if (alive && g_hi <= 0.0) {
		if (want == 0) {
			p = sample_pos(hi);
			q = sample_rot(hi);
			return true;
		}
		count = 1;
	}

	for (int it = 0; it < MAX_SCAN; it++) {
		if (hi <= first) {
			break;
		}
		int step = max(1, int(abs(g_hi) / per_sample));
		int lo = max(first, hi - step);
		float g_lo = f_sample(lo, cam, v);

		if ((g_lo <= 0.0) == (g_hi <= 0.0)) {
			hi = lo;
			g_hi = g_lo;
			continue;
		}

		// A crossing lies in [lo, hi]: narrow it to two neighbouring samples.
		int a = lo;
		int b = hi;
		bool negative_at_a = g_lo <= 0.0;
		for (int j = 0; j < MAX_STEPS; j++) {
			if (b - a <= 1) {
				break;
			}
			int mid = (a + b) / 2;
			if ((f_sample(mid, cam, v) <= 0.0) == negative_at_a) {
				a = mid;
			} else {
				b = mid;
			}
		}

		float f_a = f_sample(a, cam, v);
		// A teleport between them is a gap, not an image.
		if (fetch(b, 0).w < 0.5) {
			if (count == want) {
				refine(a, f_a, f_sample(b, cam, v), cam, v, p, q);
				return true;
			}
			count++;
		}
		hi = a;
		g_hi = f_a;
	}
	return false;
}

void vertex() {
	if (cpu_mode) {
		img_valid = 1.0;
		VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		NORMAL = normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);
	} else if (suppress) {
		img_valid = 0.0;
		VERTEX = vec3(0.0);
	} else {
		vec3 cam = CAMERA_POSITION_WORLD;
		vec3 v = VERTEX;
		bool shown = false;
		vec3 p = vec3(0.0);
		vec4 q = vec4(0.0, 0.0, 0.0, 1.0);

		int first;
		int last;
		bool alive;
		bool have;
		lifetime(first, last, alive, have);
		int n = lc_count;

		if (have) {
			if (speed_bound >= lc_c) {
				// Fast enough to have several images: find this draw's one.
				shown = find_image(image_index, first, last, alive, cam, v, p, q);
			} else if (image_index == 0 && f_sample(first, cam, v) <= 0.0) {
				// Slower than light: exactly one image. The first sample's light must have arrived.
				if (f_sample(last, cam, v) <= 0.0) {
					// Newest sample's light already here: show it if the object is alive (closer than one
					// tick of light travel); a despawned object's last light has gone past.
					if (alive) {
						p = sample_pos(last);
						q = sample_rot(last);
						shown = true;
					}
				} else {
					// Invariant: f(lo) <= 0 < f(hi).
					int lo = first;
					int hi = last;
					for (int i = 0; i < MAX_STEPS; i++) {
						if (hi - lo <= 1) {
							break;
						}
						int mid = (lo + hi) / 2;
						if (f_sample(mid, cam, v) <= 0.0) {
							lo = mid;
						} else {
							hi = mid;
						}
					}
					// A teleport at the crossing leaves a gap: nothing visible.
					if (fetch(hi, 0).w < 0.5) {
						refine(lo, f_sample(lo, cam, v), f_sample(hi, cam, v), cam, v, p, q);
						shown = true;
					}
				}
			}
			if (!shown) {
				// No such image: park the vertex at the newest pose so its triangles stay small; the
				// fragment stage discards them.
				p = sample_pos(last);
				q = sample_rot(last);
			}
			VERTEX = (VIEW_MATRIX * vec4(p + rotate(q, v), 1.0)).xyz;
			NORMAL = normalize((VIEW_MATRIX * vec4(rotate(q, NORMAL), 0.0)).xyz);
		} else {
			VERTEX = vec3(0.0); // nothing recorded yet: collapse to nothing
		}
		img_valid = shown ? 1.0 : 0.0;
	}
}

void fragment() {
	if (img_valid < 0.999) {
		discard;
	}
	ALBEDO = albedo.rgb;
	ROUGHNESS = 0.6;
}
