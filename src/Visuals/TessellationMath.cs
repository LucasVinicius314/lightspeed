using System;

namespace Lightspeed.Visuals;

/// <summary>Engine-free helper for deciding how finely to tessellate a mesh. Unit-tested.</summary>
public static class TessellationMath
{
    /// <summary>Hard cap per axis so a huge mesh or tiny edge limit cannot explode the vertex count.</summary>
    public const int MaxSegments = 64;

    /// <summary>
    /// Segments needed along a span of <paramref name="length"/> so each is at most
    /// <paramref name="maxEdge"/> long, never fewer than <paramref name="current"/> (we only add detail).
    /// </summary>
    public static int Segments(float length, float maxEdge, int current = 1)
    {
        if (maxEdge <= 0f)
            return Math.Max(current, 1);
        int needed = (int)MathF.Ceiling(MathF.Abs(length) / maxEdge);
        return Math.Clamp(Math.Max(needed, current), 1, MaxSegments);
    }
}
