// Per-pixel retarded rendering for parts at or above light speed ("Exact rays").
// GPU twin of src/History/RetardedRay.cs -- keep the two in step.
//
// Drawn on a full-screen quad. Each pixel follows its light ray back from the camera: at time t_s the ray is
// c (t_now - t_s) away. The first time that point lies inside the moving shape is the surface the pixel
// shows, so every image, fold and occlusion falls out directly, with no matching of vertices between images.
//
// To keep it cheap, the CPU hands over the few stretches of history in which light from the part can reach
// the camera at all (RetardedRay.Intervals), each with a sphere around where the part is during it. A pixel
// tests those spheres and only marches inside the one its ray enters; most pixels cost a few sphere tests.
shader_type spatial;
// blend_mix keeps this out of the opaque depth pre-pass: the pre-pass would run this march twice and keep only
// pixels whose depth matches exactly, and marching has hard thresholds, so a few pixels would drop out.
// depth_draw_always still writes DEPTH, so ray parts occlude each other and the rest of the scene correctly.
render_mode blend_mix, depth_draw_always, cull_disabled;

#include "res://shaders/lc_common.gdshaderinc"

uniform vec4 albedo : source_color = vec4(1.0);
uniform int shape_kind = 0; // 0 box, 1 sphere, 2 cylinder (axis local Y)
uniform vec3 shape_size = vec3(0.5); // box half extents | sphere radius in x | cylinder radius, half height
uniform float lin_bound = 0.0; // upper bound on the origin's speed, m/s
uniform float ang_bound = 0.0; // upper bound on the spin, rad/s
uniform float obj_radius = 1.0; // origin to farthest point of the shape

// Stretches of history where the part can be seen, newest first. RetardedRay.MaxIntervals.
uniform int iv_count = 0;
uniform vec4 iv_sphere[16]; // centre xyz, radius: where the part is during the stretch
uniform vec2 iv_k[16]; // newest, oldest fractional sample index (global, like the history texture)

const int MAX_MARCH = 512; // RetardedRay.MaxMarchIterations
const float SURFACE_EPS = 0.005; // RetardedRay.SurfaceEpsilon

float sdf(vec3 p) {
	if (shape_kind == 0) {
		vec3 q = abs(p) - shape_size;
		return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
	} else if (shape_kind == 1) {
		return length(p) - shape_size.x;
	}
	vec2 d = vec2(length(p.xz) - shape_size.x, abs(p.y) - shape_size.y);
	return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
}

vec3 sdf_normal(vec3 p) {
	const float e = 0.001;
	return normalize(vec3(
		sdf(p + vec3(e, 0.0, 0.0)) - sdf(p - vec3(e, 0.0, 0.0)),
		sdf(p + vec3(0.0, e, 0.0)) - sdf(p - vec3(0.0, e, 0.0)),
		sdf(p + vec3(0.0, 0.0, e)) - sdf(p - vec3(0.0, 0.0, e))));
}

// Distance function of the ray's point at fractional sample index kf, in the shape's frame then.
// The two samples read last are cached (ci, cp0...), because steps shorter than a sample are common near a
// surface and would otherwise fetch the same four texels again and again.
float h_at(float kf, vec3 cam, vec3 dir, out vec3 local, out vec4 q,
		inout int ci, inout vec3 cp0, inout vec3 cp1, inout vec4 cq0, inout vec4 cq1, inout float cflag) {
	int n = lc_count;
	kf = clamp(kf, 0.0, float(n - 1));
	int i = min(int(floor(kf)), n - 2);
	float a = kf - float(i);
	if (i != ci) {
		vec4 s0 = fetch(i, 0);
		vec4 s1 = fetch(i + 1, 0);
		cp0 = s0.xyz;
		cp1 = s1.xyz;
		cflag = s1.w;
		cq0 = fetch(i, 1);
		cq1 = fetch(i + 1, 1);
		ci = i;
	}
	local = vec3(0.0);
	q = vec4(0.0, 0.0, 0.0, 1.0);
	if (a > 0.0 && a < 1.0 && cflag > 0.5) {
		return 0.01; // a teleport between the samples: nothing here, but keep stepping
	}
	vec3 pos = mix(cp0, cp1, a);
	q = nlerp(cq0, cq1, a);
	vec3 point = cam + dir * (lc_c * ((float(n - 1) - kf) * DT + lc_age));
	local = rotate(vec4(-q.xyz, q.w), point - pos);
	return sdf(local);
}

