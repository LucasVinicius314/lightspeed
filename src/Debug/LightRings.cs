using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lightspeed.Debug;

/// <summary>
/// Plans the "delay rings" drawn around the camera: an object standing on the ring labelled T is
/// currently seen as it was T seconds ago. Engine-free so it can be unit-tested.
/// </summary>
public static class LightRings
{
    /// <summary>
    /// Delays (seconds) of the rings to draw: multiples of a "nice" (1, 2, 5 x 10^k) step chosen so
    /// neighbouring rings sit about <paramref name="targetSpacing"/> metres apart.
    /// </summary>
    public static IReadOnlyList<double> Delays(
        double c, double historySeconds, double maxRadius = 60, double targetSpacing = 4, int maxRings = 12)
    {
        var delays = new List<double>();
        double step = NiceCeil(targetSpacing / c);
        for (int k = 1; k <= maxRings; k++)
        {
            double d = k * step;
            if (d > historySeconds || c * d > maxRadius + 1e-9)
                break;
            delays.Add(d);
        }
        return delays;
    }

    /// <summary>Smallest value of the form 1, 2 or 5 x 10^k that is at least <paramref name="x"/>.</summary>
    public static double NiceCeil(double x)
    {
        double exp = Math.Floor(Math.Log10(x));
        double scale = Math.Pow(10, exp);
        double m = x / scale;
        double nice = m <= 1 + 1e-9 ? 1 : m <= 2 + 1e-9 ? 2 : m <= 5 + 1e-9 ? 5 : 10;
        return nice * scale;
    }

    public static string FormatDelay(double seconds)
    {
        var ci = CultureInfo.InvariantCulture;
        if (seconds >= 1) return seconds.ToString("0.##", ci) + " s";
        if (seconds >= 1e-3) return (seconds * 1e3).ToString("0.##", ci) + " ms";
        if (seconds >= 1e-6) return (seconds * 1e6).ToString("0.##", ci) + " µs";
        return (seconds * 1e9).ToString("0.##", ci) + " ns";
    }
}
