using Godot;

namespace Lightspeed.Core;

/// <summary>M0 smoke test: prints tick and t once per second of sim time.</summary>
public partial class Main : Node3D
{
    public override void _Ready()
    {
        SimClock.Instance.Ticked += OnTicked;
        Debug.ScreenshotCapture.AttachIfRequested(this);
    }

    private static void OnTicked(long tick, double time)
    {
        if (tick % SimClock.TickRate == 0)
            GD.Print($"tick={tick} t={time:F3}");
    }
}
