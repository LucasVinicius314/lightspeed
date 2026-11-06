// Shows each vertex where it was when the light now reaching the camera left it.
// GPU twin of src/History/RetardedSolver.cs -- keep the two in step.
// Texture layout is documented on HistoryTexture.cs.
shader_type spatial;
render_mode skip_vertex_transform;

global uniform sampler2DArray lc_history : filter_nearest;
global uniform sampler2D lc_meta : filter_nearest;
global uniform int lc_head;
global uniform int lc_count;
global uniform int lc_tick;
global uniform float lc_c;
global uniform float lc_age; // seconds since the newest sample

uniform int object_index = 0;
uniform vec4 albedo : source_color = vec4(1.0);
uniform bool cpu_mode = false;

const float DT = 1.0 / 60.0;
const int MAX_STEPS = 14; // enough for 2^14 samples
const int CHUNK = 16; // time slots per layer; HistoryTexture.Chunk
const int TICK_WRAP = 1048576; // HistoryTexture.TickWrap

// Texel `part` (0: position + teleport flag, 1: rotation) of logical sample k (0 = oldest in the window).
vec4 fetch(int k, int part) {
	int cap = textureSize(lc_history, 0).z * CHUNK;
	int s = (lc_head - (lc_count - 1) + k + cap) % cap;
	return texelFetch(lc_history, ivec3((s % CHUNK) * 2 + part, object_index, s / CHUNK), 0);
}

vec3 sample_pos(int k) {
	return fetch(k, 0).xyz;
}

vec4 sample_rot(int k) {
	return fetch(k, 1);
}

vec3 rotate(vec4 q, vec3 v) {
	vec3 t = 2.0 * cross(q.xyz, v);
	return v + q.w * t + cross(q.xyz, t);
}

vec4 nlerp(vec4 a, vec4 b, float t) {
	if (dot(a, b) < 0.0) {
		b = -b;
	}
	return normalize(mix(a, b, t));
}

void pose(int lo, float a, out vec3 p, out vec4 q) {
	p = mix(sample_pos(lo), sample_pos(lo + 1), a);
	q = nlerp(sample_rot(lo), sample_rot(lo + 1), a);
}

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

void vertex() {
	if (cpu_mode) {
		VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		NORMAL = normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);
	} else {
		vec3 cam = CAMERA_POSITION_WORLD;
		vec3 v = VERTEX;
		bool shown = false;
		vec3 p = vec3(0.0);
		vec4 q = vec4(0.0, 0.0, 0.0, 1.0);

		// This object's lifetime inside the shared sample window.
		vec4 meta = texelFetch(lc_meta, ivec2(0, object_index), 0);
		int n = lc_count;
		bool alive = meta.y < 0.0;
		int first = max(0, n - 1 - (lc_tick - int(meta.x) + TICK_WRAP) % TICK_WRAP);
		int last = alive ? n - 1 : n - 1 - (lc_tick - int(meta.y) + TICK_WRAP) % TICK_WRAP;

		// Light from the first sample must have arrived (existence window).
		if (n > 0 && meta.z > 0.5 && last >= first && f_sample(first, cam, v) <= 0.0) {
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
					float f0 = f_sample(lo, cam, v);
					float f1 = f_sample(hi, cam, v);
					float a = clamp(-f0 / (f1 - f0), 0.0, 1.0);

					// One secant refinement.
					float fa = f_at(lo, a, cam, v);
					float a_lo = 0.0;
					float f_lo = f0;
					float a_hi = 1.0;
					float f_hi = f1;
					if (fa > 0.0) {
						a_hi = a;
						f_hi = fa;
					} else {
						a_lo = a;
						f_lo = fa;
					}
					if (abs(f_hi - f_lo) > 1e-6) {
						a = clamp(a_lo - f_lo * (a_hi - a_lo) / (f_hi - f_lo), 0.0, 1.0);
					}
					pose(lo, a, p, q);
					shown = true;
				}
			}
		}

		if (shown) {
			VERTEX = (VIEW_MATRIX * vec4(p + rotate(q, v), 1.0)).xyz;
			NORMAL = normalize((VIEW_MATRIX * vec4(rotate(q, NORMAL), 0.0)).xyz);
		} else {
			VERTEX = vec3(0.0); // nothing to see here: collapse to nothing
		}
	}
}

void fragment() {
	ALBEDO = albedo.rgb;
	ROUGHNESS = 0.6;
}
