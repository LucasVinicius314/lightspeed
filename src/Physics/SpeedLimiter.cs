using Godot;
using Lightspeed.Core;

namespace Lightspeed.Physics;

/// <summary>
/// Keeps a RigidBody3D's fastest point below c. The retarded-time solve is only monotonic when
/// nothing moves at or above c, so the bound is linear speed plus spin speed at the bounding radius.
/// </summary>
public partial class SpeedLimiter : Node
{
    [Export(PropertyHint.Range, "0.1,0.99,0.01")] public float MaxFractionOfC { get; set; } = 0.9f;
    /// <summary>Distance from the body's centre to its farthest vertex.</summary>
    [Export] public float BoundingRadius { get; set; } = 0.5f;

    public override void _PhysicsProcess(double delta)
    {
        var body = GetParent<RigidBody3D>();
        float limit = SimClock.Instance.C * MaxFractionOfC;
        float speed = body.LinearVelocity.Length() + body.AngularVelocity.Length() * BoundingRadius;
        if (speed <= limit)
            return;

        float k = limit / speed;
        body.LinearVelocity *= k;
        body.AngularVelocity *= k;
    }
}
