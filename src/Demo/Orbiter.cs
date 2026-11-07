using Godot;
using Lightspeed.Core;

namespace Lightspeed.Demo;

/// <summary>Deterministic circular motion plus a spin, driven purely by sim time.</summary>
public partial class Orbiter : AnimatableBody3D
{
    [Export] public Vector3 Center { get; set; } = new(0, 1, 0);
    [Export] public float Radius { get; set; } = 4f;
    /// <summary>Radians per second. Linear speed = Radius * AngularSpeed, which must stay below c.</summary>
    [Export] public float AngularSpeed { get; set; } = 0.75f;
    [Export] public float SpinSpeed { get; set; } = 1.5f;

    /// <summary>Half-diagonal of the cube; its corners move at SpinSpeed * BoundingRadius relative to the centre.</summary>
    [Export] public float BoundingRadius { get; set; } = 0.87f;

    public override void _Ready()
    {
        float peak = Radius * AngularSpeed + SpinSpeed * BoundingRadius;
        if (peak >= SimClock.Instance.C)
            GD.PushWarning($"{Name}: peak vertex speed {peak:F2} m/s is not below c = {SimClock.Instance.C} m/s");
    }

    public override void _PhysicsProcess(double delta)
    {
        // Phases are reduced in double before narrowing, so they stay exact however long the sim runs.
        double t = SimClock.Instance.WorldTime;
        float a = (float)(AngularSpeed * t % System.Math.Tau);
        float spin = (float)(SpinSpeed * t % System.Math.Tau);
        Position = Center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * Radius;
        Basis = new Basis(Vector3.Up, spin) * new Basis(Vector3.Right, 0.4f);
    }
}
