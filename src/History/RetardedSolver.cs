using System;
using System.Numerics;

namespace Lightspeed.History;

/// <summary>One recorded pose of an object at a simulation tick.</summary>
public readonly record struct Sample(Vector3 Position, Quaternion Rotation);

/// <summary>Samples ordered oldest (0) to newest (Count - 1), one per tick, evenly spaced.</summary>
public interface ISampleSource
{
    int Count { get; }
    Sample this[int k] { get; }

    /// <summary>The object no longer exists after its newest sample (despawned).</summary>
    bool IsFinished => false;

    /// <summary>Sample k is discontinuous from k - 1 (a teleport): never interpolate across it.</summary>
    bool BreaksBefore(int k) => false;
}

public enum SolveStatus
{
    Ok,
    /// <summary>
    /// Nothing to see: no history yet, the light from the first sample has not arrived, the light from
    /// a despawned object's last sample has already gone past, or the camera is in the gap left by a teleport.
    /// </summary>
    NotVisible,
}

/// <param name="Delay">Seconds between the present and the retarded moment the camera sees.</param>
public readonly record struct RetardedPose(SolveStatus Status, Vector3 Position, Quaternion Rotation, double Delay);

/// <summary>
/// Finds the retarded pose: the moment t_s where light leaving the object reaches the camera now.
/// Solves f(t_s) = |camera - x(t_s)| - c (t_now - t_s) = 0 by binary search over the samples,
/// then one secant refinement inside the bracketing segment.
/// <c>age</c> is the time since the newest sample (a partial tick, plus whole ticks for a despawned
/// object), so retarded time moves smoothly between ticks instead of stepping at 60 Hz.
/// This is the CPU reference for shaders/delayed_visual.gdshader; keep the two in step.
/// </summary>
public static class RetardedSolver
{
    public static RetardedPose Solve(ISampleSource h, double dt, Vector3 camera, float c, Vector3 localVertex, double age = 0)
    {
        int n = h.Count;
        var hidden = new RetardedPose(SolveStatus.NotVisible, default, Quaternion.Identity, 0);
        if (n == 0)
            return hidden;

        float FSample(int k)
        {
            var s = h[k];
            return Vector3.Distance(camera, s.Position + Vector3.Transform(localVertex, s.Rotation))
                   - c * (float)((n - 1 - k) * dt + age);
        }

        float FAt(int lo, float a)
        {
            var (p, q) = Interpolate(h, lo, a);
            return Vector3.Distance(camera, p + Vector3.Transform(localVertex, q))
                   - c * (float)((n - 1 - lo - a) * dt + age);
        }

        // Light from even the first sample has not arrived yet.
        if (FSample(0) > 0f)
            return hidden;

        // The newest sample's light has already reached us. Alive: closer than one tick of light
        // travel, so show it. Despawned: the object's last light has gone past, nothing left to see.
        if (FSample(n - 1) <= 0f)
            return h.IsFinished
                ? hidden
                : new RetardedPose(SolveStatus.Ok, h[n - 1].Position, h[n - 1].Rotation, age);

        // Invariant: f(lo) <= 0 < f(hi).
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (FSample(mid) <= 0f) lo = mid; else hi = mid;
        }

        // The crossing falls on a teleport: the last light from the old place has passed and the first
        // light from the new place has not arrived, so nothing is visible.
        if (h.BreaksBefore(hi))
            return hidden;

        float f0 = FSample(lo), f1 = FSample(hi);
        float a = Math.Clamp(-f0 / (f1 - f0), 0f, 1f);

        // One secant step on the better-bracketed side of the first estimate.
        float fa = FAt(lo, a);
        float aLo = 0f, fLo = f0, aHi = 1f, fHi = f1;
        if (fa > 0f) { aHi = a; fHi = fa; } else { aLo = a; fLo = fa; }
        if (MathF.Abs(fHi - fLo) > 1e-6f)
            a = Math.Clamp(aLo - fLo * (aHi - aLo) / (fHi - fLo), 0f, 1f);

        var (pos, rot) = Interpolate(h, lo, a);
        return new RetardedPose(SolveStatus.Ok, pos, rot, (n - 1 - lo - a) * dt + age);
    }

    private static (Vector3, Quaternion) Interpolate(ISampleSource h, int lo, float a)
    {
        var s0 = h[lo];
        var s1 = h[lo + 1];
        return (Vector3.Lerp(s0.Position, s1.Position, a), Quaternion.Lerp(s0.Rotation, s1.Rotation, a));
    }
}
