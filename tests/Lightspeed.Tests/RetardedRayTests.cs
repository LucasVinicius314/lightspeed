using System.Numerics;
using Lightspeed.History;

namespace Lightspeed.Tests;

public class RetardedRayTests
{
    private const double Dt = 1.0 / 60.0;

    private sealed class Samples(Sample[] data) : ISampleSource
    {
        public int Count => data.Length;
        public Sample this[int k] => data[k];
    }

    private static Samples Record(int n, Func<double, Sample> pose) =>
        new(Enumerable.Range(0, n).Select(k => pose(k * Dt)).ToArray());

    [Fact]
    public void Shape_distances_are_exact()
    {
        Assert.Equal(1f, Shape.Box(new Vector3(2, 2, 2)).Distance(new Vector3(2, 0, 0)), 1e-5);
        Assert.Equal(-1f, Shape.Box(new Vector3(2, 2, 2)).Distance(Vector3.Zero), 1e-5);
        Assert.Equal(MathF.Sqrt(2), Shape.Box(new Vector3(2, 2, 2)).Distance(new Vector3(2, 2, 0)), 1e-5);
        Assert.Equal(1.5f, Shape.Sphere(0.5f).Distance(new Vector3(0, 2, 0)), 1e-5);
        Assert.Equal(1f, Shape.Cylinder(0.5f, 2f).Distance(new Vector3(1.5f, 0, 0)), 1e-5);
        Assert.Equal(0.5f, Shape.Cylinder(0.5f, 2f).Distance(new Vector3(0, 1.5f, 0)), 1e-5);
    }

    [Fact]
    public void A_stationary_box_is_hit_on_its_near_face_with_the_matching_delay()
    {
        var h = Record(600, _ => new Sample(new Vector3(10, 0, 0), Quaternion.Identity));
        var hit = RetardedRay.Trace(h, Dt, Vector3.Zero, Vector3.UnitX, 5f, 0, Shape.Box(new Vector3(2, 2, 2)),
            linearBound: 0, angularBound: 0, objectRadius: 1.8);

        Assert.True(hit.Hit);
        Assert.Equal(9f, hit.Point.X, 5e-3);
        Assert.Equal(9.0 / 5, hit.Delay, 2e-3);
        Assert.True(Vector3.Distance(new Vector3(-1, 0, 0), hit.Normal) < 1e-2);
    }

    [Fact]
    public void A_ray_that_misses_hits_nothing()
    {
        var h = Record(600, _ => new Sample(new Vector3(10, 0, 0), Quaternion.Identity));
        var hit = RetardedRay.Trace(h, Dt, Vector3.Zero, Vector3.UnitY, 5f, 0, Shape.Sphere(0.5f), 0, 0, 0.5);
        Assert.False(hit.Hit);
    }

    [Fact]
    public void Nothing_is_seen_before_its_first_light_arrives()
    {
        // 10 m away at c = 5: 2 s of history is too short for the light from sample 0 to have arrived.
        var h = Record(60, _ => new Sample(new Vector3(10, 0, 0), Quaternion.Identity));
        var hit = RetardedRay.Trace(h, Dt, Vector3.Zero, Vector3.UnitX, 5f, 0, Shape.Sphere(0.5f), 0, 0, 0.5);
        Assert.False(hit.Hit);
    }

    [Fact]
    public void A_fast_circling_cube_is_seen_wherever_a_ray_meets_one_of_its_images()
    {
        // Radius 4 at 1.2 rad/s with c = 1 m/s: the cube outruns its light, so there are several images.
        const float radius = 4, omega = 1.2f, c = 1f;
        Sample Pose(double t)
        {
            float a = omega * (float)t;
            return new Sample(new Vector3(radius * MathF.Cos(a), 1, radius * MathF.Sin(a)),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, -a));
        }
        const int n = 1800;
        var h = Record(n, Pose);
        var cam = new Vector3(0, 6, 12);
        var shape = Shape.Box(new Vector3(1, 1, 1));
        double speed = radius * omega;

