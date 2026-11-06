using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lightspeed.Core;

/// <summary>
/// The predefined values the player can step the speed of light through, slowest to fastest,
/// plus formatting helpers. Engine-free so it can be unit-tested.
/// </summary>
public static class SpeedLadder
{
    public const float LightSpeed = 299_792_458f;

    public readonly record struct Step(float Value, string? Name = null);

    public static IReadOnlyList<Step> Steps { get; } = Build();

    private static Step[] Build()
    {
        var steps = new List<Step>();
        foreach (float cm in new[] { 1f, 2f, 5f, 10f, 20f, 50f })
            steps.Add(new Step(cm / 100f));
        for (int m = 1; m <= 10; m++)
            steps.Add(new Step(m));
        steps.Add(new Step(20));
        steps.Add(new Step(50));
        steps.Add(new Step(100));
        steps.Add(new Step(343, "speed of sound"));
        steps.Add(new Step(500));
        steps.Add(new Step(1_000));
        steps.Add(new Step(10_000));
        steps.Add(new Step(LightSpeed * 0.001f, "0.1% of c"));
        steps.Add(new Step(LightSpeed * 0.01f, "1% of c"));
        steps.Add(new Step(LightSpeed * 0.1f, "10% of c"));
        steps.Add(new Step(LightSpeed * 0.25f, "25% of c"));
        steps.Add(new Step(LightSpeed * 0.5f, "50% of c"));
        steps.Add(new Step(LightSpeed * 0.9f, "90% of c"));
        steps.Add(new Step(LightSpeed * 0.99f, "99% of c"));
        steps.Add(new Step(LightSpeed, "speed of light"));
        return steps.ToArray();
    }

    /// <summary>Index of the step closest to <paramref name="value"/> on a log scale.</summary>
    public static int Nearest(float value)
    {
        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < Steps.Count; i++)
        {
            double d = Math.Abs(Math.Log(Steps[i].Value) - Math.Log(Math.Max(value, 1e-9f)));
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    public static string FormatSpeed(float v)
    {
        var ci = CultureInfo.InvariantCulture;
        if (v >= 1000f) return v.ToString("N0", ci) + " m/s";
        if (v >= 1f) return v.ToString("0.##", ci) + " m/s";
        return (v * 100f).ToString("0.##", ci) + " cm/s";
    }

    public static string FormatDistance(double meters)
    {
        var ci = CultureInfo.InvariantCulture;
        if (meters >= 100_000) return "unlimited";
        if (meters >= 1000) return (meters / 1000).ToString("0.#", ci) + " km";
        if (meters >= 1) return meters.ToString("0.#", ci) + " m";
        return (meters * 100).ToString("0.#", ci) + " cm";
    }

    /// <summary>Fraction of the real speed of light, as "12.5% of c" or "1.67e-8 c".</summary>
    public static string FormatFractionOfC(float v)
    {
        var ci = CultureInfo.InvariantCulture;
        double r = v / LightSpeed;
        return r >= 0.001 ? (r * 100).ToString("0.##", ci) + "% of c" : r.ToString("0.###e+0", ci) + " c";
    }
}
