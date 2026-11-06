// History lookup shared by delayed_visual.gdshader (per vertex) and delayed_ray.gdshader (per pixel).
// Texture layout is documented on src/History/HistoryTexture.cs.

global uniform sampler2DArray lc_history : filter_nearest;
global uniform sampler2D lc_meta : filter_nearest;
global uniform int lc_head;
global uniform int lc_count;
global uniform int lc_tick;
global uniform float lc_c;
global uniform float lc_age; // seconds since the newest sample

uniform int object_index = 0;

const float DT = 1.0 / 60.0;
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

// This object's lifetime inside the shared sample window: first and last valid logical sample.
void lifetime(out int first, out int last, out bool alive, out bool have) {
	vec4 meta = texelFetch(lc_meta, ivec2(0, object_index), 0);
	int n = lc_count;
	alive = meta.y < 0.0;
	first = max(0, n - 1 - (lc_tick - int(meta.x) + TICK_WRAP) % TICK_WRAP);
	last = alive ? n - 1 : n - 1 - (lc_tick - int(meta.y) + TICK_WRAP) % TICK_WRAP;
	have = n > 1 && meta.z > 0.5 && last >= first;
}
