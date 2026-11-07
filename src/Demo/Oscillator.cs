using Godot;
using Lightspeed.Core;

namespace Lightspeed.Demo;

/// <summary>Back-and-forth sinusoidal motion along an axis, driven purely by sim time.</summary>
public partial class Oscillator : AnimatableBody3D
{
    [Export] public Vector3 Center { get; set; } = Vector3.Zero;
    [Export] public Vector3 Axis { get; set; } = Vector3.Right;
    [Export] public float Amplitude { get; set; } = 3f;
    /// <summary>Radians per second. Peak speed = Amplitude * AngularSpeed, which must stay below c.</summary>
    [Export] public float AngularSpeed { get; set; } = 1f;

    public override void _Ready()
    {
        float peak = Amplitude * AngularSpeed;
        if (peak >= SimClock.Instance.C)
            GD.PushWarning($"{Name}: peak speed {peak:F2} m/s is not below c = {SimClock.Instance.C} m/s");
    }

    public override void _PhysicsProcess(double delta)
    {
        float phase = (float)(AngularSpeed * SimClock.Instance.WorldTime % System.Math.Tau);
        Position = Center + Axis.Normalized() * Amplitude * Mathf.Sin(phase);
    }
}
