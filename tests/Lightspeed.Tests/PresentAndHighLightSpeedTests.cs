using System.Numerics;
using Lightspeed.History;

namespace Lightspeed.Tests;

/// <summary>
/// The light history at a high speed of light: the band where light can meet a part falls between two samples, and the
/// retarded moment of a nearby part falls after the newest sample.
/// </summary>
public class PresentAndHighLightSpeedTests
{
    private const double Dt = 1.0 / 60.0;

    private sealed class Samples(Sample[] data, bool finished = false, int breakBefore = -1) : ISampleSource
    {
        public int Count => data.Length;
        public Sample this[int k] => data[k];
        public bool IsFinished => finished;
        public bool BreaksBefore(int k) => k == breakBefore;
    }

    private static Samples Record(int n, Func<double, Sample> pose, bool finished = false, int breakBefore = -1) =>
        new(Enumerable.Range(0, n).Select(k => pose(k * Dt)).ToArray(), finished, breakBefore);

    // ---- the virtual present sample ----

    [Fact]
    public void The_present_sample_continues_the_motion_one_tick_ahead()
    {
        var h = Record(10, t => new Sample(new Vector3((float)(3 * t), 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)t)));
        var present = new PresentSamples { Inner = h };

        Assert.Equal(11, present.Count);
        var v = present[10];
        Assert.Equal(3 * 10 * Dt, v.Position.X, 1e-4);
        Assert.Equal(10 * Dt, 2 * MathF.Atan2(v.Rotation.Y, v.Rotation.W), 1e-4);
        Assert.Equal(1, Quaternion.Dot(v.Rotation, v.Rotation), 1e-5); // still a unit quaternion
        Assert.Equal(h[4].Position, present[4].Position);
        Assert.False(present.BreaksBefore(10));
    }

    [Fact]
    public void The_present_sample_holds_still_after_a_teleport_and_is_absent_for_a_finished_part()
    {
        var teleported = Record(10, t => new Sample(new Vector3(t < 9 * Dt ? 0 : 50, 0, 0), Quaternion.Identity), breakBefore: 9);
        var present = new PresentSamples { Inner = teleported };
        Assert.Equal(50, present[10].Position.X, 1e-5); // not 100: nothing to extrapolate from

        var finished = new PresentSamples { Inner = Record(10, t => new Sample(new Vector3((float)t, 0, 0), Quaternion.Identity), finished: true) };
        Assert.Equal(10, finished.Count);
        Assert.Equal(0.5, finished.EffectiveAge(0.5, Dt));

        var alive = new PresentSamples { Inner = Record(10, t => new Sample(new Vector3((float)t, 0, 0), Quaternion.Identity)) };
        Assert.Equal(0.005 - Dt, alive.EffectiveAge(0.005, Dt), 1e-12);
    }

    [Fact]
    public void A_nearby_part_moves_smoothly_between_ticks_when_light_is_fast()
    {
        // 10 m/s past a camera 3 m away at c = 600: the light takes 5 ms, less than a tick, so the retarded moment lies
        // after the newest sample. The part must be where it is "now" (plus the little it moved while the light travelled),
        // not stuck at the newest sample.
        var velocity = new Vector3(10, 0, 0);
        var h = Record(120, t => new Sample(new Vector3(-19, 0, 3) + velocity * (float)t, Quaternion.Identity));
        var present = new PresentSamples { Inner = h };
        var camera = Vector3.Zero;
        const float c = 600f;

        double worstPresent = 0, worstClamped = 0;
        for (int i = 0; i <= 16; i++)
        {
            double age = Dt * i / 17.0;
            var now = h[119].Position + velocity * (float)age;

            // Where the part was when the light now reaching the camera left it.
            var expected = now;
            for (int it = 0; it < 30; it++)
                expected = now - velocity * (Vector3.Distance(camera, expected) / c);

            var withPresent = RetardedSolver.Solve(present, Dt, camera, c, Vector3.Zero, present.EffectiveAge(age, Dt));
            var clamped = RetardedSolver.Solve(h, Dt, camera, c, Vector3.Zero, age);

            Assert.Equal(SolveStatus.Ok, withPresent.Status);
            worstPresent = Math.Max(worstPresent, Vector3.Distance(expected, withPresent.Position));
            worstClamped = Math.Max(worstClamped, Vector3.Distance(expected, clamped.Position));
        }
        Assert.True(worstPresent < 5e-3, $"with the present sample the error should be tiny, got {worstPresent}");
        Assert.True(worstClamped > 0.05, $"(sanity) without it the part lags by up to a tick of motion, got {worstClamped}");
    }

    // ---- intervals at a high speed of light ----

    [Fact]
    public void A_part_that_falls_between_two_samples_still_has_an_interval()
    {
        // At c = 600 light travels 10 m per tick. A sphere 5 m away is never within its own radius of the light cone at
        // a sample (the cone is at 0 m, 10 m, 20 m ...), only between two of them.
        var h = Record(600, _ => new Sample(new Vector3(5, 0, 0), Quaternion.Identity));
        var intervals = RetardedRay.Intervals(h, Dt, Vector3.Zero, 600f, 0, 0.5, 0);
        Assert.NotEmpty(intervals);

        var hit = RetardedRay.Trace(h, Dt, Vector3.Zero, Vector3.UnitX, 600f, 0, Shape.Sphere(0.5f), 0, 0, 0.5);
        Assert.True(hit.Hit);
        double k = 599 - hit.Delay / Dt;
        Assert.Contains(intervals, i => k <= i.KNewest + 1e-6 && k >= i.KOldest - 1e-6);
    }

    [Theory]
    [InlineData(100f)]
    [InlineData(600f)]
    [InlineData(5000f)]
    public void Every_hit_lies_inside_one_of_the_intervals_at_a_high_speed_of_light(float c)
    {
        const float radius = 4, omega = 1.2f;
        Sample Pose(double t)
        {
            float a = omega * (float)t;
            return new Sample(new Vector3(radius * MathF.Cos(a), 1, radius * MathF.Sin(a)), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -a));
        }
        const int n = 1800;
        var h = Record(n, Pose);
        var shape = Shape.Box(new Vector3(1, 1, 1));
        const double objectRadius = 0.87;
        double speed = radius * omega;

        foreach (var cam in new[] { new Vector3(0, 6, 12), new Vector3(3, 1.5f, 3.5f), new Vector3(0, 1, 0) })
        {
            // Through the virtual present sample, as the game does: at this speed of light the light often left after the newest sample.
            var present = new PresentSamples { Inner = h };
            double age = Dt * 0.4;
            double effective = present.EffectiveAge(age, Dt);
            var intervals = RetardedRay.Intervals(present, Dt, cam, c, effective, objectRadius, speed);
            Assert.NotEmpty(intervals);

            int hits = 0;
            for (int ix = -10; ix <= 10; ix++)
            for (int iy = -4; iy <= 6; iy++)
            for (int iz = -10; iz <= 10; iz++)
            {
                var dir = Vector3.Normalize(new Vector3(ix * 0.6f, iy * 0.5f, iz * 0.6f) - cam + new Vector3(0.01f, 0.02f, 0.03f));
                var hit = RetardedRay.Trace(present, Dt, cam, dir, c, effective, shape, speed, omega, objectRadius);
                if (!hit.Hit)
                    continue;
                hits++;
                double k = n - hit.Delay / Dt + effective / Dt; // sample index in the extended track (the virtual sample is index n)
                var inside = intervals.FirstOrDefault(i => k <= i.KNewest + 1e-6 && k >= i.KOldest - 1e-6);
                Assert.True(inside != default, $"c={c}: hit at sample {k:0.0} is outside every interval (from {cam})");
                Assert.True(Vector3.Distance(inside.Center, hit.Point) <= inside.Radius + 1e-3f, "hit point is outside its interval's sphere");
            }
            Assert.True(hits > 20, $"c={c}: expected plenty of hits from {cam}, got {hits}");
        }
    }

    [Fact]
    public void A_ray_reaches_a_surface_closer_than_c_times_the_age_through_the_present_sample()
    {
        // c = 600 and a quarter of a tick since the newest sample: light covers 2.5 m in that time. A box whose near face is
        // 1.5 m from the camera is inside that distance; the ray must still find it.
        var h = Record(600, _ => new Sample(new Vector3(2, 0, 0), Quaternion.Identity));
        var present = new PresentSamples { Inner = h };
        double age = Dt / 4;
        var shape = Shape.Box(new Vector3(1, 1, 1));

        var withPresent = RetardedRay.Trace(present, Dt, Vector3.Zero, Vector3.UnitX, 600f, present.EffectiveAge(age, Dt), shape, 0, 0, 0.87);
        Assert.True(withPresent.Hit);
        Assert.Equal(1.5f, withPresent.Point.X, 5e-3);

        // (sanity) the plain history cannot reach a point nearer than c x age: it finds the box's far face, not the near one
        var without = RetardedRay.Trace(h, Dt, Vector3.Zero, Vector3.UnitX, 600f, age, shape, 0, 0, 0.87);
        Assert.True(!without.Hit || without.Point.X > 2.0f);
    }
}
