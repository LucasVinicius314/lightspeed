using Godot;
using Lightspeed.Core;

namespace Lightspeed.Demo;

/// <summary>
/// Spins this node about one of its own local axes, on top of whatever orientation it was placed with.
/// Driven purely by sim time. Used for wheels, rotors and fan blades.
/// </summary>
public partial class Spinner : Node3D
{
    [Export] public Vector3 Axis { get; set; } = Vector3.Up;
    /// <summary>Radians per second (sign sets the direction).</summary>
    [Export] public float AngularSpeed { get; set; } = 3f;
    /// <summary>Distance from the axis to the farthest point of this part and its children.</summary>
    [Export] public float BoundingRadius { get; set; } = 1f;

    private Basis _rest;

    public override void _Ready()
    {
        _rest = Basis;
        float tip = Mathf.Abs(AngularSpeed) * BoundingRadius;
        if (tip >= SimClock.Instance.C)
            GD.PushWarning($"{Name}: tip speed {tip:F2} m/s is not below c = {SimClock.Instance.C} m/s");
    }

    public override void _PhysicsProcess(double delta)
    {
        float angle = (float)(AngularSpeed * SimClock.Instance.WorldTime % System.Math.Tau);
        Basis = _rest * new Basis(Axis.Normalized(), angle);
    }
}
