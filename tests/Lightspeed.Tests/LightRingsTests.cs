using Lightspeed.Debug;

namespace Lightspeed.Tests;

public class LightRingsTests
{
    [Fact]
    public void Five_m_per_s_gets_one_second_rings_up_to_the_history_length()
    {
        var delays = LightRings.Delays(c: 5, historySeconds: 10);
        Assert.Equal(10, delays.Count);
        Assert.Equal(1, delays[0], 1e-9);
        Assert.Equal(10, delays[^1], 1e-9);
    }

    [Fact]
    public void Slow_light_gets_fewer_coarser_rings()
    {
        var delays = LightRings.Delays(c: 1, historySeconds: 10);
        Assert.Equal(new[] { 5.0, 10.0 }, delays);
    }

    [Fact]
    public void Fast_light_is_capped_at_the_ring_limit_and_the_radius_limit()
    {
        var delays = LightRings.Delays(c: 50, historySeconds: 10);
        Assert.Equal(12, delays.Count);
        Assert.True(50 * delays[^1] <= 60 + 1e-6);

        // At the speed of light the rings are ~6 m apart (nice step 20 ns), so the radius cap stops at 10.
        var light = LightRings.Delays(c: 299_792_448, historySeconds: 10);
        Assert.Equal(10, light.Count);
        Assert.Equal(2e-8, light[0], 1e-12);
    }

    [Fact]
    public void Light_too_slow_for_the_history_has_no_rings() =>
        Assert.Empty(LightRings.Delays(c: 0.01, historySeconds: 10));

    [Theory]
    [InlineData(0.8, 1)]
    [InlineData(1, 1)]
    [InlineData(1.5, 2)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(0.013, 0.02)]
    public void NiceCeil_rounds_up_to_1_2_5(double x, double expected) =>
        Assert.Equal(expected, LightRings.NiceCeil(x), 1e-12);

    [Theory]
    [InlineData(1, "1 s")]
    [InlineData(2.5, "2.5 s")]
    [InlineData(0.5, "500 ms")]
    [InlineData(2e-5, "20 µs")]
    [InlineData(2e-8, "20 ns")]
    public void Delays_are_formatted_with_sensible_units(double s, string expected) =>
        Assert.Equal(expected, LightRings.FormatDelay(s));
}
