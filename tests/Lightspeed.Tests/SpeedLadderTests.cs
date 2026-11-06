using Lightspeed.Core;

namespace Lightspeed.Tests;

public class SpeedLadderTests
{
    [Fact]
    public void Steps_are_strictly_increasing_and_end_at_the_speed_of_light()
    {
        var steps = SpeedLadder.Steps;
        for (int i = 1; i < steps.Count; i++)
            Assert.True(steps[i].Value > steps[i - 1].Value, $"step {i}");
        Assert.Equal(SpeedLadder.LightSpeed, steps[^1].Value);
        Assert.Equal(0.01f, steps[0].Value);
    }

    [Theory]
    [InlineData(5f, 5f)]
    [InlineData(4.9f, 5f)]
    [InlineData(300_000_000f, SpeedLadder.LightSpeed)]
    [InlineData(0.0001f, 0.01f)]
    public void Nearest_snaps_to_a_step(float input, float expected) =>
        Assert.Equal(expected, SpeedLadder.Steps[SpeedLadder.Nearest(input)].Value);

    [Theory]
    [InlineData(5f, "5 m/s")]
    [InlineData(1000f, "1,000 m/s")]
    [InlineData(0.05f, "5 cm/s")]
    [InlineData(SpeedLadder.LightSpeed, "299,792,448 m/s")] // float32 nearest to 299,792,458
    public void Speeds_are_formatted_with_sensible_units(float v, string expected) =>
        Assert.Equal(expected, SpeedLadder.FormatSpeed(v));
}
