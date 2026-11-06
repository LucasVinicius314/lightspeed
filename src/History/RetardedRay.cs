using System;
using System.Collections.Generic;
using System.Numerics;

namespace Lightspeed.History;

public enum ShapeKind
{
    Box = 0,
    Sphere = 1,
    /// <summary>Axis along local Y, like Godot's CylinderMesh.</summary>
    Cylinder = 2,
}

/// <summary>A primitive solid in an object's local frame, centred on its origin, with an exact distance function.</summary>
public readonly record struct Shape(ShapeKind Kind, Vector3 Size)
{
    public static Shape Box(Vector3 fullSize) => new(ShapeKind.Box, fullSize * 0.5f);
    public static Shape Sphere(float radius) => new(ShapeKind.Sphere, new Vector3(radius, 0, 0));
    public static Shape Cylinder(float radius, float height) => new(ShapeKind.Cylinder, new Vector3(radius, height * 0.5f, 0));

    /// <summary>Signed distance to the surface: negative inside.</summary>
    public float Distance(Vector3 p)
    {
        switch (Kind)
        {
            case ShapeKind.Box:
                var q = Vector3.Abs(p) - Size;
                return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
            case ShapeKind.Sphere:
                return p.Length() - Size.X;
            default:
                var d = new Vector2(new Vector2(p.X, p.Z).Length() - Size.X, MathF.Abs(p.Y) - Size.Y);
                return MathF.Min(MathF.Max(d.X, d.Y), 0f) + Vector2.Max(d, Vector2.Zero).Length();
        }
    }

    public Vector3 Normal(Vector3 p)
    {
        const float e = 1e-3f;
        return Vector3.Normalize(new Vector3(
            Distance(p + new Vector3(e, 0, 0)) - Distance(p - new Vector3(e, 0, 0)),
            Distance(p + new Vector3(0, e, 0)) - Distance(p - new Vector3(0, e, 0)),
            Distance(p + new Vector3(0, 0, e)) - Distance(p - new Vector3(0, 0, e))));
}
}

/// <param name="Delay">Seconds between the present and the moment the seen surface point left the object.</param>
public readonly record struct RayHit(bool Hit, Vector3 Point, Vector3 Normal, double Delay);

/// <summary>
/// A stretch of history (fractional sample indices, newest to oldest) during which light from the part can reach
/// the camera now, with a sphere around where the part is during it. A ray can only hit the part inside one.
/// </summary>
public readonly record struct RayInterval(double KNewest, double KOldest, Vector3 Center, float Radius);

/// <summary>
/// Per-pixel retarded rendering for fast parts. A pixel's light ray, followed back from the camera, is at
/// distance c (t_now - t_s) at time t_s. The first time that point lies inside the moving shape is the surface
/// the pixel shows: this finds every image, fold and occlusion directly, with no matching of vertices between
/// images. The march steps back through the history, skipping stretches where the point provably cannot reach
/// the shape (it can change by at most c + the shape's speed per second, plus its spin).
/// CPU twin of shaders/delayed_ray.gdshader; keep the two in step.
/// </summary>
public static class RetardedRay
{
    public const int MaxMarchIterations = 512;

    /// <summary>Most intervals handed to the shader; matches the uniform array sizes there.</summary>
    public const int MaxIntervals = 16;

    /// <summary>Close enough to the surface to call it a hit (5 mm). Matches SURFACE_EPS in the shader.</summary>
    public const float SurfaceEpsilon = 0.005f;

