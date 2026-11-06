using System.Numerics;
using Lightspeed.History;

namespace Lightspeed.Tests;

public class RetardedSolverTests
{
    private const double Dt = 1.0 / 60.0;
    private const float C = 5f;

    private sealed class Samples(Sample[] data) : ISampleSource
    {
        public int Count => data.Length;
        public Sample this[int k] => data[k];
    }

    private static Samples Record(int n, Func<double, Vector3> position, Func<double, Quaternion>? rotation = null) =>
        new(Enumerable.Range(0, n)
            .Select(k => new Sample(position(k * Dt), rotation?.Invoke(k * Dt) ?? Quaternion.Identity))
            .ToArray());

    [Fact]
    public void Stationary_object_is_delayed_by_distance_over_c()
    {
        var h = Record(600, _ => new Vector3(10, 0, 0));
        var r = RetardedSolver.Solve(h, Dt, Vector3.Zero, C, Vector3.Zero);

        Assert.Equal(SolveStatus.Ok, r.Status);
        Assert.Equal(10 / C, r.Delay, 1e-4);
    }

    [Fact]
    public void Camera_on_the_object_sees_the_present()
    {
        var h = Record(100, _ => new Vector3(1, 2, 3));
        var r = RetardedSolver.Solve(h, Dt, new Vector3(1, 2, 3), C, Vector3.Zero);

        Assert.Equal(SolveStatus.Ok, r.Status);
        Assert.Equal(0, r.Delay);
        Assert.Equal(new Vector3(1, 2, 3), r.Position);
    }

    [Fact]
    public void Object_receding_at_constant_speed_matches_closed_form()
    {
        // x(t) = x0 + v t; light condition x0 + v ts = c (tNow - ts)  =>  ts = (c tNow - x0) / (v + c)
        const float x0 = 10, v = 3;
        const int n = 301;
        double tNow = (n - 1) * Dt;
        var h = Record(n, t => new Vector3((float)(x0 + v * t), 0, 0));

        var r = RetardedSolver.Solve(h, Dt, Vector3.Zero, C, Vector3.Zero);

        double ts = (C * tNow - x0) / (v + C);
        Assert.Equal(SolveStatus.Ok, r.Status);
        Assert.Equal(tNow - ts, r.Delay, 1e-4);
        Assert.Equal(x0 + v * ts, r.Position.X, 1e-3);
    }

    [Fact]
    public void Orbit_matches_a_dense_bisection_of_the_analytic_motion()
    {
        const float radius = 4, omega = 0.75f;
        Vector3 Orbit(double t) => new(radius * MathF.Cos(omega * (float)t), 1, radius * MathF.Sin(omega * (float)t));
        const int n = 600;
        double tNow = (n - 1) * Dt;
        var cam = new Vector3(0, 6, 12);
        var h = Record(n, Orbit);

        double F(double ts) => Vector3.Distance(cam, Orbit(ts)) - C * (tNow - ts);
        double lo = 0, hi = tNow;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (F(mid) <= 0) lo = mid; else hi = mid;
        }

        var r = RetardedSolver.Solve(h, Dt, cam, C, Vector3.Zero);

        Assert.Equal(SolveStatus.Ok, r.Status);
        Assert.Equal(tNow - lo, r.Delay, 1e-3);
        Assert.True(Vector3.Distance(Orbit(lo), r.Position) < 2e-3);
    }

    [Fact]
    public void Object_is_hidden_until_its_first_light_arrives()
    {
        // 10 m away, c = 5: light from the first sample needs 2 s (120 ticks) to arrive.
        var early = Record(60, _ => new Vector3(10, 0, 0));
        var late = Record(150, _ => new Vector3(10, 0, 0));

        Assert.Equal(SolveStatus.NotYetVisible, RetardedSolver.Solve(early, Dt, Vector3.Zero, C, Vector3.Zero).Status);
        Assert.Equal(SolveStatus.Ok, RetardedSolver.Solve(late, Dt, Vector3.Zero, C, Vector3.Zero).Status);
    }

    [Fact]
    public void Empty_history_is_hidden()
    {
        var r = RetardedSolver.Solve(new Samples([]), Dt, Vector3.Zero, C, Vector3.Zero);
        Assert.Equal(SolveStatus.NotYetVisible, r.Status);
    }

    [Fact]
    public void Rotated_vertex_is_solved_at_its_own_retarded_time()
    {
        // A spinning object: the tip vertex's delay differs from the centre's.
        var h = Record(600, _ => new Vector3(10, 0, 0), t => Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)t));
        var centre = RetardedSolver.Solve(h, Dt, Vector3.Zero, C, Vector3.Zero);
        var tip = RetardedSolver.Solve(h, Dt, Vector3.Zero, C, new Vector3(0, 0, 1));

        Assert.NotEqual(centre.Delay, tip.Delay);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.004)]
    [InlineData(0.0166)]
    public void Time_since_newest_sample_does_not_change_a_stationary_delay(double age)
    {
        // The present is `age` past the newest sample, but a stationary object is still d / c behind.
        var h = Record(600, _ => new Vector3(10, 0, 0));
        var r = RetardedSolver.Solve(h, Dt, Vector3.Zero, C, Vector3.Zero, age);

        Assert.Equal(10 / C, r.Delay, 1e-4);
    }

    [Fact]
    public void Retarded_pose_is_continuous_across_a_tick_boundary()
    {
        // End of one tick (age = Dt) must match the start of the next (one more sample, age = 0).
        const float radius = 4, omega = 0.75f;
        Vector3 Orbit(double t) => new(radius * MathF.Cos(omega * (float)t), 1, radius * MathF.Sin(omega * (float)t));
        var cam = new Vector3(0, 6, 12);

        var endOfTick = RetardedSolver.Solve(Record(600, Orbit), Dt, cam, C, Vector3.Zero, Dt);
        var nextTick = RetardedSolver.Solve(Record(601, Orbit), Dt, cam, C, Vector3.Zero, 0);

        Assert.True(Vector3.Distance(endOfTick.Position, nextTick.Position) < 1e-3);
    }
}
