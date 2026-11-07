using Godot;
using Lightspeed.Core;

namespace Lightspeed.Demo;

/// <summary>
/// An <see cref="AnimatableBody3D"/> that turns about an axis, driven purely by world time (so every peer sees the same angle).
/// The player and thrown objects collide with it and are pushed by it. Like the other scripted movers it is a delayed part:
/// give it a LightHistory and a DelayedVisual.
/// </summary>
public partial class SpinningBody : AnimatableBody3D
{
    [Export] public Vector3 Axis { get; set; } = Vector3.Up;
    /// <summary>Radians per second (sign sets the direction).</summary>
    [Export] public float AngularSpeed { get; set; } = 1f;
    /// <summary>Distance from the axis to the farthest point of the body; the tip moves at AngularSpeed times this.</summary>
    [Export] public float BoundingRadius { get; set; } = 4f;

    public override void _Ready()
    {
        float tip = Mathf.Abs(AngularSpeed) * BoundingRadius;
        if (tip >= SimClock.Instance.C)
            GD.PushWarning($"{Name}: tip speed {tip:F2} m/s is not below c = {SimClock.Instance.C} m/s");
    }

    public override void _PhysicsProcess(double delta)
    {
        float angle = (float)(AngularSpeed * SimClock.Instance.WorldTime % System.Math.Tau);
        Basis = new Basis(Axis.Normalized(), angle);
    }
}
