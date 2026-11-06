using Lightspeed.Visuals;

namespace Lightspeed.Tests;

public class TessellationMathTests
{
    [Theory]
    [InlineData(4.4f, 0.2f, 1, 22)]   // long blade: 22 segments of 0.2 m
    [InlineData(0.04f, 0.2f, 1, 1)]   // thin side needs nothing
    [InlineData(1f, 0.2f, 1, 5)]
    [InlineData(1f, 0.2f, 8, 8)]      // never fewer than the mesh already has
    [InlineData(1000f, 0.2f, 1, TessellationMath.MaxSegments)] // capped
    [InlineData(-2f, 0.5f, 1, 4)]     // sign is ignored
    [InlineData(5f, 0f, 3, 3)]        // no limit: keep what is there
    public void Segments_keeps_every_edge_within_the_limit(float length, float maxEdge, int current, int expected) =>
        Assert.Equal(expected, TessellationMath.Segments(length, maxEdge, current));
}