        int hits = 0;
        // A fan of rays over the circle's neighbourhood.
        for (int ix = -8; ix <= 8; ix++)
        for (int iz = -8; iz <= 4; iz++)
        {
            var target = new Vector3(ix * 0.6f, 1, iz * 0.6f);
            var dir = Vector3.Normalize(target - cam);
            var hit = RetardedRay.Trace(h, Dt, cam, dir, c, 0, shape, speed, omega, 0.9);
            if (!hit.Hit)
                continue;
            hits++;

            // The point the ray hit must really be a point of the cube at that earlier moment...
            double ts = (n - 1) * Dt - hit.Delay;
            var pose = Pose(ts);
            var local = Vector3.Transform(hit.Point - pose.Position, Quaternion.Conjugate(pose.Rotation));
            Assert.True(MathF.Abs(shape.Distance(local)) < 2e-2f, $"hit is not on the surface: {shape.Distance(local)}");
            // ...and light that left it then reaches the camera now.
            Assert.Equal(c * hit.Delay, Vector3.Distance(cam, hit.Point), 2e-2);
        }
        Assert.True(hits > 10, $"expected plenty of hits, got {hits}");
    }

    [Fact]
    public void The_hit_is_the_nearest_image_along_the_ray()
    {
        // Compare with a brute-force march in tiny time steps: the first (latest) hit must agree.
        const float radius = 4, omega = 1.2f, c = 1f;
        Sample Pose(double t)
        {
            float a = omega * (float)t;
            return new Sample(new Vector3(radius * MathF.Cos(a), 1, radius * MathF.Sin(a)), Quaternion.Identity);
        }
        const int n = 1800;
        var h = Record(n, Pose);
        double tNow = (n - 1) * Dt;
        var cam = new Vector3(0, 6, 12);
        var shape = Shape.Sphere(0.7f);

        int compared = 0;
        for (int i = -6; i <= 6; i++)
        {
            var dir = Vector3.Normalize(new Vector3(i * 0.7f, 1, -1) - cam);
            double? brute = null;
            for (double delay = 0; delay < tNow; delay += 2e-4)
            {
                var p = cam + dir * (float)(c * delay);
                if (shape.Distance(p - Pose(tNow - delay).Position) <= 0) { brute = delay; break; }
            }
            var hit = RetardedRay.Trace(h, Dt, cam, dir, c, 0, shape, radius * omega, 0, 0.7);
            Assert.Equal(brute.HasValue, hit.Hit);
            if (brute.HasValue)
            {
                Assert.True(Math.Abs(brute.Value - hit.Delay) < 0.03, $"ray {i}: brute {brute} vs {hit.Delay}");
                compared++;
            }
        }
        Assert.True(compared >= 2, $"only {compared} rays hit");
    }

    [Fact]
    public void Every_hit_lies_inside_one_of_the_intervals()
    {
        // The intervals are what lets the shader skip most of the history: no hit may ever fall outside them.
        const float radius = 4, omega = 1.2f, c = 1f;
        Sample Pose(double t)
        {
            float a = omega * (float)t;
            return new Sample(new Vector3(radius * MathF.Cos(a), 1, radius * MathF.Sin(a)),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, -a));
        }
        const int n = 1800;
        var h = Record(n, Pose);
        var shape = Shape.Box(new Vector3(1, 1, 1));
        const double objectRadius = 0.87;
        double speed = radius * omega;

        foreach (var cam in new[] { new Vector3(0, 6, 12), new Vector3(3, 1.5f, 3.5f), new Vector3(0, 1, 0) })
        {
            var intervals = RetardedRay.Intervals(h, Dt, cam, c, 0, objectRadius, speed);
            Assert.NotEmpty(intervals);
            Assert.True(intervals.Count <= RetardedRay.MaxIntervals);

            int hits = 0;
            for (int ix = -10; ix <= 10; ix++)
            for (int iy = -4; iy <= 6; iy++)
            for (int iz = -10; iz <= 10; iz++)
            {
                var dir = Vector3.Normalize(new Vector3(ix * 0.6f, iy * 0.5f, iz * 0.6f) - cam + new Vector3(0.01f, 0.02f, 0.03f));
                var hit = RetardedRay.Trace(h, Dt, cam, dir, c, 0, shape, speed, omega, objectRadius);
                if (!hit.Hit)
                    continue;
                hits++;

                double k = (n - 1) - hit.Delay / Dt;
                var inside = intervals.FirstOrDefault(i => k <= i.KNewest + 1e-6 && k >= i.KOldest - 1e-6);
                Assert.True(inside != default, $"hit at sample {k:0.0} is outside every interval");
                Assert.True(Vector3.Distance(inside.Center, hit.Point) <= inside.Radius + 1e-3f,
                    "hit point is outside its interval's sphere");
            }
            Assert.True(hits > 20, $"expected plenty of hits from {cam}, got {hits}");
        }
    }

    [Fact]
    public void A_slow_part_far_away_has_only_a_few_short_intervals()
    {
        // Receding at 0.5 m/s with c = 5: the light cone crosses the part once, in a short stretch of history.
        var h = Record(1200, t => new Sample(new Vector3((float)(10 + 0.5 * t), 0, 0), Quaternion.Identity));
        var intervals = RetardedRay.Intervals(h, Dt, Vector3.Zero, 5f, 0, 1.0, 0.5);

        Assert.Single(intervals);
        Assert.True(intervals[0].KNewest - intervals[0].KOldest < 120, "should be well under 2 s of history");
    }
}