    /// <param name="linearBound">Upper bound on the shape origin's speed (m/s).</param>
    /// <param name="angularBound">Upper bound on its spin (rad/s).</param>
    /// <param name="objectRadius">Distance from the origin to the shape's farthest point.</param>
    public static RayHit Trace(
        ISampleSource h, double dt, Vector3 camera, Vector3 direction, float c, double age, Shape shape,
        double linearBound, double angularBound, double objectRadius, int maxIterations = MaxMarchIterations)
    {
        var none = new RayHit(false, default, default, 0);
        int n = h.Count;
        if (n < 2)
            return none;

        // Distance function of the ray's point at (fractional) sample index kf, in the shape's frame then.
        float Eval(double kf, out Vector3 local, out Quaternion rotation)
        {
            kf = Math.Clamp(kf, 0.0, n - 1);
            int i = Math.Min((int)Math.Floor(kf), n - 2);
            float a = (float)(kf - i);
            local = default;
            rotation = Quaternion.Identity;
            if (a > 0f && a < 1f && h.BreaksBefore(i + 1))
                return 0.01f; // a teleport between the samples: nothing here, but keep stepping

            var s0 = h[i];
            var s1 = h[i + 1];
            var position = Vector3.Lerp(s0.Position, s1.Position, a);
            rotation = Quaternion.Lerp(s0.Rotation, s1.Rotation, a);
            var point = camera + direction * (float)(c * ((n - 1 - kf) * dt + age));
            local = Vector3.Transform(point - position, Quaternion.Conjugate(rotation));
            return shape.Distance(local);
        }

        RayHit HitAt(double kf)
        {
            Eval(kf, out var local, out var rotation);
            var point = camera + direction * (float)(c * ((n - 1 - kf) * dt + age));
            var normal = Vector3.Transform(shape.Normal(local), rotation);
            return new RayHit(true, point, normal, (n - 1 - kf) * dt + age);
        }

        double k = n - 1;
        float dist = Eval(k, out _, out _);
        if (dist <= 0f)
            return HitAt(k);

        for (int it = 0; it < maxIterations && k > 0; it++)
        {
            double speed = c + linearBound + angularBound * (Math.Max(dist, 0f) + objectRadius);
            double step = Math.Max(dist / (speed * dt), 0.1);
            double next = Math.Max(0.0, k - step);
            float nextDist = Eval(next, out _, out _);

            if (nextDist <= SurfaceEpsilon)
            {
                if (nextDist > 0f)
                    return HitAt(next); // within tolerance of the surface, which also ends a slow crawl towards it

                // Crossed into the shape: bisect between outside (k) and inside (next).
                double outside = k, inside = next;
                for (int j = 0; j < 12; j++)
                {
                    double mid = (outside + inside) / 2;
                    if (Eval(mid, out _, out _) <= 0f) inside = mid; else outside = mid;
                }
                return HitAt(inside);
            }

            k = next;
            dist = nextDist;
        }
        return none;
    }

    /// <summary>
    /// The stretches of history where light from the part can reach the camera now. A surface point within
    /// <paramref name="objectRadius"/> of the origin can only be seen at a moment when the origin's own distance
    /// to the camera is within that radius of c times the delay (triangle inequality), so everywhere else the
    /// part is invisible and a ray need not march there. The origin's f changes by at most c plus its speed
    /// per second, so the scan skips ahead where |f| is far from that band and cannot miss one.
    /// Ordered newest first; when there are more than <paramref name="maxIntervals"/>, the last one is stretched
    /// to cover everything older, which is conservative.
    /// </summary>
    public static List<RayInterval> Intervals(
        ISampleSource h, double dt, Vector3 camera, float c, double age, double objectRadius, double linearBound,
        int maxIntervals = MaxIntervals)
    {
        var result = new List<RayInterval>();
        int n = h.Count;
        if (n < 2)
            return result;

        double slack = objectRadius + 0.02;
        double perSample = (c + linearBound) * dt; // largest change of f per sample

        double F(int k) => Vector3.Distance(camera, h[k].Position) - c * ((n - 1 - k) * dt + age);

        int activeFrom = -1; // newest index of the interval being built, or -1
        int k = n - 1;
        while (k >= 0)
        {
            double excess = Math.Abs(F(k)) - slack;
            if (excess <= 0)
            {
                if (activeFrom < 0)
                    activeFrom = k;
                k--;
                continue;
            }

            if (activeFrom >= 0)
            {
                result.Add(MakeInterval(h, activeFrom, k + 1));
                activeFrom = -1;
                if (result.Count == maxIntervals - 1)
                {
                    // Out of room: one last interval covering everything older that could be active.
                    result.Add(MakeInterval(h, k, 0));
                    return result;
                }
            }
            k -= (int)Math.Max(1, Math.Floor(excess / perSample)); // safe: |f| cannot reach the band sooner
        }
        if (activeFrom >= 0)
            result.Add(MakeInterval(h, activeFrom, 0));

        RayInterval MakeInterval(ISampleSource source, int newest, int oldest)
        {
            int hi = Math.Min(n - 1, newest + 1); // one sample of margin each side for the interpolation
            int lo = Math.Max(0, oldest - 1);
            var min = source[lo].Position;
            var max = min;
            for (int i = lo; i <= hi; i++)
            {
                min = Vector3.Min(min, source[i].Position);
                max = Vector3.Max(max, source[i].Position);
            }
            return new RayInterval(hi, lo, (min + max) * 0.5f, (max - min).Length() * 0.5f + (float)slack);
        }
        return result;
    }
}