// Marches back from sample index k_start to k_min for the first time the ray's point is inside the shape.
bool march(float k_start, float k_min, vec3 cam, vec3 dir, out float k_hit, out vec3 local, out vec4 q) {
	int ci = -1;
	vec3 cp0 = vec3(0.0);
	vec3 cp1 = vec3(0.0);
	vec4 cq0 = vec4(0.0);
	vec4 cq1 = vec4(0.0);
	float cflag = 0.0;

	float k = k_start;
	k_hit = k;
	float dist = h_at(k, cam, dir, local, q, ci, cp0, cp1, cq0, cq1, cflag);
	if (dist <= 0.0) {
		return true;
	}

	for (int it = 0; it < MAX_MARCH; it++) {
		if (k <= k_min) {
			break;
		}
		float speed = lc_c + lin_bound + ang_bound * (max(dist, 0.0) + obj_radius);
		float step = max(dist / (speed * DT), 0.1);
		float next = max(k_min, k - step);
		float next_dist = h_at(next, cam, dir, local, q, ci, cp0, cp1, cq0, cq1, cflag);
		if (next_dist <= SURFACE_EPS) {
			k_hit = next;
			if (next_dist <= 0.0) {
				// Crossed into the shape: bisect between outside (k) and inside (next).
				float outside = k;
				float inside = next;
				for (int j = 0; j < 12; j++) {
					float mid = 0.5 * (outside + inside);
					if (h_at(mid, cam, dir, local, q, ci, cp0, cp1, cq0, cq1, cflag) <= 0.0) {
						inside = mid;
					} else {
						outside = mid;
					}
				}
				k_hit = inside;
			}
			h_at(k_hit, cam, dir, local, q, ci, cp0, cp1, cq0, cq1, cflag);
			return true;
		}
		k = next;
		dist = next_dist;
	}
	return false;
}

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0); // cover the whole screen
}

void fragment() {
	vec3 cam = CAMERA_POSITION_WORLD;
	vec4 view_pos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, 0.5, 1.0);
	vec3 dir = normalize((INV_VIEW_MATRIX * vec4(normalize(view_pos.xyz / view_pos.w), 0.0)).xyz);

	int first;
	int last;
	bool alive;
	bool have;
	lifetime(first, last, alive, have);
	if (!have || iv_count == 0) {
		discard;
	}

	float newest = float(lc_count - 1);
	bool hit = false;
	float k_hit = 0.0;
	vec3 local = vec3(0.0);
	vec4 q = vec4(0.0, 0.0, 0.0, 1.0);

	// Newest stretch first: the first hit is the nearest along the ray, which is the one that is seen.
	for (int j = 0; j < iv_count; j++) {
		vec4 sph = iv_sphere[j];
		vec3 oc = cam - sph.xyz;
		float b = dot(oc, dir);
		float disc = b * b - (dot(oc, oc) - sph.w * sph.w);
		if (disc < 0.0) {
			continue;
		}
		float root = sqrt(disc);
		float s_near = max(0.0, -b - root);
		float s_far = -b + root;
		if (s_far <= 0.0) {
			continue;
		}

		// Light-travel distance s is delay c: the part of the stretch the ray spends inside the sphere.
		float k_start = min(min(iv_k[j].x, newest - (s_near / lc_c - lc_age) / DT), float(last));
		float k_min = max(max(iv_k[j].y, newest - (s_far / lc_c - lc_age) / DT), float(first));
		if (k_start <= k_min) {
			continue;
		}
		if (march(k_start, k_min, cam, dir, k_hit, local, q)) {
			hit = true;
			break;
		}
	}
	if (!hit) {
		discard;
	}

	vec3 point = cam + dir * (lc_c * ((newest - k_hit) * DT + lc_age));
	vec3 normal_world = rotate(q, sdf_normal(local));

	ALBEDO = albedo.rgb;
	ROUGHNESS = 0.6;
	SPECULAR = 0.0;
	ALPHA = 1.0;
	NORMAL = normalize((VIEW_MATRIX * vec4(normal_world, 0.0)).xyz);
	vec4 clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(point, 1.0);
	DEPTH = clip.z / clip.w;
}
