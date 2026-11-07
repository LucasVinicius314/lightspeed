using Godot;
using Lightspeed.Core;

namespace Lightspeed.Demo;

/// <summary>
/// Drives or flies in a circle at constant speed, nose along the direction of travel, driven purely
/// by sim time. Optional lean into the turn and vertical bobbing (for the helicopter).
/// </summary>
public partial class CircleMover : AnimatableBody3D
{
    [Export] public Vector3 Center { get; set; } = Vector3.Zero;
    [Export] public float Radius { get; set; } = 6f;
    /// <summary>Radians per second; negative goes the other way. Speed = Radius * |AngularSpeed|.</summary>
    [Export] public float AngularSpeed { get; set; } = 0.3f;
    /// <summary>Lean into the turn, in radians.</summary>
    [Export] public float Bank { get; set; }
    [Export] public float BobAmplitude { get; set; }
    [Export] public float BobSpeed { get; set; } = 1f;
    /// <summary>Distance from the origin to the farthest point of the whole machine, for the speed check.</summary>
    [Export] public float BoundingRadius { get; set; } = 1.5f;

    public override void _Ready()
    {
        float peak = Radius * Mathf.Abs(AngularSpeed);
        if (peak >= SimClock.Instance.C)
            GD.PushWarning($"{Name}: speed {peak:F2} m/s is not below c = {SimClock.Instance.C} m/s");
    }

    public override void _PhysicsProcess(double delta)
    {
        double t = SimClock.Instance.Time;
        float a = (float)(AngularSpeed * t % System.Math.Tau);
        float bob = BobAmplitude * Mathf.Sin((float)(BobSpeed * t % System.Math.Tau));
        float side = Mathf.Sign(AngularSpeed);

        Position = Center + new Vector3(Mathf.Cos(a), bob, Mathf.Sin(a)) * new Vector3(Radius, 1, Radius);
        // Local +X is the direction of travel; for a counter-clockwise path (positive speed) +Z points
        // at the centre of the circle, so a positive bank leans inward.
        float yaw = -a - side * Mathf.Pi / 2;
        Basis = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, side * Bank);
    }
}
