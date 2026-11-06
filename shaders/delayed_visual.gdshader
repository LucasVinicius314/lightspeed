// Shows each vertex where it was when the light now reaching the camera left it.
// GPU twin of src/History/RetardedSolver.cs -- keep the two in step.
shader_type spatial;
render_mode skip_vertex_transform;

global uniform sampler2D lc_history : filter_nearest;
global uniform int lc_head;
global uniform int lc_count;
global uniform float lc_c;
global uniform float lc_age; // seconds since the newest sample

uniform int object_index = 0;
uniform vec4 albedo : source_color = vec4(1.0);
uniform bool cpu_mode = false;

const float DT = 1.0 / 60.0;
const int MAX_STEPS = 14; // enough for 2^14 samples

int slot(int k) {
	int cap = textureSize(lc_history, 0).x / 2;
	return (lc_head - (lc_count - 1) + k + cap) % cap;
}

vec3 sample_pos(int k) {
	return texelFetch(lc_history, ivec2(2 * slot(k), object_index), 0).xyz;
}

vec4 sample_rot(int k) {
	return texelFetch(lc_history, ivec2(2 * slot(k) + 1, object_index), 0);
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
		int n = lc_count;
		vec3 cam = CAMERA_POSITION_WORLD;
		vec3 v = VERTEX;
		bool shown = false;
		vec3 p = vec3(0.0);
		vec4 q = vec4(0.0, 0.0, 0.0, 1.0);

		if (n > 0) {
			if (f_sample(n - 1, cam, v) <= 0.0) {
				// Closer than one tick of light travel: newest sample.
				p = sample_pos(n - 1);
				q = sample_rot(n - 1);
				shown = true;
			} else if (f_sample(0, cam, v) <= 0.0) {
				// Invariant: f(lo) <= 0 < f(hi).
				int lo = 0;
				int hi = n - 1;
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

		if (shown) {
			VERTEX = (VIEW_MATRIX * vec4(p + rotate(q, v), 1.0)).xyz;
			NORMAL = normalize((VIEW_MATRIX * vec4(rotate(q, NORMAL), 0.0)).xyz);
		} else {
			VERTEX = vec3(0.0); // light has not arrived yet: collapse to nothing
		}
	}
}

void fragment() {
	ALBEDO = albedo.rgb;
	ROUGHNESS = 0.6;
}
