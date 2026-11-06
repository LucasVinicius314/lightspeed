using System.Numerics;
using Lightspeed.History;

namespace Lightspeed.Tests;

public class MultiImageTests
{
    private const double Dt = 1.0 / 60.0;

    private sealed class Samples(Sample[] data) : ISampleSource
    {
        public int Count => data.Length;
        public Sample this[int k] => data[k];
    }

    private static Samples Record(int n, Func<double, Vector3> position) =>
        new(Enumerable.Range(0, n).Select(k => new Sample(position(k * Dt), Quaternion.Identity)).ToArray());

    /// <summary>All moments ts (newest first) where light from the analytic path reaches the camera at tNow.</summary>
    private static List<double> DenseRoots(Func<double, Vector3> path, Vector3 cam, float c, double tNow)
    {
        double G(double ts) => Vector3.Distance(cam, path(ts)) - c * (tNow - ts);
        var roots = new List<double>();
        const double step = 1e-3;
        double hi = tNow, gHi = G(hi);
        for (double lo = tNow - step; lo >= 0; lo -= step)
        {
            double gLo = G(lo);
            if ((gLo <= 0) != (gHi <= 0))
            {
                double a = lo, b = lo + step;
                bool negA = gLo <= 0;
                for (int i = 0; i < 50; i++)
                {
                    double mid = (a + b) / 2;
                    if ((G(mid) <= 0) == negA) a = mid; else b = mid;
                }
                roots.Add((a + b) / 2);
            }
            hi = lo;
            gHi = gLo;
        }
        return roots;
    }

    [Fact]
    public void Slower_than_light_has_exactly_one_image_that_matches_the_binary_search()
    {
        var h = Record(600, t => new Vector3(10 + 3 * (float)t, 0, 0)); // receding at 3 m/s, c = 5
        var one = RetardedSolver.Solve(h, Dt, Vector3.Zero, 5f, Vector3.Zero);
        var first = RetardedSolver.SolveImage(h, Dt, Vector3.Zero, 5f, Vector3.Zero, 0, 0, speedBound: 3);
        var second = RetardedSolver.SolveImage(h, Dt, Vector3.Zero, 5f, Vector3.Zero, 0, 1, speedBound: 3);

        Assert.Equal(SolveStatus.Ok, first.Status);
        Assert.Equal(one.Delay, first.Delay, 1e-4);
        Assert.True(Vector3.Distance(one.Position, first.Position) < 1e-3);
        Assert.Equal(SolveStatus.NotVisible, second.Status);
    }

    [Fact]
    public void A_point_circling_faster_than_light_shows_several_images_at_the_right_places()
    {
        // Radius 4, 1.2 rad/s (4.8 m/s) with c = 1 m/s: the point outruns its own light several times over.
        const float radius = 4, omega = 1.2f, c = 1f;
        Vector3 Orbit(double t) => new(radius * MathF.Cos(omega * (float)t), 1, radius * MathF.Sin(omega * (float)t));
        const int n = 1800; // 30 s: enough history for the light to have passed the point several times
        double tNow = (n - 1) * Dt;
        var cam = new Vector3(0, 6, 12);
        var h = Record(n, Orbit);

        var expected = DenseRoots(Orbit, cam, c, tNow);
        Assert.True(expected.Count >= 3, $"test setup should produce several images, got {expected.Count}");

        for (int i = 0; i < expected.Count; i++)
        {
            var r = RetardedSolver.SolveImage(h, Dt, cam, c, Vector3.Zero, 0, i, speedBound: radius * omega);
            Assert.True(r.Status == SolveStatus.Ok, $"image {i} of {expected.Count}: {r.Status}");
            Assert.True(Math.Abs(tNow - expected[i] - r.Delay) < 5e-3, $"image {i}: expected delay {tNow - expected[i]}, got {r.Delay}");
            Assert.True(Vector3.Distance(Orbit(expected[i]), r.Position) < 2e-2, $"image {i}");
        }
        Assert.Equal(SolveStatus.NotVisible,
            RetardedSolver.SolveImage(h, Dt, cam, c, Vector3.Zero, 0, expected.Count, radius * omega).Status);
    }

    [Fact]
    public void Skipping_with_a_speed_bound_finds_the_same_images_as_checking_every_sample()
    {
        const float radius = 4, omega = 1.2f, c = 1f;
        Vector3 Orbit(double t) => new(radius * MathF.Cos(omega * (float)t), 1, radius * MathF.Sin(omega * (float)t));
        var cam = new Vector3(3, 5, 10);
        var h = Record(1800, Orbit);

        for (int image = 0; image < 8; image++)
        {
            var dense = RetardedSolver.SolveImage(h, Dt, cam, c, Vector3.Zero, 0.004, image, speedBound: 0, maxIterations: 10_000);
            var skipping = RetardedSolver.SolveImage(h, Dt, cam, c, Vector3.Zero, 0.004, image, speedBound: radius * omega);
            Assert.True(dense.Status == skipping.Status, $"image {image}: dense {dense.Status} delay {dense.Delay}, skipping {skipping.Status} delay {skipping.Delay}");
            if (dense.Status == SolveStatus.Ok)
                Assert.Equal(dense.Delay, skipping.Delay, 1e-4);
        }
    }

    [Fact]
    public void A_teleport_gap_is_not_counted_as_an_image()
    {
        // Same setup as the single-image teleport test: in the gap there is nothing, for every image index.
        var track = new HistoryTrack(2000, 0);
        for (int t = 0; t < 100; t++) track.Record(t, new Sample(new Vector3(10, 0, 0), Quaternion.Identity));
        for (int t = 100; t < 340; t++) track.Record(t, new Sample(new Vector3(30, 0, 0), Quaternion.Identity), isBreak: t == 100);

        for (int image = 0; image < 3; image++)
            Assert.Equal(SolveStatus.NotVisible,
                RetardedSolver.SolveImage(track, Dt, Vector3.Zero, 5f, Vector3.Zero, 0, image, speedBound: 0).Status);
    }

    [Fact]
    public void Peak_speeds_follow_the_recorded_motion_and_ignore_teleports()
    {
        var track = new HistoryTrack(500, 0, Dt);
        for (int t = 0; t < 200; t++)
        {
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(2.0 * t * Dt)); // 2 rad/s
            track.Record(t, new Sample(new Vector3((float)(3.0 * t * Dt), 0, 0), rot)); // 3 m/s
        }
        track.Record(200, new Sample(new Vector3(500, 0, 0), Quaternion.Identity), isBreak: true);

        Assert.Equal(3f, track.PeakLinearSpeed, 0.05f);
        Assert.Equal(2f, track.PeakAngularSpeed, 0.05f);
        Assert.Equal(3f + 2f * 0.5f, track.PeakVertexSpeed(0.5f), 0.1f);
    }
}
